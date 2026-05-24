// ============================================================
// MainWindow.Series.cs
// Dizi kartı oluşturma, sezon/bölüm navigasyonu,
// dizi oynatma, favori, detay modalı
// ============================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
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
        // SeriesCard oluşturma helper'ı
        // ─────────────────────────────────────────────────────────────
        private SeriesCard BuildSeriesCard(string showName, List<Channel> episodes,
            Dictionary<string, WatchHistory>? historyByUrl = null)
        {
            var card = new SeriesCard { ShowName = showName };
            var first = episodes.FirstOrDefault();

            if (first != null)
            {
                card.Group   = first.Group;
                card.LogoUrl = first.LogoUrl;

                if (_tmdbPosterCache.TryGetValue(showName, out var cachedPoster) && cachedPoster != null)
                    card.LogoBitmap = cachedPoster;
                else
                    card.LogoBitmap = first.LogoBitmap;
            }

            // Sezon/bölüm yapısı
            card.Seasons = episodes.Select(e => e.Season).Distinct().OrderBy(s => s).ToList();
            card.EpisodesBySeason = new Dictionary<string, List<Channel>>();
            foreach (var season in card.Seasons)
            {
                card.EpisodesBySeason[season] = episodes
                    .Where(e => e.Season == season)
                    .OrderBy(e => e.EpisodeNumber)
                    .ToList();
            }

            // Seçim geri yükleme: önce manuel hafıza, sonra izleme geçmişi
            bool restored = false;

            if (_seriesSelections.TryGetValue(showName, out var sel))
            {
                card.RestoreSelection(sel.season, sel.episode);
                restored = true;
            }

            if (!restored && historyByUrl != null)
            {
                // En son izlenen bölümü bul
                WatchHistory? bestHistory = null;
                Channel? bestEpisode = null;

                foreach (var ep in episodes)
                {
                    if (historyByUrl.TryGetValue(ep.Url, out var hist) && hist.Position > 5000)
                    {
                        if (bestHistory == null || hist.LastWatched > bestHistory.LastWatched)
                        {
                            bestHistory = hist;
                            bestEpisode = ep;
                        }
                    }
                }

                if (bestEpisode != null && bestHistory != null)
                {
                    int sIdx = card.Seasons.IndexOf(bestEpisode.Season);
                    if (sIdx >= 0)
                    {
                        var seasonEps = card.EpisodesBySeason.ContainsKey(bestEpisode.Season)
                            ? card.EpisodesBySeason[bestEpisode.Season] : new List<Channel>();
                        int eIdx = seasonEps.FindIndex(e => e.Url == bestEpisode.Url);
                        if (eIdx >= 0)
                        {
                            card.RestoreSelection(sIdx, eIdx);
                            _seriesSelections[showName] = (sIdx, eIdx);
                        }
                    }
                }
            }

            // HasResume
            var selectedEp = card.SelectedEpisode;
            if (selectedEp != null && historyByUrl != null)
            {
                card.HasResume = historyByUrl.TryGetValue(selectedEp.Url, out var hist) && hist.Position > 5000;
            }

            return card;
        }

        // Logo yükleme (SeriesCard için) – değişiklik yok
        private async Task LoadLogosForSeriesCards(List<SeriesCard> cards, List<Channel> channels)
        {
            await LoadLogosForChannelsAsync(channels);
            foreach (var card in cards)
            {
                if (card.LogoBitmap == null)
                {
                    var ch = channels.FirstOrDefault(c => c.ShowName == card.ShowName && c.LogoBitmap != null);
                    if (ch != null)
                        await Dispatcher.UIThread.InvokeAsync(() => card.LogoBitmap = ch.LogoBitmap);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Sezon / Bölüm navigasyonları – değişiklik yok
        // ─────────────────────────────────────────────────────────────
        private void PrevSeason_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not SeriesCard card) return;
            card.SelectedSeasonIndex = card.SelectedSeasonIndex > 0
                ? card.SelectedSeasonIndex - 1
                : card.Seasons.Count - 1;
            SaveSeriesSelection(card);
            UpdateSeriesPlayButton(card);
        }

        private void NextSeason_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not SeriesCard card) return;
            card.SelectedSeasonIndex = card.SelectedSeasonIndex < card.Seasons.Count - 1
                ? card.SelectedSeasonIndex + 1
                : 0;
            SaveSeriesSelection(card);
            UpdateSeriesPlayButton(card);
        }

        private void PrevEpisode_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not SeriesCard card) return;
            card.SelectedEpisodeIndex = card.SelectedEpisodeIndex > 0
                ? card.SelectedEpisodeIndex - 1
                : card.CurrentEpisodes.Count - 1;
            SaveSeriesSelection(card);
            UpdateSeriesPlayButton(card);
        }

        private void NextEpisode_Click2(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not SeriesCard card) return;
            card.SelectedEpisodeIndex = card.SelectedEpisodeIndex < card.CurrentEpisodes.Count - 1
                ? card.SelectedEpisodeIndex + 1
                : 0;
            SaveSeriesSelection(card);
            UpdateSeriesPlayButton(card);
        }

        private void SaveSeriesSelection(SeriesCard card) =>
            _seriesSelections[card.ShowName] = (card.SelectedSeasonIndex, card.SelectedEpisodeIndex);

        private void UpdateSeriesPlayButton(SeriesCard card)
        {
            // HasResume hesaplamak için historyByUrl oluşturmak pahalı olabilir,
            // o yüzden direkt watch history listesini kullanıyoruz (buton tıklamaları seyrek).
            var ep = card.SelectedEpisode;
            if (ep != null)
            {
                var hist = _watchHistory.FirstOrDefault(h => h.Url == ep.Url);
                card.HasResume = hist != null && hist.Position > 5000;
            }
            else
            {
                card.HasResume = false;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Dizi oynat / devam et – değişiklik yok
        // ─────────────────────────────────────────────────────────────
        private async void PlaySeries_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not SeriesCard card) return;

            var episode = card.SelectedEpisode;
            if (episode == null) { ShowToast("Bölüm bulunamadı"); return; }

            SaveSeriesSelection(card);

            _currentChannel = episode;
            PlayerTitleText.Text = episode.Name;
            PlayerContainer.Background = Avalonia.Media.Brushes.Black;
            PlayerContainer.Height = 400;
            ContentScrollViewer.Margin = new Avalonia.Thickness(28, 420, 28, 24);

            _resumePosition = 0;
            await PlayChannel(episode.Url);
        }

        private async void PlaySeriesResume_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not SeriesCard card) return;

            var episode = card.SelectedEpisode;
            if (episode == null) return;

            SaveSeriesSelection(card);

            _currentChannel = episode;
            PlayerTitleText.Text = episode.Name;
            PlayerContainer.Background = Avalonia.Media.Brushes.Black;
            PlayerContainer.Height = 400;
            ContentScrollViewer.Margin = new Avalonia.Thickness(28, 420, 28, 24);

            _resumePosition = 0;
            var hist = _watchHistory.FirstOrDefault(h => h.Url == episode.Url);
            if (hist != null && hist.Position > 5000)
            {
                _resumePosition = hist.Position;
                var ts = System.TimeSpan.FromMilliseconds(hist.Position);
                ShowToast($"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2} konumundan devam ediliyor");
            }

            await PlayChannel(episode.Url);
        }

        // ─────────────────────────────────────────────────────────────
        // Dizi favori toggle – değişiklik yok
        // ─────────────────────────────────────────────────────────────
        private void SeriesFav_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not SeriesCard card) return;

            var episode = card.SelectedEpisode;
            if (episode == null) return;

            episode.IsFavorite = !episode.IsFavorite;
            card.OnPropertyChanged("FavoriteIcon");
            card.OnPropertyChanged("FavoriteBrush");
            card.OnPropertyChanged("IsFavorite");

            var activeSource = _sources.FirstOrDefault(s => s.IsActive);
            if (activeSource != null) SaveChannelsForSource(activeSource.Id);

            // Favori sekmesindeyken anında güncelle
            if (_currentTab == "Favori" && _viewState == "Categories")
                RefreshFavoriGrids();

            ShowToast(episode.IsFavorite ? "Favorilere eklendi" : "Favorilerden çıkarıldı");
        }

        // ─────────────────────────────────────────────────────────────
        // Dizi detay modalı – değişiklik yok
        // ─────────────────────────────────────────────────────────────
        private async void SeriesInfo_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not SeriesCard card) return;

            var episode = card.SelectedEpisode;
            if (episode == null) return;

            _currentVodInfo = episode;

            VodInfoOrigRow.IsVisible  = false;
            VodInfoDirRow.IsVisible   = false;
            VodInfoCastRow.IsVisible  = false;
            VodInfoDurRow.IsVisible   = false;
            VodInfoDateRow.IsVisible  = false;
            VodInfoAgeRow.IsVisible   = false;
            VodInfoPlotRow.IsVisible  = false;

            VodInfoModalTitle.Text = "Dizi Detayları";
            VodInfoTitle.Text      = card.ShowName;
            VodInfoCategory.Text   = card.Group;
            VodInfoGenre.Text      = card.Group;
            VodInfoFavText.Text    = episode.IsFavorite ? "❤️ Favorilerde" : "♡ Favori";

            var activeSource = _sources.FirstOrDefault(s => s.IsActive);
            VodInfoSource.Text = activeSource?.Name ?? "Bilinmeyen Kaynak";

            VodInfoPoster.Child = null;
            if (card.LogoBitmap != null)
            {
                VodInfoPoster.Background = Avalonia.Media.Brushes.Transparent;
                VodInfoPoster.Child = new Avalonia.Controls.Image
                {
                    Source = card.LogoBitmap,
                    Stretch = Avalonia.Media.Stretch.UniformToFill
                };
            }
            else
            {
                VodInfoPoster.Background = Avalonia.Media.Brush.Parse("#1A4f8bff");
                VodInfoPoster.Child = new Avalonia.Controls.TextBlock
                {
                    Text = "🎞️", FontSize = 40,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                    Opacity = 0.5
                };
            }

            VodInfoOverlay.IsVisible = true;
            await FetchTmdbInfo(card.ShowName, "Dizi", card);
        }

        // FetchXtreamSeriesInfo – değişiklik yok
        private async Task FetchXtreamSeriesInfo(Channel episode, SeriesCard card, TvSource source)
        {
            try
            {
                string streamId = episode.XuiId;
                if (string.IsNullOrEmpty(streamId)) return;
                if (string.IsNullOrEmpty(source.Username) || string.IsNullOrEmpty(source.Password)) return;

                string encodedUser = System.Uri.EscapeDataString(source.Username);
                string encodedPass = System.Uri.EscapeDataString(source.Password);

                string[] actions  = { "get_series_info", "get_vod_info" };
                string[] idParams = { "series_id", "vod_id" };

                for (int i = 0; i < actions.Length; i++)
                {
                    try
                    {
                        string apiUrl = $"{source.PathOrUrl}/player_api.php?username={encodedUser}&password={encodedPass}&action={actions[i]}&{idParams[i]}={streamId}";

                        string apiContent = "";
                        using (var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
                        using (var client = new HttpClient(handler) { Timeout = System.TimeSpan.FromSeconds(10) })
                        {
                            client.DefaultRequestHeaders.Add("User-Agent", "VLC/3.0.20 LibVLC/3.0.20");
                            apiContent = await client.GetStringAsync(apiUrl);
                        }

                        if (string.IsNullOrEmpty(apiContent) || !apiContent.TrimStart().StartsWith("{")) continue;

                        using var doc  = JsonDocument.Parse(apiContent);
                        var root = doc.RootElement;

                        string SafeGet(JsonElement parent, string key)
                        {
                            if (!parent.TryGetProperty(key, out var val)) return "";
                            return val.ValueKind switch
                            {
                                JsonValueKind.String => val.GetString() ?? "",
                                JsonValueKind.Number => val.GetRawText(),
                                _                    => ""
                            };
                        }

                        bool foundData = false;
                        if (root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
                        {
                            string genre    = SafeGet(info, "genre");
                            string xDir     = SafeGet(info, "director");
                            string xCast    = SafeGet(info, "cast");
                            if (string.IsNullOrEmpty(xCast)) xCast = SafeGet(info, "actors");
                            string xDur     = SafeGet(info, "episode_run_time");
                            if (string.IsNullOrEmpty(xDur)) xDur = SafeGet(info, "duration");
                            string xDate    = SafeGet(info, "release_date");
                            if (string.IsNullOrEmpty(xDate)) xDate = SafeGet(info, "releasedate");
                            string xPlot    = SafeGet(info, "plot");
                            if (string.IsNullOrEmpty(xPlot)) xPlot = SafeGet(info, "description");
                            string xAge     = SafeGet(info, "age");
                            string xOrig    = SafeGet(info, "o_name");
                            if (string.IsNullOrEmpty(xOrig)) xOrig = SafeGet(info, "name");

                            if (!string.IsNullOrEmpty(xDir) || !string.IsNullOrEmpty(xCast) ||
                                !string.IsNullOrEmpty(xPlot) || !string.IsNullOrEmpty(xOrig))
                            {
                                foundData = true;
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    if (!string.IsNullOrEmpty(genre))   VodInfoGenre.Text = genre;
                                    if (!string.IsNullOrEmpty(xOrig))   { VodInfoOrigName.Text  = xOrig;        VodInfoOrigRow.IsVisible  = true; }
                                    if (!string.IsNullOrEmpty(xDir))    { VodInfoDirector.Text  = xDir;         VodInfoDirRow.IsVisible   = true; }
                                    if (!string.IsNullOrEmpty(xCast))   { VodInfoCast.Text      = xCast;        VodInfoCastRow.IsVisible  = true; }
                                    if (!string.IsNullOrEmpty(xDur))    { VodInfoDuration.Text  = xDur + " dk"; VodInfoDurRow.IsVisible   = true; }
                                    if (!string.IsNullOrEmpty(xDate))   { VodInfoDate.Text      = xDate;        VodInfoDateRow.IsVisible  = true; }
                                    if (!string.IsNullOrEmpty(xAge))    { VodInfoAge.Text       = xAge + "+";   VodInfoAgeRow.IsVisible   = true; }
                                    if (!string.IsNullOrEmpty(xPlot))   { VodInfoPlot.Text      = xPlot;        VodInfoPlotRow.IsVisible  = true; }
                                });
                            }

                            string coverUrl = SafeGet(info, "cover_big");
                            if (string.IsNullOrEmpty(coverUrl)) coverUrl = SafeGet(info, "cover");
                            if (string.IsNullOrEmpty(coverUrl)) coverUrl = SafeGet(info, "movie_image");

                            if (!string.IsNullOrEmpty(coverUrl))
                            {
                                foundData = true;
                                try
                                {
                                    EnsureLogoHttpClient();
                                    var bytes = await _logoHttpClient!.GetByteArrayAsync(coverUrl);
                                    using var ms = new MemoryStream(bytes);
                                    var bitmap = new Bitmap(ms);
                                    await Dispatcher.UIThread.InvokeAsync(() =>
                                    {
                                        VodInfoPoster.Background = Avalonia.Media.Brushes.Transparent;
                                        VodInfoPoster.Child = new Avalonia.Controls.Image
                                            { Source = bitmap, Stretch = Avalonia.Media.Stretch.UniformToFill };
                                        card.LogoBitmap = bitmap;
                                    });
                                }
                                catch { }
                            }
                        }

                        if (foundData) break;
                    }
                    catch { continue; }
                }
            }
            catch { }
        }
    }
}