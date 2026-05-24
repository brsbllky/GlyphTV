// ============================================================
// MainWindow.Player.cs
// ============================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow
    {
        private async void Content_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Channel channel) return;

            _currentChannel = channel;
            PlayerTitleText.Text = channel.Name;
            PlayerContainer.Background = Brushes.Black;
            PlayerContainer.Height = 400;
            ContentScrollViewer.Margin = new Thickness(28, 420, 28, 24);

            _resumePosition = 0;
            await PlayChannel(channel.Url);
        }

        private async void Content_Resume_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Channel channel) return;

            _currentChannel = channel;
            PlayerTitleText.Text = channel.Name;
            PlayerContainer.Background = Brushes.Black;
            PlayerContainer.Height = 400;
            ContentScrollViewer.Margin = new Thickness(28, 420, 28, 24);

            _resumePosition = 0;
            var hist = _watchHistory.FirstOrDefault(h => h.Url == channel.Url);
            if (hist != null && hist.Position > 5000)
            {
                _resumePosition = hist.Position;
                var ts = TimeSpan.FromMilliseconds(hist.Position);
                ShowToast($"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2} konumundan devam ediliyor");
            }

            await PlayChannel(channel.Url);
        }

        private void ClosePlayer_Click(object? sender, RoutedEventArgs e)
        {
            SaveCurrentWatchPosition();

            if (_currentChannel != null && _currentChannel.Type != "Canlı")
            {
                _currentChannel.HasResume = _watchHistory.Any(h => h.Url == _currentChannel.Url && h.Position > 5000);

                if (_currentChannel.Type == "Dizi" && SeriesContentGrid.IsVisible &&
                    SeriesContentGrid.ItemsSource is List<SeriesCard> cards)
                {
                    var card = cards.FirstOrDefault(c => c.ShowName == _currentChannel.ShowName);
                    if (card != null) card.HasResume = _currentChannel.HasResume;
                }
            }

            if (_mediaPlayer != null)
            {
                _mediaPlayer.Stop();
                _mediaPlayer.Media?.Dispose();
            }
            MainVideoView.MediaPlayer = null;

            ChannelListPanel.IsVisible = false;
            AudioTrackPopup.IsVisible = false;
            SubtitlePopup.IsVisible = false;
            AspectRatioPopup.IsVisible = false;

            if (this.WindowState == WindowState.FullScreen)
                Fullscreen_Click(sender, e);

            PlayerContainer.Height = 0;
            PlayerContainer.Background = Brushes.Transparent;
            ContentScrollViewer.Margin = new Thickness(28, 24, 28, 24);

            Dispatcher.UIThread.Post(() => TrimProcessMemory(), DispatcherPriority.Background);
        }

        private void SaveCurrentWatchPosition()
        {
            try
            {
                if (_currentChannel == null || _mediaPlayer == null) return;
                if (_currentChannel.Type == "Canlı") return;

                long pos = _mediaPlayer.Time;
                long dur = _mediaPlayer.Length;
                if (pos > 0) UpsertWatchHistory(_currentChannel, pos, dur);
            }
            catch { }
        }

        private async Task PlayChannel(string url)
        {
            try
            {
                if (!_isVlcInitialized) InitializePlayer();
                if (_mediaPlayer == null || _libVLC == null) return;

                // VideoView'a bağlı değilse bağla
                if (MainVideoView.MediaPlayer == null)
                    MainVideoView.MediaPlayer = _mediaPlayer;

                // Önceki medyayı temizle
                _mediaPlayer.Media?.Dispose();

                CurrentTimeText.Text = "00:00:00";
                TotalTimeText.Text = "00:00:00";
                TimeSlider.Value = 0;
                PlayerTitleText.Text = _currentChannel?.Name ?? "";

                _isLiveContent = _currentChannel?.Type == "Canlı";
                ConfigurePlayerUIForContentType();

                var media = new Media(_libVLC, new Uri(url));

                if (_resumePosition > 0)
                {
                    double startSec = _resumePosition / 1000.0;
                    media.AddOption($":start-time={startSec.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }

                _mediaPlayer.Play(media);
                _resumePosition = 0;
                PlayPauseIcon.Text = "⏸";
                AudioTrackPopup.IsVisible = false;
                SubtitlePopup.IsVisible = false;
                AspectRatioPopup.IsVisible = false;

                await Task.Delay(800);
                Dispatcher.UIThread.Post(() =>
                {
                    if (AspectRatioText != null)
                        AspectRatioText.Text = GetResolutionLabel();
                });
            }
            catch (Exception ex) { PlayerTitleText.Text = "HATA: " + ex.Message; }
        }

        private void ConfigurePlayerUIForContentType()
        {
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

                // Canlı'ya geçerken hızı sıfırla
                if (_mediaPlayer != null) _mediaPlayer.SetRate(1.0f);
                _speedIndex = 0;
                SpeedBtnText.Text = "1x";
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

                // Yeni içerik açılırken hızı 1x'e sıfırla
                _speedIndex = 0;
                SpeedBtnText.Text = "1x";
            }
        }

        private static readonly float[]  _speedSteps       = { 1.0f, 1.25f, 1.5f, 1.75f, 2.0f };
        private static readonly string[] _speedStepLabels  = { "1x", "1.25x", "1.5x", "1.75x", "2x" };
        private int _speedIndex = 0;

        private void Speed_Click(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;

            _speedIndex = (_speedIndex + 1) % _speedSteps.Length;

            float  rate  = _speedSteps[_speedIndex];
            string label = _speedStepLabels[_speedIndex];

            _mediaPlayer.SetRate(rate);
            SpeedBtnText.Text = label;
            ShowToast($"Oynatma hızı: {label}");
        }

        private async void NextEpisode_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentChannel == null || _currentChannel.Type != "Dizi") return;

            var nextEpisode = FindNextEpisode(_currentChannel);
            if (nextEpisode == null) { ShowToast("Sonraki bölüm bulunamadı."); return; }

            var previousChannel = _currentChannel;
            SaveCurrentWatchPosition();

            if (previousChannel != null)
            {
                previousChannel.HasResume = _watchHistory.Any(h => h.Url == previousChannel.Url && h.Position > 5000);

                if (SeriesContentGrid.IsVisible && SeriesContentGrid.ItemsSource is List<SeriesCard> cards)
                {
                    var card = cards.FirstOrDefault(c => c.ShowName == previousChannel.ShowName);
                    if (card != null) card.HasResume = previousChannel.HasResume;
                }
            }

            _currentChannel = nextEpisode;
            PlayerTitleText.Text = nextEpisode.Name;
            _resumePosition = 0;

            await PlayChannel(nextEpisode.Url);
            ShowToast($"Sonraki bölüm: {nextEpisode.Name}");
        }

        private Channel? FindNextEpisode(Channel current)
        {
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

        private async void PrevChannel_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentChannel == null) return;
            int index = _displayContents.IndexOf(_currentChannel);
            if (index > 0)
            {
                _currentChannel = _displayContents[index - 1];
                PlayerTitleText.Text = _currentChannel.Name;
                await PlayChannel(_currentChannel.Url);
            }
        }

        private async void NextChannel_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentChannel == null) return;
            int index = _displayContents.IndexOf(_currentChannel);
            if (index >= 0 && index < _displayContents.Count - 1)
            {
                _currentChannel = _displayContents[index + 1];
                PlayerTitleText.Text = _currentChannel.Name;
                await PlayChannel(_currentChannel.Url);
            }
        }

        private void TimeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isUpdatingSliderFromCode) return;
            if (_mediaPlayer != null && _mediaPlayer.IsSeekable && _mediaPlayer.Length > 0)
            {
                long newTime = (long)((e.NewValue / 100.0) * _mediaPlayer.Length);
                _mediaPlayer.Time = newTime;
            }
        }

        private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_mediaPlayer != null && _mediaPlayer.Length > 0)
                {
                    _isUpdatingSliderFromCode = true;
                    TimeSlider.Value = ((double)_mediaPlayer.Time / _mediaPlayer.Length) * 100;
                    _isUpdatingSliderFromCode = false;

                    var current = TimeSpan.FromMilliseconds(_mediaPlayer.Time);
                    var total = TimeSpan.FromMilliseconds(_mediaPlayer.Length);
                    CurrentTimeText.Text = current.ToString(@"hh\:mm\:ss");
                    TotalTimeText.Text = total.ToString(@"hh\:mm\:ss");
                }
            });
        }

        private void VolumeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (sender is not Slider slider) return;
            if (VolumeText != null) VolumeText.Text = $"{(int)slider.Value}%";
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Volume = (int)slider.Value;
                if (slider.Value > 0 && _isMuted)
                {
                    _isMuted = false;
                    _mediaPlayer.Mute = false;
                    MuteIcon.Text = "🔊";
                }
            }
        }

        private void PlayPause_Click(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            if (_mediaPlayer.IsPlaying) { _mediaPlayer.Pause(); PlayPauseIcon.Text = "▶"; }
            else { _mediaPlayer.Play(); PlayPauseIcon.Text = "⏸"; }
        }

        private void Mute_Click(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            _isMuted = !_isMuted;
            _mediaPlayer.Mute = _isMuted;
            MuteIcon.Text = _isMuted ? "🔇" : "🔊";
        }

        private void SkipBack_Click(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer != null && _mediaPlayer.IsSeekable)
            {
                _mediaPlayer.Time = Math.Max(0, _mediaPlayer.Time - 10000);
                ShowToast("10 saniye geri");
            }
        }

        private void SkipForward_Click(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer != null && _mediaPlayer.IsSeekable && _mediaPlayer.Length > 0)
            {
                _mediaPlayer.Time = Math.Min(_mediaPlayer.Length - 500, _mediaPlayer.Time + 10000);
                ShowToast("10 saniye ileri");
            }
        }

        private void AspectRatio_Click(object? sender, RoutedEventArgs e)
        {
            AspectRatioPopup.IsVisible = !AspectRatioPopup.IsVisible;
            AudioTrackPopup.IsVisible = false;
            SubtitlePopup.IsVisible = false;
            ChannelListPanel.IsVisible = false;
        }

        private void SetAspectRatio_Click(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            if (sender is not Button btn || btn.Tag is not string ratio) return;

            if (string.IsNullOrEmpty(ratio))
            {
                _mediaPlayer.AspectRatio = null;
                AspectRatioText.Text = GetResolutionLabel();
            }
            else if (ratio == "fill")
            {
                _mediaPlayer.AspectRatio = null;
                _mediaPlayer.Scale = 0;
                AspectRatioText.Text = "Fill";
            }
            else
            {
                _mediaPlayer.AspectRatio = ratio;
                AspectRatioText.Text = ratio;
            }

            AspectRatioPopup.IsVisible = false;
            ShowToast($"En:Boy oranı: {AspectRatioText.Text}");
        }

        private string GetResolutionLabel()
        {
            try
            {
                uint w = 0, h = 0;
                _mediaPlayer?.Size(0, ref w, ref h);
                if (w > 0 && h > 0)
                {
                    uint g = Gcd(w, h);
                    uint rw = w / g, rh = h / g;

                    if ((rw == 16 && rh == 9) || (rw == 32 && rh == 18)) return "16:9";
                    if ((rw == 4 && rh == 3) || (rw == 8 && rh == 6)) return "4:3";
                    if (rw == 21 && rh == 9) return "21:9";
                    if (rw == 16 && rh == 10) return "16:10";
                    if (rw == 1 && rh == 1) return "1:1";

                    return $"{rw}:{rh}";
                }
            }
            catch { }
            return "Auto";
        }

        private static uint Gcd(uint a, uint b) => b == 0 ? a : Gcd(b, a % b);

        private void Fullscreen_Click(object? sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.FullScreen)
            {
                this.WindowState = WindowState.Normal;
                SidebarPanel.IsVisible = true;
                TitleBarPanel.IsVisible = true;
                RootGrid.ColumnDefinitions[0].Width = new GridLength(220);
                MainContentGrid.RowDefinitions[0].Height = new GridLength(46);
                PlayerContainer.Height = 400;
                PlayerContainer.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
                ContentScrollViewer.IsVisible = true;
                _inactivityTimer?.Stop();
                ShowPlayerControls();
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
                ContentScrollViewer.IsVisible = false;
                ResetInactivityTimer();
            }
        }

        private void ResetInactivityTimer()
        {
            _inactivityTimer?.Stop();
            if (PlayerContainer.Height <= 0) return;

            _inactivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _inactivityTimer.Tick += (s, ev) =>
            {
                HidePlayerControls();
                _inactivityTimer?.Stop();
            };
            _inactivityTimer.Start();
        }

        private void ShowPlayerControls()
        {
            PlayerTopBar.IsVisible = true;
            PlayerBottomBar.IsVisible = true;
            this.Cursor = new Cursor(StandardCursorType.Arrow);
        }

        private void HidePlayerControls()
        {
            if (ChannelListPanel.IsVisible || AudioTrackPopup.IsVisible ||
                SubtitlePopup.IsVisible || AspectRatioPopup.IsVisible) return;

            PlayerTopBar.IsVisible = false;
            PlayerBottomBar.IsVisible = false;

            if (this.WindowState == WindowState.FullScreen)
                this.Cursor = new Cursor(StandardCursorType.None);
        }

        private void PlayerContainer_PointerMoved(object? sender, PointerEventArgs e)
        {
            ShowPlayerControls();
            ResetInactivityTimer();
        }

        private void PlayerOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            bool insidePopup = false;
            bool onButtonOrSlider = false;

            if (e.Source is Visual v)
            {
                Visual? cur = v;
                while (cur != null)
                {
                    if (cur == ChannelListPanel || cur == AudioTrackPopup ||
                        cur == SubtitlePopup || cur == AspectRatioPopup)
                    { insidePopup = true; break; }

                    if (cur is Button || cur is Slider)
                    { onButtonOrSlider = true; break; }

                    if (cur == PlayerOverlayRoot) break;
                    cur = cur.GetVisualParent();
                }
            }

            if (!insidePopup && (ChannelListPanel.IsVisible || AudioTrackPopup.IsVisible ||
                                  SubtitlePopup.IsVisible || AspectRatioPopup.IsVisible))
            {
                ChannelListPanel.IsVisible = false;
                AudioTrackPopup.IsVisible = false;
                SubtitlePopup.IsVisible = false;
                AspectRatioPopup.IsVisible = false;
            }

            if (e.ClickCount == 2 && !insidePopup && !onButtonOrSlider)
                Fullscreen_Click(this, new RoutedEventArgs());
        }

        private void Window_KeyDown(object? sender, KeyEventArgs e)
        {
            if (PlayerContainer.Height == 0) return;
            if (e.Source is TextBox) return;

            ShowPlayerControls();
            ResetInactivityTimer();

            switch (e.Key)
            {
                case Key.Space:
                    PlayPause_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.F:
                    Fullscreen_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.Escape:
                    if (this.WindowState == WindowState.FullScreen)
                    { Fullscreen_Click(this, new RoutedEventArgs()); e.Handled = true; }
                    break;
                case Key.M:
                    Mute_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.Up:
                    if (_isLiveContent) PrevChannel_Click(this, new RoutedEventArgs());
                    else if (_mediaPlayer != null) VolumeSlider.Value = Math.Min(200, VolumeSlider.Value + 5);
                    e.Handled = true; break;
                case Key.Down:
                    if (_isLiveContent) NextChannel_Click(this, new RoutedEventArgs());
                    else if (_mediaPlayer != null) VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
                    e.Handled = true; break;
                case Key.Left:
                    if (!_isLiveContent) { SkipBack_Click(this, new RoutedEventArgs()); e.Handled = true; }
                    break;
                case Key.Right:
                    if (!_isLiveContent) { SkipForward_Click(this, new RoutedEventArgs()); e.Handled = true; }
                    break;
            }
        }

        private void ContentScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_isLoadingMore) return;
            var sv = ContentScrollViewer;
            double scrollPos = sv.Offset.Y + sv.Viewport.Height;
            double totalHeight = sv.Extent.Height;
            if (totalHeight > 0 && scrollPos >= totalHeight * 0.8)
                LoadMoreItems();
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
                    {
                        var current = (VodContentGrid.ItemsSource as List<Channel>) ?? new List<Channel>();
                        VodContentGrid.ItemsSource = current.Concat(nextBatch).ToList();
                    }

                    _ = LoadLogosForChannelsAsync(nextBatch);
                    if (_currentTab != "Canlı") _ = LoadTmdbPostersForChannels(nextBatch);
                }
                else if (_viewState == "Shows" && _allFilteredCards.Count > _loadedCount)
                {
                    var nextBatch = _allFilteredCards.Skip(_loadedCount).Take(PAGE_SIZE).ToList();
                    _loadedCount += nextBatch.Count;

                    var current = (SeriesContentGrid.ItemsSource as List<SeriesCard>) ?? new List<SeriesCard>();
                    SeriesContentGrid.ItemsSource = current.Concat(nextBatch).ToList();

                    _ = LoadTmdbPostersForCards(nextBatch);
                }
            }
            finally
            {
                _isLoadingMore = false;

                if (_viewState == "Content" && _loadedCount >= _allFilteredContents.Count && _allFilteredContents.Count > 0)
                    _contentCache[$"{_currentTab}_{_currentCategory}"] = _allFilteredContents;
                else if (_viewState == "Shows" && _loadedCount >= _allFilteredCards.Count && _allFilteredCards.Count > 0)
                    _seriesCardCache[$"Dizi_{_currentCategory}"] = _allFilteredCards;
            }
        }

        private void ToggleChannelList_Click(object? sender, RoutedEventArgs e)
        {
            AudioTrackPopup.IsVisible = false;
            SubtitlePopup.IsVisible = false;
            AspectRatioPopup.IsVisible = false;

            if (ChannelListPanel.IsVisible) { ChannelListPanel.IsVisible = false; return; }
            PopulatePlayerChannelList();
            ChannelListPanel.IsVisible = true;
        }

        private void CloseChannelList_Click(object? sender, RoutedEventArgs e) =>
            ChannelListPanel.IsVisible = false;

        private void PopulatePlayerChannelList()
        {
            PlayerChannelListContainer.Children.Clear();
            var list = _displayContents.ToList();
            if (list.Count == 0)
                list = _allChannels.Where(c => !c.IsHidden && c.Type == "Canlı").ToList();

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

                var btn = new Button { Classes = { "PlayerChannelBtn" }, Tag = ch };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

                var nameText = new TextBlock
                {
                    Text = ch.Name,
                    Foreground = Brushes.White,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                Grid.SetColumn(nameText, 0);
                grid.Children.Add(nameText);

                if (_currentChannel != null && ch.Url == _currentChannel.Url)
                {
                    nameText.Foreground = Brush.Parse("#20c70e");
                    nameText.FontWeight = FontWeight.Bold;
                    btn.Classes.Add("Active");
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

                btn.Content = grid;
                btn.Click += PlayerChannelSelect_Click;
                PlayerChannelListContainer.Children.Add(btn);
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

            if (AudioTrackPopup.IsVisible) { AudioTrackPopup.IsVisible = false; return; }
            PopulateAudioTracks(AudioTrackContainer);
            AudioTrackPopup.IsVisible = true;
        }

        private void Subtitle_Click(object? sender, RoutedEventArgs e)
        {
            ChannelListPanel.IsVisible = false;
            AudioTrackPopup.IsVisible = false;

            if (SubtitlePopup.IsVisible) { SubtitlePopup.IsVisible = false; AspectRatioPopup.IsVisible = false; return; }
            PopulateSubtitles(SubtitleContainer);
            PopulateAudioTracks(AudioTrackContainerVod);
            SubtitlePopup.IsVisible = true;
        }

        private void PopulateAudioTracks(StackPanel container)
        {
            container.Children.Clear();

            if (_mediaPlayer == null || _mediaPlayer.AudioTrackDescription == null ||
                _mediaPlayer.AudioTrackDescription.Length == 0)
            {
                container.Children.Add(new TextBlock
                {
                    Text = "Ses parçası bulunamadı",
                    Foreground = Brush.Parse("#888"),
                    FontSize = 12,
                    Margin = new Thickness(10, 6)
                });
                return;
            }

            int activeId = _mediaPlayer.AudioTrack;
            foreach (var track in _mediaPlayer.AudioTrackDescription)
            {
                var trackName = track.Name;
                var btn = new Button
                {
                    Classes = { "TrackBtn" },
                    Content = new TextBlock { Text = trackName, Foreground = Brushes.White },
                    Tag = track.Id
                };
                if (track.Id == activeId) btn.Classes.Add("Active");

                btn.Click += (s, ev) =>
                {
                    if (s is Button b && b.Tag is int id && _mediaPlayer != null)
                    {
                        _mediaPlayer.SetAudioTrack(id);
                        AudioTrackPopup.IsVisible = false;
                        SubtitlePopup.IsVisible = false;
                        AspectRatioPopup.IsVisible = false;
                        ShowToast($"Ses: {trackName}");
                    }
                };
                container.Children.Add(btn);
            }
        }

        private void PopulateSubtitles(StackPanel container)
        {
            container.Children.Clear();

            if (_mediaPlayer == null)
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

            var offBtn = new Button
            {
                Classes = { "TrackBtn" },
                Content = new TextBlock { Text = "Kapalı", Foreground = Brushes.White },
                Tag = -1
            };
            if (_mediaPlayer.Spu == -1) offBtn.Classes.Add("Active");
            offBtn.Click += (s, ev) =>
            {
                _mediaPlayer?.SetSpu(-1);
                SubtitlePopup.IsVisible = false;
                AspectRatioPopup.IsVisible = false;
                ShowToast("Altyazı kapatıldı");
            };
            container.Children.Add(offBtn);

            if (_mediaPlayer.SpuDescription == null || _mediaPlayer.SpuDescription.Length <= 1)
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

            int activeSpu = _mediaPlayer.Spu;
            foreach (var sub in _mediaPlayer.SpuDescription)
            {
                if (sub.Id == -1) continue;
                var subName = sub.Name;
                var btn = new Button
                {
                    Classes = { "TrackBtn" },
                    Content = new TextBlock { Text = subName, Foreground = Brushes.White },
                    Tag = sub.Id
                };
                if (sub.Id == activeSpu) btn.Classes.Add("Active");

                btn.Click += (s, ev) =>
                {
                    if (s is Button b && b.Tag is int id && _mediaPlayer != null)
                    {
                        _mediaPlayer.SetSpu(id);
                        SubtitlePopup.IsVisible = false;
                        AspectRatioPopup.IsVisible = false;
                        ShowToast($"Altyazı: {subName}");
                    }
                };
                container.Children.Add(btn);
            }
        }
    }
}