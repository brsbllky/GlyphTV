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
using System.Linq;
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

        // ─────────────────────────────────────────────────────────────
        // DÜZELTME: Oynatma sırasında (Sonraki Bölüm butonu / otomatik bölüm
        // geçişi) _currentChannel değişiyordu ama ekrandaki ya da önbellekteki
        // SeriesCard nesnelerinin SelectedSeasonIndex/SelectedEpisodeIndex'i
        // hiç güncellenmiyordu. Bu yüzden art arda birkaç bölüm izleyip
        // player'ı kapattığınızda kart hâlâ ilk açılan bölümü seçili
        // gösteriyor, "Devam Et" de yanlış bölümü oynatıyordu. Doğru seçim
        // sadece uygulama yeniden başlatılıp BuildSeriesCard watch history'den
        // seçimi yeniden hesapladığında ortaya çıkıyordu.
        //
        // Bu metod gerçek oynatılan bölümü; görünür gridlerdeki, sayfalama
        // listelerindeki ve kategori/favori önbelleklerindeki TÜM SeriesCard
        // kopyalarına anında yansıtır.
        // ─────────────────────────────────────────────────────────────
        private void SyncSeriesCardSelection(Channel? episode, bool? hasResume = null)
        {
            if (episode == null || episode.Type != "Dizi" || string.IsNullOrEmpty(episode.ShowName))
                return;

            void TryUpdate(SeriesCard card)
            {
                if (card.ShowName != episode.ShowName) return;

                int seasonIdx = card.Seasons.IndexOf(episode.Season);
                if (seasonIdx >= 0)
                {
                    var seasonEps = card.EpisodesBySeason.TryGetValue(episode.Season, out var eps)
                        ? eps : new List<Channel>();
                    int episodeIdx = seasonEps.FindIndex(e => e.Url == episode.Url);
                    if (episodeIdx >= 0)
                    {
                        card.RestoreSelection(seasonIdx, episodeIdx);
                        _seriesSelections[episode.ShowName] = (seasonIdx, episodeIdx);
                    }
                }

                if (hasResume.HasValue) card.HasResume = hasResume.Value;
            }

            try
            {
                // KALICI DÜZELTME: ItemsSource cast'i yerine artık sabit
                // ObservableCollection referansları doğrudan kullanılıyor.
                foreach (var c in _displaySeriesCards) TryUpdate(c);
                foreach (var c in _displayFavoriSeriesCards) TryUpdate(c);

                foreach (var c in _allFilteredCards) TryUpdate(c);
                foreach (var c in _allFavoriSeriesCards) TryUpdate(c);

                foreach (var cacheList in _seriesCardCache.Values)
                    foreach (var c in cacheList) TryUpdate(c);
            }
            catch { }
        }

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

    }
}