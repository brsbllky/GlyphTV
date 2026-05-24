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
using Avalonia.VisualTree;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
            await FetchTmdbInfo(channel.Name, channel.Type ?? "VOD");
        }

        // ─────────────────────────────────────────────────────────────
        // Xtream API – VOD bilgisi
        // ─────────────────────────────────────────────────────────────
        private async Task FetchXtreamVodInfo(Channel channel, TvSource source)
        {
            try
            {
                string streamId = channel.XuiId;
                if (string.IsNullOrEmpty(streamId))
                {
                    var parts = channel.Url.Split('/');
                    if (parts.Length > 0)
                    {
                        string last = parts[parts.Length - 1];
                        if (last.Contains('#')) last = last[..last.IndexOf('#')];
                        if (last.Contains('?')) last = last[..last.IndexOf('?')];
                        streamId = System.IO.Path.GetFileNameWithoutExtension(last);
                    }
                }

                if (string.IsNullOrEmpty(streamId)) return;
                if (string.IsNullOrEmpty(source.Username) || string.IsNullOrEmpty(source.Password)) return;

                string encodedUser = System.Uri.EscapeDataString(source.Username);
                string encodedPass = System.Uri.EscapeDataString(source.Password);
                string action  = channel.Type == "Dizi" ? "get_series_info" : "get_vod_info";
                string idParam = channel.Type == "Dizi" ? "series_id" : "vod_id";
                string apiUrl  = $"{source.PathOrUrl}/player_api.php?username={encodedUser}&password={encodedPass}&action={action}&{idParam}={streamId}";

                string apiContent = "";
                using (var handler = new System.Net.Http.HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
                using (var client = new System.Net.Http.HttpClient(handler) { Timeout = System.TimeSpan.FromSeconds(15) })
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "VLC/3.0.20 LibVLC/3.0.20");
                    apiContent = await client.GetStringAsync(apiUrl);
                }

                if (string.IsNullOrEmpty(apiContent) || !apiContent.TrimStart().StartsWith("{")) return;

                using var doc  = System.Text.Json.JsonDocument.Parse(apiContent);
                var root = doc.RootElement;

                string SafeGet(System.Text.Json.JsonElement parent, string key)
                {
                    if (!parent.TryGetProperty(key, out var val)) return "";
                    return val.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => val.GetString() ?? "",
                        System.Text.Json.JsonValueKind.Number => val.GetRawText(),
                        _ => ""
                    };
                }

                if (root.TryGetProperty("info", out var info))
                {
                    if (info.TryGetProperty("genre", out var genre) && genre.GetString() is string g && !string.IsNullOrEmpty(g))
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => VodInfoGenre.Text = g);

                    string xDir  = SafeGet(info, "director");
                    string xCast = SafeGet(info, "cast");
                    if (string.IsNullOrEmpty(xCast)) xCast = SafeGet(info, "actors");
                    string xDur  = SafeGet(info, "episode_run_time");
                    if (string.IsNullOrEmpty(xDur)) xDur = SafeGet(info, "duration");
                    string xDate = SafeGet(info, "release_date");
                    if (string.IsNullOrEmpty(xDate)) xDate = SafeGet(info, "releasedate");
                    string xPlot = SafeGet(info, "plot");
                    if (string.IsNullOrEmpty(xPlot)) xPlot = SafeGet(info, "description");
                    string xAge  = SafeGet(info, "age");
                    string xOrig = SafeGet(info, "o_name");

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!string.IsNullOrEmpty(xOrig)) { VodInfoOrigName.Text  = xOrig;        VodInfoOrigRow.IsVisible  = true; }
                        if (!string.IsNullOrEmpty(xDir))  { VodInfoDirector.Text  = xDir;         VodInfoDirRow.IsVisible   = true; }
                        if (!string.IsNullOrEmpty(xCast)) { VodInfoCast.Text      = xCast;        VodInfoCastRow.IsVisible  = true; }
                        if (!string.IsNullOrEmpty(xDur))  { VodInfoDuration.Text  = xDur + " dk"; VodInfoDurRow.IsVisible   = true; }
                        if (!string.IsNullOrEmpty(xDate)) { VodInfoDate.Text      = xDate;        VodInfoDateRow.IsVisible  = true; }
                        if (!string.IsNullOrEmpty(xAge))  { VodInfoAge.Text       = xAge + "+";   VodInfoAgeRow.IsVisible   = true; }
                        if (!string.IsNullOrEmpty(xPlot)) { VodInfoPlot.Text      = xPlot;        VodInfoPlotRow.IsVisible  = true; }
                    });

                    string coverUrl = SafeGet(info, "cover_big");
                    if (string.IsNullOrEmpty(coverUrl)) coverUrl = SafeGet(info, "cover");
                    if (string.IsNullOrEmpty(coverUrl)) coverUrl = SafeGet(info, "movie_image");

                    if (!string.IsNullOrEmpty(coverUrl))
                    {
                        try
                        {
                            EnsureLogoHttpClient();
                            var bytes = await _logoHttpClient!.GetByteArrayAsync(coverUrl);
                            using var ms = new System.IO.MemoryStream(bytes);
                            var bitmap = new Avalonia.Media.Imaging.Bitmap(ms);
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                VodInfoPoster.Background = Avalonia.Media.Brushes.Transparent;
                                VodInfoPoster.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
                            });
                        }
                        catch { }
                    }
                }
            }
            catch { }
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
        // Daha fazla bilgi göster (panel genişlet)
        // ─────────────────────────────────────────────────────────────
        private void VodMoreInfo_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentVodInfo == null) return;

            var scrollViewer = VodInfoOverlay.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scrollViewer != null) scrollViewer.MaxHeight = 600;

            var innerBorder = VodInfoOverlay.Child as Border;
            if (innerBorder != null) innerBorder.Width = 650;

            VodInfoPoster.Height = 240;
            VodInfoPoster.Width  = 160;

            ShowToast("Detaylı bilgi gösteriliyor");
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

        // ─────────────────────────────────────────────────────────────
        // Bilgi satırı ekleme yardımcı metodu
        // ─────────────────────────────────────────────────────────────
        private void AddInfoRow(StackPanel parent, string label, string value)
        {
            var grid = new Grid { Tag = "xtream_extra" };
            grid.ColumnDefinitions.Add(new ColumnDefinition(70, GridUnitType.Pixel));
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

            var labelText = new TextBlock
            {
                Text = label, FontSize = 12,
                FontWeight = Avalonia.Media.FontWeight.Medium,
                Foreground = (IBrush)this.Resources["Text"]!,
                Width = 70
            };
            var valueText = new TextBlock
            {
                Text = value, FontSize = 12,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Foreground = (IBrush)this.Resources["TextSec"]!
            };
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(labelText);
            grid.Children.Add(valueText);
            parent.Children.Add(grid);
        }
    }
}
