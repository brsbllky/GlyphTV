// ============================================================
// MainWindow.Sources.cs
// Kaynak yönetimi: ekleme, silme, seçme, yenileme
// M3U parse, Xtream Code player_api.php, URL indirme, içerik türü tespiti
// ============================================================

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow
    {
        // ─────────────────────────────────────────────────────────────
        // Önceden derlenmiş Regex'ler — ParseM3u / DetermineContentType
        //
        // Her #EXTINF satırı için new Regex(...) çağrısı yerine static
        // readonly ile tek seferlik derleme yapılır. 50.000+ satırlık
        // M3U dosyalarında belirgin parse hızı artışı sağlar.
        // ─────────────────────────────────────────────────────────────
        private static readonly Regex _rxGroupTitle  = new(@"group-title=""([^""]+)""",  RegexOptions.Compiled);
        private static readonly Regex _rxTvgLogo     = new(@"tvg-logo=""([^""]*)""",     RegexOptions.Compiled);
        private static readonly Regex _rxXuiId       = new(@"xui-id=""([^""]+)""",       RegexOptions.Compiled);
        private static readonly Regex _rxVodInGroup  = new(@"\bvod\b",                   RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rx4kInGroup   = new(@"\b4k\b",                    RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxSeasonEp    = new(@"\bS\d{1,2}\s*[.\-_]?\s*E\d{1,3}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxShowInfo    = new(@"^(.+?)[\s\._\-]+S(\d{1,3})[\s\._\-]*E(\d{1,3})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxShowNameEnd = new(@"[:\-_\.]+$",                RegexOptions.Compiled);

        // ─────────────────────────────────────────────────────────────
        // Kaynaklar – kaydet / yükle
        // ─────────────────────────────────────────────────────────────
        // ─────────────────────────────────────────────────────────────
        // DÜZELTME (performans/akıcılık): Bu metod önceden dosyaya yazdıktan
        // sonra _sources koleksiyonunu Clear() edip aynı öğelerle yeniden
        // dolduruyordu — sebebi TvSource'ta IsActive değişikliğinin UI'ya
        // bildirilmemesiydi (bkz. TvSource.cs). Bu artık TvSource içinde
        // INotifyPropertyChanged ile düzgün şekilde çözüldüğü için, burada
        // koleksiyonu sıfırlamaya gerek yok; bu da Ayarlar > Kaynaklar
        // listesinin her kaynak seçme/silme/ekleme işleminde gereksiz yere
        // tamamen yeniden oluşturulmasını (görünür titreme + ekstra layout
        // maliyeti) önler.
        // ─────────────────────────────────────────────────────────────
        private void SaveSources()
        {
            try
            {
                // Diske yazmadan önce hassas alanları DPAPI ile şifrele;
                // düz metin alanlar WhenWritingDefault ile JSON'a yazılmaz.
                foreach (var s in _sources)
                {
                    s.PathOrUrlEncrypted = ProtectString(s.PathOrUrl);
                    s.UsernameEncrypted  = ProtectString(s.Username);
                    s.PasswordEncrypted  = ProtectString(s.Password);
                }
                File.WriteAllText(GetSourcesPath(), JsonSerializer.Serialize(_sources, JsonOptions));
            }
            catch (Exception ex) { LogError("SaveSources", ex); }
        }

        private void LoadSources()
        {
            try
            {
                string path = GetSourcesPath();
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<List<TvSource>>(File.ReadAllText(path), JsonOptions);
                    if (loaded != null)
                    {
                        bool needsMigration = false;
                        foreach (var s in loaded)
                        {
                            if (!string.IsNullOrEmpty(s.PathOrUrlEncrypted))
                            {
                                // Yeni format — şifreli alanları çöz
                                s.PathOrUrl = UnprotectString(s.PathOrUrlEncrypted);
                                s.Username  = UnprotectString(s.UsernameEncrypted);
                                s.Password  = UnprotectString(s.PasswordEncrypted);
                            }
                            else if (!string.IsNullOrEmpty(s.LegacyPathOrUrl) ||
                                     !string.IsNullOrEmpty(s.LegacyPassword))
                            {
                                // Eski format — düz metin okundu, migration gerekli
                                s.PathOrUrl = s.LegacyPathOrUrl ?? "";
                                s.Username  = s.LegacyUsername  ?? "";
                                s.Password  = s.LegacyPassword  ?? "";
                                needsMigration = true;
                            }

                            // Legacy alanları her durumda temizle ki bir daha
                            // diske düz metin olarak yazılmasınlar.
                            s.LegacyPathOrUrl = null;
                            s.LegacyUsername  = null;
                            s.LegacyPassword  = null;
                        }

                        _sources.Clear();
                        foreach (var s in loaded) _sources.Add(s);

                        // Migration: eski düz metin dosyayı hemen şifreli
                        // formatla üzerine yaz; kullanıcı fark etmez.
                        if (needsMigration) SaveSources();

                        var active = _sources.FirstOrDefault(s => s.IsActive);
                        if (active != null) { _ = LoadChannelsForSourceAsync(active.Id); return; }
                    }
                }
            }
            catch (Exception ex) { LogError("LoadSources", ex); }

            if (_sources.Count == 0) UpdateView();
        }

        // ─────────────────────────────────────────────────────────────
        // DPAPI yardımcı metodları
        //
        // Windows DPAPI (ProtectedData), veriyi mevcut kullanıcı hesabına
        // bağlı bir anahtarla şifreler. Aynı bilgisayar + aynı Windows
        // kullanıcısı dışında çözülemez. sources.json ve channels_*.json
        // dosyaları kopyalanıp başka bir ortamda açılsa bile içerik görünmez.
        // ─────────────────────────────────────────────────────────────
        private static string ProtectString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            try
            {
                var bytes     = Encoding.UTF8.GetBytes(plainText);
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
            }
            catch (Exception ex) { LogError("ProtectString", ex); return ""; }
        }

        private static string UnprotectString(string encryptedBase64)
        {
            if (string.IsNullOrEmpty(encryptedBase64)) return "";
            try
            {
                var encrypted = Convert.FromBase64String(encryptedBase64);
                var bytes     = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) { LogError("UnprotectString", ex); return ""; }
        }

        // ─────────────────────────────────────────────────────────────
        // Kanallar – kaydet / yükle
        // ─────────────────────────────────────────────────────────────
        // ─────────────────────────────────────────────────────────────
        // Kanal kayıtları için debounce + senkronizasyon
        //
        // Favori/gizle gibi aksiyonlar art arda hızlıca tetiklenirse
        // (örn. bir dizide art arda favori tıklamak) eski kodda her çağrı
        // ayrı bir Task.Run ile aynı dosyaya eşzamanlı yazabiliyordu —
        // bu, sıralaması garanti olmayan ve teorik olarak yarım yazılmış
        // dosya riski taşıyan bir race condition'dı. Burada her sourceId
        // için: (a) sadece en son snapshot saklanır, (b) kısa bir debounce
        // süresi sonunda tek seferde yazılır, (c) aynı kaynağa eşzamanlı
        // yazma engellenir.
        // ─────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, CancellationTokenSource> _saveDebounceTokens = new();
        private static readonly Dictionary<string, (List<Channel> Snapshot, string Path)> _pendingChannelSaves = new();
        private static readonly object _saveDebounceLock = new object();

        // ─────────────────────────────────────────────────────────────
        // KALICI HIZ İYİLEŞTİRMESİ (isteğe bağlı, güvenlik ödünleşmesi var):
        // Bir kaynak bu oturumda bir kez yüklenip URL'leri çözüldükten sonra,
        // aynı kaynağa tekrar geçişte dosya okuma + JSON parse + DPAPI şifre
        // çözme adımlarının TAMAMI atlanır — çözülmüş liste burada saklanır.
        // Favori/gizle gibi değişiklikler aynı Channel nesne referanslarını
        // mutasyona uğrattığı için (SaveChannelsForSource _allChannels'ı
        // KOPYALAR ama Channel nesneleri paylaşılır) cache otomatik güncel
        // kalır; ekstra senkronizasyon sadece kaynak YENİLENDİĞİNDE,
        // SİLİNDİĞİNDE veya YENİ EKLENDİĞİNDE gerekir (bkz. ilgili metodlar).
        //
        // ÖDÜNLEŞME: Önceden sadece o an AKTİF olan kaynağın çözülmüş
        // URL'leri bellekteydi. Artık bu oturumda ziyaret edilen TÜM
        // kaynakların çözülmüş URL'leri aynı anda process belleğinde kalır
        // (diske asla yazılmaz, sadece RAM). Kullanıcı bunu bilerek talep
        // etti; kabul edilemez bulunursa bu sözlük ve aşağıdaki 3 senkron
        // noktası kaldırılıp LoadChannelsForSourceAsync eski (her zaman
        // diskten paralel-decrypt) haline döndürülebilir.
        // ─────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, List<Channel>> _decryptedChannelsCache = new();

        private void SaveChannelsForSource(string sourceId)
        {
            var snapshot = _allChannels.ToList();
            var path     = GetChannelsPath(sourceId);

            CancellationTokenSource cts;
            lock (_saveDebounceLock)
            {
                if (_saveDebounceTokens.TryGetValue(sourceId, out var existing))
                {
                    try { existing.Cancel(); } catch { }
                }

                cts = new CancellationTokenSource();
                _saveDebounceTokens[sourceId]   = cts;
                _pendingChannelSaves[sourceId]  = (snapshot, path);
            }

            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    // 400ms içinde gelen art arda kayıt isteklerini birleştir;
                    // sadece en sonuncusu diske yazılır.
                    await Task.Delay(400, token);
                    WritePendingChannelSave(sourceId);
                }
                catch (TaskCanceledException) { /* yeni bir kayıt isteği geldi, bu işlem atlandı */ }
                catch { }
            }, token);
        }

        private static void WritePendingChannelSave(string sourceId)
        {
            (List<Channel> Snapshot, string Path)? entry = null;
            lock (_saveDebounceLock)
            {
                if (_pendingChannelSaves.TryGetValue(sourceId, out var pending))
                {
                    entry = pending;
                    _pendingChannelSaves.Remove(sourceId);
                    _saveDebounceTokens.Remove(sourceId);
                }
            }
            if (entry == null) return;

            try
            {
                foreach (var ch in entry.Value.Snapshot)
                    ch.UrlEncrypted = ProtectString(ch.Url);
                File.WriteAllText(entry.Value.Path, JsonSerializer.Serialize(entry.Value.Snapshot, JsonOptions));
            }
            catch (Exception ex) { LogError($"WritePendingChannelSave({sourceId})", ex); }
        }

        /// <summary>
        /// Uygulama kapanırken bekleyen (debounce edilmiş, henüz diske
        /// yazılmamış) tüm kanal kayıtlarını senkron olarak diske yazar.
        /// MainWindow.OnClosed içinden çağrılır; aksi halde son
        /// favori/gizle değişiklikleri kaybolabilir.
        /// </summary>
        internal static void FlushPendingChannelSaves()
        {
            List<string> sourceIds;
            lock (_saveDebounceLock)
                sourceIds = _pendingChannelSaves.Keys.ToList();

            foreach (var id in sourceIds)
            {
                lock (_saveDebounceLock)
                {
                    if (_saveDebounceTokens.TryGetValue(id, out var cts))
                    {
                        try { cts.Cancel(); } catch { }
                    }
                }
                WritePendingChannelSave(id);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // KALICI DÜZELTME (donma / "Yanıt Vermiyor"):
        //
        // Bu metod önceden tamamen SENKRON çalışıyordu ve UI thread'inde
        // şunları yapıyordu: File.ReadAllText (disk I/O), JsonSerializer.
        // Deserialize (büyük kaynaklarda binlerce kanal), ve — en pahalısı —
        // HER KANAL İÇİN UnprotectString çağrısı, yani her kanal başına bir
        // DPAPI (CryptUnprotectData) syscall'ı. Birkaç bin kanallı bir
        // kaynakta bu toplam süre saniyeler mertebesine çıkabiliyor ve bu
        // süre boyunca UI thread tamamen bloke olduğu için Windows pencereyi
        // "Yanıt Vermiyor" olarak işaretliyordu.
        //
        // Artık dosya okuma + JSON parse + DPAPI şifre çözme + ParseShowInfo
        // tamamı Task.Run içinde arka plan thread'inde yapılıyor; UI
        // thread'i sadece hazır sonucu _allChannels'a atayıp UpdateView()
        // çağırıyor (bunlar zaten ucuz, senkron kalabilir).
        // ─────────────────────────────────────────────────────────────
        private async Task LoadChannelsForSourceAsync(string sourceId)
        {
            // Bu oturumda daha önce yüklenmiş ve o zamandan beri yenilenmemiş/
            // silinmemiş bir kaynağa dönüş: disk + parse + decrypt tamamen
            // atlanır, geçiş anında gerçekleşir.
            if (_decryptedChannelsCache.TryGetValue(sourceId, out var cachedChannels))
            {
                _allChannels = cachedChannels;
                _contentCache.Clear();
                _seriesCardCache.Clear();
                _seriesSelections.Clear();
                UpdateView();
                return;
            }

            List<Channel> loaded = new();
            string? migrationPath = null;

            await Task.Run(() =>
            {
                try
                {
                    string path = GetChannelsPath(sourceId);
                    if (!File.Exists(path)) return;

                    var list = JsonSerializer.Deserialize<List<Channel>>(File.ReadAllText(path), JsonOptions);
                    if (list == null) return;

                    // ─────────────────────────────────────────────────
                    // HIZ İYİLEŞTİRMESİ: Her kanal için UnprotectString
                    // (DPAPI/CryptUnprotectData) çağrısı bağımsız bir
                    // işlemdir — bir kanalın şifre çözümü diğerini
                    // etkilemez. Önceden bu döngü tek thread'de sırayla
                    // çalışıyordu; binlerce kanallı kaynaklarda bu, kaynak
                    // değiştirmenin "eskisi kadar hızlı hissettirmemesinin"
                    // asıl sebebiydi (artık UI'yı bloklamıyor ama toplam
                    // süre aynıydı). Parallel.ForEach ile çok çekirdekli
                    // makinelerde bu adım birkaç kat hızlanır; güvenlik
                    // modelinde hiçbir değişiklik yok — hâlâ aynı DPAPI
                    // çağrısı, hâlâ sadece bellekte.
                    // ─────────────────────────────────────────────────
                    bool needsMigration = false;
                    var migrationLock = new object();

                    System.Threading.Tasks.Parallel.ForEach(list, ch =>
                    {
                        if (!string.IsNullOrEmpty(ch.UrlEncrypted))
                        {
                            // Yeni format — şifreli URL'yi çöz
                            ch.Url = UnprotectString(ch.UrlEncrypted);
                        }
                        else if (!string.IsNullOrEmpty(ch.LegacyUrl))
                        {
                            // Eski format — düz metin URL okundu
                            ch.Url = ch.LegacyUrl;
                            lock (migrationLock) { needsMigration = true; }
                        }

                        // Legacy alanı her durumda temizle ki bir daha
                        // diske düz metin olarak yazılmasın.
                        ch.LegacyUrl = null;
                    });

                    // ParseShowInfo (regex tabanlı) da bağımsız bir işlem;
                    // aynı sebeple paralelleştirildi.
                    System.Threading.Tasks.Parallel.ForEach(
                        list.Where(c => c.Type == "Dizi" && string.IsNullOrEmpty(c.ShowName)),
                        ch =>
                        {
                            var (showName, season, episode) = ParseShowInfo(ch.Name);
                            ch.ShowName      = showName;
                            ch.Season        = season;
                            ch.EpisodeNumber = episode;
                        });

                    loaded = list;
                    if (needsMigration) migrationPath = path;
                }
                catch (Exception ex) { LogError($"LoadChannelsForSource({sourceId})", ex); }
            });

            // Buradan sonrası hafif işlemler — UI thread'inde kalabilir.
            _allChannels = loaded;
            if (loaded.Count > 0) _decryptedChannelsCache[sourceId] = loaded;

            // Migration: eski düz metin kanalları arka planda şifrele
            if (migrationPath != null)
            {
                var snapshot = _allChannels;
                var savePath = migrationPath;
                _ = Task.Run(() =>
                {
                    try
                    {
                        foreach (var ch in snapshot)
                            ch.UrlEncrypted = ProtectString(ch.Url);
                        File.WriteAllText(savePath, JsonSerializer.Serialize(snapshot, JsonOptions));
                    }
                    catch (Exception ex) { LogError("ChannelMigration", ex); }
                });
            }

            _contentCache.Clear();
            _seriesCardCache.Clear();
            // Önceki kaynaktan kalan sezon/bölüm seçim hafızası farklı bir
            // kaynakta aynı isimli bir diziye (örn. "Breaking Bad") sızıp
            // yanlış bölüm seçimine yol açabiliyordu — kaynak değişiminde temizle.
            _seriesSelections.Clear();

            UpdateView();
        }

        // ─────────────────────────────────────────────────────────────
        // Kaynak işlemleri (seç / sil / yenile)
        // ─────────────────────────────────────────────────────────────
        private async void SelectSource_Click(object? sender, RoutedEventArgs e)
        {
            // KALICI DÜZELTME: kaynak yükleme artık async; bu bayrak aynı
            // kaynağa hızlı art arda tıklama veya bir geçiş sürerken başka
            // bir geçişin başlayıp yarış durumu yaratmasını engeller.
            if (_isSwitchingSource) return;
            if (sender is not Button btn || btn.Tag is not TvSource source) return;

            _isSwitchingSource = true;
            try
            {
                foreach (var s in _sources) s.IsActive = false;
                source.IsActive = true;
                SaveSources();
                ShowToast($"'{source.Name}' yükleniyor...");
                await LoadChannelsForSourceAsync(source.Id);
                ShowToast($"'{source.Name}' kaynağı aktifleştirildi.");
            }
            finally { _isSwitchingSource = false; }
        }

        private async void DeleteSource_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not TvSource source) return;
            _sources.Remove(source);
            try { File.Delete(GetChannelsPath(source.Id)); } catch { }

            // Silinen kaynağın cache'lenmiş çözülmüş kanal listesi artık
            // anlamsız — temizlenmezse (aynı Id yeniden kullanılmaz zaten
            // ama hijyen açısından) bellekte gereksiz yer kaplar.
            _decryptedChannelsCache.Remove(source.Id);

            if (source.IsActive && _sources.Count > 0)
            {
                _sources[0].IsActive = true;
                await LoadChannelsForSourceAsync(_sources[0].Id);
            }
            else if (_sources.Count == 0)
            {
                _allChannels.Clear();
                UpdateView();
            }
            SaveSources();
            ShowToast("Kaynak silindi.");
        }

        private async void RefreshSource_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not TvSource source) return;
            await RefreshSourceInternal(source);
        }

        // Aynı kaynağa eşzamanlı iki yenileme isteği (örn. çift tıklama veya
        // otomatik yenileme ile manuel yenilemenin çakışması) birbirini
        // ezebiliyordu — hangi isteğin önce/sonra tamamlandığı garanti
        // olmadığından favori/gizli durumları kaybolabiliyordu. Bu küme,
        // bir kaynak için yenileme sürerken yeni bir yenileme isteğini
        // engeller.
        private static readonly HashSet<string> _refreshingSourceIds = new();

        // ─────────────────────────────────────────────────────────────
        // Kaynağı yenile — favori/gizli durumları URL bazlı korur
        // ─────────────────────────────────────────────────────────────
        private async Task RefreshSourceInternal(TvSource source)
        {
            lock (_refreshingSourceIds)
            {
                if (!_refreshingSourceIds.Add(source.Id))
                {
                    ShowToast($"'{source.Name}' zaten yenileniyor, lütfen bekleyin...");
                    return;
                }
            }

            try
            {
                await RefreshSourceCore(source);
            }
            finally
            {
                lock (_refreshingSourceIds) _refreshingSourceIds.Remove(source.Id);
            }
        }

        private async Task RefreshSourceCore(TvSource source)
        {
            ShowToast($"'{source.Name}' yenileniyor, lütfen bekleyin...");

            var oldStates = new Dictionary<string, (bool isFavorite, bool isHidden)>();

            if (source.IsActive)
            {
                foreach (var ch in _allChannels.Where(c => c.IsFavorite || c.IsHidden))
                    oldStates[ch.Url] = (ch.IsFavorite, ch.IsHidden);
            }
            else
            {
                try
                {
                    string path = GetChannelsPath(source.Id);
                    if (File.Exists(path))
                    {
                        var loaded = JsonSerializer.Deserialize<List<Channel>>(File.ReadAllText(path), JsonOptions);
                        if (loaded != null)
                        {
                            // DÜZELTME: Url artık [JsonIgnore] olduğundan
                            // deserialize sonrası boş gelir; favori/gizli
                            // eşleştirmenin doğru çalışması için şifreli
                            // (veya eski format'ta legacy) URL'den geri
                            // çözülmesi gerekir.
                            foreach (var ch in loaded)
                            {
                                ch.Url = !string.IsNullOrEmpty(ch.UrlEncrypted)
                                    ? UnprotectString(ch.UrlEncrypted)
                                    : (ch.LegacyUrl ?? "");
                                ch.LegacyUrl = null;
                            }

                            foreach (var ch in loaded.Where(c => c.IsFavorite || c.IsHidden))
                                oldStates[ch.Url] = (ch.IsFavorite, ch.IsHidden);
                        }
                    }
                }
                catch { }
            }

            try
            {
                var newChannels = await FetchChannelsForSource(source);

                int restoredFav = 0, restoredHidden = 0;
                foreach (var ch in newChannels)
                {
                    if (!oldStates.TryGetValue(ch.Url, out var state)) continue;
                    ch.IsFavorite = state.isFavorite;
                    ch.IsHidden   = state.isHidden;
                    if (state.isFavorite) restoredFav++;
                    if (state.isHidden)   restoredHidden++;
                }

                if (source.IsActive)
                {
                    _allChannels = newChannels;

                    // KALICI HIZ İYİLEŞTİRMESİ: yenilenen kanallar farklı
                    // Channel referansları içeriyor; bu kaynağa ait eski
                    // cache girdisi artık bayat — yeni listeyle değiştirilir
                    // (aksi halde bir sonraki geçişte eski/silinmiş kanallar
                    // görünür).
                    _decryptedChannelsCache[source.Id] = newChannels;

                    // Yenileme sonrası kayıt: newChannels zaten hazır ve
                    // değişmeyecek; ToList() snapshot maliyeti olmadan
                    // direkt arka planda yazılır.
                    var channelsToSave = newChannels;
                    var savePath       = GetChannelsPath(source.Id);
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            foreach (var ch in channelsToSave)
                                ch.UrlEncrypted = ProtectString(ch.Url);
                            File.WriteAllText(savePath, JsonSerializer.Serialize(channelsToSave, JsonOptions));
                        }
                        catch (Exception ex) { LogError("RefreshSourceCore.Save", ex); }
                    });

                    // Yenilenen kanallar artık farklı Channel referansları
                    // içeriyor; önceden gezilip önbelleğe alınmış kategori/dizi
                    // listeleri eski (artık _allChannels'ta bulunmayan)
                    // referanslara işaret edebilir. Temizlenmezse kullanıcı
                    // yenileme sonrası daha önce gezdiği bir kategoriye
                    // dönünce bayat içerik görür.
                    _contentCache.Clear();
                    _seriesCardCache.Clear();
                    _seriesSelections.Clear();

                    UpdateView();
                }
                else
                {
                    // KALICI HIZ İYİLEŞTİRMESİ: bu kaynak daha önce ziyaret
                    // edilip cache'lenmiş olabilir (kullanıcı geçmişte açtı,
                    // sonra başka bir kaynağa geçti). Arka planda yenilendiği
                    // için o eski cache girdisi artık bayat — burada zaten
                    // elimizde olan taze newChannels ile güncelleniyor ki
                    // kullanıcı bu kaynağa bir sonraki geçişinde eski veri
                    // görmesin.
                    _decryptedChannelsCache[source.Id] = newChannels;

                    // DÜZELTME: Bu dal (aktif olmayan bir kaynağın arka planda
                    // yenilenmesi) önceden UrlEncrypted hiç doldurulmadan
                    // doğrudan yazıyordu — Url artık [JsonIgnore] olduğundan
                    // bu, URL'lerin tamamen kaybolmasına yol açardı. Aktif
                    // kaynak yolundaki (RefreshSourceCore.Save Task.Run bloğu)
                    // ile aynı şekilde, yazmadan önce her kanalın URL'si
                    // şifrelenir.
                    try
                    {
                        foreach (var ch in newChannels)
                            ch.UrlEncrypted = ProtectString(ch.Url);
                        File.WriteAllText(GetChannelsPath(source.Id), JsonSerializer.Serialize(newChannels, JsonOptions));
                    }
                    catch { }
                }

                ShowToast($"'{source.Name}' yenilendi: {newChannels.Count} içerik ({restoredFav} favori korundu).");
            }
            catch (HttpRequestException hre) { ShowToast($"Yenileme hatası: {hre.Message}"); }
            catch (TaskCanceledException)    { ShowToast("Yenileme zaman aşımına uğradı."); }
            catch (Exception ex)             { ShowToast($"Yenileme hatası: {ex.Message}"); }
        }

        // ─────────────────────────────────────────────────────────────
        // Kaynak türüne göre kanal listesi çek  (YENİLEME için)
        //
        // Xtream için tek M3U isteği kullanılır — orijinal hız korunur.
        // ParseXtreamApi (player_api.php, N+3 istek) yalnızca ilk kaynak
        // eklemesinde çağrılır; yenileme her zaman bu hızlı yolu kullanır.
        // ─────────────────────────────────────────────────────────────
        private async Task<List<Channel>> FetchChannelsForSource(TvSource source)
        {
            switch (source.Type)
            {
                case "M3U":
                {
                    string content = File.Exists(source.PathOrUrl)
                        ? await File.ReadAllTextAsync(source.PathOrUrl)
                        : throw new FileNotFoundException("Kaynak dosyası mevcut değil.");
                    // Büyük M3U dosyalarında parse işlemi UI thread'ini
                    // kilitlemesin diye arka plana alınıyor.
                    return await Task.Run(() => ParseM3u(content));
                }
                case "Link":
                {
                    string content = await DownloadM3uContent(source.PathOrUrl);
                    return await Task.Run(() => ParseM3u(content));
                }
                case "Xtream":
                {
                    // Xtream: get.php → tek HTTP isteği, tüm içerik (canlı + VOD + dizi)
                    // Kaynak ekleme sırasında zaten ParseXtreamApi çalışmıştır;
                    // yenileme işlevi hızlı M3U yolunu kullanır.
                    string content = await DownloadM3uContent(
                        $"{source.PathOrUrl.TrimEnd('/')}/get.php" +
                        $"?username={Uri.EscapeDataString(source.Username)}" +
                        $"&password={Uri.EscapeDataString(source.Password)}" +
                        $"&type=m3u_plus&output=ts");
                    return await Task.Run(() => ParseM3u(content));
                }
                default:
                    throw new InvalidOperationException("Bilinmeyen kaynak türü.");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Xtream Code – player_api.php entegrasyonu (ARTIK KULLANILMIYOR)
        // Canlı / VOD / Dizi içeriklerini ayrı ayrı çeker.
        //
        // NOT: Bu metot her dizi için ayrı bir get_series_info isteği attığı
        // için (N+3 istek) binlerce dizisi olan kaynaklarda 5-10 dakikaya
        // kadar süren yüklemelere yol açıyordu. Kaynak EKLEME akışı artık
        // ConfirmAddSource_Click içinde bunun yerine FetchChannelsForSource
        // (tek get.php isteği + ParseM3u — RefreshSourceCore'un kullandığı
        // hızlı yol) kullanıyor. Bu metot referans/geri dönüş amacıyla
        // dosyada bırakıldı, aktif olarak çağrılmıyor.
        // ─────────────────────────────────────────────────────────────
        private async Task<List<Channel>> ParseXtreamApi(TvSource source)
        {
            var allChannels = new List<Channel>();
            string server   = source.PathOrUrl.TrimEnd('/');
            string userEnc  = Uri.EscapeDataString(source.Username);
            string passEnc  = Uri.EscapeDataString(source.Password);
            string baseApi  = $"{server}/player_api.php?username={userEnc}&password={passEnc}";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("User-Agent", "GlyphTV/1.1.0");

            // ── 1. Canlı TV ──────────────────────────────────────────
            try
            {
                var liveCats = await GetXtreamCategoryMap(client, baseApi, "get_live_categories");
                var json     = await client.GetStringAsync($"{baseApi}&action=get_live_streams");

                using var doc = JsonDocument.Parse(json);
                foreach (var s in doc.RootElement.EnumerateArray())
                {
                    string catId   = XtreamStr(s, "category_id");
                    string catName = liveCats.TryGetValue(catId, out var cn) ? cn : "Diğer";
                    string stId    = s.TryGetProperty("stream_id", out var sid) ? sid.GetRawText() : "0";
                    string ext     = XtreamStr(s, "container_extension", "ts");

                    allChannels.Add(new Channel
                    {
                        Name    = XtreamStr(s, "name"),
                        Url     = $"{server}/{source.Username}/{source.Password}/{stId}.{ext}",
                        Group   = catName,
                        Type    = "Canlı",
                        LogoUrl = XtreamStr(s, "stream_icon")
                    });
                }
            }
            catch { /* Canlı yüklenemedi – devam et */ }

            // ── 2. Filmler (VOD) ─────────────────────────────────────
            try
            {
                var vodCats = await GetXtreamCategoryMap(client, baseApi, "get_vod_categories");
                var json    = await client.GetStringAsync($"{baseApi}&action=get_vod_streams");

                using var doc = JsonDocument.Parse(json);
                foreach (var s in doc.RootElement.EnumerateArray())
                {
                    string catId   = XtreamStr(s, "category_id");
                    string catName = vodCats.TryGetValue(catId, out var cn) ? cn : "Diğer";
                    string stId    = s.TryGetProperty("stream_id", out var sid) ? sid.GetRawText() : "0";
                    string ext     = XtreamStr(s, "container_extension", "mp4");

                    allChannels.Add(new Channel
                    {
                        Name    = XtreamStr(s, "name"),
                        Url     = $"{server}/movie/{source.Username}/{source.Password}/{stId}.{ext}",
                        Group   = catName,
                        Type    = "VOD",
                        LogoUrl = XtreamStr(s, "stream_icon")
                    });
                }
            }
            catch { /* VOD yüklenemedi – devam et */ }

            // ── 3. Diziler ───────────────────────────────────────────
            // Her dizi için ayrı bir API çağrısı gerekiyor (bölüm listesi için).
            // Paralel fakat kontrollü: max 5 eş zamanlı istek.
            try
            {
                var seriesCats = await GetXtreamCategoryMap(client, baseApi, "get_series_categories");
                var seriesJson = await client.GetStringAsync($"{baseApi}&action=get_series");

                using var seriesDoc = JsonDocument.Parse(seriesJson);
                var seriesList = seriesDoc.RootElement.EnumerateArray().ToList();

                int total     = seriesList.Count;
                int done      = 0;
                int lastToast = 0;

                Dispatcher.UIThread.Post(() =>
                    ShowToast($"Diziler yükleniyor: {total} dizi bulundu..."));

                var semaphore = new SemaphoreSlim(5);
                // Xtream client'ı paralel requestlere hazır, ayrı HttpClient kullanmıyoruz
                // using: tasks içinde bir istisna oluşsa bile epClient kesin dispose edilir.
                using var epClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                epClient.DefaultRequestHeaders.Add("User-Agent", "GlyphTV/1.1.0");

                var tasks = seriesList.Select(async series =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        string seriesId = series.TryGetProperty("series_id", out var sid)
                            ? sid.GetRawText() : "";
                        if (string.IsNullOrEmpty(seriesId)) return;

                        string showName = XtreamStr(series, "name");
                        string catId    = XtreamStr(series, "category_id");
                        string catName  = seriesCats.TryGetValue(catId, out var cn) ? cn : "Diğer";
                        string logoUrl  = XtreamStr(series, "cover");

                        try
                        {
                            var infoJson = await epClient.GetStringAsync(
                                $"{baseApi}&action=get_series_info&series_id={seriesId}");
                            using var infoDoc = JsonDocument.Parse(infoJson);

                            if (infoDoc.RootElement.TryGetProperty("episodes", out var episodes))
                            {
                                foreach (var seasonProp in episodes.EnumerateObject())
                                {
                                    string season = $"S{seasonProp.Name.PadLeft(2, '0')}";

                                    foreach (var ep in seasonProp.Value.EnumerateArray())
                                    {
                                        string epId  = ep.TryGetProperty("id",          out var eid)  ? eid.GetRawText()  : "0";
                                        string epNum = ep.TryGetProperty("episode_num", out var eno)  ? eno.GetRawText()  : "0";
                                        string ext   = XtreamStr(ep, "container_extension", "mkv");
                                        string title = XtreamStr(ep, "title");

                                        string epName = $"{showName} {season}E{epNum.PadLeft(2, '0')}";
                                        if (!string.IsNullOrEmpty(title)) epName += $" - {title}";

                                        lock (allChannels)
                                        {
                                            allChannels.Add(new Channel
                                            {
                                                Name          = epName,
                                                Url           = $"{server}/series/{source.Username}/{source.Password}/{epId}.{ext}",
                                                Group         = catName,
                                                Type          = "Dizi",
                                                LogoUrl       = logoUrl,
                                                ShowName      = showName,
                                                Season        = season,
                                                EpisodeNumber = int.TryParse(epNum, out var en) ? en : 0
                                            });
                                        }
                                    }
                                }
                            }
                        }
                        catch { /* Bu dizi bölümleri alınamadı – atla */ }

                        int current = Interlocked.Increment(ref done);

                        // Her %25'te bir veya son dizide toast göster
                        int pct = current * 100 / Math.Max(1, total);
                        if (pct / 25 > lastToast / 25 || current == total)
                        {
                            lastToast = pct;
                            Dispatcher.UIThread.Post(() =>
                                ShowToast($"Diziler yükleniyor... %{pct} ({current}/{total})"));
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks);
            }
            catch { /* Dizi listesi alınamadı – devam et */ }

            return allChannels;
        }

        // ─────────────────────────────────────────────────────────────
        // Xtream kategori haritası: category_id → category_name
        // ─────────────────────────────────────────────────────────────
        private async Task<Dictionary<string, string>> GetXtreamCategoryMap(
            HttpClient client, string baseApi, string action)
        {
            var map = new Dictionary<string, string>();
            try
            {
                var json = await client.GetStringAsync($"{baseApi}&action={action}");
                using var doc = JsonDocument.Parse(json);
                foreach (var cat in doc.RootElement.EnumerateArray())
                {
                    string id   = XtreamStr(cat, "category_id");
                    string name = XtreamStr(cat, "category_name", "Diğer");
                    if (!string.IsNullOrEmpty(id)) map[id] = name;
                }
            }
            catch { }
            return map;
        }

        // ─────────────────────────────────────────────────────────────
        // Xtream JSON string okuma yardımcısı
        // category_id bazen number, bazen string gelir – ikisini de destekler
        // ─────────────────────────────────────────────────────────────
        private static string XtreamStr(JsonElement el, string key, string fallback = "")
        {
            if (!el.TryGetProperty(key, out var val)) return fallback;
            return val.ValueKind switch
            {
                JsonValueKind.String => val.GetString() ?? fallback,
                JsonValueKind.Number => val.GetRawText(),
                _                   => fallback
            };
        }

        // ─────────────────────────────────────────────────────────────
        // Uygulama sıfırlama
        // ─────────────────────────────────────────────────────────────
        private void ResetApp_Click(object? sender, RoutedEventArgs e)
        {
            _sources.Clear();
            _allChannels.Clear();
            _watchHistory.Clear();
            _contentCache.Clear();
            _seriesCardCache.Clear();
            _decryptedChannelsCache.Clear();

            string appData = AppDataDir();
            try
            {
                foreach (var f in new[] { "sources.json", "history.json" })
                {
                    var p = Path.Combine(appData, f);
                    if (File.Exists(p)) File.Delete(p);
                }
                foreach (var f in Directory.GetFiles(appData, "channels_*.json"))
                    File.Delete(f);
            }
            catch { }

            LoadSources();
            ShowToast("Uygulama sıfırlandı.");
        }

        // ─────────────────────────────────────────────────────────────
        // Kaynak ekleme modalı
        // ─────────────────────────────────────────────────────────────
        private void ShowChangeSource_Click(object? sender, RoutedEventArgs e)
        {
            SourceNameInput.Text = "";
            M3uUrlInput.Text     = "";
            XtreamUrlInput.Text  = "";
            XtreamUserInput.Text = "";
            XtreamPassInput.Text = "";
            SelectedFilePath.Text = "";
            SelectedFileName.IsVisible = false;

            SetSourceType("M3U");
            ChangeSourceOverlay.IsVisible = true;
        }

        private void CancelChangeSource_Click(object? sender, RoutedEventArgs e) =>
            ChangeSourceOverlay.IsVisible = false;

        private void SourceType_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null)
                SetSourceType(btn.Tag.ToString()!);
        }

        private void SetSourceType(string type)
        {
            _selectedSourceType = type;

            BtnTypeM3U.Foreground    = (Avalonia.Media.IBrush)this.Resources["TextSec"]!;
            BtnTypeM3U.BorderBrush   = (Avalonia.Media.IBrush)this.Resources["Border"]!;
            BtnTypeLink.Foreground   = (Avalonia.Media.IBrush)this.Resources["TextSec"]!;
            BtnTypeLink.BorderBrush  = (Avalonia.Media.IBrush)this.Resources["Border"]!;
            BtnTypeXtream.Foreground = (Avalonia.Media.IBrush)this.Resources["TextSec"]!;
            BtnTypeXtream.BorderBrush= (Avalonia.Media.IBrush)this.Resources["Border"]!;

            InputAreaM3U.IsVisible    = false;
            InputAreaXtream.IsVisible = false;

            switch (type)
            {
                case "M3U":
                    BtnTypeM3U.Foreground    = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    BtnTypeM3U.BorderBrush   = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    InputAreaM3U.IsVisible   = true;
                    M3uFilePickerPanel.IsVisible = true;
                    M3uUrlInput.IsVisible        = false;
                    break;
                case "Link":
                    BtnTypeLink.Foreground   = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    BtnTypeLink.BorderBrush  = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    InputAreaM3U.IsVisible   = true;
                    M3uFilePickerPanel.IsVisible = false;
                    M3uUrlInput.IsVisible        = true;
                    break;
                case "Xtream":
                    BtnTypeXtream.Foreground  = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    BtnTypeXtream.BorderBrush = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    InputAreaXtream.IsVisible = true;
                    break;
            }
        }

        private async void SelectM3uFile_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
            if (files.Count >= 1)
            {
                SelectedFilePath.Text      = files[0].Path.LocalPath;
                SelectedFileName.Text      = "Seçilen Dosya: " + files[0].Name;
                SelectedFileName.IsVisible = true;
            }
        }

        private async void ConfirmAddSource_Click(object? sender, RoutedEventArgs e)
        {
            string sourceName = SourceNameInput.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(sourceName)) sourceName = "Yeni Kaynak";

            var newSource = new TvSource { Name = sourceName, Type = _selectedSourceType, IsActive = true };
            ChangeSourceOverlay.IsVisible = false;
            ShowToast("Kaynak ekleniyor, lütfen bekleyin...");

            try
            {
                if (_selectedSourceType == "M3U")
                {
                    string path = SelectedFilePath.Text ?? "";
                    if (string.IsNullOrEmpty(path))  { ShowToast("Lütfen bir M3U dosyası seçin."); return; }
                    if (!File.Exists(path))           { ShowToast("Seçilen dosya bulunamadı.");     return; }
                    newSource.PathOrUrl = path;
                    await ParseAndLoadM3uAsync(await File.ReadAllTextAsync(path), newSource);
                }
                else if (_selectedSourceType == "Link")
                {
                    string url = M3uUrlInput.Text?.Trim() ?? "";
                    if (string.IsNullOrEmpty(url))    { ShowToast("Lütfen bir link girin."); return; }
                    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        url = "http://" + url;
                    newSource.PathOrUrl = url;
                    await ParseAndLoadM3uAsync(await DownloadM3uContent(url), newSource);
                }
                else if (_selectedSourceType == "Xtream")
                {
                    string server = XtreamUrlInput.Text?.Trim().TrimEnd('/') ?? "";
                    string user   = XtreamUserInput.Text?.Trim() ?? "";
                    string pass   = XtreamPassInput.Text?.Trim() ?? "";

                    if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(user))
                    { ShowToast("Sunucu URL ve kullanıcı adı zorunludur."); return; }

                    if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        server = "http://" + server;

                    newSource.PathOrUrl = server;
                    newSource.Username  = user;
                    newSource.Password  = pass;

                    // DÜZELTME: Eskiden ParseXtreamApi (her dizi için ayrı
                    // get_series_info isteği, N+3 istek) kullanılıyordu —
                    // binlerce dizisi olan kaynaklarda 5-10 dakikaya kadar
                    // sürebiliyordu. RefreshSourceCore'un kullandığı hızlı
                    // yol (tek get.php isteği + ParseM3u) burada da
                    // kullanılıyor; ilk ekleme artık yenileme kadar hızlı.
                    var channels = await FetchChannelsForSource(newSource);
                    _allChannels = channels;
                    FinishAddingSource(newSource);
                }
            }
            catch (HttpRequestException hre) { ShowToast($"Bağlantı hatası: {hre.Message}"); }
            catch (TaskCanceledException)    { ShowToast("İstek zaman aşımına uğradı."); }
            catch (UriFormatException)       { ShowToast("Geçersiz URL formatı."); }
            catch (Exception ex)             { ShowToast($"Hata: {ex.Message}"); }
        }

        // ─────────────────────────────────────────────────────────────
        // HTTP indirici (M3U playlist için)
        // ─────────────────────────────────────────────────────────────
        private async Task<string> DownloadM3uContent(string url)
        {
            EnsureDownloadHttpClient();
            return await _downloadHttpClient!.GetStringAsync(url);
        }

        // ─────────────────────────────────────────────────────────────
        // M3U parse → Channel listesi
        // ─────────────────────────────────────────────────────────────
        private async Task ParseAndLoadM3uAsync(string content, TvSource newSource)
        {
            // Büyük M3U dosyalarında parse işlemi UI thread'ini kilitlemesin
            // diye arka plana alınıyor.
            _allChannels = await Task.Run(() => ParseM3u(content));
            FinishAddingSource(newSource);
        }

        private List<Channel> ParseM3u(string content)
        {
            var lines  = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<Channel>();
            string currentName = "Bilinmeyen Kanal", currentGroup = "Diğer",
                   currentLogo = "", currentXuiId = "";

            foreach (var line in lines)
            {
                if (line.StartsWith("#EXTINF:"))
                {
                    var groupMatch = _rxGroupTitle.Match(line);
                    currentGroup = groupMatch.Success ? groupMatch.Groups[1].Value : "Diğer";

                    var logoMatch = _rxTvgLogo.Match(line);
                    currentLogo  = logoMatch.Success ? logoMatch.Groups[1].Value : "";

                    var xuiMatch  = _rxXuiId.Match(line);
                    currentXuiId = xuiMatch.Success ? xuiMatch.Groups[1].Value : "";

                    int ci = line.LastIndexOf(',');
                    if (ci != -1 && ci < line.Length - 1)
                        currentName = line[(ci + 1)..].Trim();
                }
                else if (!line.StartsWith("#"))
                {
                    string type = DetermineContentType(currentName, currentGroup, line);
                    var channel = new Channel
                    {
                        Name    = currentName,
                        Url     = line.Trim(),
                        Group   = currentGroup,
                        Type    = type,
                        LogoUrl = currentLogo,
                        XuiId   = currentXuiId
                    };

                    if (type == "Dizi")
                    {
                        var (showName, season, episode) = ParseShowInfo(currentName);
                        channel.ShowName      = showName;
                        channel.Season        = season;
                        channel.EpisodeNumber = episode;
                    }

                    result.Add(channel);
                    currentName  = "Bilinmeyen Kanal";
                    currentGroup = "Diğer";
                    currentLogo  = "";
                    currentXuiId = "";
                }
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────
        // İçerik türü tespiti — öncelik sırası:
        //   1) URL path  (/series/, /movie/, /live/)
        //   2) Grup adı  (Series/Dizi → VOD ÖNCESİ Canlı kontrol edilir)
        //   3) Kanal adı S##E## deseni  (grup yoksa yardımcı)
        //   4) URL uzantısı (.mp4 vb.)
        //   5) Varsayılan: Canlı
        //
        // Düzeltme notları:
        //   • Grup kontrolü S##E## regex'inden ÖNCE yapılıyor.
        //     → "Film S01E01" adlı bir içerik, grubu "Movies" ise artık VOD olarak sınıflanır.
        //   • "Canlı" grubu VOD grubundan ÖNCE kontrol ediliyor.
        //     → "4K Sports" gibi gruplar yanlışlıkla VOD'a düşmez.
        //   • "vod" sözcüğü \b ile sınır kontrollü aranıyor.
        //     → "avoidance" gibi yanlış eşleşmeler engellenir.
        //   • "4k" yalnızca başka hiçbir Canlı anahtar kelimesi yoksa VOD'a düşer.
        // ─────────────────────────────────────────────────────────────
        private string DetermineContentType(string channelName, string groupTitle, string url)
        {
            string lowerUrl   = url.ToLower();
            string lowerGroup = groupTitle.ToLower();

            // ── 1. URL path — en güvenilir kaynak ────────────────────
            if (lowerUrl.Contains("/series/"))                                  return "Dizi";
            if (lowerUrl.Contains("/movie/") || lowerUrl.Contains("/movies/")) return "VOD";
            if (lowerUrl.Contains("/live/"))                                    return "Canlı";

            // ── 2. Grup adı — S##E## regex'inden önce bakılmalı ──────

            // Dizi
            if (lowerGroup.Contains("series") || lowerGroup.Contains("dizi")  ||
                lowerGroup.Contains("sezon")  || lowerGroup.Contains("season"))
                return "Dizi";

            // Canlı — VOD'dan ÖNCE kontrol: "4K Sports" gibi gruplar burada yakalanır
            if (lowerGroup.Contains("live")    || lowerGroup.Contains("canlı") ||
                lowerGroup.Contains("news")    || lowerGroup.Contains("haber") ||
                lowerGroup.Contains("spor")    || lowerGroup.Contains("sport") ||
                lowerGroup.Contains("kids")    || lowerGroup.Contains("çocuk") ||
                lowerGroup.Contains("music")   || lowerGroup.Contains("müzik") ||
                lowerGroup.Contains("belgesel")|| lowerGroup.Contains("documentary"))
                return "Canlı";

            // VOD — "4k" tek başına kaldıysa (Canlı anahtar kelimesi yoktu) film grubuna alır
            if (lowerGroup.Contains("movie")  || lowerGroup.Contains("film")   ||
                lowerGroup.Contains("cinema") || lowerGroup.Contains("sinema") ||
                _rxVodInGroup.IsMatch(lowerGroup) ||
                _rx4kInGroup.IsMatch(lowerGroup))
                return "VOD";

            // ── 3. Kanal adı — grup bilgisi yetersizse yardımcı kaynak
            if (_rxSeasonEp.IsMatch(channelName))
                return "Dizi";

            // ── 4. URL uzantısı ───────────────────────────────────────
            if (lowerUrl.EndsWith(".mp4") || lowerUrl.EndsWith(".mkv") ||
                lowerUrl.EndsWith(".avi") || lowerUrl.EndsWith(".mov"))
                return "VOD";

            // ── 5. Varsayılan ─────────────────────────────────────────
            return "Canlı";
        }

        // ─────────────────────────────────────────────────────────────
        // Dizi adından show / sezon / bölüm parse
        // ─────────────────────────────────────────────────────────────
        private (string showName, string season, int episode) ParseShowInfo(string channelName)
        {
            var match = _rxShowInfo.Match(channelName);
            if (match.Success)
            {
                string showName = match.Groups[1].Value.Trim();
                showName = _rxShowNameEnd.Replace(showName, "").Trim();
                if (string.IsNullOrEmpty(showName)) showName = channelName;

                string season = "S" + match.Groups[2].Value.PadLeft(2, '0');
                int episode   = int.TryParse(match.Groups[3].Value, out var ep) ? ep : 0;
                return (showName, season, episode);
            }
            return (channelName, "Bilinmeyen Sezon", 0);
        }

        private void FinishAddingSource(TvSource newSource)
        {
            foreach (var s in _sources) s.IsActive = false;
            _sources.Add(newSource);

            // _allChannels bu noktada zaten yeni kaynağın (çözülmüş) kanal
            // listesidir — bir sonraki geçişte tekrar diskten okuyup
            // decrypt etmemek için doğrudan cache'e yazılır.
            if (_allChannels.Count > 0) _decryptedChannelsCache[newSource.Id] = _allChannels;

            SaveChannelsForSource(newSource.Id);
            SaveSources();
            UpdateView();
            ShowToast("Kaynak başarıyla eklendi.");
        }
    }
}
