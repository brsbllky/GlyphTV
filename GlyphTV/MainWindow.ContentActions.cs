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
                var vodFavs = _allChannels
                    .Where(c => !c.IsHidden && c.IsFavorite && c.Type == "VOD")
                    .ToList();

                FavoriVodGrid.ItemsSource  = vodFavs;
                FavoriVodSection.IsVisible = vodFavs.Count > 0;

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
                var currentCards = (FavoriSeriesGrid.ItemsSource as List<SeriesCard>)
                                   ?? new List<SeriesCard>();
                var updatedCards = currentCards
                    .Where(c => favShowNames.Contains(c.ShowName))
                    .ToList();

                FavoriSeriesGrid.ItemsSource  = updatedCards;
                FavoriSeriesSection.IsVisible = updatedCards.Count > 0;
            }
            catch { }
        }
    }
}
