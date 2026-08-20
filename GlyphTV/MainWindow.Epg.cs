// ============================================================
// MainWindow.Epg.cs
// EPG (Yayın Akışı) — tam zaman çizelgesi/grid.
//
// VERİ KAYNAĞI:
//   • Xtream kaynakları  → sunucunun standart "xmltv.php" uç noktası
//     otomatik denenir (kullanıcıdan hiçbir ek bilgi istenmez).
//   • M3U / Link kaynakları → kullanıcının kaynak eklerken girdiği
//     (opsiyonel) harici XMLTV linki (TvSource.EpgUrl) kullanılır.
//     XMLTV dosyası düz metin veya gzip (.xml.gz) olabilir, ikisi de
//     desteklenir.
//
// EŞLEŞTİRME (İKİ AŞAMALI):
//   1) Channel.TvgId (M3U/Xtream #EXTINF satırındaki tvg-id özelliğinden)
//      ile XMLTV'deki <programme channel="..."> birebir (büyük/küçük harf
//      duyarsız) karşılaştırılır.
//   2) tvg-id eksikse veya o id için hiç <programme> yoksa, XMLTV'deki
//      <channel id="X"><display-name>Ad</display-name></channel>
//      eşlemesi üzerinden kanal ADI normalize edilip karşılaştırılır
//      (bkz. NormalizeEpgName). Birçok Xtream sağlayıcısı tvg-id'yi ya
//      hiç vermez ya da sadece bazı kanallar için verir; bu ikinci
//      aşama olmadan EPG kapsamı gereğinden çok düşük kalıyordu.
//
// PERFORMANS (AÇILIŞ HIZI + KAYDIRMA):
//   Önceki sürüm TÜM kanallar için (binlerce olabilir) Button+Border+
//   TextBlock ağaçlarını PEŞİNEN oluşturuyordu — büyük kataloglarda EPG
//   panelinin açılışı gözle görülür şekilde yavaştı. Artık bu dosyadaki
//   RenderVisibleEpgRows, uygulamanın başka yerlerinde zaten kullanılan
//   sanallaştırma prensibiyle (bkz. RowGroupedCollection.cs) aynı fikri
//   elle uyguluyor: sadece o an görünür olan (+ birkaç satırlık tampon)
//   kanal satırları gerçek kontrollere dönüştürülüyor; kaydırma sırasında
//   aralığın dışına çıkanlar kaldırılıp yenileri eklenir. Toplam kanal
//   sayısı ne olursa olsun aynı anda ~30-40 satır kontrolü var olur.
//
// KAYDIRMA SENKRONU: Sol (kanal isimleri) ve sağ (program blokları) artık
// İKİ YÖNLÜ senkronize — hangisi kaydırılırsa kaydırılsın diğeri aynı
// anda takip eder (_epgSyncingScroll reentrancy koruması ile).
//
// ÖNBELLEK: İndirilen EPG appdata\epg_{sourceId}.json içinde saklanır ve
// EPG_CACHE_HOURS saat boyunca tekrar indirilmez.
//
// ARKA PLAN YÜKLEME: EPG paneli hiç açılmasa bile, aktif kaynak
// yüklendiğinde TriggerBackgroundEpgLoad (bkz. MainWindow.Sources.cs →
// LoadChannelsForSourceAsync) sessizce EPG'yi indirip Canlı TV kategori
// listesindeki "şu an oynuyor" rozetlerini (Channel.EpgNowTitle) besler.
// ============================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace GlyphTV
{
    public partial class MainWindow
    {
        // ─── Görsel sabitler ────────────────────────────────────────
        private const double EPG_PPM        = 4.0;   // piksel / dakika (1 saat = 240px)
        private const double EPG_ROW_HEIGHT = 54;
        private const double EPG_NAME_COL_WIDTH = 188;
        private const int    EPG_CACHE_HOURS = 6;
        private const int    EPG_ROW_BUFFER  = 8;     // görünür aralığın üstüne/altına eklenen tampon satır sayısı
        private const string EPG_ALL_CATEGORIES = "Tüm Kategoriler";

        // ─── EPG verisi ────────────────────────────────────────────
        private Dictionary<string, List<EpgProgram>> _epgByChannelId =
            new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<EpgProgram>> _epgByNormalizedName =
            new(StringComparer.OrdinalIgnoreCase);
        private string? _epgLoadedForSourceId = null;
        private bool _epgIsLoading = false;

        // ─── Zaman çizelgesi görünüm durumu ────────────────────────
        private DateTime _epgRangeStart = DateTime.Today;
        private List<Channel> _epgCurrentChannelRows = new();
        private double _epgTotalWidth = 0;
        private bool _epgPopulatingCombo = false;

        // ─── Sanallaştırma durumu (bkz. RenderVisibleEpgRows) ──────
        private readonly Dictionary<int, Control> _epgRenderedNames = new();
        private readonly Dictionary<int, List<Control>> _epgRenderedRowControls = new();
        private int _epgRenderedFirst = -1, _epgRenderedLast = -1;
        private bool _epgSyncingScroll = false;

        // ─── Zamanlayıcılar ─────────────────────────────────────────
        private DispatcherTimer? _epgNowLineTimer;   // sadece panel açıkken: kırmızı "şu an" çizgisi
        private DispatcherTimer? _epgNowInfoTimer;   // panel kapalıyken de çalışır: kategori listesi rozetleri
        private DispatcherTimer? _epgAutoRefreshTimer; // panel kapalıyken de çalışır: periyodik önbellek tazeleme (madde 1)

        // ─── Seçim ──────────────────────────────────────────────────
        private Channel? _epgSelectedChannel;
        private EpgProgram? _epgSelectedProgram;
        private Avalonia.Controls.Shapes.Rectangle? _epgNowLineRow;
        private Avalonia.Controls.Shapes.Rectangle? _epgNowLineHeader;

        /// <summary>
        /// Program bloklarının Border.Tag'inde saklanan basit taşıyıcı.
        /// (Bir C# value-tuple değil — Tag'in derleme zamanı türü object
        /// olduğundan value-tuple'ı geri okurken C#'ın bunu bir tür deseni
        /// değil pozisyonel/deconstruction deseni sanıp object üzerinde
        /// var olmayan bir Deconstruct araması derleme hatası veriyordu.)
        /// </summary>
        private sealed class EpgBlockTag
        {
            public Channel Channel = null!;
            public EpgProgram Program = null!;
        }

        // ═════════════════════════════════════════════════════════════
        // Aç / kapat
        // ═════════════════════════════════════════════════════════════
        // ─────────────────────────────────────────────────────────────
        // KATMAN DÜZELTMESİ (EPG/Yayın Akışı modalı üzerine oynatıcı
        // katmanının çıkması — hem VLC hem mpv):
        //
        // EPG modalı (EpgOverlay) MainWindow'un İÇİNDE Avalonia tarafından
        // çizilen bir katmandır. Oynatıcı ise İKİ ayrı native katmandan
        // oluşur: (1) video yüzeyi (VLC VideoView / mpv MpvVideoHost — bir
        // Win32 alt HWND) ve (2) PlayerOverlayWindow (kontrollerin taşındığı
        // AYRI, şeffaf, Topmost bir pencere). Windows'ta native pencereler
        // Avalonia'nın kendi çiziminin (dolayısıyla EpgOverlay'ın) HER ZAMAN
        // önünde görünür — ZIndex ile değiştirilemez ("airspace" kısıtı).
        //
        // Önceden EPG açılırken SADECE video yüzeyi gizleniyordu
        // (SetVideoSurfaceVisible(false)); PlayerOverlayWindow hiç
        // gizlenmiyordu. Bu yüzden oynatıcı açıkken (bir içerik oynatılıp
        // kapatıldıktan sonra overlay penceresi hâlâ görünür durumdayken)
        // EPG modalı açıldığında oynatıcı kontrol katmanı modalın üzerine
        // çıkıyordu. Artık EPG açılırken İKİSİ de gizleniyor, kapanırken
        // (oynatıcı hâlâ açıksa) geri açılıyor.
        // ─────────────────────────────────────────────────────────────
        private void HidePlayerLayerForModal()
        {
            _modalOpen = true; // EvaluateOverlayActivation overlay'i yeniden göstermesin
            _engine?.SetVideoSurfaceVisible(false); // native video yüzeyini gizle
            HidePlayerOverlay();                    // PlayerOverlayWindow'u gizle

            // DÜZELTME (katman modalın üstünde kalmaya devam ediyordu —
            // EK GÜVENLİK AĞI): Yukarıdaki senkron gizleme çağrıları
            // TEORİDE yeterli olmalı, ama native pencere durumunu etkileyen
            // BAŞKA bir olay (ör. tam bu anda tetiklenen bir mpv reveal
            // olayı, bir layout/boyut değişikliği) ÇOK KISA bir süre
            // içinde arka arkaya gelirse, hangisinin "son sırada" kazandığı
            // garanti değildir. Kısa bir gecikmeyle (modal hâlâ açıksa,
            // yani _modalOpen hâlâ true ise) gizleme komutu BİR KEZ DAHA
            // uygulanır — bu, ilk çağrının olası bir yarışta "ezilmesi"
            // ihtimaline karşı düşük maliyetli bir sigorta.
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await Task.Delay(150);
                    if (_modalOpen) _engine?.SetVideoSurfaceVisible(false);
                }
                catch { }
            });
        }

        private void RestorePlayerLayerAfterModal()
        {
            _modalOpen = false;
            // Oynatıcı hâlâ açıksa (PlayerContainer.Height > 0) hem video
            // yüzeyini hem de overlay penceresini geri aç. Oynatıcı EPG
            // açıkken kapatıldıysa (Height==0) hiçbirini geri açma — aksi
            // hâlde kapalı oynatıcının native katmanı yeniden görünür olur.
            if (PlayerContainer.Height > 0)
            {
                _engine?.SetVideoSurfaceVisible(true);
                ShowPlayerOverlay();
            }
        }

        private async void EpgButton_Click(object? sender, RoutedEventArgs e)
        {
            var active = _sources.FirstOrDefault(s => s.IsActive);
            if (active == null)
            {
                ShowToast("Önce bir kaynak ekleyip aktifleştirin.");
                return;
            }

            HidePlayerLayerForModal();
            EpgOverlay.IsVisible = true;
            PopulateEpgCategoryCombo();
            StartEpgNowLineTimer();
            BuildEpgTimeline();
            if (_epgRangeStart.Date == DateTime.Today) ScrollEpgToNow();

            // DÜZELTME (madde 1 — panel açıldığında otomatik yenilenmiyordu):
            // Önceden sadece bu kaynak için EPG bu oturumda HİÇ yüklenmediyse
            // (_epgLoadedForSourceId != active.Id) bir yükleme deneniyordu —
            // arka planda (TriggerBackgroundEpgLoad, kaynak seçilirken/uygulama
            // açılışında) zaten bir kez yüklenmişse, panel bir daha ASLA
            // tazelik kontrolü yapmıyordu; kullanıcı önbellek süresi
            // (EPG_CACHE_HOURS) dolmuş olsa bile hep aynı bayat veriyi
            // görüyordu. Artık panel HER açılışta LoadEpgForSourceAsync'i
            // forceRefresh:false ile çağırıyor — bu metod zaten önbelleği
            // kendisi kontrol ediyor: süre dolmadıysa hızlıca hiçbir şey
            // yapmadan döner (sadece durum metnini günceller), süre
            // dolduysa sessizce yeni veriyi indirir. Böylece her açılışta
            // "en son ne zaman güncellendi" bilgisi her zaman doğru
            // gösterilir ve gerekiyorsa otomatik tazelenir.
            EpgStatusText.Text = "EPG kontrol ediliyor...";
            await LoadEpgForSourceAsync(active, forceRefresh: false);
        }

        private void CloseEpg_Click(object? sender, RoutedEventArgs e)
        {
            RestorePlayerLayerAfterModal();
            EpgOverlay.IsVisible = false;
            _epgNowLineTimer?.Stop();
        }

        private async void EpgRefresh_Click(object? sender, RoutedEventArgs e)
        {
            var active = _sources.FirstOrDefault(s => s.IsActive);
            if (active == null) return;
            EpgStatusText.Text = "EPG yeniden indiriliyor...";
            await LoadEpgForSourceAsync(active, forceRefresh: true);
        }

        // ═════════════════════════════════════════════════════════════
        // Kategori filtresi
        // ═════════════════════════════════════════════════════════════
        private void PopulateEpgCategoryCombo()
        {
            var groups = _allChannels
                .Where(c => !c.IsHidden && c.Type == "Canlı")
                .Select(c => c.Group)
                .Distinct()
                .OrderBy(g => g)
                .ToList();

            var items = new List<string> { EPG_ALL_CATEGORIES };
            items.AddRange(groups);

            string? previous = EpgCategoryCombo.SelectedItem as string;

            // DÜZELTME (gereksiz üçüncü BuildEpgTimeline çağrısı): SelectedItem
            // ataması SelectionChanged'i senkron tetikler; bu bayrak sayesinde
            // o tetiklenme sırasında BuildEpgTimeline tekrar çalışmıyor —
            // EpgButton_Click zaten kendi BuildEpgTimeline çağrısını yapıyor.
            _epgPopulatingCombo = true;
            try
            {
                EpgCategoryCombo.ItemsSource = items;
                EpgCategoryCombo.SelectedItem = (previous != null && items.Contains(previous)) ? previous : items[0];
            }
            finally { _epgPopulatingCombo = false; }
        }

        private void EpgCategoryCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_epgPopulatingCombo) return;
            BuildEpgTimeline();
        }

        // ═════════════════════════════════════════════════════════════
        // Gün gezinme
        // ═════════════════════════════════════════════════════════════
        private void EpgPrevDay_Click(object? sender, RoutedEventArgs e)
        {
            _epgRangeStart = _epgRangeStart.AddDays(-1);
            BuildEpgTimeline();
        }

        private void EpgNextDay_Click(object? sender, RoutedEventArgs e)
        {
            _epgRangeStart = _epgRangeStart.AddDays(1);
            BuildEpgTimeline();
        }

        private void EpgJumpNow_Click(object? sender, RoutedEventArgs e)
        {
            _epgRangeStart = DateTime.Today;
            BuildEpgTimeline();
            ScrollEpgToNow();
        }

        // ═════════════════════════════════════════════════════════════
        // Arka planda (panel açılmadan) EPG yükleme — kategori listesindeki
        // "şu an oynuyor" rozetleri için. MainWindow.Sources.cs →
        // LoadChannelsForSourceAsync tarafından çağrılır.
        // ═════════════════════════════════════════════════════════════
        internal void TriggerBackgroundEpgLoad(string sourceId)
        {
            try
            {
                var source = _sources.FirstOrDefault(s => s.Id == sourceId);
                if (source == null) return;

                if (_epgLoadedForSourceId == source.Id)
                {
                    UpdateLiveChannelsEpgNowInfo();
                    StartEpgNowInfoTimer();
                    return;
                }

                _ = LoadEpgForSourceAsync(source, forceRefresh: false);
            }
            catch { /* arka plan yüklemesi — sessizce yut, ön plana yansımaz */ }
        }

        // ═════════════════════════════════════════════════════════════
        // EPG indirme + önbellek
        // ═════════════════════════════════════════════════════════════
        private string GetEpgCachePath(string sourceId) => Path.Combine(AppDataDir(), $"epg_{sourceId}.json");

        private async Task LoadEpgForSourceAsync(TvSource source, bool forceRefresh)
        {
            if (_epgIsLoading) return;
            _epgIsLoading = true;
            try
            {
                string signature = source.Type == "Xtream"
                    ? $"xtream|{source.PathOrUrl}|{source.Username}"
                    : $"xmltv|{source.EpgUrl}";

                bool hasEpgSource = source.Type == "Xtream" || !string.IsNullOrWhiteSpace(source.EpgUrl);
                if (!hasEpgSource)
                {
                    _epgByChannelId.Clear();
                    _epgByNormalizedName.Clear();
                    _epgLoadedForSourceId = source.Id;
                    if (EpgOverlay.IsVisible)
                    {
                        EpgStatusText.Text = "Bu kaynak için EPG linki tanımlı değil. " +
                                             "Kaynağı silip 'Yayın Akışı (EPG) linki' alanını doldurarak yeniden ekleyebilirsiniz.";
                        BuildEpgTimeline();
                    }
                    return;
                }

                string cachePath = GetEpgCachePath(source.Id);

                if (!forceRefresh && File.Exists(cachePath))
                {
                    try
                    {
                        var cachedText = await File.ReadAllTextAsync(cachePath);
                        var cached = JsonSerializer.Deserialize<EpgCache>(cachedText, JsonOptions);
                        if (cached != null && cached.SourceSignature == signature &&
                            (DateTime.Now - cached.FetchedAt).TotalHours < EPG_CACHE_HOURS)
                        {
                            ApplyEpgPrograms(cached.Programs, cached.ChannelNames, source.Id);
                            if (EpgOverlay.IsVisible)
                            {
                                EpgStatusText.Text = $"EPG güncel · {cached.FetchedAt:dd.MM HH:mm} tarihinde önbellekten yüklendi";
                                BuildEpgTimeline();
                            }
                            UpdateLiveChannelsEpgNowInfo();
                            StartEpgNowInfoTimer();
                            return;
                        }
                    }
                    catch { /* bozuk/eski cache — yeniden indir */ }
                }

                XmltvParseResult parsed;
                try
                {
                    string url = source.Type == "Xtream"
                        ? $"{source.PathOrUrl.TrimEnd('/')}/xmltv.php?username={Uri.EscapeDataString(source.Username)}&password={Uri.EscapeDataString(source.Password)}"
                        : source.EpgUrl;

                    parsed = await DownloadAndParseXmltv(url);
                }
                catch (Exception ex)
                {
                    LogError("LoadEpgForSourceAsync.Download", ex);
                    if (EpgOverlay.IsVisible)
                    {
                        EpgStatusText.Text = "EPG indirilemedi — bağlantıyı veya EPG linkini kontrol edin.";
                        BuildEpgTimeline();
                    }
                    return;
                }

                if (parsed.Programs.Count == 0)
                {
                    if (EpgOverlay.IsVisible)
                    {
                        EpgStatusText.Text = "EPG sunucudan boş döndü (bu sunucu EPG desteklemiyor olabilir).";
                        BuildEpgTimeline();
                    }
                    ApplyEpgPrograms(parsed.Programs, parsed.ChannelNames, source.Id);
                    return;
                }

                ApplyEpgPrograms(parsed.Programs, parsed.ChannelNames, source.Id);
                source.EpgLastFetchedDate = DateTime.Now;

                if (EpgOverlay.IsVisible)
                {
                    EpgStatusText.Text = $"EPG güncellendi · {parsed.Programs.Count} program · {DateTime.Now:HH:mm}";
                    BuildEpgTimeline();
                }
                UpdateLiveChannelsEpgNowInfo();
                StartEpgNowInfoTimer();

                try
                {
                    var cache = new EpgCache
                    {
                        FetchedAt = DateTime.Now,
                        SourceSignature = signature,
                        Programs = parsed.Programs,
                        ChannelNames = parsed.ChannelNames
                    };
                    await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cache, JsonOptions));
                }
                catch (Exception ex) { LogError("LoadEpgForSourceAsync.SaveCache", ex); }
            }
            finally { _epgIsLoading = false; }
        }

        private void ApplyEpgPrograms(List<EpgProgram> programs, Dictionary<string, string> channelNames, string sourceId)
        {
            _epgByChannelId = programs
                .Where(p => !string.IsNullOrWhiteSpace(p.ChannelId))
                .GroupBy(p => p.ChannelId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Start).ToList(), StringComparer.OrdinalIgnoreCase);

            // İSİM BAZLI YEDEK EŞLEŞTİRME (bkz. dosya başındaki açıklama):
            // tvg-id eksik/uyuşmuyorsa kanal adı üzerinden bulunabilsin diye
            // <channel><display-name> eşlemesi normalize edilerek indekslenir.
            var byName = new Dictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in channelNames)
            {
                if (!_epgByChannelId.TryGetValue(kv.Key, out var progList) || progList.Count == 0) continue;
                string key = NormalizeEpgName(kv.Value);
                if (key.Length == 0) continue;
                if (!byName.ContainsKey(key)) byName[key] = progList;
            }
            _epgByNormalizedName = byName;

            _epgLoadedForSourceId = sourceId;
        }

        /// <summary>
        /// Bir kanal için (varsa) EPG program listesini döndürür. Önce
        /// tvg-id ile, bulunamazsa kanal adının normalize edilmiş haliyle
        /// (XMLTV &lt;channel&gt; display-name eşlemesi üzerinden) dener.
        /// </summary>
        private List<EpgProgram>? FindEpgProgramsForChannel(Channel ch)
        {
            if (!string.IsNullOrWhiteSpace(ch.TvgId) &&
                _epgByChannelId.TryGetValue(ch.TvgId, out var byId) && byId.Count > 0)
                return byId;

            string key = NormalizeEpgName(ch.Name);
            if (key.Length > 0 && _epgByNormalizedName.TryGetValue(key, out var byName) && byName.Count > 0)
                return byName;

            return null;
        }

        // Kanal adı normalizasyonu — kalite etiketlerini (HD/FHD/4K vb.)
        // atar, tüm harf/rakam-dışı karakterleri siler, küçük harfe çevirir.
        // "TRT 1 HD", "TRT1", "trt-1" gibi varyasyonların hepsi aynı anahtara
        // ("trt1") düşer.
        private static readonly Regex _rxEpgQualityTags =
            new(@"\b(HD|FHD|UHD|SD|4K|H\.?265|HEVC|H\.?264)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string NormalizeEpgName(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = _rxEpgQualityTags.Replace(raw, " ");
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // ═════════════════════════════════════════════════════════════
        // "Şu an oynuyor" rozetleri (Canlı TV kategori listesi)
        // ═════════════════════════════════════════════════════════════
        private void UpdateLiveChannelsEpgNowInfo()
        {
            try
            {
                var now = DateTime.Now;
                foreach (var ch in _allChannels)
                {
                    if (ch.Type != "Canlı") continue;

                    var programs = FindEpgProgramsForChannel(ch);
                    var current = programs?.FirstOrDefault(p => now >= p.Start && now < p.Stop);

                    ch.EpgNowTitle     = current?.Title ?? "";
                    ch.EpgNowTimeRange = current != null ? $"{current.Start:HH:mm}-{current.Stop:HH:mm}" : "";
                }
            }
            catch { }
        }

        private void StartEpgNowInfoTimer()
        {
            if (_epgNowInfoTimer == null)
            {
                _epgNowInfoTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                _epgNowInfoTimer.Tick += (s, e) => UpdateLiveChannelsEpgNowInfo();
            }
            if (!_epgNowInfoTimer.IsEnabled) _epgNowInfoTimer.Start();

            StartEpgAutoRefreshTimer();
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ (madde 1 — periyodik otomatik yenileme): EPG paneli hiç
        // açılmasa/kapatılsa bile, aktif kaynak için EPG en az bir kez
        // yüklendikten sonra bu zamanlayıcı arka planda çalışmaya devam
        // eder. Her tetiklenmede LoadEpgForSourceAsync forceRefresh:false
        // ile çağrılır — metod önbelleği KENDİSİ kontrol eder: süre
        // (EPG_CACHE_HOURS) dolmadıysa hiçbir ağ isteği yapmadan hemen
        // döner, dolduysa sessizce yeni veriyi indirir. Kontrol aralığı
        // (30 dk), önbellek süresinden (6 saat) kasıtlı olarak çok daha
        // kısa tutuldu ki önbellek süresi dolar dolmaz (kullanıcı panели
        // hiç açmasa bile) en geç ~30 dakika içinde otomatik tazelensin.
        // ─────────────────────────────────────────────────────────────
        private const int EPG_AUTO_REFRESH_CHECK_MINUTES = 30;

        private void StartEpgAutoRefreshTimer()
        {
            if (_epgAutoRefreshTimer == null)
            {
                _epgAutoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(EPG_AUTO_REFRESH_CHECK_MINUTES) };
                _epgAutoRefreshTimer.Tick += async (s, e) =>
                {
                    var active = _sources.FirstOrDefault(src => src.IsActive);
                    if (active != null) await LoadEpgForSourceAsync(active, forceRefresh: false);
                };
            }
            if (!_epgAutoRefreshTimer.IsEnabled) _epgAutoRefreshTimer.Start();
        }

        // ═════════════════════════════════════════════════════════════
        // XMLTV indirme + ayrıştırma (gzip destekli)
        // ═════════════════════════════════════════════════════════════
        private sealed class XmltvParseResult
        {
            public List<EpgProgram> Programs { get; set; } = new();
            public Dictionary<string, string> ChannelNames { get; set; } = new();
        }

        private async Task<XmltvParseResult> DownloadAndParseXmltv(string url)
        {
            EnsureDownloadHttpClient();
            byte[] bytes = await _downloadHttpClient!.GetByteArrayAsync(url);
            return await Task.Run(() => ParseXmltvBytes(bytes));
        }

        private static XmltvParseResult ParseXmltvBytes(byte[] bytes)
        {
            var result = new XmltvParseResult();

            Stream raw = new MemoryStream(bytes);
            Stream source = raw;
            bool isGzip = bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
            if (isGzip) source = new GZipStream(raw, CompressionMode.Decompress);

            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                    CheckCharacters = false
                };
                using var reader = XmlReader.Create(source, settings);

                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;

                    if (reader.Name == "channel")
                    {
                        string id = reader.GetAttribute("id") ?? "";
                        bool isEmpty = reader.IsEmptyElement;
                        string displayName = "";

                        if (!isEmpty)
                        {
                            using var sub = reader.ReadSubtree();
                            sub.Read();
                            while (sub.Read())
                            {
                                if (sub.NodeType == XmlNodeType.Element && sub.Name == "display-name" &&
                                    string.IsNullOrEmpty(displayName))
                                    displayName = sub.ReadElementContentAsString();
                            }
                        }

                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(displayName) &&
                            !result.ChannelNames.ContainsKey(id))
                            result.ChannelNames[id] = displayName.Trim();

                        continue;
                    }

                    if (reader.Name != "programme") continue;

                    string channelId = reader.GetAttribute("channel") ?? "";
                    DateTime start = ParseXmltvTime(reader.GetAttribute("start"));
                    DateTime stop  = ParseXmltvTime(reader.GetAttribute("stop"));
                    bool isEmptyProgramme = reader.IsEmptyElement;

                    string title = "", desc = "";

                    if (!isEmptyProgramme)
                    {
                        using var sub = reader.ReadSubtree();
                        sub.Read(); // <programme> öğesinin kendisi
                        while (sub.Read())
                        {
                            if (sub.NodeType != XmlNodeType.Element) continue;
                            if (sub.Name == "title" && string.IsNullOrEmpty(title))
                                title = sub.ReadElementContentAsString();
                            else if (sub.Name == "desc" && string.IsNullOrEmpty(desc))
                                desc = sub.ReadElementContentAsString();
                        }
                    }

                    if (!string.IsNullOrEmpty(channelId) && start != DateTime.MinValue)
                    {
                        result.Programs.Add(new EpgProgram
                        {
                            ChannelId   = channelId,
                            Title       = string.IsNullOrWhiteSpace(title) ? "(İsimsiz program)" : title.Trim(),
                            Description = desc.Trim(),
                            Start       = start,
                            Stop        = stop > start ? stop : start.AddMinutes(30)
                        });
                    }
                }
            }
            finally
            {
                source.Dispose();
                if (!ReferenceEquals(source, raw)) raw.Dispose();
            }

            return result;
        }

        // XMLTV zaman biçimi: "yyyyMMddHHmmss" veya "yyyyMMddHHmmss +HHMM"
        private static DateTime ParseXmltvTime(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return DateTime.MinValue;
            raw = raw.Trim();

            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0].Length < 14) return DateTime.MinValue;

            try
            {
                var dt = DateTime.ParseExact(parts[0].Substring(0, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture);

                if (parts.Length > 1)
                {
                    string offsetPart = parts[1];
                    if (offsetPart.Length >= 5 && (offsetPart[0] == '+' || offsetPart[0] == '-') &&
                        int.TryParse(offsetPart.Substring(1, 2), out int oh) &&
                        int.TryParse(offsetPart.Substring(3, 2), out int om))
                    {
                        int totalMin = oh * 60 + om;
                        var offset = TimeSpan.FromMinutes(offsetPart[0] == '-' ? -totalMin : totalMin);
                        var dto = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), offset);
                        return dto.LocalDateTime;
                    }
                }

                // Saat dilimi verilmemişse zaten yerel saat olduğu varsayılır
                // (çoğu IPTV sağlayıcısının XMLTV çıktısı böyledir).
                return dt;
            }
            catch { return DateTime.MinValue; }
        }

        // ═════════════════════════════════════════════════════════════
        // Zaman çizelgesi UI'sini inşa et — SADECE İSKELET + SANALLAŞTIRMA
        // KURULUMU. Gerçek satır kontrolleri RenderVisibleEpgRows ile,
        // sadece görünür aralık için, tembel olarak oluşturulur.
        // ═════════════════════════════════════════════════════════════
        private void BuildEpgTimeline()
        {
            try
            {
                EpgChannelNamesPanel.Children.Clear();
                EpgHeaderCanvas.Children.Clear();
                EpgRowsCanvas.Children.Clear();
                _epgRenderedNames.Clear();
                _epgRenderedRowControls.Clear();
                _epgRenderedFirst = -1;
                _epgRenderedLast = -1;
                _epgNowLineRow = null;
                _epgNowLineHeader = null;

                string? selectedCategory = EpgCategoryCombo.SelectedItem as string;
                // DÜZELTME (madde 2 — EPG'deki kanal sırası kaynaktaki
                // sıralamayla uyuşmuyordu): Burada önceden .OrderBy(Group)
                // .ThenBy(Name) ile ALFABETİK olarak yeniden sıralanıyordu.
                // _allChannels zaten M3U/Xtream playlist'indeki (sağlayıcının
                // kendi numaralandırdığı) sırayla doludur — ParseM3u satırları
                // dosyadaki sırayla ekler, yenileme sonrası da aynı sıra
                // korunur. Burada sadece Where filtreleri uygulanıp sıralama
                // hiç dokunulmadan bırakılıyor ki EPG'deki kanal sırası,
                // Canlı TV listesindeki/kaynaktaki sırayla birebir aynı olsun.
                var channels = _allChannels
                    .Where(c => !c.IsHidden && c.Type == "Canlı")
                    .Where(c => string.IsNullOrEmpty(selectedCategory) ||
                                selectedCategory == EPG_ALL_CATEGORIES ||
                                c.Group == selectedCategory)
                    .ToList();

                _epgCurrentChannelRows = channels;

                DateTime rangeStart = _epgRangeStart.Date;
                _epgTotalWidth = 1440 * EPG_PPM;
                double totalHeight = Math.Max(channels.Count, 1) * EPG_ROW_HEIGHT;

                EpgTitleText.Text = $"🗓️ Yayın Akışı — {rangeStart.ToString("dd MMMM yyyy, dddd", new CultureInfo("tr-TR"))}";

                EpgHeaderCanvas.Width   = _epgTotalWidth;
                EpgRowsCanvas.Width     = _epgTotalWidth;
                EpgRowsCanvas.Height    = totalHeight;
                EpgChannelNamesPanel.Height = totalHeight;

                // ── Saat cetveli ──────────────────────────────────────
                for (int h = 0; h <= 24; h++)
                {
                    double x = h * 60 * EPG_PPM;

                    var tick = new Border
                    {
                        Width = 1, Height = 40,
                        Background = (IBrush)this.Resources["Border"]!,
                        Opacity = 0.6
                    };
                    Canvas.SetLeft(tick, x);
                    EpgHeaderCanvas.Children.Add(tick);

                    if (h < 24)
                    {
                        var label = new TextBlock
                        {
                            Text = $"{h:D2}:00",
                            FontSize = 11,
                            Foreground = (IBrush)this.Resources["TextSec"]!,
                            Margin = new Thickness(4, 12, 0, 0)
                        };
                        Canvas.SetLeft(label, x);
                        EpgHeaderCanvas.Children.Add(label);
                    }
                }

                // Satırların kendisi burada OLUŞTURULMAZ — sadece görünür
                // aralık render edilir (bkz. RenderVisibleEpgRows). İlk
                // çağrıda ScrollViewer henüz ölçülmemiş olabileceğinden
                // (Viewport.Height == 0) birkaç dispatcher önceliğinde
                // tekrar denenir — bu uygulamada zaten ApplyGridColumnsRecalc
                // WithRetries'de kullanılan aynı desen.
                RenderVisibleEpgRows();
                Dispatcher.UIThread.Post(RenderVisibleEpgRows, DispatcherPriority.Loaded);
                Dispatcher.UIThread.Post(() =>
                {
                    RenderVisibleEpgRows();
                    DrawEpgNowLine();
                }, DispatcherPriority.Render);

                DrawEpgNowLine();
            }
            catch (Exception ex) { LogError("BuildEpgTimeline", ex); }
        }

        // ─────────────────────────────────────────────────────────────
        // Sanallaştırma: sadece görünür (+ tampon) satır aralığını
        // gerçek kontrollere dönüştürür; aralık dışına çıkanları söker.
        // ─────────────────────────────────────────────────────────────
        private void RenderVisibleEpgRows()
        {
            if (_epgCurrentChannelRows.Count == 0) return;

            double viewportHeight = EpgRowsScroll.Viewport.Height;
            if (viewportHeight <= 0) return; // henüz ölçülmedi — BuildEpgTimeline'daki retry'lar tekrar çağıracak

            double offsetY = EpgRowsScroll.Offset.Y;
            int first = Math.Max(0, (int)(offsetY / EPG_ROW_HEIGHT) - EPG_ROW_BUFFER);
            int last  = Math.Min(_epgCurrentChannelRows.Count - 1,
                                  (int)((offsetY + viewportHeight) / EPG_ROW_HEIGHT) + EPG_ROW_BUFFER);

            if (first == _epgRenderedFirst && last == _epgRenderedLast) return;

            var toRemove = _epgRenderedRowControls.Keys.Where(i => i < first || i > last).ToList();
            foreach (var i in toRemove)
            {
                foreach (var ctrl in _epgRenderedRowControls[i]) EpgRowsCanvas.Children.Remove(ctrl);
                _epgRenderedRowControls.Remove(i);

                if (_epgRenderedNames.TryGetValue(i, out var nameCtrl))
                {
                    EpgChannelNamesPanel.Children.Remove(nameCtrl);
                    _epgRenderedNames.Remove(i);
                }
            }

            for (int i = first; i <= last; i++)
            {
                if (_epgRenderedRowControls.ContainsKey(i)) continue;
                RenderEpgRow(i);
            }

            _epgRenderedFirst = first;
            _epgRenderedLast  = last;
        }

        private void RenderEpgRow(int i)
        {
            if (i < 0 || i >= _epgCurrentChannelRows.Count) return;

            var ch = _epgCurrentChannelRows[i];
            double rowTop = i * EPG_ROW_HEIGHT;
            DateTime rangeStart = _epgRangeStart.Date;
            DateTime rangeEnd   = rangeStart.AddDays(1);

            // ── Kanal adı (sol) ─────────────────────────────────────
            var nameBtn = new Button
            {
                Width = EPG_NAME_COL_WIDTH,
                Height = EPG_ROW_HEIGHT,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = (IBrush)this.Resources["Border"]!,
                Padding = new Thickness(10, 6),
                Tag = ch,
                Cursor = new Cursor(StandardCursorType.Hand),
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = ch.Name, FontSize = 12, FontWeight = FontWeight.Medium,
                            Foreground = (IBrush)this.Resources["Text"]!,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        },
                        new TextBlock
                        {
                            Text = ch.Group, FontSize = 10,
                            Foreground = (IBrush)this.Resources["TextSec"]!,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    }
                }
            };
            nameBtn.Click += EpgChannelName_Click;
            Canvas.SetLeft(nameBtn, 0);
            Canvas.SetTop(nameBtn, rowTop);
            EpgChannelNamesPanel.Children.Add(nameBtn);
            _epgRenderedNames[i] = nameBtn;

            var rowControls = new List<Control>();

            // ── Satır arka planı ─────────────────────────────────────
            var rowBg = new Border
            {
                Width = _epgTotalWidth, Height = EPG_ROW_HEIGHT,
                Background = i % 2 == 0 ? Brushes.Transparent : new SolidColorBrush(Color.Parse("#08808080")),
                BorderBrush = (IBrush)this.Resources["Border"]!,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            Canvas.SetLeft(rowBg, 0);
            Canvas.SetTop(rowBg, rowTop);
            EpgRowsCanvas.Children.Add(rowBg);
            rowControls.Add(rowBg);

            var programs = FindEpgProgramsForChannel(ch);

            if (programs == null || programs.Count == 0)
            {
                bool hasTvgId = !string.IsNullOrWhiteSpace(ch.TvgId);
                var noData = new TextBlock
                {
                    Text = hasTvgId ? "EPG verisi yok" : "EPG eşleşmesi yok (tvg-id eksik)",
                    FontSize = 10, Opacity = 0.55,
                    Foreground = (IBrush)this.Resources["TextSec"]!,
                    Margin = new Thickness(10, EPG_ROW_HEIGHT / 2 - 8, 0, 0)
                };
                Canvas.SetLeft(noData, 4);
                Canvas.SetTop(noData, rowTop);
                EpgRowsCanvas.Children.Add(noData);
                rowControls.Add(noData);
            }
            else
            {
                foreach (var prog in programs)
                {
                    if (prog.Stop <= rangeStart || prog.Start >= rangeEnd) continue;

                    DateTime clipStart = prog.Start < rangeStart ? rangeStart : prog.Start;
                    DateTime clipStop  = prog.Stop  > rangeEnd   ? rangeEnd   : prog.Stop;
                    double left  = (clipStart - rangeStart).TotalMinutes * EPG_PPM;
                    double width = Math.Max(20, (clipStop - clipStart).TotalMinutes * EPG_PPM - 2);

                    bool isNow = prog.IsNow;
                    var block = new Border
                    {
                        Width = width, Height = EPG_ROW_HEIGHT - 8,
                        CornerRadius = new CornerRadius(6),
                        Background = isNow ? (IBrush)this.Resources["Accent"]! : (IBrush)this.Resources["BgActive"]!,
                        BorderBrush = (IBrush)this.Resources["Border"]!,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(6, 3),
                        Cursor = new Cursor(StandardCursorType.Hand),
                        Tag = new EpgBlockTag { Channel = ch, Program = prog },
                        Child = new TextBlock
                        {
                            Text = prog.Title,
                            FontSize = 11,
                            FontWeight = isNow ? FontWeight.Bold : FontWeight.Normal,
                            Foreground = isNow ? Brushes.White : (IBrush)this.Resources["Text"]!,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            TextWrapping = TextWrapping.NoWrap
                        }
                    };
                    block.PointerPressed += EpgProgramBlock_PointerPressed;
                    Canvas.SetLeft(block, left + 1);
                    Canvas.SetTop(block, rowTop + 4);
                    EpgRowsCanvas.Children.Add(block);
                    rowControls.Add(block);
                }
            }

            _epgRenderedRowControls[i] = rowControls;
        }

        // ═════════════════════════════════════════════════════════════
        // Etkileşimler
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// DÜZELTME (EPG'den oynatınca player'daki kanal listesi pop-up'ında
        /// TÜM canlı kanalların görünmesi): Normal akışta bir kategoriye
        /// girip bir kanal oynatıldığında _displayContents zaten o
        /// kategorinin kanallarıyla doludur ve PopulatePlayerChannelList
        /// (MainWindow.Player.cs) bunu kullanır. EPG panelinden doğrudan
        /// oynatıldığında _displayContents ya boştur (→ "tüm kanallar"a
        /// düşülüyordu) ya da ziyaret edilmiş BAŞKA bir kategoriye ait
        /// bayat veriydi. Oynatmadan hemen önce _displayContents'i seçilen
        /// kanalın KENDİ kategorisiyle dolduruyoruz — böylece pop-up (ve
        /// Önceki/Sonraki Kanal) normal gezinmedekiyle birebir aynı davranır.
        /// </summary>
        private void PrepareLiveChannelListContext(Channel channel)
        {
            if (channel.Type != "Canlı") return;
            var peers = _allChannels
                .Where(c => !c.IsHidden && c.Type == "Canlı" && c.Group == channel.Group)
                .ToList();
            ReplaceCollection(_displayContents, peers);
        }

        private async void EpgChannelName_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Channel channel) return;
            RestorePlayerLayerAfterModal();
            EpgOverlay.IsVisible = false;
            PrepareLiveChannelListContext(channel);
            await StartPlayingChannel(channel, resume: false);
        }

        private async void EpgProgramBlock_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border b || b.Tag is not EpgBlockTag tag) return;
            var channel = tag.Channel;
            var prog = tag.Program;

            _epgSelectedChannel = channel;
            _epgSelectedProgram = prog;

            EpgSelectedTitleText.Text = prog.Title;
            EpgSelectedTimeText.Text  = $"{channel.Name}   •   {prog.Start:dd.MM HH:mm}–{prog.Stop:HH:mm}" +
                                        (prog.IsNow ? "   •   ● ŞU AN YAYINDA" : "");
            EpgSelectedDescText.Text  = string.IsNullOrWhiteSpace(prog.Description) ? "Açıklama yok." : prog.Description;
            EpgSelectedInfoBar.IsVisible = true;

            if (e.ClickCount >= 2)
            {
                RestorePlayerLayerAfterModal();
                EpgOverlay.IsVisible = false;
                PrepareLiveChannelListContext(channel);
                await StartPlayingChannel(channel, resume: false);
            }
        }

        private async void EpgPlaySelected_Click(object? sender, RoutedEventArgs e)
        {
            if (_epgSelectedChannel == null) return;
            RestorePlayerLayerAfterModal();
            EpgOverlay.IsVisible = false;
            PrepareLiveChannelListContext(_epgSelectedChannel);
            await StartPlayingChannel(_epgSelectedChannel, resume: false);
        }

        // ─────────────────────────────────────────────────────────────
        // "Şimdi" çizgisi — her dakika yeniden çizilir, tüm ızgarayı
        // yeniden inşa etmeden sadece çizgiyi taşır. Sadece panel açıkken
        // çalışır (bkz. StartEpgNowLineTimer/CloseEpg_Click).
        // ─────────────────────────────────────────────────────────────
        private void DrawEpgNowLine()
        {
            try
            {
                if (_epgNowLineRow != null) EpgRowsCanvas.Children.Remove(_epgNowLineRow);
                if (_epgNowLineHeader != null) EpgHeaderCanvas.Children.Remove(_epgNowLineHeader);
                _epgNowLineRow = null;
                _epgNowLineHeader = null;

                DateTime rangeStart = _epgRangeStart.Date;
                DateTime now = DateTime.Now;
                if (now < rangeStart || now >= rangeStart.AddDays(1)) return; // görüntülenen gün bugün değil

                double x = (now - rangeStart).TotalMinutes * EPG_PPM;

                _epgNowLineRow = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 2,
                    Height = Math.Max(EpgRowsCanvas.Height, 1),
                    Fill = Brushes.OrangeRed
                };
                Canvas.SetLeft(_epgNowLineRow, x);
                Canvas.SetTop(_epgNowLineRow, 0);
                EpgRowsCanvas.Children.Add(_epgNowLineRow);

                _epgNowLineHeader = new Avalonia.Controls.Shapes.Rectangle { Width = 2, Height = 40, Fill = Brushes.OrangeRed };
                Canvas.SetLeft(_epgNowLineHeader, x);
                Canvas.SetTop(_epgNowLineHeader, 0);
                EpgHeaderCanvas.Children.Add(_epgNowLineHeader);
            }
            catch { }
        }

        private void StartEpgNowLineTimer()
        {
            if (_epgNowLineTimer == null)
            {
                _epgNowLineTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                _epgNowLineTimer.Tick += (s, e) => DrawEpgNowLine();
            }
            _epgNowLineTimer.Stop();
            _epgNowLineTimer.Start();
        }

        private void ScrollEpgToNow()
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    double x = (DateTime.Now - _epgRangeStart.Date).TotalMinutes * EPG_PPM;
                    double target = Math.Max(0, x - 200);
                    EpgTimelineScroll.Offset = new Vector(target, EpgTimelineScroll.Offset.Y);
                }
                catch { }
            }, DispatcherPriority.Loaded);
        }

        // ─────────────────────────────────────────────────────────────
        // DÜZELTME (kanal isimleri ile program satırlarının ayrı ayrı
        // kaydırılması / sıralamanın karışması): Önceki sürümde sadece
        // sağdan sola (rows → names) tek yönlü bir senkron vardı. Kullanıcı
        // fare tekerleğini SOL (kanal isimleri) sütununun üzerinde
        // kullandığında o taraf bağımsız kayıyor, sağ taraf yerinde
        // kalıyordu. Artık İKİ YÖN de birbirini besliyor;
        // _epgSyncingScroll bayrağı sonsuz döngüyü engelliyor. Sanallaştırma
        // (RenderVisibleEpgRows) da her iki yönde de tetiklenir ki hangi
        // taraftan kaydırılırsa kaydırılsın yeni satırlar oluşsun.
        // ─────────────────────────────────────────────────────────────
        private void EpgRowsScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            RenderVisibleEpgRows();

            if (_epgSyncingScroll) return;
            _epgSyncingScroll = true;
            try { EpgChannelNamesScroll.Offset = new Vector(0, EpgRowsScroll.Offset.Y); }
            finally { _epgSyncingScroll = false; }
        }

        private void EpgChannelNamesScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_epgSyncingScroll) return;
            _epgSyncingScroll = true;
            try { EpgRowsScroll.Offset = new Vector(EpgRowsScroll.Offset.X, EpgChannelNamesScroll.Offset.Y); }
            finally { _epgSyncingScroll = false; }

            RenderVisibleEpgRows();
        }
    }
}
