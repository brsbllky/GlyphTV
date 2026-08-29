// ============================================================
// MainWindow.axaml.cs
// Alan tanımları, constructor, player başlatma (motor-bağımsız) ve
// toast sistemi
//
// YENİ (mpv desteği): Bu dosya artık doğrudan LibVLC/MediaPlayer
// tiplerine değil, GlyphTV.PlayerEngines.IPlayerEngine soyutlamasına
// bağımlı. _engine alanı ayarlara göre (AppSettings.PlayerEngine)
// VlcPlayerEngine veya MpvPlayerEngine olabilir. Video yüzeyi artık
// XAML'de sabit bir <vlc:VideoView x:Name="MainVideoView"> DEĞİL,
// boş bir <Panel x:Name="PlayerVideoHost"> — motor oluşturulunca
// kendi native yüzeyi (engine.VideoSurface) bu panele eklenir.
// Bkz. sohbetteki "MainWindow.axaml entegrasyon rehberi" için gerekli
// XAML değişikliği.
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
using GlyphTV.PlayerEngines;
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
        // ─────────────────────────────────────────────────────────────
        internal static void LogError(string context, Exception ex)
        {
            try
            {
                string logPath = Path.Combine(_appDataDir, "crash.log");
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // Emoji font ısıtma — değişiklik yok
        // ─────────────────────────────────────────────────────────────
        private static void WarmUpEmojiFont()
        {
            try
            {
                var tb = new TextBlock
                {
                    Text = "📺🎬🎞️🤍🛠️❤️♡▶⏸✕✚↻⓪ⓘ📁🎨🔊",
                    FontSize = 18
                };

                var size = new Size(400, 60);
                tb.Measure(size);
                tb.Arrange(new Rect(size));

                using var rtb = new RenderTargetBitmap(new PixelSize(400, 60));
                rtb.Render(tb);
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // Bellek temizliği — Modern & Non-blocking .NET GC yönetimi
        // ─────────────────────────────────────────────────────────────
        internal static void TrimProcessMemory()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

                IntPtr hProc = Process.GetCurrentProcess().Handle;
                if (hProc != IntPtr.Zero)
                {
                    EmptyWorkingSet(hProc);
                    SetProcessWorkingSetSize(hProc, (IntPtr)(-1), (IntPtr)(-1));
                }
            }
            catch { }
        }

        internal static void TrimProcessMemoryLight()
        {
            try
            {
                GC.Collect(1, GCCollectionMode.Optimized, blocking: false, compacting: false);
            }
            catch { }
        }

        internal static void TrimProcessMemoryOnPlayerClose()
        {
            Task.Run(async () =>
            {
                await Task.Delay(200);
                TrimProcessMemory();
            });
        }

        // ─── Oynatıcı motoru (VLC / mpv) ────────────────────────────
        //
        // YENİ: Artık doğrudan LibVLC/MediaPlayer alanları YOK. Tüm
        // oynatma mantığı (MainWindow.Player.cs) bu _engine alanı
        // üzerinden, IPlayerEngine arayüzüyle konuşur. Hangi motorun
        // aktif olduğu (_appSettings.PlayerEngine) sadece burada ve
        // MainWindow.Settings.cs → SwitchPlayerEngine'de bilinir.
        private IPlayerEngine? _engine;
        private bool _isEngineInitialized => _engine?.IsInitialized ?? false;

        private bool _isSwitchingSource = false;

        private DispatcherTimer? _inactivityTimer;
        private bool _isUpdatingSliderFromCode = false;
        private bool _isLiveContent = true;
        private bool _isMuted = false;
        private Channel? _currentVodInfo = null;

        private volatile bool _timeChangedUpdatePending = false;

        private static readonly float[] _speedSteps = { 1.0f, 1.25f, 1.5f, 1.75f, 2.0f };
        private static readonly string[] _speedStepLabels = { "1×", "1.25×", "1.5×", "1.75×", "2×" };
        private int _speedIndex = 0;

        private static Dictionary<string, (int season, int episode)> _seriesSelections = new();
        private static Dictionary<string, List<Channel>> _contentCache = new();
        private static Dictionary<string, List<SeriesCard>> _seriesCardCache = new();

        private const int PAGE_SIZE = 50;

        private const double CARD_FOOTPRINT_LIVE = 250;
        private const double CARD_FOOTPRINT_CARD = 250;
        private const int GRID_COLUMNS_FALLBACK = 4;

        private List<Channel> _allFilteredContents = new();
        private List<SeriesCard> _allFilteredCards = new();
        private int _loadedCount = 0;
        private bool _isLoadingMore = false;

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

        private ObservableCollection<Channel> _displayVodContents = new();
        private ObservableCollection<SeriesCard> _displaySeriesCards = new();
        private ObservableCollection<Channel> _displayFavoriVod = new();
        private ObservableCollection<SeriesCard> _displayFavoriSeriesCards = new();

        private RowGroupedCollection<Channel>? _rowsContentItems;
        private RowGroupedCollection<Channel>? _rowsVodContents;
        private RowGroupedCollection<SeriesCard>? _rowsSeriesCards;

        private DispatcherTimer? _gridColumnsDebounceTimer;
        private double _scrollOffsetBeforeInactive = -1;

        private Channel? _currentChannel;
        private bool _isDarkMode = false;
        private string _currentTab = "Anasayfa";
        private string _currentCategory = "";
        private string _viewState = "Categories";
        private string _selectedSourceType = "M3U";

        private DispatcherTimer? _toastTimer;
        private DispatcherTimer? _sidebarClockTimer;

        private List<WatchHistory> _watchHistory = new();
        private AppSettings _appSettings = new();
        private static Dictionary<string, Bitmap?> _logoCache = new();

        private const string TMDB_API_KEY = "buraya_api_keyinizi_girin"; // TMDb API Key Anahtarınızı buraya gireceksiniz
        private const string TMDB_BASE = "https://api.themoviedb.org/3";
        private const string TMDB_IMG = "https://image.tmdb.org/t/p/w500";
        private const string TMDB_BACKDROP_IMG = "https://image.tmdb.org/t/p/w1280";
        private static Dictionary<string, JsonElement?> _tmdbCache = new();
        private static Dictionary<string, Bitmap?> _tmdbPosterCache = new();
        private static Dictionary<string, Bitmap?> _tmdbBackdropCache = new();
        private static HttpClient? _logoHttpClient;
        private static HttpClient? _tmdbHttpClient;

        private static HttpClient? _downloadHttpClient;
        private long _resumePosition = 0;

        private Dictionary<string, WatchHistory>? _watchHistoryByUrlCache = null;

        // ─────────────────────────────────────────────────────────────
        // Constructor
        // ─────────────────────────────────────────────────────────────
        public MainWindow()
        {
            WarmUpEmojiFont();

            InitializeComponent();
            CategoriesGrid.ItemsSource   = _displayCategories;
            SourcesList.ItemsSource      = _sources;

            _rowsContentItems = new RowGroupedCollection<Channel>(_displayContents, GRID_COLUMNS_FALLBACK);
            _rowsVodContents  = new RowGroupedCollection<Channel>(_displayVodContents, GRID_COLUMNS_FALLBACK);
            _rowsSeriesCards  = new RowGroupedCollection<SeriesCard>(_displaySeriesCards, GRID_COLUMNS_FALLBACK);

            ContentItemsGrid.ItemsSource  = _rowsContentItems;
            VodContentGrid.ItemsSource    = _rowsVodContents;
            SeriesContentGrid.ItemsSource = _rowsSeriesCards;

            FavoriVodGrid.ItemsSource     = _displayFavoriVod;
            FavoriSeriesGrid.ItemsSource  = _displayFavoriSeriesCards;

            HeroDotsControl.ItemsSource   = _heroDots;
            HomeResumeGrid.ItemsSource    = _displayResumeItems;

            ContentScrollViewer.PropertyChanged += (s, e) =>
            {
                if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty)
                    UpdateCustomScrollThumb();
            };

            // DÜZELTME (CPU optimizasyonu): LayoutUpdated her render pass'inde saniyede yüzlerce kez
            // tetikleniyordu. Bunun yerine sadece ContentScrollViewer boyutu (genişliği) değiştiğinde
            // sütun hesaplaması tetiklenir.
            ContentScrollViewer.SizeChanged += (s, e) =>
            {
                if (e.NewSize.Width > 0)
                    ScheduleGridColumnsRecalc();
            };

            this.Deactivated += (s, e) => SaveScrollOffsetForInactivity();
            this.Activated   += (s, e) => RestoreScrollOffsetAfterInactivity();

            // YENİ: Oynatıcı kontrol paneli artık ayrı bir native pencerede
            // (PlayerOverlayWindow) — bkz. MainWindow.PlayerOverlay.cs dosya
            // başındaki "airspace" açıklaması. Bu çağrı sadece PlayerContainer.
            // Height değişimini dinlemeye başlar; pencere ilk oynatmada lazy
            // olarak oluşturulur.
            InitPlayerOverlay();

            _isDarkMode = false;

            LoadAppSettings();
            ApplyThemeMode(_appSettings.ThemeMode);
            UpdatePlayerEngineButtonsActiveState();
            UpdateHwDecodeItemsActiveState();
            UpdateInterlaceToggleState();
            UpdateHdrToneMappingItemsActiveState();
            UpdateHdrTargetPeakItemsActiveState();
            UpdateScalingQualityButtonsActiveState();
            UpdateAutoRefreshButtonsActiveState();
            UpdateCheckUpdatesButtonsActiveState();

            InitSidebarClock();

            this.Opened += (s, e) =>
            {
                // 1. Izgara sütunlarını hesapla
                ApplyGridColumnsRecalcWithRetries();

                // 2. Motoru arka planda HEMEN ön-ısıt (Pre-Warming)
                _ = Task.Run(async () => await EnsureEngineInitializedAsync());

                // 3. Verileri tamamen arka planda yükle (UI Thread 0 milisaniye bile bloklanmaz)
                _ = Task.Run(async () =>
                {
                    LoadWatchHistory();
                    LoadCachedTmdbPopular();
                    Dispatcher.UIThread.Post(RefreshHomeResumeSection);
                    _ = LoadWeeklyPopularFromTmdbAsync();

                    await LoadSourcesAsync();

                    if (_appSettings.AutoRefreshOnStartup)
                    {
                        var activeSource = _sources.FirstOrDefault(src => src.IsActive);
                        if (activeSource != null)
                            _ = RefreshSourceInternal(activeSource);
                    }
                });

                // 4. Açılışta sessiz çevrimiçi güncelleme denetimi (açılış arayüzünü geciktirmemek için 3 sn sonra)
                if (_appSettings.CheckUpdatesOnStartup)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);
                        await Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            await CheckForUpdatesAsync(manualTrigger: false);
                        });
                    });
                }
            };
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: Motor fabrikası — AppSettings.PlayerEngine'e göre doğru
        // IPlayerEngine implementasyonunu üretir.
        // ─────────────────────────────────────────────────────────────
        private IPlayerEngine CreateEngine(string engineName) => engineName switch
        {
            "Mpv" => new MpvPlayerEngine(),
            _     => new VlcPlayerEngine()
        };

        private readonly SemaphoreSlim _engineInitLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Aktif motoru (_appSettings.PlayerEngine) arka planda thread-safe şekilde
        /// oluşturur, video yüzeyini PlayerVideoHost paneline yerleştirir ve olaylarına
        /// abone olur. UI thread'i kesinlikle bloklanmaz, Yanıt Vermiyor hatası önlenir.
        /// </summary>
        private async Task EnsureEngineInitializedAsync()
        {
            if (_isEngineInitialized) return;

            await _engineInitLock.WaitAsync();
            try
            {
                if (_isEngineInitialized) return;

                string engineType = _appSettings.PlayerEngine;
                var newEngine = CreateEngine(engineType);

                // Ağır native başlatmayı arka plan thread'inde çalıştır
                await Task.Run(() =>
                {
                    try
                    {
                        newEngine.Initialize();
                        newEngine.SetHardwareDecoding(_appSettings.HwDecodeMode);
                        newEngine.SetDeinterlace(_appSettings.RemoveInterlacing);
                        newEngine.SetFastZapping(_appSettings.FastZapping);
                        newEngine.SetAudioEnhancement(_appSettings.AudioEnhancement);
                        newEngine.SetShaderMode(_appSettings.ShaderMode);

                        if (newEngine is MpvPlayerEngine mpvNewEngine)
                        {
                            mpvNewEngine.SetHdrToneMapping(_appSettings.HdrToneMapping);
                            mpvNewEngine.SetHdrTargetPeak(_appSettings.HdrTargetPeak);
                            mpvNewEngine.SetScalingQuality(_appSettings.ScalingQuality);
                            mpvNewEngine.SetBrightness(_appSettings.Brightness);
                            mpvNewEngine.SetContrast(_appSettings.Contrast);
                            mpvNewEngine.SetSaturation(_appSettings.Saturation);
                            mpvNewEngine.SetGamma(_appSettings.Gamma);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError("NativeEngineInit", ex);
                    }
                });

                // Video yüzeyini UI thread'inde ekle
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PlayerVideoHost.Children.Clear();
                    PlayerVideoHost.Children.Add(newEngine.VideoSurface);
                    if (PlayerContainer.Height <= 0)
                    {
                        newEngine.SetVideoSurfaceVisible(false);
                    }
                });

                newEngine.TimeChanged   += Engine_TimeChanged;
                newEngine.EndReached    += Engine_EndReached;
                newEngine.TracksChanged += Engine_TracksChanged;

                _engine = newEngine;
            }
            catch (Exception ex)
            {
                LogError("EnsureEngineInitializedAsync", ex);
            }
            finally
            {
                _engineInitLock.Release();
            }
        }

        /// <summary>
        /// Ayarlar > Oynatıcı Motoru panelinden çağrılır. Mevcut motoru
        /// Dispose edip yeni seçilen motoru arka planda HEMEN ön-ısıtır (pre-warm).
        /// Böylece kullanıcı sonraki içeriğe tıkladığında motor zaten hazır olur.
        /// </summary>
        private void SwitchPlayerEngine(string engineName)
        {
            var oldEngine = _engine;
            _engine = null;

            if (oldEngine != null)
            {
                oldEngine.TimeChanged   -= Engine_TimeChanged;
                oldEngine.EndReached    -= Engine_EndReached;
                oldEngine.TracksChanged -= Engine_TracksChanged;

                Task.Run(() =>
                {
                    try { oldEngine.Dispose(); } catch { }
                    Dispatcher.UIThread.Post(() => TrimProcessMemory(), DispatcherPriority.Background);
                });
            }

            PlayerVideoHost.Children.Clear();

            // Yeni motoru hemen arka planda hazırlamaya başla (pre-warm)
            _ = Task.Run(async () => await EnsureEngineInitializedAsync());
        }

        // ─────────────────────────────────────────────────────────────
        // Motor olayları → mevcut UI güncelleme metodlarına köprü.
        // Bu üç metodun UI-güncelleme gövdesi (slider/zaman etiketleri,
        // sonraki bölüm geçişi, ses/altyazı listesi tazeleme) MainWindow.
        // Player.cs'te tanımlıdır — bkz. entegrasyon rehberi.
        // ─────────────────────────────────────────────────────────────
        private void Engine_TimeChanged(object? sender, long timeMs) => OnEngineTimeChanged(timeMs);
        private void Engine_EndReached(object? sender, EventArgs e)  => OnEngineEndReached();
        private void Engine_TracksChanged(object? sender, EventArgs e) => OnEngineTracksChanged();

        /// <summary>
        /// RecalculateGridColumns'ı birkaç kez tekrar uygular — değişiklik yok.
        /// </summary>
        private void ApplyGridColumnsRecalcWithRetries()
        {
            ContentScrollViewer.InvalidateMeasure();
            RecalculateGridColumns();
            Dispatcher.UIThread.Post(() =>
            {
                ContentScrollViewer.InvalidateMeasure();
                RecalculateGridColumns();
                Dispatcher.UIThread.Post(RecalculateGridColumns, DispatcherPriority.Render);
            }, DispatcherPriority.Loaded);
        }

        private void RecalculateGridColumns()
        {
            double width = ContentScrollViewer.Bounds.Width;
            if (width <= 0) return;

            double usable = Math.Max(CARD_FOOTPRINT_CARD, width - 20);

            int liveCols = Math.Max(1, (int)(usable / CARD_FOOTPRINT_LIVE));
            int cardCols = Math.Max(1, (int)(usable / CARD_FOOTPRINT_CARD));

            _rowsContentItems?.SetColumns(liveCols);
            _rowsVodContents?.SetColumns(cardCols);
            _rowsSeriesCards?.SetColumns(cardCols);
        }

        private void ScheduleGridColumnsRecalc()
        {
            if (_gridColumnsDebounceTimer == null)
            {
                _gridColumnsDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                _gridColumnsDebounceTimer.Tick += (s, e) =>
                {
                    _gridColumnsDebounceTimer!.Stop();
                    RecalculateGridColumns();
                };
            }

            _gridColumnsDebounceTimer.Stop();
            _gridColumnsDebounceTimer.Start();
        }

        private void SaveScrollOffsetForInactivity()
        {
            try
            {
                if (PlayerContainer.Height > 0) return;
                _scrollOffsetBeforeInactive = ContentScrollViewer.Offset.Y;
            }
            catch { }
        }

        private void RestoreScrollOffsetAfterInactivity()
        {
            if (_scrollOffsetBeforeInactive < 0) return;
            double targetY = _scrollOffsetBeforeInactive;
            _scrollOffsetBeforeInactive = -1;

            void Apply()
            {
                try
                {
                    double maxY = Math.Max(0, ContentScrollViewer.Extent.Height - ContentScrollViewer.Viewport.Height);
                    double clampedY = Math.Clamp(targetY, 0, maxY);
                    ContentScrollViewer.Offset = new Avalonia.Vector(0, clampedY);
                }
                catch { }
            }

            Apply();
            Dispatcher.UIThread.Post(() =>
            {
                Apply();
                Dispatcher.UIThread.Post(Apply, DispatcherPriority.Render);
            }, DispatcherPriority.Loaded);
        }

        // ─────────────────────────────────────────────────────────────
        // Toast — değişiklik yok
        // ─────────────────────────────────────────────────────────────
        private void ShowToast(string message)
        {
            NotificationText.Text = message;
            NotificationToast.IsVisible = true;

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
            try { FlushPendingChannelSaves(); } catch { }

            // YENİ: Ayrı native pencere (PlayerOverlayWindow) MainWindow ile
            // birlikte kapanmıyor otomatik — elle kapatılmazsa uygulama
            // sürecinin sonlanmasını engelleyebilecek başıboş bir pencere
            // kalır (bkz. MainWindow.PlayerOverlay.cs).
            try { ClosePlayerOverlayWindow(); } catch { }

            try { _inactivityTimer?.Stop(); }   catch { }
            try { _toastTimer?.Stop(); }         catch { }
            try { _searchDebounceTimer?.Stop(); } catch { }
            try { _scrollThumbFadeTimer?.Stop(); } catch { }
            try { _gridColumnsDebounceTimer?.Stop(); } catch { }

            try { _rowsContentItems?.Dispose(); } catch { }
            try { _rowsVodContents?.Dispose(); }  catch { }
            try { _rowsSeriesCards?.Dispose(); }  catch { }

            // YENİ: Motor-bağımsız temizlik — hangi motor aktifse (VLC/mpv)
            // Dispose() kendi native kaynaklarını (LibVLC/MediaPlayer ya da
            // mpv_ctx + event thread) doğru şekilde serbest bırakır.
            try
            {
                if (_engine != null)
                {
                    _engine.TimeChanged   -= Engine_TimeChanged;
                    _engine.EndReached    -= Engine_EndReached;
                    _engine.TracksChanged -= Engine_TracksChanged;
                }
            }
            catch { }

            try { DwmFlush(); } catch { }

            try { _engine?.Dispose(); } catch { }
            _engine = null;

            DisposeBitmapCaches();

            try { _logoHttpClient?.Dispose(); _logoHttpClient = null; } catch { }
            try { _tmdbHttpClient?.Dispose(); _tmdbHttpClient = null; } catch { }
            try { _downloadHttpClient?.Dispose(); _downloadHttpClient = null; } catch { }
            try { _tmdbCache.Clear(); _tmdbCacheOrder.Clear(); } catch { }
            try { _contentCache.Clear(); }    catch { }
            try { _seriesCardCache.Clear(); } catch { }
            try { _decryptedChannelsCache.Clear(); } catch { }

            try { _sidebarClockTimer?.Stop(); _sidebarClockTimer = null; } catch { }

            TrimProcessMemory();

            base.OnClosed(e);
        }

        // ─────────────────────────────────────────────────────────────
        // Sol Panel Canlı Saat & Tarih Gösterimi
        // ─────────────────────────────────────────────────────────────
        private void InitSidebarClock()
        {
            UpdateSidebarClock();
            _sidebarClockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _sidebarClockTimer.Tick += (s, e) => UpdateSidebarClock();
            _sidebarClockTimer.Start();
        }

        private void UpdateSidebarClock()
        {
            try
            {
                var now = DateTime.Now;
                if (SidebarClockTime != null)
                {
                    SidebarClockTime.Text = now.ToString("HH:mm");
                }
                if (SidebarClockDate != null)
                {
                    SidebarClockDate.Text = now.ToString("d MMMM", System.Globalization.CultureInfo.CurrentCulture);
                }
            }
            catch { }
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
