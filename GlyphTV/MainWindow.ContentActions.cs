// ============================================================
// MainWindow.ContentActions.cs
// İçerik üzerindeki kullanıcı aksiyonları:
//   Favori ekle/çıkar, İçeriği gizle, Gizlilikten geri al
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

            // Favori sekmesinde anında güncelle – ItemsSource'u yerinde filtrele.
            // UpdateView() tam rebuild yaptığı için Avalonia'nın binding döngüsü
            // henüz bitmemişken ItemsSource değişirse NullReferenceException çıkar.
            // Bunun yerine sadece etkilenen grid'leri güncelliyoruz.
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

                // Mevcut kartları koru (LogoBitmap kaybolmasın), sadece listeyi filtrele
                var currentCards = (FavoriSeriesGrid.ItemsSource as List<SeriesCard>)
                                   ?? new List<SeriesCard>();
                var updatedCards = currentCards
                    .Where(c => favShowNames.Contains(c.ShowName))
                    .ToList();

                FavoriSeriesGrid.ItemsSource   = updatedCards;
                FavoriSeriesSection.IsVisible  = updatedCards.Count > 0;
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // İçeriği gizle
        // ─────────────────────────────────────────────────────────────
        private void HideContent_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Channel channel) return;

            channel.IsHidden = true;

            var activeSource = _sources.FirstOrDefault(s => s.IsActive);
            if (activeSource != null) SaveChannelsForSource(activeSource.Id);

            UpdateView();
            ShowToast("İçerik gizlendi. Ayarlar'dan geri alabilirsiniz.");
        }

        // ─────────────────────────────────────────────────────────────
        // Gizli içeriği geri getir (Ayarlar panelinden)
        // ─────────────────────────────────────────────────────────────
        private void RestoreHidden_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Channel channel) return;

            channel.IsHidden = false;

            var activeSource = _sources.FirstOrDefault(s => s.IsActive);
            if (activeSource != null) SaveChannelsForSource(activeSource.Id);

            UpdateView();
            ShowToast("İçerik tekrar görünür yapıldı.");
        }
    }
}
