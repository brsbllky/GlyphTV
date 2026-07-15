// ============================================================
// MainWindow.axaml.cs
// Alan tanımları, constructor, player başlatma ve toast sistemi
// ============================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow : Window
    {
        // ─── Win32 P/Invoke ───────────────────────────────────────────
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern uint EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(
            IntPtr hProcess, IntPtr dwMin, IntPtr dwMax);

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        // ─────────────────────────────────────────────────────────────
        // Sessiz hata loglama
        //
        // Çoğu catch{} bloğu kasıtlı olarak boş bırakılmıştır (örn. disk
        // temizliği, opsiyonel ağ istekleri) — bunlar için loglama gürültü
        // yaratır. Ancak kullanıcı verisi (favoriler, ayarlar, izleme
        // geçmişi, kaynak listesi) ile ilgili sessiz hatalar "neden
        // favorim kayboldu" türü sorunları debug edilemez hale getirir.
        // Bu metod, böyle veri-kritik noktalarda crash.log'a (Program.cs
        // ile aynı dosya) bir satır ekler; uygulamayı yavaşlatmaz, kendi
        // hatasını da yutar.
        // ─────────────────────────────────────────────────────────────
        internal static void LogError(string context, Exception ex)
        {
            try
            {
                // _appDataDir statik alanda zaten oluşturuldu; burada
                // tekrar Directory.Exists / CreateDirectory gerekmez.
                string logPath = Path.Combine(_appDataDir, "crash.log");
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { /* loglama da başarısız olursa sessizce devam et */ }
        }

        // ─────────────────────────────────────────────────────────────
        // Bellek temizliği — iki seviye:
        //   • TrimProcessMemoryLight: ucuz, bloklamayan, compacting
        //     olmayan bir GC turu. Her video kapatıldığında çağrılabilir,
        //     hissedilir bir donmaya sebep olmaz.
        //   • TrimProcessMemory (full): tam, bloklayan, compacting GC +
        //     working set trim. Maliyetlidir; bu yüzden yalnızca
        //     uygulama kapanışında veya periyodik olarak (her N video
        //     kapatmada bir, arka plan thread'inde) tetiklenir.
        // ─────────────────────────────────────────────────────────────
        private static int _playerCloseCountSinceFullTrim = 0;
        private const int FULL_TRIM_EVERY_N_CLOSES = 6;

        internal static void TrimProcessMemoryLight()
        {
            // Bloklamayan, compacting olmayan, sadece Gen0/Gen1 — UI thread'i
            // ciddi şekilde durdurmaz, eskisi gibi her video kapanışında
            // gözle görülür bir donma yaratmaz.
            GC.Collect(1, GCCollectionMode.Optimized, blocking: false, compacting: false);
        }

        /// <summary>
        /// Video/oynatıcı kapatıldığında çağrılır. Her kapatmada ağır temizlik
        /// yapmak yerine hafif bir GC turu uygular; ağır (bloklayan) temizliği
        /// sadece belirli aralıklarla ve arka plan thread'inde tetikler ki
        /// UI donmasın.
        /// </summary>
        internal static void TrimProcessMemoryOnPlayerClose()
        {
            TrimProcessMemoryLight();

            int count = Interlocked.Increment(ref _playerCloseCountSinceFullTrim);
            if (count >= FULL_TRIM_EVERY_N_CLOSES)
            {
                Interlocked.Exchange(ref _playerCloseCountSinceFullTrim, 0);
                // Ağır (bloklayan, compacting) GC turu UI thread'ini kilitlemesin
                // diye arka plan thread'inde tetiklenir.
                Task.Run(() => TrimProcessMemory());
            }
        }

        internal static void TrimProcessMemory()
        {
            try { DwmFlush(); } catch { }

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            try
            {
                using var proc = Process.GetCurrentProcess();
                EmptyWorkingSet(proc.Handle);
                SetProcessWorkingSetSize(proc.Handle, new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }

        // ─── VLC Player ───────────────────────────────────────────────
        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;
        private bool _isVlcInitialized = false;

        // KALICI DÜZELTME (donma): kaynak değişimi artık async; bu bayrak
        // aynı kaynağa çift tıklama veya geçiş sırasında ikinci bir
        // geçişin başlamasını (ve iki LoadChannelsForSourceAsync
        // çağrısının birbirini ezmesini) engeller.
        private bool _isSwitchingSource = false;

        private DispatcherTimer? _inactivityTimer;
        private bool _isUpdatingSliderFromCode = false;
        private bool _isLiveContent = true;
        private bool _isMuted = false;
        private Channel? _currentVodInfo = null;

        // MediaPlayer_TimeChanged çok sık tetiklenir (~her 200ms); Dispatcher
        // kuyruğunda birikmeyi önlemek için tek seferde bir güncelleme bekletilir.
        private volatile bool _timeChangedUpdatePending = false;

        private static readonly float[] _speedSteps = { 1.0f, 1.25f, 1.5f, 1.75f, 2.0f };
        private static readonly string[] _speedStepLabels = { "1×", "1.25×", "1.5×", "1.75×", "2×" };
        private int _speedIndex = 0;

        private static Dictionary<string, (int season, int episode)> _seriesSelections = new();
        private static Dictionary<string, List<Channel>> _contentCache = new();
        private static Dictionary<string, List<SeriesCard>> _seriesCardCache = new();

        private const int PAGE_SIZE = 50;
        private List<Channel> _allFilteredContents = new();
        private List<SeriesCard> _allFilteredCards = new();
        private int _loadedCount = 0;
        private bool _isLoadingMore = false;

        // ─── Favoriler paneli sayfalaması ──────────────────────────────
        // FavoriVodGrid / FavoriSeriesGrid'de UI virtualizasyonu olmadığı
        // için tüm favori listesini tek seferde render etmek büyük
        // koleksiyonlarda donmaya yol açabiliyordu; bu yüzden Content/Shows
        // görünümlerindeki sayfalama deseni favori listelerine de uygulandı.
        private const int FAVORI_PAGE_SIZE = 30;
        private List<Channel> _allFavoriVod = new();
        private int _favoriVodLoadedCount = 0;
        private bool _isLoadingMoreFavoriVod = false;
        private List<SeriesCard> _allFavoriSeriesCards = new();
        private int _favoriSeriesLoadedCount = 0;
        private bool _isLoadingMoreFavoriSeries = false;

        private ObservableCollection<TvSource> _sources = new();
        private List<Channel> _allChannels = new();
        private ObservableCollection<string> _displayCategories = new();
        private ObservableCollection<Channel> _displayContents = new();

        // ─────────────────────────────────────────────────────────────
        // KALICI DÜZELTME (scroll donması): VodContentGrid / SeriesContentGrid /
        // FavoriVodGrid / FavoriSeriesGrid önceden her sayfalama adımında
        // (LoadMoreItems, favori scroll handler'ları) ItemsSource'a TAMAMEN
        // YENİ bir List<T> atanıyordu (current.Concat(nextBatch).ToList()).
        // Avalonia, ItemsSource referansı değiştiğinde bunu "bambaşka bir
        // koleksiyon" sayıp WrapPanel içindeki (virtualizasyonsuz) TÜM
        // öğeleri yeniden ölçüp yerleştiriyordu — liste büyüdükçe (kullanıcı
        // scroll ettikçe) her yeni sayfa yüklemesi giderek pahalılaşıyor ve
        // kısa süreli "Yanıt Vermiyor" donmalarına yol açıyordu.
        //
        // Bu dört grid artık _displayContents/_displayCategories ile aynı
        // desende SABİT birer ObservableCollection'a bağlanıyor; sayfalama
        // artık sadece yeni öğeleri Add() ediyor (O(yeni sayfa) maliyet),
        // kategori/favori değişiminde ise ReplaceCollection() ile yerinde
        // güncelleniyor (Reset bildirimi yerine mümkün olduğunca Replace).
        // ─────────────────────────────────────────────────────────────
        private ObservableCollection<Channel> _displayVodContents = new();
        private ObservableCollection<SeriesCard> _displaySeriesCards = new();
        private ObservableCollection<Channel> _displayFavoriVod = new();
        private ObservableCollection<SeriesCard> _displayFavoriSeriesCards = new();

        private Channel? _currentChannel;
        private bool _isDarkMode = false;
        private string _currentTab = "Canlı";
        private string _currentCategory = "";
        private string _viewState = "Categories";
        private string _selectedSourceType = "M3U";

        private DispatcherTimer? _toastTimer;

        private List<WatchHistory> _watchHistory = new();
        private AppSettings _appSettings = new();
        private static Dictionary<string, Bitmap?> _logoCache = new();

        private const string TMDB_API_KEY = ""; // Kendi key'inizi buraya girin
        private const string TMDB_BASE = "https://api.themoviedb.org/3";
        private const string TMDB_IMG = "https://image.tmdb.org/t/p/w500";
        private static Dictionary<string, JsonElement?> _tmdbCache = new();
        private static Dictionary<string, Bitmap?> _tmdbPosterCache = new();
        private static HttpClient? _logoHttpClient;
        private static HttpClient? _tmdbHttpClient;

        // M3U/Xtream indirme istemcisi — önceden her DownloadM3uContent
        // çağrısında yeni bir HttpClient/HttpClientHandler oluşturuluyordu.
        // Soket/handler tahsisi maliyetli olduğu için (özellikle birden
        // fazla kaynak art arda yenilendiğinde) tek seferlik, paylaşılan
        // bir istemciye taşındı.
        private static HttpClient? _downloadHttpClient;
        private long _resumePosition = 0;

        // İzleme geçmişi hızlı erişim cache'i — sadece değiştiğinde yeniden hesaplanır
        private Dictionary<string, WatchHistory>? _watchHistoryByUrlCache = null;

        // ─────────────────────────────────────────────────────────────
        // Constructor
        // ─────────────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            CategoriesGrid.ItemsSource   = _displayCategories;
            ContentItemsGrid.ItemsSource = _displayContents;
            SourcesList.ItemsSource      = _sources;

            // KALICI DÜZELTME: bu dört grid artık sabit koleksiyonlara bağlı;
            // UpdateView/LoadMoreItems bir daha ItemsSource'a yeni liste atamaz.
            VodContentGrid.ItemsSource    = _displayVodContents;
            SeriesContentGrid.ItemsSource = _displaySeriesCards;
            FavoriVodGrid.ItemsSource     = _displayFavoriVod;
            FavoriSeriesGrid.ItemsSource  = _displayFavoriSeriesCards;

            // Özel scroll thumb: Offset değişmeden sadece Extent (içerik
            // yüksekliği) değiştiğinde ScrollChanged tetiklenmeyebilir
            // (örn. kategori değişip yeni içerik daha kısa geldiğinde).
            // LayoutUpdated her layout pass'inde tetiklenir ve thumb'ın
            // her zaman güncel kalmasını garanti eder.
            ContentScrollViewer.LayoutUpdated += (s, e) => UpdateCustomScrollThumb();

            _isDarkMode = false;
            this.Resources["Bg"]        = Brush.Parse("#f5f5f7");
            this.Resources["BgSidebar"] = Brush.Parse("#f0f0f2");
            this.Resources["BgCard"]    = Brush.Parse("#ffffff");
            this.Resources["BgHover"]   = Brush.Parse("#e8e8ec");
            this.Resources["BgActive"]  = Brush.Parse("#e2e2e8");
            this.Resources["Border"]    = Brush.Parse("#d4d4d8");
            this.Resources["Text"]      = Brush.Parse("#18181b");
            this.Resources["TextSec"]   = Brush.Parse("#6b6b73");

            // VOD/Dizi poster kartlarındaki placeholder ve overlay renkleri.
            // Önceden XAML'de sabit (#1A4f8bff / #0A000000) tanımlıydı; karanlık
            // temada BgCard zaten neredeyse siyah olduğundan bu sabit renkler
            // posterleri okunmaz/seçilmez derecede koyu gösteriyordu.
            this.Resources["PosterPlaceholderBg"] = Brush.Parse("#1A4f8bff");
            this.Resources["PosterOverlayBg"]     = Brush.Parse("#0A000000");

            Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            if (SettingsThemeBtn != null) SettingsThemeBtn.Content = "🌙 Koyu";

            LoadAppSettings();
            LoadWatchHistory();
            LoadSources();
            UpdateAutoRefreshButtonText();

            this.Opened += (s, e) =>
            {
                // DÜZELTME: Önceden VLC başlatma `await` ile bekleniyor, ardından
                // ayrıca 500ms daha gecikme uygulanıyordu — otomatik yenileme
                // pencere açıldıktan epey sonra başlıyordu. VLC başlatma ile
                // kaynak yenileme birbirine bağımlı değil; ikisi de paralel,
                // hiçbir ekstra gecikme olmadan fire-and-forget başlatılır.
                _ = Task.Run(() => InitializePlayer());

                if (_appSettings.AutoRefreshOnStartup)
                {
                    var activeSource = _sources.FirstOrDefault(src => src.IsActive);
                    if (activeSource != null)
                        _ = RefreshSourceInternal(activeSource);
                }
            };
        }

        // ─────────────────────────────────────────────────────────────
        // VLC başlatma
        // ─────────────────────────────────────────────────────────────
        private void InitializePlayer()
        {
            if (_isVlcInitialized) return;
            try
            {
                _libVLC = new LibVLC(
                    // ── Buffer / caching ──────────────────────────────
                    "--network-caching=300",
                    "--live-caching=800",
                    "--file-caching=300",

                    // ── Donanım hızlandırma ───────────────────────────
                    // Önceden "none" idi (her zaman yazılımsal/CPU decode).
                    // Özellikle HEVC/H.265 ve 4K yayınlarda bu, CPU kullanımını
                    // gereksiz yere yükseltip takılmaya/ısınmaya yol açabiliyordu.
                    // "any" → LibVLC, sistemde uygun olan donanım decoder'ını
                    // (Windows'ta DXVA2/D3D11VA) otomatik seçer; uygun değilse
                    // kendiliğinden yazılım decode'a düşer (fallback güvenlidir).
                    // Belirli bir GPU/sürücüde görüntü artefaktı/uyumsuzluk
                    // görülürse buradaki değeri tekrar "none" yapmak yeterlidir.
                    "--avcodec-hw=none",

                    // ── Geç kare / atlama ─────────────────────────────
                    "--no-video-title-show",
                    "--no-overlay",
                    "--no-snapshot-preview"
                );

                _mediaPlayer = new MediaPlayer(_libVLC);
                _mediaPlayer.Volume = 100;
                _mediaPlayer.TimeChanged  += MediaPlayer_TimeChanged;
                _mediaPlayer.ESAdded      += MediaPlayer_ESAdded;
                _mediaPlayer.EndReached   += MediaPlayer_EndReached;
                _isVlcInitialized = true;

                // ─────────────────────────────────────────────────────
                // ÖNEMLİ DÜZELTME (stabilite):
                // InitializePlayer(), constructor'daki `this.Opened` olayında
                // `Task.Run(() => InitializePlayer())` ile ARKA PLAN
                // thread'inde çalıştırılıyor. MainVideoView.MediaPlayer bir
                // Avalonia kontrol özelliğidir ve sadece UI thread'inden
                // değiştirilmelidir — bu satır önceden doğrudan burada
                // (arka plan thread'inde) çalıştığı için tanımsız davranışa
                // ve nadir/aralıklı çökmelere yol açabilecek bir UI thread
                // ihlaliydi. LibVLC/MediaPlayer nesnelerinin native
                // oluşturulması arka planda kalabilir (zaten asıl maliyetli
                // kısım budur), ama kontrole atama Dispatcher ile UI
                // thread'ine taşınır.
                // ─────────────────────────────────────────────────────
                Dispatcher.UIThread.Post(() =>
                {
                    if (MainVideoView != null && MainVideoView.MediaPlayer == null)
                        MainVideoView.MediaPlayer = _mediaPlayer;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("VLC init hatası: " + ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Toast
        // ─────────────────────────────────────────────────────────────
        private void ShowToast(string message)
        {
            NotificationText.Text = message;
            NotificationToast.IsVisible = true;

            // Her çağrıda yeni DispatcherTimer oluşturmak yerine tek
            // bir timer nesnesini yeniden kullanıyoruz. Yoğun toast
            // çağrılarında (kaynak yenileme sırasında) GC baskısı önlenir.
            if (_toastTimer == null)
            {
                _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _toastTimer.Tick += (s, e) =>
                {
                    NotificationToast.IsVisible = false;
                    _toastTimer!.Stop();
                };
            }

            _toastTimer.Stop();
            _toastTimer.Start();
        }

        // ─────────────────────────────────────────────────────────────
        // Pencere kapanırken – TAMAMEN SENKRON temizlik
        // ─────────────────────────────────────────────────────────────
        protected override void OnClosed(EventArgs e)
        {
            try { SaveCurrentWatchPosition(); } catch { }

            // Debounce edilmiş (henüz diske yazılmamış) favori/gizle değişiklikleri
            // varsa, pencere kapanmadan senkron olarak diske yaz — aksi halde
            // son yapılan değişiklikler kaybolabilir.
            try { FlushPendingChannelSaves(); } catch { }

            try { _inactivityTimer?.Stop(); }   catch { }
            try { _toastTimer?.Stop(); }         catch { }
            try { _searchDebounceTimer?.Stop(); } catch { }
            try { _scrollThumbFadeTimer?.Stop(); } catch { }

            try
            {
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.TimeChanged  -= MediaPlayer_TimeChanged;
                    _mediaPlayer.ESAdded      -= MediaPlayer_ESAdded;
                    _mediaPlayer.EndReached   -= MediaPlayer_EndReached;
                    _mediaPlayer.Stop();
                }
            }
            catch { }

            try { MainVideoView.MediaPlayer = null; } catch { }

            // NOT: Bu beklemeler sadece pencere kapanış sırasında (uygulama
            // zaten sonlanıyorken) çalışır; normal kullanım sırasında UI
            // donmasına sebep olmazlar. VLC'nin native handle'ı bırakması
            // için kısa bir süre veriyoruz.
            Thread.Sleep(150);
            try { DwmFlush(); } catch { }

            try { _mediaPlayer?.Dispose(); _mediaPlayer = null; } catch { }
            try { _libVLC?.Dispose(); _libVLC = null; } catch { }
            Thread.Sleep(100);

            DisposeBitmapCaches();

            try { _logoHttpClient?.Dispose(); _logoHttpClient = null; } catch { }
            try { _tmdbHttpClient?.Dispose(); _tmdbHttpClient = null; } catch { }
            try { _downloadHttpClient?.Dispose(); _downloadHttpClient = null; } catch { }
            try { _tmdbCache.Clear(); _tmdbCacheOrder.Clear(); } catch { }
            try { _contentCache.Clear(); }    catch { }
            try { _seriesCardCache.Clear(); } catch { }
            try { _decryptedChannelsCache.Clear(); } catch { }

            TrimProcessMemory();

            base.OnClosed(e);
        }

        private void DisposeBitmapCaches()
        {
            try
            {
                foreach (var bmp in _logoCache.Values)
                    try { bmp?.Dispose(); } catch { }
                _logoCache.Clear();
                _logoCacheOrder.Clear();
            }
            catch { }

            try
            {
                foreach (var bmp in _tmdbPosterCache.Values)
                    try { bmp?.Dispose(); } catch { }
                _tmdbPosterCache.Clear();
                _posterCacheOrder.Clear();
            }
            catch { }

            try
            {
                foreach (var ch in _allChannels)
                {
                    try { ch.LogoBitmap?.Dispose(); } catch { }
                    ch.LogoBitmap = null!;
                }
            }
            catch { }

            try
            {
                foreach (var cardList in _seriesCardCache.Values)
                    foreach (var card in cardList)
                    {
                        try { card.LogoBitmap?.Dispose(); } catch { }
                        card.LogoBitmap = null!;
                    }
            }
            catch { }
        }
    }
}
