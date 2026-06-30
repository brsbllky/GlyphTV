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
        private const int MAX_LOGO_CACHE   = 300;
        private const int MAX_POSTER_CACHE = 150;
        private const int MAX_TMDB_CACHE   = 200;

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
        private static readonly Queue<string> _logoCacheOrder   = new Queue<string>();
        private static readonly Queue<string> _posterCacheOrder = new Queue<string>();
        private static readonly Queue<string> _tmdbCacheOrder   = new Queue<string>();

        // ─────────────────────────────────────────────────────────────
        // Logo cache – boyut korumalı ekleme (thread‑safe)
        // ─────────────────────────────────────────────────────────────
        private static void SetLogoCache(string key, Bitmap? bitmap)
        {
            lock (_logoCacheLock)
            {
                if (_logoCache.TryGetValue(key, out var existing))
                {
                    // Aynı LogoUrl için eşzamanlı iki indirme tamamlanıp ikisi de
                    // SetLogoCache çağırabilir (çok kanalın aynı logoyu paylaştığı
                    // playlist'lerde yaygın). Üzerine yazmadan önce eski bitmap'i
                    // dispose et, aksi halde referanssız kalan native handle sızar.
                    if (existing != null && !ReferenceEquals(existing, bitmap))
                    {
                        try { existing.Dispose(); } catch { }
                    }
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

        private string GetSourcesPath()  => _sourcesPath;
        private string GetSettingsPath() => _settingsPath;
        private string GetHistoryPath()  => _historyPath;
        private string GetChannelsPath(string sourceId) => Path.Combine(_appDataDir, $"channels_{sourceId}.json");

        private static readonly string _tmdbPosterDir = InitTmdbPosterDir();
        private static string InitTmdbPosterDir()
        {
            string dir = Path.Combine(_appDataDir, "tmdb_posters");
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

        private string GetLogoCacheDir() => _logoCacheDir;

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
        private void EnsureLogoHttpClient()
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
                    if (loaded != null) _watchHistory = loaded;
                }
            }
            catch (Exception ex) { LogError("LoadWatchHistory", ex); }
            _watchHistoryByUrlCache = null;
        }

        private void SaveWatchHistory()
        {
            try { File.WriteAllText(GetHistoryPath(), JsonSerializer.Serialize(_watchHistory, JsonOptions)); }
            catch (Exception ex) { LogError("SaveWatchHistory", ex); }
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

                    string hash      = GetUrlHash(ch.LogoUrl);
                    string cachePath = Path.Combine(cacheDir, hash);
                    Bitmap? bitmap   = null;

                    if (File.Exists(cachePath))
                    {
                        try
                        {
                            await using var fs = File.OpenRead(cachePath);
                            bitmap = Bitmap.DecodeToWidth(fs, 300);
                        }
                        catch { bitmap = null; }
                    }
                    else
                    {
                        try
                        {
                            var bytes = await _logoHttpClient!.GetByteArrayAsync(ch.LogoUrl);
                            await File.WriteAllBytesAsync(cachePath, bytes);
                            using var ms = new MemoryStream(bytes);
                            bitmap = Bitmap.DecodeToWidth(ms, 300);
                        }
                        catch { bitmap = null; }
                    }

                    // Thread‑safe cache ekleme (dispose içermez)
                    SetLogoCache(ch.LogoUrl, bitmap);

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