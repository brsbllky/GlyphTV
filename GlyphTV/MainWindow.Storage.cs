// ============================================================
// MainWindow.Storage.cs
// Dosya yolları, ayarlar, izleme geçmişi, logo yükleme,
// hash yardımcısı, HTTP istemcisi, cache boyut limitleri
// ============================================================

using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow
    {
        // ─── Cache boyut sabitleri ────────────────────────────────────
        // Bellek (RAM) ayak izini optimize etmek için cache limitleri dengelendi:
        // Disk önbelleği zaten kalıcı olduğundan, RAM'de sadece aktif görüntülenenler tutulur.
        private const int MAX_LOGO_CACHE     = 120;
        private const int MAX_POSTER_CACHE   = 60;
        private const int MAX_BACKDROP_CACHE = 12;
        private const int MAX_TMDB_CACHE     = 100;

        // ─── Paylaşımlı JsonSerializerOptions ────────────────────────
        // Her Serialize/Deserialize çağrısında varsayılan options nesnesi
        // oluşturulur; statik paylaşımlı instance bu allocationı önler.
        internal static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions { WriteIndented = false };

        // ─── Eşzamanlılık kilit nesneleri ─────────────────────────────
        private static readonly object _logoCacheLock   = new object();
        private static readonly object _posterCacheLock = new object();

        // Logo cache'in ekleme sırasını takip etmek için (FIFO/LRU)
        // List<string>.RemoveAt(0) → O(n): listedeki tüm öğeler kaydırılır.
        // Queue<string>.Dequeue()  → O(1): sadece baş işaretçi ilerler.
        // Büyük cache'lerde (MAX_LOGO_CACHE=300, MAX_POSTER_CACHE=150)
        // eviction döngüsü ciddi biçimde hızlanır.
        private static readonly Queue<string> _logoCacheOrder     = new Queue<string>();
        private static readonly Queue<string> _posterCacheOrder   = new Queue<string>();
        private static readonly Queue<string> _backdropCacheOrder = new Queue<string>();
        private static readonly Queue<string> _tmdbCacheOrder     = new Queue<string>();

        // ─────────────────────────────────────────────────────────────
        // Logo cache – boyut korumalı ekleme (thread‑safe)
        // ─────────────────────────────────────────────────────────────
        private static void SetLogoCache(string key, Bitmap? bitmap)
        {
            lock (_logoCacheLock)
            {
                if (_logoCache.ContainsKey(key))
                {
                    // DÜZELTME: Burada eski bitmap'i artık Dispose ETMİYORUZ.
                    // Aynı LogoUrl için eşzamanlı yükleme istekleri artık
                    // GetOrLoadLogoBitmap/_inFlightLogoLoads ile tekilleştiriliyor,
                    // bu yüzden normal şartlarda buraya iki farklı bitmap ile
                    // girilmez. Yine de - eviction yolundaki ("hâlâ kullanılıyor
                    // olabilir") aynı prensiple - burada dispose etmemek
                    // güvenlidir: eski bitmap hâlâ bir Channel.LogoBitmap'e atanmış
                    // ve ekranda render ediliyor olabilir; onu burada Dispose etmek
                    // Image kontrolünün bir sonraki layout/render turunda
                    // NullReferenceException (Bitmap.get_Size) ile çökmesine yol
                    // açar — crash.log'daki hata tam olarak buydu.
                    _logoCache[key] = bitmap;
                    return;
                }

                // Limit aşıldıysa en eski girdileri sadece referans olarak sil,
                // bitmap'i dispose etme (hâlâ kullanılıyor olabilir!)
                while (_logoCacheOrder.Count >= MAX_LOGO_CACHE && _logoCacheOrder.Count > 0)
                {
                    string oldest = _logoCacheOrder.Dequeue(); // O(1)
                    _logoCache.Remove(oldest);
                }

                _logoCache[key] = bitmap;
                _logoCacheOrder.Enqueue(key);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Aynı LogoUrl için eşzamanlı yükleme istekleri tekilleştirilir.
        //
        // DÜZELTME (crash fix): Onlarca kanal aynı logoyu paylaştığında
        // (yaygın durum), önceden her biri bağımsız olarak indirip ayrı
        // Bitmap örnekleri oluşturuyordu. İlk tamamlanan SetLogoCache
        // çağırıp bitmap'ini cache'e koyuyor ve ekranda gösteriliyordu;
        // ikinci tamamlanan da SetLogoCache çağırınca (eski kodda) hâlâ
        // ekranda gösterilmekte olan ilk bitmap'i Dispose ediyordu — bir
        // sonraki layout/render turunda Image.MeasureOverride dispose
        // edilmiş bitmap'in Size'ına erişmeye çalışıp NullReferenceException
        // fırlatıyordu (crash.log). Burada ilk isteyen indirmeyi başlatır,
        // aynı URL için gelen diğer tüm istekler aynı Task'i bekler — böylece
        // aynı logo için ikinci bir bitmap örneği asla oluşmaz.
        // ─────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, Task<Bitmap?>> _inFlightLogoLoads = new();
        private static readonly object _inFlightLogoLoadsLock = new object();

        private async Task<Bitmap?> GetOrLoadLogoBitmap(string logoUrl, string cacheDir)
        {
            Task<Bitmap?> loadTask;
            bool isOwner;
            lock (_inFlightLogoLoadsLock)
            {
                if (_inFlightLogoLoads.TryGetValue(logoUrl, out var existingTask))
                {
                    loadTask = existingTask;
                    isOwner = false;
                }
                else
                {
                    loadTask = LoadLogoBitmapCore(logoUrl, cacheDir);
                    _inFlightLogoLoads[logoUrl] = loadTask;
                    isOwner = true;
                }
            }

            try
            {
                var bitmap = await loadTask;
                if (isOwner) SetLogoCache(logoUrl, bitmap);
                return bitmap;
            }
            finally
            {
                if (isOwner)
                {
                    lock (_inFlightLogoLoadsLock) { _inFlightLogoLoads.Remove(logoUrl); }
                }
            }
        }

        private async Task<Bitmap?> LoadLogoBitmapCore(string logoUrl, string cacheDir)
        {
            string hash      = GetUrlHash(logoUrl);
            string cachePath = Path.Combine(cacheDir, hash);

            if (File.Exists(cachePath))
            {
                try
                {
                    await using var fs = File.OpenRead(cachePath);
                    return Bitmap.DecodeToWidth(fs, 96);
                }
                catch { /* disk cache bozuksa aşağıda tekrar indirilecek */ }
            }

            try
            {
                EnsureLogoHttpClient();
                var bytes = await _logoHttpClient!.GetByteArrayAsync(logoUrl);
                await File.WriteAllBytesAsync(cachePath, bytes);
                using var ms = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(ms, 96);
            }
            catch { return null; }
        }

        // ─────────────────────────────────────────────────────────────
        // TMDb poster cache – boyut korumalı ekleme (thread‑safe)
        // ─────────────────────────────────────────────────────────────
        private static void SetPosterCache(string key, Bitmap? bitmap)
        {
            lock (_posterCacheLock)
            {
                if (_tmdbPosterCache.ContainsKey(key))
                {
                    _tmdbPosterCache[key] = bitmap;
                    return;
                }

                while (_posterCacheOrder.Count >= MAX_POSTER_CACHE && _posterCacheOrder.Count > 0)
                {
                    string oldest = _posterCacheOrder.Dequeue(); // O(1)
                    _tmdbPosterCache.Remove(oldest);
                }

                _tmdbPosterCache[key] = bitmap;
                _posterCacheOrder.Enqueue(key);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // TMDb backdrop (arka plan) cache – boyut korumalı ekleme
        // ─────────────────────────────────────────────────────────────
        private static void SetBackdropCache(string key, Bitmap? bitmap)
        {
            lock (_posterCacheLock)
            {
                if (_tmdbBackdropCache.ContainsKey(key))
                {
                    _tmdbBackdropCache[key] = bitmap;
                    return;
                }

                while (_backdropCacheOrder.Count >= MAX_BACKDROP_CACHE && _backdropCacheOrder.Count > 0)
                {
                    string oldest = _backdropCacheOrder.Dequeue();
                    _tmdbBackdropCache.Remove(oldest);
                }

                _tmdbBackdropCache[key] = bitmap;
                _backdropCacheOrder.Enqueue(key);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // TMDb detay (JSON) cache – boyut korumalı ekleme (thread‑safe)
        //
        // Logo/poster cache'lerinin aksine bu cache hiçbir boyut sınırı
        // olmadan büyüyordu — uzun bir oturumda kullanıcı yüzlerce farklı
        // film/dizi detayı açtığında (her açılış için genres/cast/crew/
        // overview gibi alanlar içeren ayrıştırılmış JSON belgesi) bellek
        // sürekli artar, uygulama yeniden başlatılana kadar serbest
        // bırakılmazdı. Aynı LRU-benzeri tahliye deseni burada da uygulanır.
        // ─────────────────────────────────────────────────────────────
        private static void SetTmdbCache(string key, JsonElement? value)
        {
            lock (_posterCacheLock) // _tmdbCache de aynı kilit altında yönetilir
            {
                if (_tmdbCache.ContainsKey(key))
                {
                    _tmdbCache[key] = value;
                    return;
                }

                while (_tmdbCacheOrder.Count >= MAX_TMDB_CACHE && _tmdbCacheOrder.Count > 0)
                {
                    string oldest = _tmdbCacheOrder.Dequeue(); // O(1)
                    _tmdbCache.Remove(oldest);
                }

                _tmdbCache[key] = value;
                _tmdbCacheOrder.Enqueue(key);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Dosya yolları
        // ─────────────────────────────────────────────────────────────

        // Uygulama veri dizini — ilk erişimde oluşturulur, sonraki
        // çağrılarda Directory.Exists maliyeti olmadan döner.
        private static readonly string _appDataDir = InitAppDataDir();
        private static string InitAppDataDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GlyphTV");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        private string AppDataDir() => _appDataDir;

        private static readonly string _sourcesPath  = Path.Combine(_appDataDir, "sources.json");
        private static readonly string _settingsPath = Path.Combine(_appDataDir, "settings.json");
        private static readonly string _historyPath  = Path.Combine(_appDataDir, "history.json");
        private static readonly string _tmdbPopularCachePath = Path.Combine(_appDataDir, "tmdb_popular_cache.json");

        private string GetSourcesPath()  => _sourcesPath;
        private string GetSettingsPath() => _settingsPath;
        private string GetHistoryPath()  => _historyPath;
        private static string GetTmdbPopularCachePath() => _tmdbPopularCachePath;
        private string GetChannelsPath(string sourceId) => Path.Combine(_appDataDir, $"channels_{sourceId}.json");
        private string GetCategoriesPath(string sourceId) => Path.Combine(_appDataDir, $"categories_{sourceId}.json");
        private string GetSeriesDetailsDiskPath(string sourceId) => Path.Combine(_appDataDir, $"series_details_{sourceId}.json");

        private static readonly string _tmdbPosterDir = InitTmdbPosterDir();
        private static string InitTmdbPosterDir()
        {
            string dir = Path.Combine(_appDataDir, "tmdb_posters");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        private static readonly string _tmdbBackdropDir = InitTmdbBackdropDir();
        private static string InitTmdbBackdropDir()
        {
            string dir = Path.Combine(_appDataDir, "tmdb_backdrops");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        private static readonly string _tmdbMetaDir = InitTmdbMetaDir();
        private static string InitTmdbMetaDir()
        {
            string dir = Path.Combine(_appDataDir, "tmdb_meta");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        private static readonly string _logoCacheDir = InitLogoCacheDir();
        private static string InitLogoCacheDir()
        {
            string dir = Path.Combine(_appDataDir, "logos");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        private string GetTmdbPosterDir() => _tmdbPosterDir;

        private string GetPosterDiskPath(string name) =>
            Path.Combine(_tmdbPosterDir, GetUrlHash(name) + ".jpg");

        private string GetBackdropDiskPath(string key) =>
            Path.Combine(_tmdbBackdropDir, GetUrlHash(key) + ".jpg");

        private string GetTmdbMetaDiskPath(string key) =>
            Path.Combine(_tmdbMetaDir, GetUrlHash(key) + ".json");

        private string GetLogoCacheDir() => _logoCacheDir;

        private string GetLogoDiskPath(string logoUrl) =>
            Path.Combine(_logoCacheDir, GetUrlHash(logoUrl));

        // ─────────────────────────────────────────────────────────────
        // MD5 hash
        // ─────────────────────────────────────────────────────────────
        private static string GetUrlHash(string input)
        {
            // MD5.HashData: her çağrıda new MD5() oluşturmak yerine
            // tek statik çağrı — .NET 5+ destekli, allocation yok.
            // ToLowerInvariant: kültür-bağımsız, ToLower()'dan daha güvenli.
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        // ─────────────────────────────────────────────────────────────
        // Sunucu sertifika doğrulaması (IPTV / TMDb)
        //
        // NOT: Çoğu IPTV sağlayıcısı self-signed veya hatalı zincirli
        // sertifika kullanır; bu yüzden sertifika hatalarını tolere
        // ediyoruz (önceki davranışla aynı). Bu metodu tek bir yere
        // toplamanın amacı: ileride doğrulamayı sıkılaştırmak istenirse
        // (örn. sadece zincir hatalarını tolere edip hostname uyuşmazlığını
        // reddetmek gibi) tek bir noktadan değiştirilebilmesidir.
        // ─────────────────────────────────────────────────────────────
        private static bool AcceptServerCertificate(
            HttpRequestMessage request,
            X509Certificate2? certificate,
            X509Chain? chain,
            SslPolicyErrors sslErrors) => true;

        // ─────────────────────────────────────────────────────────────
        // HTTP istemcileri
        // ─────────────────────────────────────────────────────────────
        private static void EnsureLogoHttpClient()
        {
            if (_logoHttpClient != null) return;
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = AcceptServerCertificate };
            _logoHttpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            _logoHttpClient.DefaultRequestHeaders.Add("User-Agent", "VLC/3.0.20 LibVLC/3.0.20");
        }

        // TMDb resmi, geçerli sertifikalı bir servistir. IPTV kaynaklarının
        // (genelde self-signed/hatalı zincirli sertifika kullanan) aksine
        // burada sertifika doğrulamasını gevşetmenin hiçbir faydası yok,
        // sadece gereksiz bir MITM yüzeyi açar — bu yüzden varsayılan
        // (standart) doğrulama davranışı kullanılır.
        private static void EnsureTmdbHttpClient()
        {
            if (_tmdbHttpClient != null) return;
            _tmdbHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _tmdbHttpClient.DefaultRequestHeaders.Add("User-Agent", "GlyphTV/1.2.1");
        }

        // M3U/Xtream playlist indirme istemcisi — kaynak ekleme/yenileme
        // sırasında tekrar tekrar yeni HttpClient/handler oluşturmak yerine
        // tek seferlik paylaşılan istemci kullanılır.
        private static void EnsureDownloadHttpClient()
        {
            if (_downloadHttpClient != null) return;
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = AcceptServerCertificate,
                AllowAutoRedirect        = true,
                MaxAutomaticRedirections = 10
            };
            _downloadHttpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
            _downloadHttpClient.DefaultRequestHeaders.Add("User-Agent", "VLC/3.0.20 LibVLC/3.0.20");
        }

        // ─────────────────────────────────────────────────────────────
        // Ayarlar
        // ─────────────────────────────────────────────────────────────
        private void LoadAppSettings()
        {
            try
            {
                string path = GetSettingsPath();
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                    if (loaded != null) _appSettings = loaded;
                }
            }
            catch (Exception ex) { LogError("LoadAppSettings", ex); }
        }

        private void SaveAppSettings()
        {
            try { File.WriteAllText(GetSettingsPath(), JsonSerializer.Serialize(_appSettings, JsonOptions)); }
            catch (Exception ex) { LogError("SaveAppSettings", ex); }
        }

        // ─────────────────────────────────────────────────────────────
        // İzleme geçmişi
        // ─────────────────────────────────────────────────────────────
        private void LoadWatchHistory()
        {
            try
            {
                string path = GetHistoryPath();
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<List<WatchHistory>>(File.ReadAllText(path), JsonOptions);
                    if (loaded != null)
                    {
                        bool needsMigration = false;
                        foreach (var h in loaded)
                        {
                            if (!string.IsNullOrEmpty(h.UrlEncrypted))
                            {
                                // Yeni format — şifreli URL'yi çöz
                                h.Url = UnprotectString(h.UrlEncrypted);
                            }
                            else if (!string.IsNullOrEmpty(h.LegacyUrl))
                            {
                                // Eski format — düz metin URL okundu, migration gerekli
                                h.Url = h.LegacyUrl;
                                needsMigration = true;
                            }

                            // Legacy alanı her durumda temizle ki bir daha
                            // diske düz metin olarak yazılmasın.
                            h.LegacyUrl = null;
                        }

                        _watchHistory = loaded;

                        // Migration: eski düz metin geçmişi hemen şifreli
                        // formatla üzerine yaz.
                        if (needsMigration) SaveWatchHistory();
                    }
                }
            }
            catch (Exception ex) { LogError("LoadWatchHistory", ex); }
            _watchHistoryByUrlCache = null;
        }

        private void SaveWatchHistory()
        {
            try
            {
                // Diske yazmadan önce URL'yi DPAPI ile şifrele; düz metin
                // Url alanı [JsonIgnore] olduğu için zaten JSON'a yazılmaz,
                // ama UrlEncrypted'ı burada güncel tutmak gerekir.
                foreach (var h in _watchHistory)
                    h.UrlEncrypted = ProtectString(h.Url);

                File.WriteAllText(GetHistoryPath(), JsonSerializer.Serialize(_watchHistory, JsonOptions));
            }
            catch (Exception ex) { LogError("SaveWatchHistory", ex); }
        }

        private Dictionary<string, WatchHistory> GetWatchHistoryByUrlCache()
        {
            if (_watchHistoryByUrlCache == null)
            {
                _watchHistoryByUrlCache = _watchHistory
                    .Where(h => !string.IsNullOrEmpty(h.Url))
                    .GroupBy(h => h.Url)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.LastWatched).First());
            }
            return _watchHistoryByUrlCache;
        }

        private void UpsertWatchHistory(Channel channel, long position, long duration)
        {
            if (position < 5000) return;

            if (duration > 0 && (double)position / duration > 0.95)
            {
                _watchHistory.RemoveAll(h => h.Url == channel.Url);
                SaveWatchHistory();
                return;
            }

            var existing = _watchHistory.FirstOrDefault(h => h.Url == channel.Url);
            if (existing != null)
            {
                existing.Position    = position;
                existing.Duration    = duration;
                existing.LastWatched = DateTime.Now;
            }
            else
            {
                _watchHistory.Add(new WatchHistory
                {
                    Url           = channel.Url,
                    Name          = channel.Name,
                    Group         = channel.Group,
                    Type          = channel.Type,
                    Position      = position,
                    Duration      = duration,
                    LastWatched   = DateTime.Now,
                    ShowName      = channel.ShowName,
                    Season        = channel.Season,
                    EpisodeNumber = channel.EpisodeNumber
                });
            }

            if (_watchHistory.Count > 100)
                _watchHistory = _watchHistory.OrderByDescending(h => h.LastWatched).Take(100).ToList();

            _watchHistoryByUrlCache = null;
            SaveWatchHistory();
        }

        // ─────────────────────────────────────────────────────────────
        // Logo yükleme (M3U tvg-logo'ları)
        // Paralel yükleme: en fazla 4 aynı anda, disk cache destekli
        // Cache boyutu MAX_LOGO_CACHE ile sınırlanmış
        // ─────────────────────────────────────────────────────────────
        private async Task LoadLogosForChannelsAsync(IEnumerable<Channel> channels)
        {
            var list = channels.Where(c => !string.IsNullOrEmpty(c.LogoUrl) && c.LogoBitmap == null)
                .Where(c =>
                {
                    Bitmap? dummy;
                    lock (_posterCacheLock)
                    {
                        if (_tmdbPosterCache.TryGetValue(c.Name,    out dummy) && dummy != null) return false;
                        if (_tmdbPosterCache.TryGetValue(c.ShowName ?? "", out dummy) && dummy != null) return false;
                    }
                    return true;
                }).ToList();

            if (list.Count == 0) return;

            EnsureLogoHttpClient();
            string cacheDir = GetLogoCacheDir();

            var semaphore = new SemaphoreSlim(6, 6);
            try
            {
            var tasks = list.Select(async ch =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // Thread‑safe cache okuması
                    Bitmap? cached = null;
                    lock (_logoCacheLock)
                    {
                        _logoCache.TryGetValue(ch.LogoUrl, out cached);
                    }
                    if (cached != null)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => ch.LogoBitmap = cached);
                        return;
                    }

                    // DÜZELTME (crash fix): Aynı LogoUrl'i paylaşan farklı
                    // kanallar artık GetOrLoadLogoBitmap üzerinden tekilleştirilir
                    // — indirme/decode sadece bir kez yapılır, tüm kanallar aynı
                    // Bitmap örneğini paylaşır. Bu sayede aynı logo için ikinci
                    // bir SetLogoCache çağrısı ekranda gösterilmekte olan bitmap'i
                    // asla ezmez/dispose etmez.
                    var bitmap = await GetOrLoadLogoBitmap(ch.LogoUrl, cacheDir);

                    if (bitmap != null)
                        await Dispatcher.UIThread.InvokeAsync(() => ch.LogoBitmap = bitmap);
                }
                catch { }
                finally { semaphore.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);
            }
            finally { semaphore.Dispose(); }
        }
    }
}