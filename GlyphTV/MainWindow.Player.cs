using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GlyphTV.PlayerEngines;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow
    {
        private DispatcherTimer? _liveBadgeTimer;
        private int _isEndReachedHandlingInt = 0;

        private bool _isLiveBadgePulse = false;
        private int _badgeUpdateTickCounter = 0;

        private void StartLiveBadgePulse()
        {
            if (_liveBadgeTimer == null)
            {
                _liveBadgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
                _liveBadgeTimer.Tick += (s, e) =>
                {
                    if (PlayerContainer.Height > 0 && _isLiveContent)
                    {
                        _isLiveBadgePulse = !_isLiveBadgePulse;
                        LiveBadge.Opacity = _isLiveBadgePulse ? 0.35 : 1.0;

                        if (++_badgeUpdateTickCounter >= 2)
                        {
                            _badgeUpdateTickCounter = 0;
                            UpdateMediaInfoBadges();
                        }
                    }
                };
            }

            _isLiveBadgePulse = false;
            _badgeUpdateTickCounter = 0;
            LiveBadge.Opacity = 1.0;
            _liveBadgeTimer.Stop();
            _liveBadgeTimer.Start();
        }

        private void StopLiveBadgePulse()
        {
            _liveBadgeTimer?.Stop();
            LiveBadge.Opacity = 1.0;
        }

        private double _scrollOffsetBeforePlayer = 0;

        private async void Content_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Channel channel) return;
            await StartPlayingChannel(channel, resume: false);
        }

        private async void Content_Resume_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Channel channel) return;
            await StartPlayingChannel(channel, resume: true);
        }

        private async Task StartPlayingChannel(Channel channel, bool resume)
        {
            _scrollOffsetBeforePlayer = ContentScrollViewer.Offset.Y;

            _currentChannel = channel;
            PlayerTitleText.Text = channel.Name;
            PlayerContainer.IsVisible = true;
            PlayerVideoHost.IsVisible = true;
            PlayerContainer.Background = Brushes.Black;
            PlayerContainer.Height = 450;

            _resumePosition = 0;
            if (resume)
            {
                var hist = _watchHistory.FirstOrDefault(h => h.Url == channel.Url);
                if (hist != null && hist.Position > 5000)
                {
                    _resumePosition = hist.Position;
                    var ts = TimeSpan.FromMilliseconds(hist.Position);
                    ShowToast($"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2} konumundan devam ediliyor");
                }
            }
            await PlayChannel(channel.Url);
        }

        internal void ClosePlayer_Click(object? sender, RoutedEventArgs e)
        {
            SaveCurrentWatchPosition();
            StopLiveBadgePulse();

            if (_currentChannel != null && _currentChannel.Type != "Canlı")
            {
                _currentChannel.HasResume = _watchHistory.Any(h => h.Url == _currentChannel.Url && h.Position > 5000);

                if (_currentChannel.Type == "Dizi")
                    SyncSeriesCardSelection(_currentChannel, _currentChannel.HasResume);
            }

            try { RefreshHomeResumeSection(); } catch { }

            try { _engine?.Stop(); } catch { }

            _engine?.SetVideoSurfaceVisible(false);
            HidePlayerOverlay();

            ChannelListPanel.IsVisible = false;
            AudioTrackPopup.IsVisible = false;
            SubtitlePopup.IsVisible = false;
            AspectRatioPopup.IsVisible = false;

            if (this.WindowState == WindowState.FullScreen)
                Fullscreen_Click(sender, e);

            if (_isPipMode)
            {
                ExitPipMode(restoreLayout: true);
            }

            PlayerContainer.Height = 0;
            PlayerContainer.IsVisible = false;
            PlayerVideoHost.IsVisible = false;
            PlayerContainer.Background = Brushes.Transparent;
            ResetMediaInfoBadges();

            RestoreContentScrollPosition();

            Dispatcher.UIThread.Post(() => TrimProcessMemoryOnPlayerClose(), DispatcherPriority.Background);
        }

        private void RestoreContentScrollPosition()
        {
            double targetY = _scrollOffsetBeforePlayer;

            void Apply()
            {
                if (BackBtn.IsVisible) BackBtn.Focus();

                double maxY = Math.Max(0, ContentScrollViewer.Extent.Height - ContentScrollViewer.Viewport.Height);
                double clampedY = Math.Clamp(targetY, 0, maxY);
                ContentScrollViewer.Offset = new Avalonia.Vector(0, clampedY);
            }

            Apply();
            Dispatcher.UIThread.Post(() =>
            {
                Apply();
                Dispatcher.UIThread.Post(Apply, DispatcherPriority.Render);
            }, DispatcherPriority.Loaded);
        }

        private void ResetScrollToTop()
        {
            if (BackBtn.IsVisible) BackBtn.Focus();
            ContentScrollViewer.Offset = new Avalonia.Vector(0, 0);
            ContentScrollViewer.ScrollToHome();

            Dispatcher.UIThread.Post(() =>
            {
                if (BackBtn.IsVisible) BackBtn.Focus();
                ContentScrollViewer.Offset = new Avalonia.Vector(0, 0);
                ContentScrollViewer.ScrollToHome();

                Dispatcher.UIThread.Post(() =>
                {
                    if (BackBtn.IsVisible) BackBtn.Focus();
                    ContentScrollViewer.Offset = new Avalonia.Vector(0, 0);
                    ContentScrollViewer.ScrollToHome();
                }, DispatcherPriority.Render);
            }, DispatcherPriority.Loaded);
        }

        private void SaveCurrentWatchPosition()
        {
            try
            {
                if (_currentChannel == null || _engine == null || !_engine.IsInitialized) return;
                if (_currentChannel.Type == "Canlı") return;
                long pos = _engine.Time;
                long dur = _engine.Length;
                if (pos > 0) UpsertWatchHistory(_currentChannel, pos, dur);
            }
            catch { }
        }

        private async Task PlayChannel(string url)
        {
            try
            {
                if (!_isEngineInitialized) await EnsureEngineInitializedAsync();
                if (_engine == null) return;

                System.Threading.Interlocked.Exchange(ref _isEndReachedHandlingInt, 0);

                CurrentTimeText.Text = "00:00:00";
                TotalTimeText.Text = "00:00:00";
                _isUpdatingSliderFromCode = true;
                TimeSlider.Value = 0;
                _isUpdatingSliderFromCode = false;
                PlayerTitleText.Text = _currentChannel?.Name ?? "";

                if (_isPipMode)
                {
                    if (AspectRatioText != null) AspectRatioText.Text = "Fill";
                    _engine?.SetAspectRatio("fill");
                }
                else
                {
                    if (AspectRatioText != null) AspectRatioText.Text = "Auto";
                    _engine?.SetAspectRatio("12:5");
                }

                ResetMediaInfoBadges();
                ResetMpvEnhancedSettingsForNewContent();

                _isLiveContent = _currentChannel?.Type == "Canlı";
                _engine?.SetIsLiveStream(_isLiveContent);
                ConfigurePlayerUIForContentType();

                _engine?.Play(url, _resumePosition);
                _resumePosition = 0;

                ShowPlayerControls();
                ResetInactivityTimer();
                ShowPlayerOverlay();

                IconPlay.IsVisible = false;
                IconPause.IsVisible = true;
                AudioTrackPopup.IsVisible = false;
                SubtitlePopup.IsVisible = false;
                AspectRatioPopup.IsVisible = false;
            }
            catch (Exception ex) { PlayerTitleText.Text = "HATA: " + ex.Message; }
        }

        private void ConfigurePlayerUIForContentType()
        {
            UpdateMpvSettingsButtonVisibility();

            if (_isLiveContent)
            {
                BtnAudioTrack.IsVisible = true;
                BtnChannelList.IsVisible = true;
                BtnSubtitle.IsVisible = false;
                PrevChannelBtn.IsVisible = true;
                NextChannelBtn.IsVisible = true;
                SkipBackBtn.IsVisible = false;
                SkipForwardBtn.IsVisible = false;
                NextEpisodeBtn.IsVisible = false;
                SpeedBtn.IsVisible = false;
                SeekBarContainer.IsVisible = false;
                LiveBadge.IsVisible = true;
                IconPlay.IsVisible = false;
                IconPause.IsVisible = true;

                if (_engine != null) _engine.PlaybackRate = 1.0f;
                _speedIndex = 0;
                SpeedBtnText.Text = "1×";
                StartLiveBadgePulse();
            }
            else
            {
                BtnAudioTrack.IsVisible = false;
                BtnChannelList.IsVisible = false;
                BtnSubtitle.IsVisible = true;
                PrevChannelBtn.IsVisible = false;
                NextChannelBtn.IsVisible = false;
                SkipBackBtn.IsVisible = true;
                SkipForwardBtn.IsVisible = true;
                NextEpisodeBtn.IsVisible = _currentChannel?.Type == "Dizi";
                SpeedBtn.IsVisible = true;
                SeekBarContainer.IsVisible = true;
                LiveBadge.IsVisible = false;
                IconPlay.IsVisible = false;
                IconPause.IsVisible = true;

                _speedIndex = 0;
                SpeedBtnText.Text = "1×";
                StopLiveBadgePulse();
            }
        }

        private int _currentSubtitleDelayMs = 0;
        private int _currentAudioDelayMs = 0;

        private void ResetMpvEnhancedSettingsForNewContent()
        {
            _appSettings.Brightness = 0;
            _appSettings.Contrast = 0;
            _appSettings.Saturation = 0;
            _appSettings.Gamma = 0;
            _appSettings.HdrToneMapping = "auto";
            _appSettings.HdrTargetPeak = "auto";

            _engine?.SetBrightness(0);
            _engine?.SetContrast(0);
            _engine?.SetSaturation(0);
            _engine?.SetGamma(0);
            if (_engine is GlyphTV.PlayerEngines.MpvPlayerEngine mpvEngine)
            {
                mpvEngine.SetHdrToneMapping("auto");
                mpvEngine.SetHdrTargetPeak("auto");
            }

            SetSubtitleDelay(0);
            SetAudioDelay(0);
            InitializeMpvEqSliderValues();
            UpdateHdrToneMappingItemsActiveState();
            UpdateHdrTargetPeakItemsActiveState();
        }

        internal void BtnAudioTrack_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            AudioTrack_Click(sender, new RoutedEventArgs());
        }
        internal void BtnChannelList_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ToggleChannelList_Click(sender, new RoutedEventArgs());
        }
        internal void BtnAspectRatio_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            AspectRatio_Click(sender, new RoutedEventArgs());
        }
        internal void BtnSubtitle_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            Subtitle_Click(sender, new RoutedEventArgs());
        }

        internal void PlayPause_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            PlayPause_Core();
        }
        internal void PlayPause_Click(object? sender, RoutedEventArgs e) => PlayPause_Core();
        private void PlayPause_Core()
        {
            if (_engine == null) return;
            bool willPlay = !_engine.IsPlaying;
            _engine.PauseToggle();
            IconPlay.IsVisible = !willPlay;
            IconPause.IsVisible = willPlay;
        }

        internal void Mute_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            Mute_Core();
        }
        internal void Mute_Click(object? sender, RoutedEventArgs e) => Mute_Core();
        private void Mute_Core()
        {
            if (_engine == null) return;
            _isMuted = !_isMuted;
            _engine.Mute = _isMuted;
            IconMuteOn.IsVisible = !_isMuted;
            IconMuteOff.IsVisible = _isMuted;
        }

        internal void SkipBack_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            SkipBack_Core();
        }
        internal void SkipBack_Click(object? sender, RoutedEventArgs e) => SkipBack_Core();
        private void SkipBack_Core()
        {
            if (_engine != null && _engine.IsSeekable)
            {
                _engine.Time = Math.Max(0, _engine.Time - 10000);
                ShowToast("10 saniye geri");
            }
        }

        internal void SkipForward_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            SkipForward_Core();
        }
        internal void SkipForward_Click(object? sender, RoutedEventArgs e) => SkipForward_Core();
        private void SkipForward_Core()
        {
            if (_engine != null && _engine.IsSeekable && _engine.Length > 0)
            {
                _engine.Time = Math.Min(_engine.Length - 500, _engine.Time + 10000);
                ShowToast("10 saniye ileri");
            }
        }

        internal void PrevChannel_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            _ = DoPrevChannel();
        }
        internal void PrevChannel_Click(object? sender, RoutedEventArgs e) => _ = DoPrevChannel();
        private async Task DoPrevChannel()
        {
            if (_currentChannel == null) return;
            var list = _displayContents.Count > 0
                ? _displayContents.ToList()
                : _allChannels.Where(c => !c.IsHidden && c.Type == "Canlı").ToList();
            int index = list.FindIndex(c => c.Url == _currentChannel.Url);
            int next = index <= 0 ? list.Count - 1 : index - 1;
            if (next >= 0 && next < list.Count)
            {
                _currentChannel = list[next];
                PlayerTitleText.Text = _currentChannel.Name;
                await PlayChannel(_currentChannel.Url);
            }
        }

        internal void NextChannel_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            _ = DoNextChannel();
        }
        internal void NextChannel_Click(object? sender, RoutedEventArgs e) => _ = DoNextChannel();
        private async Task DoNextChannel()
        {
            if (_currentChannel == null) return;
            var list = _displayContents.Count > 0
                ? _displayContents.ToList()
                : _allChannels.Where(c => !c.IsHidden && c.Type == "Canlı").ToList();
            int index = list.FindIndex(c => c.Url == _currentChannel.Url);
            int next = (index >= list.Count - 1) ? 0 : index + 1;
            if (next >= 0 && next < list.Count)
            {
                _currentChannel = list[next];
                PlayerTitleText.Text = _currentChannel.Name;
                await PlayChannel(_currentChannel.Url);
            }
        }

        internal void NextEpisode_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            _ = DoNextEpisode();
        }
        internal void NextEpisode_Click(object? sender, RoutedEventArgs e) => _ = DoNextEpisode();

        private async Task DoNextEpisode()
        {
            var nextEpisode = FindNextEpisode(_currentChannel);
            if (nextEpisode == null) { ShowToast("Sonraki bölüm bulunamadı."); return; }

            var previousChannel = _currentChannel;
            SaveCurrentWatchPosition();

            if (previousChannel != null)
            {
                previousChannel.HasResume = _watchHistory.Any(h => h.Url == previousChannel.Url && h.Position > 5000);
                if (SeriesContentGrid.IsVisible)
                {
                    var card = _displaySeriesCards.FirstOrDefault(c => c.ShowName == previousChannel.ShowName);
                    if (card != null) card.HasResume = previousChannel.HasResume;
                }
            }

            _currentChannel = nextEpisode;
            PlayerTitleText.Text = nextEpisode.Name;
            _resumePosition = 0;
            SyncSeriesCardSelection(nextEpisode, hasResume: false);
            await PlayChannel(nextEpisode.Url);
            ShowToast($"Sonraki bölüm: {nextEpisode.Name}");
        }

        private Channel? FindNextEpisode(Channel? current)
        {
            if (current == null) return null;
            var allEpisodes = _allChannels
                .Where(c => !c.IsHidden && c.Type == "Dizi"
                         && c.ShowName == current.ShowName
                         && c.Group == current.Group)
                .ToList();

            var sameSeasonNext = allEpisodes
                .Where(c => c.Season == current.Season && c.EpisodeNumber > current.EpisodeNumber)
                .OrderBy(c => c.EpisodeNumber)
                .FirstOrDefault();

            if (sameSeasonNext != null) return sameSeasonNext;

            return allEpisodes
                .Where(c => string.Compare(c.Season, current.Season, StringComparison.Ordinal) > 0)
                .OrderBy(c => c.Season)
                .ThenBy(c => c.EpisodeNumber)
                .FirstOrDefault();
        }

        private void OnEngineTimeChanged(long timeMs)
        {
            if (_timeChangedUpdatePending) return;
            _timeChangedUpdatePending = true;

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    long length = _engine?.Length ?? 0;
                    if (length > 0)
                    {
                        _isUpdatingSliderFromCode = true;
                        TimeSlider.Value = ((double)timeMs / length) * 100;
                        _isUpdatingSliderFromCode = false;

                        var current = TimeSpan.FromMilliseconds(timeMs);
                        var total = TimeSpan.FromMilliseconds(length);
                        CurrentTimeText.Text = current.ToString(@"hh\:mm\:ss");
                        TotalTimeText.Text = total.ToString(@"hh\:mm\:ss");
                    }
                }
                finally { _timeChangedUpdatePending = false; }
            }, DispatcherPriority.Background);
        }

        private void OnEngineEndReached()
        {
            if (_currentChannel?.Type != "Dizi") return;

            if (System.Threading.Interlocked.CompareExchange(ref _isEndReachedHandlingInt, 1, 0) != 0)
                return;

            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var nextEpisode = FindNextEpisode(_currentChannel);
                    if (nextEpisode == null)
                    {
                        ShowToast("Dizinin son bölümü izlendi.");
                        await Task.Delay(2000);
                        ClosePlayer_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
                        return;
                    }

                    SaveCurrentWatchPosition();

                    var previousChannel = _currentChannel;
                    if (previousChannel != null)
                    {
                        previousChannel.HasResume = false;
                        if (SeriesContentGrid.IsVisible)
                        {
                            var card = _displaySeriesCards.FirstOrDefault(c => c.ShowName == previousChannel.ShowName);
                            if (card != null) card.HasResume = false;
                        }
                    }

                    _currentChannel = nextEpisode;
                    PlayerTitleText.Text = nextEpisode.Name;
                    _resumePosition = 0;
                    SyncSeriesCardSelection(nextEpisode, hasResume: false);
                    await PlayChannel(nextEpisode.Url);
                    ShowToast($"Sonraki bölüm: {nextEpisode.Name}");
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _isEndReachedHandlingInt, 0);
                }
            }, DispatcherPriority.Normal);
        }

        private void OnEngineTracksChanged()
        {
            Dispatcher.UIThread.Post(UpdateMediaInfoBadges);
        }

        internal void Speed_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            Speed_Core();
        }
        internal void Speed_Click(object? sender, RoutedEventArgs e) => Speed_Core();
        private void Speed_Core()
        {
            if (_engine == null) return;
            _speedIndex = (_speedIndex + 1) % _speedSteps.Length;
            float rate = _speedSteps[_speedIndex];
            string label = _speedStepLabels[_speedIndex];

            _engine.PlaybackRate = rate;

            SpeedBtnText.Text = label;
            ShowToast($"Oynatma hızı: {label}");
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: PiP (Picture-in-Picture / Resim İçinde Resim) Modu
        // ─────────────────────────────────────────────────────────────
        internal bool _isPipMode = false;
        internal bool _isSyncingPipPosition = false;

        private WindowState _prePipWindowState = WindowState.Normal;
        private double _prePipWidth = 1280;
        private double _prePipHeight = 775;
        private PixelPoint _prePipPosition;
        private double _prePipPlayerHeight = 450;
        private double _prePipMinWidth = 0;
        private double _prePipMinHeight = 0;

        internal void Pip_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            TogglePipMode();
        }
        internal void Pip_Click(object? sender, RoutedEventArgs e) => TogglePipMode();

        public void TogglePipMode()
        {
            if (_isPipMode)
            {
                ExitPipMode();
            }
            else
            {
                EnterPipMode();
            }
        }

        public void EnterPipMode()
        {
            if (PlayerContainer.Height <= 0 && !double.IsNaN(PlayerContainer.Height)) return;

            if (this.WindowState == WindowState.FullScreen)
            {
                Fullscreen_Core();
            }

            _isPipMode = true;

            // Önceki pencere ve yerleşim durumlarını sakla
            _prePipWindowState = this.WindowState;
            _prePipWidth = this.Width;
            _prePipHeight = this.Height;
            _prePipPosition = this.Position;
            _prePipPlayerHeight = PlayerContainer.Height;
            _prePipMinWidth = this.MinWidth;
            _prePipMinHeight = this.MinHeight;

            // Video haricindeki tüm ana uygulama arayüz elemanlarını gizle
            SidebarPanel.IsVisible = false;
            TitleBarPanel.IsVisible = false;
            RootGrid.ColumnDefinitions[0].Width = new GridLength(0);
            MainContentGrid.RowDefinitions[0].Height = new GridLength(0);
            ContentScrollViewer.IsVisible = false;
            CategoryListPanel.IsVisible = false;

            // Oynatıcıyı tüm pencereyi kaplayacak şekilde ayarla
            PlayerRowGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            PlayerRowGrid.RowDefinitions[1].Height = new GridLength(0);
            PlayerContainer.Height = double.NaN;
            PlayerContainer.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;

            // Pencereyi kompakt boyutlara küçült
            this.MinWidth = 320;
            this.MinHeight = 180;

            double pipW = 620;
            double pipH = 380;

            _isSyncingPipPosition = true;
            try
            {
                this.WindowState = WindowState.Normal;
                this.Width = pipW;
                this.Height = pipH;
                if (_playerOverlay != null)
                {
                    _playerOverlay.Width = pipW;
                    _playerOverlay.Height = pipH;
                }
            }
            finally
            {
                _isSyncingPipPosition = false;
            }

            // Ekranın tam ortasına konumlandır
            void CenterPipWindow()
            {
                try
                {
                    var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
                    if (screen != null)
                    {
                        var workArea = screen.WorkingArea;
                        int targetW = (int)(pipW * screen.Scaling);
                        int targetH = (int)(pipH * screen.Scaling);
                        int x = workArea.X + (workArea.Width - targetW) / 2;
                        int y = workArea.Y + (workArea.Height - targetH) / 2;
                        this.Position = new PixelPoint(x, y);
                        if (_playerOverlay != null)
                        {
                            _playerOverlay.Position = new PixelPoint(x, y);
                        }
                    }
                }
                catch { }
            }

            CenterPipWindow();

            this.Topmost = true;
            if (_playerOverlay != null)
            {
                if (!_playerOverlay.IsVisible)
                {
                    _playerOverlay.Show(this);
                }
                _playerOverlay.Topmost = false;
                _playerOverlay.Topmost = true;
            }

            // Layout ve WindowState geçişinden sonra da kesin ortalanmasını garanti et
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_isPipMode)
                {
                    CenterPipWindow();
                    SyncPlayerOverlayBounds();
                    if (_playerOverlay != null)
                    {
                        _playerOverlay.Topmost = false;
                        _playerOverlay.Topmost = true;
                    }
                    ShowPlayerControls();
                    ResetMediaInfoBadges();
                }
            }, Avalonia.Threading.DispatcherPriority.Loaded);

            SetPipButtonActive(true);
            SyncPlayerOverlayBounds();
            ShowPlayerControls();
            ResetMediaInfoBadges();
            ResetInactivityTimer();

            // PİP modunda En:Boy oranını ekrana sığdır (fill) yap
            if (AspectRatioText != null) AspectRatioText.Text = "Fill";
            _engine?.SetAspectRatio("fill");

            ShowToast("PiP Modu Aktif (Sürükleyip Taşıyabilirsiniz) [Shift+T]");
        }

        public void ExitPipMode(bool restoreLayout = true)
        {
            _isPipMode = false;
            this.Topmost = false;
            if (_playerOverlay != null)
            {
                _playerOverlay.Topmost = false;
                _playerOverlay.Topmost = true;
            }

            SetPipButtonActive(false);

            this.MinWidth = _prePipMinWidth > 0 ? _prePipMinWidth : 0;
            this.MinHeight = _prePipMinHeight > 0 ? _prePipMinHeight : 0;

            if (restoreLayout)
            {
                this.Width = _prePipWidth > 0 ? _prePipWidth : 1280;
                this.Height = _prePipHeight > 0 ? _prePipHeight : 775;
                if (_prePipPosition.X != 0 || _prePipPosition.Y != 0)
                {
                    this.Position = _prePipPosition;
                }
                this.WindowState = _prePipWindowState;

                SidebarPanel.IsVisible = true;
                TitleBarPanel.IsVisible = true;
                RootGrid.ColumnDefinitions[0].Width = new GridLength(200);
                MainContentGrid.RowDefinitions[0].Height = new GridLength(46);

                PlayerContainer.Height = _prePipPlayerHeight > 0 ? _prePipPlayerHeight : 450;
                PlayerContainer.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

                PlayerRowGrid.RowDefinitions[0].Height = GridLength.Auto;
                PlayerRowGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);

                ContentScrollViewer.IsVisible = true;
                ShowPlayerControls();
                ApplyGridColumnsRecalcWithRetries();
                UpdateView();
            }

            SyncPlayerOverlayBounds();
            UpdateMediaInfoBadges();

            // Normal moda dönüldüğünde Auto (12:5) oranına geri dön
            if (AspectRatioText != null) AspectRatioText.Text = "Auto";
            _engine?.SetAspectRatio("12:5");

            ShowToast("PiP Modu Kapatıldı");
        }

        private void SetPipButtonActive(bool active)
        {
            try
            {
                if (PipBtn != null)
                {
                    PipBtn.Background = active ? Brush.Parse("#3b82f6") : Brushes.Transparent;
                    ToolTip.SetTip(PipBtn, active ? "PiP Modundan Çık [Shift+T]" : "Picture-in-Picture (PiP) [Shift+T]");
                }
            }
            catch { }
        }

        internal void Fullscreen_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            Fullscreen_Core();
        }
        internal void Fullscreen_Click(object? sender, RoutedEventArgs e) => Fullscreen_Core();

        internal void TimeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isUpdatingSliderFromCode) return;
            if (_engine != null && _engine.IsSeekable && _engine.Length > 0)
            {
                long newTime = (long)((e.NewValue / 100.0) * _engine.Length);
                _engine.Time = newTime;
            }
        }

        internal void VolumeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (sender is not Slider slider) return;
            if (VolumeText != null) VolumeText.Text = $"{(int)slider.Value}%";
            if (_engine != null)
            {
                _engine.Volume = (int)slider.Value;
                if (slider.Value > 0 && _isMuted)
                {
                    _isMuted = false;
                    _engine.Mute = false;
                    IconMuteOn.IsVisible = true;
                    IconMuteOff.IsVisible = false;
                }
            }
        }

        private void AspectRatio_Click(object? sender, RoutedEventArgs e)
        {
            AudioTrackPopup.IsVisible = false;
            SubtitlePopup.IsVisible = false;
            ChannelListPanel.IsVisible = false;
            MpvSettingsPopup.IsVisible = false;
            if (AspectRatioPopup.IsVisible) { AspectRatioPopup.IsVisible = false; return; }
            PopulateAspectRatioOptions();
            AspectRatioPopup.IsVisible = true;
        }

        private static readonly (string Tag, string Label)[] _aspectRatioOptions =
        {
            ("original", "Orijinal (Varsayılan)"),
            ("16:9",     "16:9"),
            ("4:3",      "4:3"),
            ("12:5",     "Auto"),
            ("21:9",     "21:9 (Sinema)"),
            ("fill",     "Ekranı Doldur"),
        };

        private void PopulateAspectRatioOptions()
        {
            AspectRatioContainer.Children.Clear();

            string currentLabel = AspectRatioText?.Text ?? "Auto";
            var activeBg = Brush.Parse("#3b82f6");

            foreach (var (tag, label) in _aspectRatioOptions)
            {
                var t = tag;
                var lbl = label;

                bool isActive = string.Equals(lbl, currentLabel, StringComparison.OrdinalIgnoreCase)
                             || (t == "12:5" && string.Equals(currentLabel, "Auto", StringComparison.OrdinalIgnoreCase))
                             || (t == "original" && string.Equals(currentLabel, "Orijinal", StringComparison.OrdinalIgnoreCase))
                             || (t == "fill" && string.Equals(currentLabel, "Fill", StringComparison.OrdinalIgnoreCase))
                             || string.Equals(t, currentLabel, StringComparison.OrdinalIgnoreCase);

                var border = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background = isActive ? activeBg : Brushes.Transparent,
                    Padding = new Thickness(10, 6),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child = new TextBlock { Text = lbl, Foreground = Brushes.White, FontSize = 13 }
                };
                border.PointerEntered += (s, ev) =>
                {
                    if (s is Border b && !ReferenceEquals(b.Background, activeBg))
                        b.Background = Brush.Parse("#22ffffff");
                };
                border.PointerExited += (s, ev) =>
                {
                    if (s is Border b && !ReferenceEquals(b.Background, activeBg))
                        b.Background = Brushes.Transparent;
                };
                border.PointerPressed += (s, ev) =>
                {
                    ev.Handled = true;
                    ApplyAspectRatio(t, lbl);
                };
                AspectRatioContainer.Children.Add(border);
            }
        }

        private void ApplyAspectRatio(string tag, string label)
        {
            if (_engine == null) return;

            if (tag == "fill")
            {
                _engine.SetAspectRatio("fill");
                AspectRatioText.Text = "Fill";
            }
            else if (tag == "original")
            {
                _engine.SetAspectRatio(null);
                AspectRatioText.Text = "Orijinal";
            }
            else if (tag == "12:5")
            {
                _engine.SetAspectRatio("12:5");
                AspectRatioText.Text = "Auto";
            }
            else
            {
                _engine.SetAspectRatio(tag);
                AspectRatioText.Text = tag;
            }

            AspectRatioPopup.IsVisible = false;
            ShowToast($"En:Boy oranı: {AspectRatioText.Text}");
        }

        private string GetResolutionLabel()
        {
            try
            {
                if (_engine == null) return "Auto";
                var (w, h) = _engine.GetVideoSize();
                if (w > 0 && h > 0)
                {
                    double ratio = (double)w / h;

                    var knownRatios = new (string Label, double Value)[]
                    {
                        ("4:3",    4.0  / 3.0),
                        ("16:10",  16.0 / 10.0),
                        ("16:9",   16.0 / 9.0),
                        ("21:9",   21.0 / 9.0),
                        ("1.85:1", 1.85),
                        ("2.35:1", 2.35),
                        ("1:1",    1.0),
                    };

                    const double tolerance = 0.03;
                    foreach (var (label, value) in knownRatios)
                        if (Math.Abs(ratio - value) < tolerance) return label;

                    uint g = Gcd(w, h);
                    uint rw = w / g, rh = h / g;
                    return $"{rw}:{rh}";
                }
            }
            catch { }
            return "Auto";
        }

        private static uint Gcd(uint a, uint b) => b == 0 ? a : Gcd(b, a % b);

        private void Fullscreen_Core()
        {
            if (this.WindowState == WindowState.FullScreen)
            {
                this.WindowState = WindowState.Normal;
                SidebarPanel.IsVisible = true;
                TitleBarPanel.IsVisible = true;
                RootGrid.ColumnDefinitions[0].Width = new GridLength(200);
                MainContentGrid.RowDefinitions[0].Height = new GridLength(46);
                PlayerContainer.Height = 450;
                PlayerContainer.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

                PlayerRowGrid.RowDefinitions[0].Height = GridLength.Auto;
                PlayerRowGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);

                ContentScrollViewer.IsVisible = true;
                _inactivityTimer?.Stop();
                ShowPlayerControls();
                ApplyGridColumnsRecalcWithRetries();

                UpdateView();
            }
            else
            {
                this.WindowState = WindowState.FullScreen;
                SidebarPanel.IsVisible = false;
                TitleBarPanel.IsVisible = false;
                RootGrid.ColumnDefinitions[0].Width = new GridLength(0);
                MainContentGrid.RowDefinitions[0].Height = new GridLength(0);
                PlayerContainer.Height = double.NaN;
                PlayerContainer.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;

                PlayerRowGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
                PlayerRowGrid.RowDefinitions[1].Height = new GridLength(0);

                ContentScrollViewer.IsVisible = false;
                CategoryListPanel.IsVisible = false;
                ResetInactivityTimer();
            }
        }

        private void ResetInactivityTimer()
        {
            if (PlayerContainer.Height <= 0)
            {
                _inactivityTimer?.Stop();
                return;
            }

            if (_inactivityTimer == null)
            {
                _inactivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                _inactivityTimer.Tick += (s, ev) =>
                {
                    HidePlayerControls();
                    _inactivityTimer!.Stop();
                };
            }

            _inactivityTimer.Stop();
            _inactivityTimer.Start();
        }

        private void ShowPlayerControls()
        {
            PlayerTopBar.IsVisible = true;
            PlayerBottomBar.IsVisible = true;

            var cursor = new Cursor(StandardCursorType.Arrow);
            this.Cursor = cursor;
            if (_playerOverlay != null) _playerOverlay.Cursor = cursor;
        }

        private void HidePlayerControls()
        {
            if (ChannelListPanel.IsVisible || AudioTrackPopup.IsVisible ||
                SubtitlePopup.IsVisible || AspectRatioPopup.IsVisible ||
                MpvSettingsPopup.IsVisible) return;

            PlayerTopBar.IsVisible = false;
            PlayerBottomBar.IsVisible = false;

            if (this.WindowState == WindowState.FullScreen)
            {
                var cursor = new Cursor(StandardCursorType.None);
                this.Cursor = cursor;
                if (_playerOverlay != null) _playerOverlay.Cursor = cursor;
            }
        }

        internal void PlayerContainer_PointerMoved(object? sender, PointerEventArgs e)
        {
            ShowPlayerControls();
            ResetInactivityTimer();
        }

        internal void PlayerOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (ChannelListPanel.IsVisible || AudioTrackPopup.IsVisible ||
                SubtitlePopup.IsVisible || AspectRatioPopup.IsVisible ||
                MpvSettingsPopup.IsVisible)
            {
                ChannelListPanel.IsVisible = false;
                AudioTrackPopup.IsVisible = false;
                SubtitlePopup.IsVisible = false;
                AspectRatioPopup.IsVisible = false;
                MpvSettingsPopup.IsVisible = false;
                return;
            }

            if (_isPipMode)
            {
                if (e.ClickCount == 2)
                {
                    TogglePipMode();
                    e.Handled = true;
                    return;
                }

                var point = e.GetCurrentPoint(_playerOverlay ?? (Visual)this);
                if (point.Properties.IsLeftButtonPressed && !IsPointerOverControlBar(e))
                {
                    _playerOverlay?.BeginMoveDrag(e);
                    e.Handled = true;
                    return;
                }
            }
            else
            {
                if (e.ClickCount == 2 && e.Source is not Slider && !IsPointerOverControlBar(e))
                    Fullscreen_Core();
            }
        }

        private bool IsPointerOverControlBar(PointerEventArgs e)
        {
            try
            {
                double rootHeight = PlayerOverlayRoot.Bounds.Height;
                var posInRoot = e.GetPosition(PlayerOverlayRoot);

                if (PlayerTopBar.IsVisible)
                {
                    double topBarHeight = PlayerTopBar.Bounds.Height;
                    if (posInRoot.Y <= topBarHeight) return true;
                }

                if (PlayerBottomBar.IsVisible)
                {
                    double bottomBarHeight = PlayerBottomBar.Bounds.Height;
                    if (posInRoot.Y >= rootHeight - bottomBarHeight) return true;
                }
            }
            catch { }
            return false;
        }

        internal void Window_KeyDown(object? sender, KeyEventArgs e)
        {
            if (PlayerContainer.Height == 0) return;
            if (e.Source is TextBox) return;

            ShowPlayerControls();
            ResetInactivityTimer();

            switch (e.Key)
            {
                case Key.Space:
                    PlayPause_Core(); e.Handled = true; break;
                case Key.F:
                    if (_isPipMode) ExitPipMode(restoreLayout: true);
                    Fullscreen_Core(); e.Handled = true; break;
                case Key.Escape:
                    if (this.WindowState == WindowState.FullScreen)
                    { Fullscreen_Core(); e.Handled = true; }
                    else if (_isPipMode)
                    { TogglePipMode(); e.Handled = true; }
                    break;
                case Key.M:
                    Mute_Core(); e.Handled = true; break;
                case Key.Up:
                    if (_isLiveContent) _ = DoPrevChannel();
                    else if (_engine != null) VolumeSlider.Value = Math.Min(200, VolumeSlider.Value + 5);
                    e.Handled = true; break;
                case Key.Down:
                    if (_isLiveContent) _ = DoNextChannel();
                    else if (_engine != null) VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
                    e.Handled = true; break;
                case Key.Left:
                    if (!_isLiveContent) { SkipBack_Core(); e.Handled = true; }
                    break;
                case Key.Right:
                    if (!_isLiveContent) { SkipForward_Core(); e.Handled = true; }
                    break;
                case Key.G:
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) AdjustSubtitleDelay(-500);
                    else AdjustSubtitleDelay(-50);
                    e.Handled = true;
                    break;
                case Key.H:
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) AdjustSubtitleDelay(500);
                    else AdjustSubtitleDelay(50);
                    e.Handled = true;
                    break;
                case Key.Z:
                    SetSubtitleDelay(0, showToast: true);
                    e.Handled = true;
                    break;
                case Key.J:
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) AdjustAudioDelay(-500);
                    else AdjustAudioDelay(-50);
                    e.Handled = true;
                    break;
                case Key.K:
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) AdjustAudioDelay(500);
                    else AdjustAudioDelay(50);
                    e.Handled = true;
                    break;
                case Key.L:
                    SetAudioDelay(0, showToast: true);
                    e.Handled = true;
                    break;
                case Key.T:
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    {
                        TogglePipMode();
                        e.Handled = true;
                    }
                    break;
                case Key.F1:
                    ApplyPicturePreset("natural");
                    e.Handled = true;
                    break;
                case Key.F2:
                    ApplyPicturePreset("vivid");
                    e.Handled = true;
                    break;
                case Key.F3:
                    ApplyPicturePreset("sports");
                    e.Handled = true;
                    break;
                case Key.F4:
                    ApplyPicturePreset("cinema");
                    e.Handled = true;
                    break;
            }
        }

        private void ContentScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            UpdateCustomScrollThumb();
            ShowCustomScrollThumbTemporarily();

            if (_isLoadingMore) return;
            var sv = ContentScrollViewer;
            double scrollPos = sv.Offset.Y + sv.Viewport.Height;
            double totalHeight = sv.Extent.Height;
            if (totalHeight > 0 && scrollPos >= totalHeight * 0.8)
                LoadMoreItems();
        }

        private DispatcherTimer? _scrollThumbFadeTimer;
        private bool _isDraggingScrollThumb = false;
        private bool _isPointerOverScrollThumb = false;
        private double _dragStartPointerY = 0;
        private double _dragStartOffsetY = 0;

        private const double ThumbWidthNormal = 6;
        private const double ThumbWidthExpanded = 13;

        private void SetCustomScrollThumbExpanded(bool expanded) =>
            CustomScrollThumbVisual.Width = expanded ? ThumbWidthExpanded : ThumbWidthNormal;

        private void UpdateCustomScrollThumb()
        {
            try
            {
                var sv = ContentScrollViewer;
                double extent = sv.Extent.Height;
                double viewport = sv.Viewport.Height;

                if (extent <= viewport || viewport <= 0)
                {
                    CustomScrollThumb.IsVisible = false;
                    return;
                }

                CustomScrollThumb.IsVisible = true;

                const double thumbHeight = 80;
                const double topMargin = 24;
                const double bottomMargin = 24;

                double trackHeight = viewport - topMargin - bottomMargin;
                if (trackHeight < thumbHeight) trackHeight = thumbHeight;

                double maxOffset = extent - viewport;
                double scrollRatio = maxOffset > 0 ? sv.Offset.Y / maxOffset : 0;
                scrollRatio = Math.Clamp(scrollRatio, 0, 1);

                double thumbTravel = Math.Max(0, trackHeight - thumbHeight);
                double thumbY = topMargin + (thumbTravel * scrollRatio);

                CustomScrollThumb.Margin = new Thickness(0, thumbY, 3, 0);
            }
            catch { }
        }

        private void ShowCustomScrollThumbTemporarily()
        {
            if (!CustomScrollThumb.IsVisible) return;

            CustomScrollThumb.Opacity = 1;

            if (_scrollThumbFadeTimer == null)
            {
                _scrollThumbFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                _scrollThumbFadeTimer.Tick += (s, e) =>
                {
                    _scrollThumbFadeTimer!.Stop();
                    if (!_isDraggingScrollThumb && !_isPointerOverScrollThumb)
                        CustomScrollThumb.Opacity = 0;
                };
            }

            _scrollThumbFadeTimer.Stop();
            _scrollThumbFadeTimer.Start();
        }

        private void CustomScrollThumb_PointerEntered(object? sender, PointerEventArgs e)
        {
            _isPointerOverScrollThumb = true;
            CustomScrollThumb.Opacity = 1;
            SetCustomScrollThumbExpanded(true);
            _scrollThumbFadeTimer?.Stop();
        }

        private void CustomScrollThumb_PointerExited(object? sender, PointerEventArgs e)
        {
            _isPointerOverScrollThumb = false;
            if (!_isDraggingScrollThumb)
            {
                SetCustomScrollThumbExpanded(false);
                ShowCustomScrollThumbTemporarily();
            }
        }

        private void CustomScrollThumb_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(CustomScrollThumb).Properties.IsLeftButtonPressed) return;

            _isDraggingScrollThumb = true;
            _dragStartPointerY = e.GetPosition(ContentScrollViewer).Y;
            _dragStartOffsetY = ContentScrollViewer.Offset.Y;
            e.Pointer.Capture(CustomScrollThumb);
            CustomScrollThumb.Opacity = 1;
            SetCustomScrollThumbExpanded(true);
            _scrollThumbFadeTimer?.Stop();
            e.Handled = true;
        }

        private void CustomScrollThumb_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isDraggingScrollThumb) return;

            var sv = ContentScrollViewer;
            double extent = sv.Extent.Height;
            double viewport = sv.Viewport.Height;
            double maxOffset = extent - viewport;
            if (maxOffset <= 0) return;

            const double thumbHeight = 40;
            const double topMargin = 24;
            const double bottomMargin = 24;
            double trackHeight = Math.Max(thumbHeight, viewport - topMargin - bottomMargin);
            double thumbTravel = Math.Max(1, trackHeight - thumbHeight);

            double currentPointerY = e.GetPosition(ContentScrollViewer).Y;
            double deltaPointer = currentPointerY - _dragStartPointerY;

            double deltaOffset = deltaPointer * (maxOffset / thumbTravel);
            double newOffset = Math.Clamp(_dragStartOffsetY + deltaOffset, 0, maxOffset);

            sv.Offset = new Vector(sv.Offset.X, newOffset);
            e.Handled = true;
        }

        private void CustomScrollThumb_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_isDraggingScrollThumb) return;
            _isDraggingScrollThumb = false;
            e.Pointer.Capture(null);

            if (!_isPointerOverScrollThumb)
            {
                SetCustomScrollThumbExpanded(false);
                ShowCustomScrollThumbTemporarily();
            }
        }

        private void LoadMoreItems()
        {
            if (_isLoadingMore) return;
            _isLoadingMore = true;
            try
            {
                if (_viewState == "Content" && _allFilteredContents.Count > _loadedCount)
                {
                    var nextBatch = _allFilteredContents.Skip(_loadedCount).Take(PAGE_SIZE).ToList();
                    _loadedCount += nextBatch.Count;

                    if (_currentTab == "Canlı")
                        foreach (var item in nextBatch) _displayContents.Add(item);
                    else
                        foreach (var item in nextBatch) _displayVodContents.Add(item);

                    _ = LoadLogosForChannelsAsync(nextBatch);
                    if (_currentTab != "Canlı") _ = LoadTmdbPostersForChannels(nextBatch);
                }
                else if (_viewState == "Shows" && _allFilteredCards.Count > _loadedCount)
                {
                    var nextBatch = _allFilteredCards.Skip(_loadedCount).Take(PAGE_SIZE).ToList();
                    _loadedCount += nextBatch.Count;

                    foreach (var item in nextBatch) _displaySeriesCards.Add(item);

                    _ = LoadTmdbPostersForCards(nextBatch);
                }
            }
            finally
            {
                _isLoadingMore = false;

                if (_viewState == "Content" && _loadedCount >= _allFilteredContents.Count && _allFilteredContents.Count > 0)
                    _contentCache[$"{_currentTab}_{_favoriCategoryType}_{_currentCategory}"] = _allFilteredContents;
                else if (_viewState == "Shows" && _loadedCount >= _allFilteredCards.Count && _allFilteredCards.Count > 0)
                    _seriesCardCache[$"Dizi_{_currentCategory}"] = _allFilteredCards;
            }
        }

        private void ToggleChannelList_Click(object? sender, RoutedEventArgs e)
        {
            AudioTrackPopup.IsVisible = false;
            SubtitlePopup.IsVisible = false;
            AspectRatioPopup.IsVisible = false;
            MpvSettingsPopup.IsVisible = false;

            if (ChannelListPanel.IsVisible) { ChannelListPanel.IsVisible = false; return; }
            PopulatePlayerChannelList();
            ChannelListPanel.IsVisible = true;
        }

        internal void CloseChannelList_Click(object? sender, RoutedEventArgs e) =>
            ChannelListPanel.IsVisible = false;

        private void PopulatePlayerChannelList()
        {
            PlayerChannelListContainer.Children.Clear();

            var list = _displayContents.ToList();
            if (list.Count == 0)
                list = _allChannels.Where(c => !c.IsHidden && c.Type == "Canlı").ToList();

            Border? activeBorder = null;
            string? currentGroup = null;
            foreach (var ch in list)
            {
                if (ch.Group != currentGroup)
                {
                    currentGroup = ch.Group;
                    PlayerChannelListContainer.Children.Add(new TextBlock
                    {
                        Text = ch.Group.ToUpper(),
                        Foreground = Brush.Parse("#888"),
                        FontSize = 10,
                        FontWeight = FontWeight.Bold,
                        Margin = new Thickness(12, 12, 12, 6)
                    });
                }

                bool isActive = _currentChannel != null && ch.Url == _currentChannel.Url;
                var channel = ch;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

                var nameText = new TextBlock
                {
                    Text = ch.Name,
                    Foreground = isActive ? Brush.Parse("#20c70e") : Brushes.White,
                    FontSize = 13,
                    FontWeight = isActive ? FontWeight.Bold : FontWeight.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                Grid.SetColumn(nameText, 0);
                grid.Children.Add(nameText);

                if (isActive)
                {
                    var badge = new TextBlock
                    {
                        Text = "● İZLENİYOR",
                        Foreground = Brush.Parse("#20c70e"),
                        FontSize = 9,
                        FontWeight = FontWeight.Bold,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(badge, 1);
                    grid.Children.Add(badge);
                }

                var activeBg = Brush.Parse("#33a855f7");
                var border = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = isActive ? activeBg : Brushes.Transparent,
                    Padding = new Thickness(12, 10),
                    Margin = new Thickness(0, 1),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child = grid
                };
                border.PointerEntered += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brush.Parse("#22ffffff"); };
                border.PointerExited += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brushes.Transparent; };
                border.PointerPressed += async (s, ev) =>
                {
                    ev.Handled = true;
                    _currentChannel = channel;
                    PlayerTitleText.Text = channel.Name;
                    await PlayChannel(channel.Url);
                    PopulatePlayerChannelList();
                };
                PlayerChannelListContainer.Children.Add(border);

                if (isActive) activeBorder = border;
            }

            if (activeBorder != null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    activeBorder.BringIntoView();
                }, DispatcherPriority.Loaded);
            }
        }

        private async void PlayerChannelSelect_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Channel channel) return;
            _currentChannel = channel;
            PlayerTitleText.Text = channel.Name;
            await PlayChannel(channel.Url);
            PopulatePlayerChannelList();
        }

        private void AudioTrack_Click(object? sender, RoutedEventArgs e)
        {
            ChannelListPanel.IsVisible = false;
            SubtitlePopup.IsVisible = false;
            AspectRatioPopup.IsVisible = false;
            MpvSettingsPopup.IsVisible = false;

            if (AudioTrackPopup.IsVisible) { AudioTrackPopup.IsVisible = false; return; }
            PopulateAudioTracks(AudioTrackContainer, closeOnSelect: true);
            AudioTrackPopup.IsVisible = true;
        }

        private void Subtitle_Click(object? sender, RoutedEventArgs e)
        {
            ChannelListPanel.IsVisible = false;
            AudioTrackPopup.IsVisible = false;
            AspectRatioPopup.IsVisible = false;
            MpvSettingsPopup.IsVisible = false;

            if (SubtitlePopup.IsVisible) { SubtitlePopup.IsVisible = false; return; }
            PopulateSubtitles(SubtitleContainer);
            PopulateAudioTracks(AudioTrackContainerVod);
            SubtitlePopup.IsVisible = true;
        }

        private void PopulateAudioTracks(StackPanel container, bool closeOnSelect = false)
        {
            container.Children.Clear();

            var tracks = _engine?.AudioTracks;
            if (_engine == null || tracks == null || tracks.Count == 0)
            {
                container.Children.Add(new TextBlock
                {
                    Text = "Ses parçası bulunamadı",
                    Foreground = Brush.Parse("#888"),
                    FontSize = 12,
                    Margin = new Thickness(10, 6)
                });
                UpdateAudioDelayDisplay();
                return;
            }

            int activeId = _engine.ActiveAudioTrackId;
            foreach (var track in tracks)
            {
                var trackName = track.Name;
                var trackId = track.Id;
                bool isActive = trackId == activeId;
                var activeBg = Brush.Parse("#3b82f6");
                var border = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background = isActive ? activeBg : Brushes.Transparent,
                    Padding = new Thickness(10, 6),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child = new TextBlock { Text = trackName, Foreground = Brushes.White, FontSize = 13 }
                };
                border.PointerEntered += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brush.Parse("#22ffffff"); };
                border.PointerExited += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brushes.Transparent; };
                border.PointerPressed += (s, ev) =>
                {
                    ev.Handled = true;
                    if (_engine != null)
                    {
                        _engine.SetAudioTrack(trackId);
                        ShowToast($"Ses: {trackName}");
                        if (closeOnSelect)
                        {
                            AudioTrackPopup.IsVisible = false;
                        }
                        else
                        {
                            PopulateAudioTracks(container, closeOnSelect: false);
                        }
                    }
                };
                container.Children.Add(border);
            }

            UpdateAudioDelayDisplay();
        }

        private void PopulateSubtitles(StackPanel container)
        {
            container.Children.Clear();

            if (_engine == null)
            {
                container.Children.Add(new TextBlock
                {
                    Text = "Oynatıcı hazır değil",
                    Foreground = Brush.Parse("#888"),
                    FontSize = 12,
                    Margin = new Thickness(10, 6)
                });
                return;
            }

            var activeBg = Brush.Parse("#3b82f6");
            bool offActive = _engine.ActiveSubtitleTrackId <= 0;
            var offBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = offActive ? activeBg : Brushes.Transparent,
                Padding = new Thickness(10, 6),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Child = new TextBlock { Text = "Kapalı", Foreground = Brushes.White, FontSize = 13 }
            };
            offBorder.PointerEntered += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brush.Parse("#22ffffff"); };
            offBorder.PointerExited += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brushes.Transparent; };
            offBorder.PointerPressed += (s, ev) =>
            {
                ev.Handled = true;
                _engine?.SetSubtitleTrack(-1);
                ShowToast("Altyazı kapatıldı");
                PopulateSubtitles(container);
                PopulateAudioTracks(AudioTrackContainerVod, closeOnSelect: false);
            };
            container.Children.Add(offBorder);

            var subs = _engine.SubtitleTracks;
            if (subs.Count == 0)
            {
                container.Children.Add(new TextBlock
                {
                    Text = "Bu içerikte altyazı yok",
                    Foreground = Brush.Parse("#888"),
                    FontSize = 11,
                    Margin = new Thickness(10, 6)
                });
                return;
            }

            int activeSpu = _engine.ActiveSubtitleTrackId;
            foreach (var sub in subs)
            {
                var subName = sub.Name;
                var subId = sub.Id;
                bool isActive = subId == activeSpu;
                var border = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background = isActive ? activeBg : Brushes.Transparent,
                    Padding = new Thickness(10, 6),
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child = new TextBlock { Text = subName, Foreground = Brushes.White, FontSize = 13 }
                };
                border.PointerEntered += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brush.Parse("#22ffffff"); };
                border.PointerExited += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brushes.Transparent; };
                border.PointerPressed += (s, ev) =>
                {
                    ev.Handled = true;
                    if (_engine != null)
                    {
                        _engine.SetSubtitleTrack(subId);
                        ShowToast($"Altyazı: {subName}");
                        PopulateSubtitles(container);
                        PopulateAudioTracks(AudioTrackContainerVod, closeOnSelect: false);
                    }
                };
                container.Children.Add(border);
            }

            UpdateSubtitleDelayDisplay();
        }

        internal void SubDelayMinus500_Click(object? sender, PointerPressedEventArgs e) { e.Handled = true; AdjustSubtitleDelay(-500); }
        internal void SubDelayMinus100_Click(object? sender, PointerPressedEventArgs e) { e.Handled = true; AdjustSubtitleDelay(-100); }
        internal void SubDelayMinus50_Click(object? sender, PointerPressedEventArgs e) { e.Handled = true; AdjustSubtitleDelay(-50); }
        internal void SubDelayPlus50_Click(object? sender, PointerPressedEventArgs e) { e.Handled = true; AdjustSubtitleDelay(50); }
        internal void SubDelayPlus100_Click(object? sender, PointerPressedEventArgs e) { e.Handled = true; AdjustSubtitleDelay(100); }
        internal void SubDelayPlus500_Click(object? sender, PointerPressedEventArgs e) { e.Handled = true; AdjustSubtitleDelay(500); }
        internal void SubDelayReset_Click(object? sender, PointerPressedEventArgs e) { e.Handled = true; SetSubtitleDelay(0, showToast: true); }

        internal void AudioDelayMinus500_Click(object? sender, PointerPressedEventArgs e) { e.Handled = true; AdjustAudioDelay(-500); }
        internal void AudioDelayMinus100_Click(object? sender, PointerPressedEventArgs e) { e.Handled = true; AdjustAudioDelay(-100); }
        internal void AudioDelayMinus50_Click(object? sender, PointerPressedEventArgs e) { e.Handled = true; AdjustAudioDelay(-50); }
        internal void AudioDelayPlus50_Click(object? sender, PointerPressedEventArgs e) { e.Handled = true; AdjustAudioDelay(50); }
        internal void AudioDelayPlus100_Click(object? sender, PointerPressedEventArgs e) { e.Handled = true; AdjustAudioDelay(100); }
        internal void AudioDelayPlus500_Click(object? sender, PointerPressedEventArgs e) { e.Handled = true; AdjustAudioDelay(500); }
        internal void AudioDelayReset_Click(object? sender, PointerPressedEventArgs e) { e.Handled = true; SetAudioDelay(0, showToast: true); }

        private void AdjustAudioDelay(int deltaMs)
        {
            SetAudioDelay(_currentAudioDelayMs + deltaMs, showToast: true);
        }

        private void SetAudioDelay(int delayMs, bool showToast = false)
        {
            _currentAudioDelayMs = delayMs;
            if (_engine != null)
            {
                _engine.SetAudioDelay(delayMs);
            }
            UpdateAudioDelayDisplay();
            if (showToast)
            {
                string sign = delayMs > 0 ? "+" : "";
                ShowToast($"Ses Senkron: {sign}{delayMs} ms");
            }
        }

        private void UpdateAudioDelayDisplay()
        {
            try
            {
                if (AudioDelayText != null)
                {
                    string sign = _currentAudioDelayMs > 0 ? "+" : "";
                    AudioDelayText.Text = $"{sign}{_currentAudioDelayMs} ms";
                }
            }
            catch { }
        }

        private void AdjustSubtitleDelay(int deltaMs)
        {
            SetSubtitleDelay(_currentSubtitleDelayMs + deltaMs, showToast: true);
        }

        private void SetSubtitleDelay(int delayMs, bool showToast = false)
        {
            _currentSubtitleDelayMs = delayMs;
            if (_engine != null)
            {
                _engine.SetSubtitleDelay(delayMs);
            }
            UpdateSubtitleDelayDisplay();
            if (showToast)
            {
                string sign = delayMs > 0 ? "+" : "";
                ShowToast($"Altyazı Senkron: {sign}{delayMs} ms");
            }
        }

        private void UpdateSubtitleDelayDisplay()
        {
            try
            {
                if (SubtitleDelayText != null)
                {
                    string sign = _currentSubtitleDelayMs > 0 ? "+" : "";
                    SubtitleDelayText.Text = $"{sign}{_currentSubtitleDelayMs} ms";
                }
            }
            catch { }
        }

        internal void CloseBtn_PointerEntered(object? sender, PointerEventArgs e)
        {
            if (sender is Border b)
            {
                b.Background = Brush.Parse("#BBef4444");
                b.BorderBrush = Brush.Parse("#88ef4444");
            }
        }

        internal void CloseBtn_PointerExited(object? sender, PointerEventArgs e)
        {
            if (sender is Border b)
            {
                b.Background = Brush.Parse("#18ffffff");
                b.BorderBrush = Brush.Parse("#2Affffff");
            }
        }

        // ----- Yayın bilgi rozetleri -----
        private void ResetMediaInfoBadges()
        {
            MediaInfoBadgesPanel.IsVisible = false;
            ResolutionBadge.IsVisible = false;
            FpsBadge.IsVisible = false;
            BitrateBadge.IsVisible = false;
            VideoCodecBadge.IsVisible = false;
            AudioCodecBadge.IsVisible = false;
            AudioChannelsBadge.IsVisible = false;
        }

        private void UpdateMediaInfoBadges()
        {
            try
            {
                if (_isPipMode)
                {
                    ResetMediaInfoBadges();
                    return;
                }

                if (_engine == null) return;
                var info = _engine.GetMediaInfo();
                bool anyVisible = false;

                if (info.Width > 0 && info.Height > 0)
                {
                    string resText = $"{info.Width}x{info.Height}";
                    if (ResolutionBadgeText.Text != resText) ResolutionBadgeText.Text = resText;
                    if (!ResolutionBadge.IsVisible) ResolutionBadge.IsVisible = true;
                    anyVisible = true;
                }
                else if (ResolutionBadge.IsVisible) ResolutionBadge.IsVisible = false;

                if (info.Fps > 0)
                {
                    string fpsText = $"{info.Fps:0.##} FPS";
                    if (FpsBadgeText.Text != fpsText) FpsBadgeText.Text = fpsText;
                    if (!FpsBadge.IsVisible) FpsBadge.IsVisible = true;
                    anyVisible = true;
                }
                else if (FpsBadge.IsVisible) FpsBadge.IsVisible = false;

                // ── Bitrate Rozeti Gösterimi (SADECE Canlı İçeriklerde Gösterilir) ──
                if (_isLiveContent)
                {
                    double bitrate = info.BitrateKbps > 0 ? info.BitrateKbps : _engine.GetBitrateKbps();
                    if (bitrate > 0)
                    {
                        string bitText = bitrate >= 1000 ? $"{(bitrate / 1000.0):0.#} Mb/s" : $"{(int)bitrate} kb/s";
                        if (BitrateBadgeText.Text != bitText) BitrateBadgeText.Text = bitText;
                        if (!BitrateBadge.IsVisible) BitrateBadge.IsVisible = true;
                        anyVisible = true;
                    }
                    else if (BitrateBadge.IsVisible)
                    {
                        BitrateBadge.IsVisible = false;
                    }
                }
                else if (BitrateBadge.IsVisible)
                {
                    // Film / Dizi (VOD) içeriklerinde bitrate kesinlikle gizlenir
                    BitrateBadge.IsVisible = false;
                }

                if (!string.IsNullOrEmpty(info.VideoCodec))
                {
                    if (VideoCodecBadgeText.Text != info.VideoCodec) VideoCodecBadgeText.Text = info.VideoCodec;
                    if (!VideoCodecBadge.IsVisible) VideoCodecBadge.IsVisible = true;
                    anyVisible = true;
                }
                else if (VideoCodecBadge.IsVisible) VideoCodecBadge.IsVisible = false;

                if (!string.IsNullOrEmpty(info.AudioCodec))
                {
                    if (AudioCodecBadgeText.Text != info.AudioCodec) AudioCodecBadgeText.Text = info.AudioCodec;
                    if (!AudioCodecBadge.IsVisible) AudioCodecBadge.IsVisible = true;
                    anyVisible = true;
                }
                else if (AudioCodecBadge.IsVisible) AudioCodecBadge.IsVisible = false;

                if (info.AudioChannels > 0)
                {
                    string chText = info.AudioChannels switch
                    {
                        1 => "Mono",
                        2 => "2.0",
                        6 => "5.1",
                        8 => "7.1",
                        _ => $"{info.AudioChannels}ch"
                    };
                    if (AudioChannelsBadgeText.Text != chText) AudioChannelsBadgeText.Text = chText;
                    if (!AudioChannelsBadge.IsVisible) AudioChannelsBadge.IsVisible = true;
                    anyVisible = true;
                }
                else if (AudioChannelsBadge.IsVisible) AudioChannelsBadge.IsVisible = false;

                if (MediaInfoBadgesPanel.IsVisible != anyVisible)
                    MediaInfoBadgesPanel.IsVisible = anyVisible;
            }
            catch { }
        }
    }
}