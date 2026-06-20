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

        private DispatcherTimer? _inactivityTimer;
        private bool _isUpdatingSliderFromCode = false;
        private bool _isLiveContent = true;
        private bool _isMuted = false;
        private Channel? _currentVodInfo = null;

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

        private ObservableCollection<TvSource> _sources = new();
        private List<Channel> _allChannels = new();
        private ObservableCollection<string> _displayCategories = new();
        private ObservableCollection<Channel> _displayContents = new();

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

            _isDarkMode = false;
            this.Resources["Bg"]        = Brush.Parse("#f5f5f7");
            this.Resources["BgSidebar"] = Brush.Parse("#f0f0f2");
            this.Resources["BgCard"]    = Brush.Parse("#ffffff");
            this.Resources["BgHover"]   = Brush.Parse("#e8e8ec");
            this.Resources["BgActive"]  = Brush.Parse("#e2e2e8");
            this.Resources["Border"]    = Brush.Parse("#d4d4d8");
            this.Resources["Text"]      = Brush.Parse("#18181b");
            this.Resources["TextSec"]   = Brush.Parse("#6b6b73");
            Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            if (SettingsThemeBtn != null) SettingsThemeBtn.Content = "🌙 Koyu";

            LoadAppSettings();
            LoadWatchHistory();
            LoadSources();
            UpdateAutoRefreshButtonText();

            this.Opened += async (s, e) =>
            {
                await Task.Run(() => InitializePlayer());

                if (_appSettings.AutoRefreshOnStartup)
                {
                    var activeSource = _sources.FirstOrDefault(src => src.IsActive);
                    if (activeSource != null)
                    {
                        await Task.Delay(500);
                        await RefreshSourceInternal(activeSource);
                    }
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
                    "--network-caching=300",
                    "--live-caching=300",
                    "--file-caching=300",
                    "--no-video-title-show",
                    "--avcodec-hw=any",
                    "--drop-late-frames",
                    "--skip-frames",
                    "--clock-jitter=0",
                    "--clock-synchro=0",
                    "--no-overlay",
                    "--no-snapshot-preview"
                );

                _mediaPlayer = new MediaPlayer(_libVLC);
                MainVideoView.MediaPlayer = _mediaPlayer;
                _mediaPlayer.Volume = 100;
                _mediaPlayer.TimeChanged  += MediaPlayer_TimeChanged;
                _mediaPlayer.ESAdded      += MediaPlayer_ESAdded;
                _mediaPlayer.EndReached   += MediaPlayer_EndReached;
                _isVlcInitialized = true;
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

            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _toastTimer.Tick += (s, e) =>
            {
                NotificationToast.IsVisible = false;
                _toastTimer!.Stop();
            };
            _toastTimer.Start();
        }

        // ─────────────────────────────────────────────────────────────
        // Pencere kapanırken – TAMAMEN SENKRON temizlik
        // ─────────────────────────────────────────────────────────────
        protected override void OnClosed(EventArgs e)
        {
            try { SaveCurrentWatchPosition(); } catch { }

            try { _inactivityTimer?.Stop(); }   catch { }
            try { _toastTimer?.Stop(); }         catch { }
            try { _searchDebounceTimer?.Stop(); } catch { }

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

            Thread.Sleep(300);
            try { DwmFlush(); } catch { }

            try { _mediaPlayer?.Dispose(); _mediaPlayer = null; } catch { }
            try { _libVLC?.Dispose(); _libVLC = null; } catch { }
            Thread.Sleep(200);

            DisposeBitmapCaches();

            try { _logoHttpClient?.Dispose(); _logoHttpClient = null; } catch { }
            try { _tmdbHttpClient?.Dispose(); _tmdbHttpClient = null; } catch { }
            try { _tmdbCache.Clear(); }       catch { }
            try { _contentCache.Clear(); }    catch { }
            try { _seriesCardCache.Clear(); } catch { }

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
