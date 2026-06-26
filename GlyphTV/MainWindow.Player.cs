// ============================================================
// MainWindow.Player.cs - SADECE METODLAR (Alan tanımı yok)
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
using System.Text;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow
    {
        // ----- Canlı rozeti yanıp sönme -----
        private DispatcherTimer? _liveBadgeTimer;

        private void StartLiveBadgePulse()
        {
            _liveBadgeTimer?.Stop();
            _liveBadgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            bool dim = false;
            _liveBadgeTimer.Tick += (s, e) =>
            {
                dim = !dim;
                LiveBadge.Opacity = dim ? 0.35 : 1.0;
            };
            _liveBadgeTimer.Start();
        }

        private void StopLiveBadgePulse()
        {
            _liveBadgeTimer?.Stop();
            _liveBadgeTimer = null;
        }

        // ----- İçerik tıklama / oynatma -----
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
            _currentChannel = channel;
            PlayerTitleText.Text = channel.Name;
            PlayerContainer.IsVisible = true;
            PlayerContainer.Background = Brushes.Black;
            PlayerContainer.Height = 400;
            ContentScrollViewer.Margin = new Thickness(28, 420, 28, 24);

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

        // ----- Player kapatma ve konum kaydetme -----
        private void ClosePlayer_Click(object? sender, RoutedEventArgs e)
        {
            SaveCurrentWatchPosition();
            StopLiveBadgePulse();

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

            // Native video sink'i (HWND) görünümden ayır.
            // PlayChannel() bir sonraki oynatmada MainVideoView.MediaPlayer == null
            // kontrolü ile bunu zaten yeniden bağlıyor, bu yüzden burada
            // null'lamak güvenli ve eski "donmuş" video frame'inin
            // kanal listesi üzerinde kalmasını engeller.
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
            ResetMediaInfoBadges();

            ResetScrollToTop();

            Dispatcher.UIThread.Post(() => TrimProcessMemory(), DispatcherPriority.Background);
        }

        /// <summary>
        /// ContentScrollViewer'ı en üste sıfırlar. Margin/Height değişikliği
        /// sonrası layout henüz tamamlanmadan Offset set edilirse, Avalonia
        /// layout tamamlandığında Offset'i eski scroll konumuna göre tekrar
        /// hesaplayabiliyor. Bu yüzden birden fazla dispatcher önceliğinde
        /// tekrar tekrar sıfırlıyoruz.
        /// </summary>
        private void ResetScrollToTop()
        {
            // ScrollViewer içindeki son tıklanan kanal item'ı focus'ta kalabiliyor
            // ve bu, ScrollViewer'ın "focus'lu elemanı görünür tut" (BringIntoView)
            // davranışını tetikleyip Offset=0 sıfırlamasını eziyor.
            // - SearchBox'a focus vermek istenmeyen aramaları tetikliyor.
            // - ContentScrollViewer'ın kendisine focus vermek, ScrollViewer'ın
            //   son scroll konumunu (EVENT 22) hatırlayıp oraya geri dönmesine
            //   sebep oluyor.
            // BackBtn, ScrollViewer'ın dışında (üst başlık satırında), görünür
            // ve metin girişi yakalamayan bir kontrol olduğu için focus hedefi
            // olarak güvenli.
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
                if (_currentChannel == null || _mediaPlayer == null) return;
                if (_currentChannel.Type == "Canlı") return;
                long pos = _mediaPlayer.Time;
                long dur = _mediaPlayer.Length;
                if (pos > 0) UpsertWatchHistory(_currentChannel, pos, dur);
            }
            catch { }
        }

        // ----- Kanal oynatma -----
        private async Task PlayChannel(string url)
        {
            try
            {
                if (!_isVlcInitialized) InitializePlayer();
                if (_mediaPlayer == null || _libVLC == null) return;

                if (MainVideoView.MediaPlayer == null)
                    MainVideoView.MediaPlayer = _mediaPlayer;

                _mediaPlayer.Media?.Dispose();

                CurrentTimeText.Text = "00:00:00";
                TotalTimeText.Text = "00:00:00";
                TimeSlider.Value = 0;
                PlayerTitleText.Text = _currentChannel?.Name ?? "";

                ResetMediaInfoBadges();

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
                IconPlay.IsVisible = false;
                IconPause.IsVisible = true;
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
                LiveBadge.IsVisible = true;
                IconPlay.IsVisible = false;
                IconPause.IsVisible = true;

                if (_mediaPlayer != null) _mediaPlayer.SetRate(1.0f);
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

        // ----- Üst bar pill butonları (PointerPressed → RoutedEventArgs) -----
        private void BtnAudioTrack_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            AudioTrack_Click(sender, new RoutedEventArgs());
        }
        private void BtnChannelList_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ToggleChannelList_Click(sender, new RoutedEventArgs());
        }
        private void BtnAspectRatio_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            AspectRatio_Click(sender, new RoutedEventArgs());
        }
        private void BtnSubtitle_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            Subtitle_Click(sender, new RoutedEventArgs());
        }

        // ----- Alt bar kontroller (çekirdek metotlar) -----
        private void PlayPause_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            PlayPause_Core();
        }
        private void PlayPause_Click(object? sender, RoutedEventArgs e) => PlayPause_Core();
        private void PlayPause_Core()
        {
            if (_mediaPlayer == null) return;
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                IconPlay.IsVisible = true;
                IconPause.IsVisible = false;
            }
            else
            {
                _mediaPlayer.Play();
                IconPlay.IsVisible = false;
                IconPause.IsVisible = true;
            }
        }

        private void Mute_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            Mute_Core();
        }
        private void Mute_Click(object? sender, RoutedEventArgs e) => Mute_Core();
        private void Mute_Core()
        {
            if (_mediaPlayer == null) return;
            _isMuted = !_isMuted;
            _mediaPlayer.Mute = _isMuted;
            IconMuteOn.IsVisible = !_isMuted;
            IconMuteOff.IsVisible = _isMuted;
        }

        private void SkipBack_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            SkipBack_Core();
        }
        private void SkipBack_Click(object? sender, RoutedEventArgs e) => SkipBack_Core();
        private void SkipBack_Core()
        {
            if (_mediaPlayer != null && _mediaPlayer.IsSeekable)
            {
                _mediaPlayer.Time = Math.Max(0, _mediaPlayer.Time - 10000);
                ShowToast("10 saniye geri");
            }
        }

        private void SkipForward_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            SkipForward_Core();
        }
        private void SkipForward_Click(object? sender, RoutedEventArgs e) => SkipForward_Core();
        private void SkipForward_Core()
        {
            if (_mediaPlayer != null && _mediaPlayer.IsSeekable && _mediaPlayer.Length > 0)
            {
                _mediaPlayer.Time = Math.Min(_mediaPlayer.Length - 500, _mediaPlayer.Time + 10000);
                ShowToast("10 saniye ileri");
            }
        }

        private void PrevChannel_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            _ = DoPrevChannel();
        }
        private void PrevChannel_Click(object? sender, RoutedEventArgs e) => _ = DoPrevChannel();
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

        private void NextChannel_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            _ = DoNextChannel();
        }
        private void NextChannel_Click(object? sender, RoutedEventArgs e) => _ = DoNextChannel();
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

        private void NextEpisode_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            _ = DoNextEpisode();
        }
        private void NextEpisode_Click(object? sender, RoutedEventArgs e) => _ = DoNextEpisode();

        private async Task DoNextEpisode()
        {
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

        // ----- Bölüm bitişinde otomatik geçiş -----
        // EndReached VLC'nin kendi thread'inden fırlatılır; UI işlemleri Dispatcher'a taşınır.
        private void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            if (_currentChannel?.Type != "Dizi") return;

            Dispatcher.UIThread.Post(async () =>
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
                    if (SeriesContentGrid.IsVisible &&
                        SeriesContentGrid.ItemsSource is List<SeriesCard> cards)
                    {
                        var card = cards.FirstOrDefault(c => c.ShowName == previousChannel.ShowName);
                        if (card != null) card.HasResume = false;
                    }
                }

                _currentChannel      = nextEpisode;
                PlayerTitleText.Text = nextEpisode.Name;
                _resumePosition      = 0;
                await PlayChannel(nextEpisode.Url);
                ShowToast($"Sonraki bölüm: {nextEpisode.Name}");
            }, DispatcherPriority.Normal);
        }

        private void Speed_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            Speed_Core();
        }
        private void Speed_Click(object? sender, RoutedEventArgs e) => Speed_Core();
        private void Speed_Core()
        {
            if (_mediaPlayer == null) return;
            _speedIndex = (_speedIndex + 1) % _speedSteps.Length;
            _mediaPlayer.SetRate(_speedSteps[_speedIndex]);
            SpeedBtnText.Text = _speedStepLabels[_speedIndex];
            ShowToast($"Oynatma hızı: {_speedStepLabels[_speedIndex]}");
        }

        private void Fullscreen_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            Fullscreen_Core();
        }
        private void Fullscreen_Click(object? sender, RoutedEventArgs e) => Fullscreen_Core();

        // ----- Seek / Zaman -----
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

        // ----- Ses -----
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
                    IconMuteOn.IsVisible = true;
                    IconMuteOff.IsVisible = false;
                }
            }
        }

        // ----- Aspect Ratio -----
        private void AspectRatio_Click(object? sender, RoutedEventArgs e)
        {
            AudioTrackPopup.IsVisible = false;
            SubtitlePopup.IsVisible = false;
            ChannelListPanel.IsVisible = false;
            AspectRatioPopup.IsVisible = !AspectRatioPopup.IsVisible;
        }

        private void SetAspectRatio_Click(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;
            string? ratio = (sender as Border)?.Tag as string ?? (sender as Button)?.Tag as string;
            if (ratio == null) return;

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

        // ----- Tam ekran ve inaktivite -----
        private void Fullscreen_Core()
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

            _inactivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
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
            if (ChannelListPanel.IsVisible || AudioTrackPopup.IsVisible ||
                SubtitlePopup.IsVisible || AspectRatioPopup.IsVisible)
            {
                ChannelListPanel.IsVisible = false;
                AudioTrackPopup.IsVisible = false;
                SubtitlePopup.IsVisible = false;
                AspectRatioPopup.IsVisible = false;
                return;
            }

            if (e.ClickCount == 2 && e.Source is not Slider)
                Fullscreen_Core();
        }

        // ----- Klavye kısayolları -----
        private void Window_KeyDown(object? sender, KeyEventArgs e)
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
                    Fullscreen_Core(); e.Handled = true; break;
                case Key.Escape:
                    if (this.WindowState == WindowState.FullScreen)
                    { Fullscreen_Core(); e.Handled = true; }
                    break;
                case Key.M:
                    Mute_Core(); e.Handled = true; break;
                case Key.Up:
                    if (_isLiveContent) _ = DoPrevChannel();
                    else if (_mediaPlayer != null) VolumeSlider.Value = Math.Min(200, VolumeSlider.Value + 5);
                    e.Handled = true; break;
                case Key.Down:
                    if (_isLiveContent) _ = DoNextChannel();
                    else if (_mediaPlayer != null) VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
                    e.Handled = true; break;
                case Key.Left:
                    if (!_isLiveContent) { SkipBack_Core(); e.Handled = true; }
                    break;
                case Key.Right:
                    if (!_isLiveContent) { SkipForward_Core(); e.Handled = true; }
                    break;
            }
        }

        // ----- Scroll / lazy loading -----
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

        // ----- Kanal listesi (player içi) -----
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
                    Background   = isActive ? activeBg : Brushes.Transparent,
                    Padding      = new Thickness(12, 10),
                    Margin       = new Thickness(0, 1),
                    Cursor       = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child        = grid
                };
                border.PointerEntered += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brush.Parse("#22ffffff"); };
                border.PointerExited  += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brushes.Transparent; };
                border.PointerPressed += async (s, ev) =>
                {
                    ev.Handled = true; // overlay'e yayılmasın, panel kapanmasın
                    _currentChannel = channel;
                    PlayerTitleText.Text = channel.Name;
                    await PlayChannel(channel.Url);
                    PopulatePlayerChannelList(); // aktif kanalı güncelle
                };
                PlayerChannelListContainer.Children.Add(border);

                if (isActive) activeBorder = border;
            }

            // Aktif (izlenmekte olan) kanalı listede görünür alana kaydır.
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

        // ----- Ses dili ve altyazı -----
        private void AudioTrack_Click(object? sender, RoutedEventArgs e)
        {
            ChannelListPanel.IsVisible = false;
            SubtitlePopup.IsVisible = false;
            AspectRatioPopup.IsVisible = false;

            if (AudioTrackPopup.IsVisible) { AudioTrackPopup.IsVisible = false; return; }
            PopulateAudioTracks(AudioTrackContainer, closeOnSelect: true);
            AudioTrackPopup.IsVisible = true;
        }

        private void Subtitle_Click(object? sender, RoutedEventArgs e)
        {
            ChannelListPanel.IsVisible = false;
            AudioTrackPopup.IsVisible = false;
            AspectRatioPopup.IsVisible = false;

            if (SubtitlePopup.IsVisible) { SubtitlePopup.IsVisible = false; return; }
            PopulateSubtitles(SubtitleContainer);
            PopulateAudioTracks(AudioTrackContainerVod);
            SubtitlePopup.IsVisible = true;
        }

        // closeOnSelect=true  → Canlı TV ses dili: seçim yapılınca popup kapanır
        // closeOnSelect=false → VOD/Dizi altyazı paneli ses dili: popup açık kalır
        private void PopulateAudioTracks(StackPanel container, bool closeOnSelect = false)
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
                var trackId   = track.Id;
                bool isActive = track.Id == activeId;
                var activeBg  = Brush.Parse("#a855f7");
                var border = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background   = isActive ? activeBg : Brushes.Transparent,
                    Padding      = new Thickness(10, 6),
                    Cursor       = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child        = new TextBlock { Text = trackName, Foreground = Brushes.White, FontSize = 13 }
                };
                border.PointerEntered += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brush.Parse("#22ffffff"); };
                border.PointerExited  += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brushes.Transparent; };
                border.PointerPressed += (s, ev) =>
                {
                    ev.Handled = true; // overlay'e yayılmasın
                    if (_mediaPlayer != null)
                    {
                        _mediaPlayer.SetAudioTrack(trackId);
                        ShowToast($"Ses: {trackName}");
                        if (closeOnSelect)
                        {
                            // Canlı TV: seçim sonrası popup kapan
                            AudioTrackPopup.IsVisible = false;
                        }
                        else
                        {
                            // VOD/Dizi: popup açık kalsın, aktif seçimi güncelle
                            PopulateAudioTracks(container, closeOnSelect: false);
                        }
                    }
                };
                container.Children.Add(border);
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

            var activeBg = Brush.Parse("#a855f7");
            bool offActive = _mediaPlayer.Spu == -1;
            var offBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background   = offActive ? activeBg : Brushes.Transparent,
                Padding      = new Thickness(10, 6),
                Cursor       = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Child        = new TextBlock { Text = "Kapalı", Foreground = Brushes.White, FontSize = 13 }
            };
            offBorder.PointerEntered += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brush.Parse("#22ffffff"); };
            offBorder.PointerExited  += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brushes.Transparent; };
            offBorder.PointerPressed += (s, ev) =>
            {
                ev.Handled = true;
                _mediaPlayer?.SetSpu(-1);
                ShowToast("Altyazı kapatıldı");
                PopulateSubtitles(container);
                PopulateAudioTracks(AudioTrackContainerVod, closeOnSelect: false);
            };
            container.Children.Add(offBorder);

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
                var subId   = sub.Id;
                bool isActive = sub.Id == activeSpu;
                var border = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background   = isActive ? activeBg : Brushes.Transparent,
                    Padding      = new Thickness(10, 6),
                    Cursor       = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child        = new TextBlock { Text = subName, Foreground = Brushes.White, FontSize = 13 }
                };
                border.PointerEntered += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brush.Parse("#22ffffff"); };
                border.PointerExited  += (s, ev) => { if (s is Border b && !ReferenceEquals(b.Background, activeBg)) b.Background = Brushes.Transparent; };
                border.PointerPressed += (s, ev) =>
                {
                    ev.Handled = true;
                    if (_mediaPlayer != null)
                    {
                        _mediaPlayer.SetSpu(subId);
                        ShowToast($"Altyazı: {subName}");
                        PopulateSubtitles(container);
                        PopulateAudioTracks(AudioTrackContainerVod, closeOnSelect: false);
                    }
                };
                container.Children.Add(border);
            }
        }
        // ----- Aspect Ratio için PointerPressed handler (Border) -----
        private void SetAspectRatio_PP(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            if (_mediaPlayer == null) return;
            string? ratio = (sender as Border)?.Tag as string;
            if (ratio == null) return;
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

        // ----- Kapat butonu hover efektleri -----
        private void CloseBtn_PointerEntered(object? sender, PointerEventArgs e)
        {
            if (sender is Border b)
            {
                b.Background = Brush.Parse("#BBef4444");
                b.BorderBrush = Brush.Parse("#88ef4444");
            }
        }

        private void CloseBtn_PointerExited(object? sender, PointerEventArgs e)
        {
            if (sender is Border b)
            {
                b.Background = Brush.Parse("#18ffffff");
                b.BorderBrush = Brush.Parse("#2Affffff");
            }
        }

        // ----- Yayın bilgi rozetleri (çözünürlük / FPS / codec / ses) -----
        private void MediaPlayer_ESAdded(object? sender, MediaPlayerESAddedEventArgs e)
        {
            Dispatcher.UIThread.Post(UpdateMediaInfoBadges);
        }

        private void ResetMediaInfoBadges()
        {
            MediaInfoBadgesPanel.IsVisible = false;
            ResolutionBadge.IsVisible      = false;
            FpsBadge.IsVisible             = false;
            VideoCodecBadge.IsVisible      = false;
            AudioCodecBadge.IsVisible      = false;
            AudioChannelsBadge.IsVisible   = false;
        }

        private void UpdateMediaInfoBadges()
        {
            try
            {
                if (_mediaPlayer?.Media == null) return;

                var tracks = _mediaPlayer.Media.Tracks;
                if (tracks == null) return;

                MediaTrack? videoTrack = null;
                MediaTrack? audioTrack = null;
                foreach (var t in tracks)
                {
                    if (t.TrackType == TrackType.Video && videoTrack == null) videoTrack = t;
                    if (t.TrackType == TrackType.Audio && audioTrack == null) audioTrack = t;
                }

                bool anyVisible = false;

                if (videoTrack.HasValue)
                {
                    var v = videoTrack.Value.Data.Video;

                    if (v.Width > 0 && v.Height > 0)
                    {
                        ResolutionBadgeText.Text = $"{v.Width}x{v.Height}";
                        ResolutionBadge.IsVisible = true;
                        anyVisible = true;
                    }
                    else ResolutionBadge.IsVisible = false;

                    if (v.FrameRateDen > 0 && v.FrameRateNum > 0)
                    {
                        double fps = (double)v.FrameRateNum / v.FrameRateDen;
                        FpsBadgeText.Text = $"{fps:0.##} FPS";
                        FpsBadge.IsVisible = true;
                        anyVisible = true;
                    }
                    else FpsBadge.IsVisible = false;

                    string vCodec = FourCcToString(videoTrack.Value.Codec);
                    if (!string.IsNullOrEmpty(vCodec))
                    {
                        VideoCodecBadgeText.Text = vCodec;
                        VideoCodecBadge.IsVisible = true;
                        anyVisible = true;
                    }
                    else VideoCodecBadge.IsVisible = false;
                }
                else
                {
                    ResolutionBadge.IsVisible  = false;
                    FpsBadge.IsVisible         = false;
                    VideoCodecBadge.IsVisible  = false;
                }

                if (audioTrack.HasValue)
                {
                    var a = audioTrack.Value.Data.Audio;

                    string aCodec = FourCcToString(audioTrack.Value.Codec);
                    if (!string.IsNullOrEmpty(aCodec))
                    {
                        AudioCodecBadgeText.Text = aCodec;
                        AudioCodecBadge.IsVisible = true;
                        anyVisible = true;
                    }
                    else AudioCodecBadge.IsVisible = false;

                    if (a.Channels > 0)
                    {
                        AudioChannelsBadgeText.Text = a.Channels switch
                        {
                            1 => "Mono",
                            2 => "2.0",
                            6 => "5.1",
                            8 => "7.1",
                            _ => $"{a.Channels}ch"
                        };
                        AudioChannelsBadge.IsVisible = true;
                        anyVisible = true;
                    }
                    else AudioChannelsBadge.IsVisible = false;
                }
                else
                {
                    AudioCodecBadge.IsVisible    = false;
                    AudioChannelsBadge.IsVisible = false;
                }

                MediaInfoBadgesPanel.IsVisible = anyVisible;
            }
            catch { }
        }

        private static string FourCcToString(uint fourcc)
        {
            try
            {
                if (fourcc == 0) return "";
                var bytes = BitConverter.GetBytes(fourcc);
                var str = Encoding.ASCII.GetString(bytes).Trim('\0', ' ').ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(str)) return "";

                return str switch
                {
                    "H264" => "H.264",
                    "HEVC" or "H265" => "HEVC",
                    "AVC1" => "H.264",
                    "VP09" or "VP90" => "VP9",
                    "AV01" => "AV1",
                    "MP4A" or "MP4V" => "AAC",
                    "AC3" or "A52" or "A52B" => "AC3",
                    "EAC3" => "E-AC3",
                    "MP3" or "MPGA" or "MP3L" => "MP3",
                    "DTS " or "DTS" => "DTS",
                    "OPUS" => "Opus",
                    "VORB" or "VORBIS" => "Vorbis",
                    _ => str
                };
            }
            catch { return ""; }
        }
    }
}