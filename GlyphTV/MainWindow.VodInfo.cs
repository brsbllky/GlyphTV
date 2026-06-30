// ============================================================
// MainWindow.VodInfo.cs
// VOD/Film detay modalı (poster, meta bilgi, oynat/favori)
// ============================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow
    {
        // ─────────────────────────────────────────────────────────────
        // VOD detay modalını aç
        // ─────────────────────────────────────────────────────────────
        private async void VodInfo_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Channel channel) return;

            _currentVodInfo = channel;

            VodInfoOrigRow.IsVisible  = false;
            VodInfoDirRow.IsVisible   = false;
            VodInfoCastRow.IsVisible  = false;
            VodInfoDurRow.IsVisible   = false;
            VodInfoDateRow.IsVisible  = false;
            VodInfoAgeRow.IsVisible   = false;
            VodInfoPlotRow.IsVisible  = false;

            VodInfoTitle.Text      = channel.Name;
            VodInfoCategory.Text   = channel.Group;
            VodInfoGenre.Text      = channel.Group;
            VodInfoModalTitle.Text = "Film Detayları";

            var activeSource = _sources.FirstOrDefault(s => s.IsActive);
            VodInfoSource.Text  = activeSource?.Name ?? "Bilinmeyen Kaynak";
            VodInfoFavText.Text = channel.IsFavorite ? "❤️ Favorilerde" : "♡ Favori";

            // Poster
            VodInfoPoster.Child = null;
            if (channel.LogoBitmap != null)
            {
                VodInfoPoster.Background = Avalonia.Media.Brushes.Transparent;
                VodInfoPoster.Child = new Image
                {
                    Source = channel.LogoBitmap,
                    Stretch = Stretch.UniformToFill
                };
            }
            else
            {
                VodInfoPoster.Background = Brush.Parse("#1A4f8bff");
                VodInfoPoster.Child = new TextBlock
                {
                    Text = "🎬", FontSize = 40,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                    Opacity = 0.5
                };
            }

            VodInfoOverlay.IsVisible = true;

            // Canlı kanallar TMDb'de film/dizi olarak aranamaz — anlamsız
            // sonuç + gereksiz ağ isteğine yol açtığından burada atlanır.
            if (channel.Type != "Canlı")
                await FetchTmdbInfo(channel.Name, channel.Type ?? "VOD");
        }

        // ─────────────────────────────────────────────────────────────
        // Modalı kapat
        // ─────────────────────────────────────────────────────────────
        private void CloseVodInfo_Click(object? sender, RoutedEventArgs e)
        {
            VodInfoOverlay.IsVisible = false;
            _currentVodInfo = null;
        }

        // ─────────────────────────────────────────────────────────────
        // Detaydan oynat
        // ─────────────────────────────────────────────────────────────
        private async void VodInfoPlay_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentVodInfo == null) return;
            VodInfoOverlay.IsVisible = false;

            _currentChannel = _currentVodInfo;
            PlayerTitleText.Text = _currentVodInfo.Name;
            PlayerContainer.Background = Avalonia.Media.Brushes.Black;
            PlayerContainer.Height = 400;
            ContentScrollViewer.Margin = new Avalonia.Thickness(28, 420, 28, 24);

            _resumePosition = 0;
            var hist = _watchHistory.FirstOrDefault(h => h.Url == _currentVodInfo.Url);
            if (hist != null && hist.Position > 5000)
            {
                _resumePosition = hist.Position;
                var ts = System.TimeSpan.FromMilliseconds(hist.Position);
                ShowToast($"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2} konumundan devam ediliyor");
            }

            await PlayChannel(_currentVodInfo.Url);
            _currentVodInfo = null;
        }

        // ─────────────────────────────────────────────────────────────
        // Detaydan favori toggle
        // ─────────────────────────────────────────────────────────────
        private void VodInfoFav_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentVodInfo == null) return;
            _currentVodInfo.IsFavorite = !_currentVodInfo.IsFavorite;

            var activeSource = _sources.FirstOrDefault(s => s.IsActive);
            if (activeSource != null) SaveChannelsForSource(activeSource.Id);

            VodInfoFavText.Text = _currentVodInfo.IsFavorite ? "❤️ Favorilerde" : "♡ Favori";
            ShowToast(_currentVodInfo.IsFavorite ? "Favorilere eklendi" : "Favorilerden çıkarıldı");
        }

    }
}
