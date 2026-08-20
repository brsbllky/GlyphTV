// ============================================================
// MainWindow.ContentActions.cs
// İçerik üzerindeki kullanıcı aksiyonları:
//   Favori ekle/çıkar
// ============================================================

using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.Generic;
using System.Linq;

namespace GlyphTV
{
    public partial class MainWindow
    {
        // ─────────────────────────────────────────────────────────────
        // Favori toggle (VOD / Canlı kart)
        // ─────────────────────────────────────────────────────────────
        private void ToggleFavorite_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Channel channel) return;

            channel.IsFavorite = !channel.IsFavorite;

            var activeSource = _sources.FirstOrDefault(s => s.IsActive);
            if (activeSource != null) SaveChannelsForSource(activeSource.Id);

            if (_currentTab == "Favori" && _viewState == "Categories")
                RefreshFavoriGrids();

            try
            {
                foreach (var res in _displayResumeItems)
                {
                    if (res.Channel == channel || (res.History != null && res.History.Url == channel.Url))
                    {
                        if (res.Channel == null) res.Channel = channel;
                        res.NotifyFavoriteChanged();
                    }
                }
                foreach (var res in _allResumeItems)
                {
                    if (res.Channel == channel || (res.History != null && res.History.Url == channel.Url))
                    {
                        if (res.Channel == null) res.Channel = channel;
                        res.NotifyFavoriteChanged();
                    }
                }
            }
            catch { }

            ShowToast(channel.IsFavorite ? "Favorilere eklendi" : "Favorilerden çıkarıldı");
        }

        /// <summary>
        /// Favori panelindeki grid'leri UpdateView() çağırmadan günceller.
        /// Async poster/logo yüklemesi tetiklenmez; mevcut bitmap'ler korunur.
        /// </summary>
        private void RefreshFavoriGrids()
        {
            try
            {
                // Canlı TV: favori grup adları
                var liveFavGroups = _allChannels
                    .Where(c => !c.IsHidden && c.IsFavorite && c.Type == "Canlı")
                    .Select(c => c.Group)
                    .Distinct()
                    .OrderBy(g => g)
                    .ToList();

                FavoriLiveGrid.ItemsSource  = liveFavGroups;
                FavoriLiveSection.IsVisible = liveFavGroups.Count > 0;

                // Filmler: favori VOD kanalları
                // (Async poster/logo tetiklenmez; mevcut bitmap'ler korunur.
                //  Sayfalama listesi de (_allFavoriVod) güncel favori durumuna
                //  göre senkronize edilir ki sonraki scroll ile daha fazla
                //  yükleme adımı tutarlı kalsın.)
                var vodFavsAll = _allChannels
                    .Where(c => !c.IsHidden && c.IsFavorite && c.Type == "VOD")
                    .ToList();
                _allFavoriVod = vodFavsAll;

                var stillFavoriteVodUrls = vodFavsAll.Select(c => c.Url).ToHashSet();
                var updatedVodCards = _displayFavoriVod
                    .Where(c => stillFavoriteVodUrls.Contains(c.Url))
                    .ToList();

                _favoriVodLoadedCount = updatedVodCards.Count;
                ReplaceCollection(_displayFavoriVod, updatedVodCards);
                FavoriVodSection.IsVisible = updatedVodCards.Count > 0;

                // Diziler: favori bölüm içeren dizi kartları
                var seriesFavEps = _allChannels
                    .Where(c => !c.IsHidden && c.IsFavorite && c.Type == "Dizi"
                             && !string.IsNullOrEmpty(c.ShowName))
                    .ToList();

                var favShowNames = seriesFavEps
                    .Select(c => c.ShowName)
                    .Distinct()
                    .OrderBy(s => s)
                    .ToList();

                // Mevcut kartları koru (LogoBitmap kaybolmasın)
                var updatedCards = _displayFavoriSeriesCards
                    .Where(c => favShowNames.Contains(c.ShowName))
                    .ToList();

                // Sayfalama listesini (_allFavoriSeriesCards) de güncel favori
                // duruma göre senkronize et — aksi halde scroll ile sonradan
                // yüklenecek öğeler eski/yanlış bir listeden gelebilir.
                _allFavoriSeriesCards   = _allFavoriSeriesCards
                    .Where(c => favShowNames.Contains(c.ShowName))
                    .ToList();
                _favoriSeriesLoadedCount = updatedCards.Count;

                ReplaceCollection(_displayFavoriSeriesCards, updatedCards);
                FavoriSeriesSection.IsVisible = updatedCards.Count > 0;
            }
            catch { }
        }
    }
}
