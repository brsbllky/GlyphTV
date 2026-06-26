// ============================================================
// MainWindow.Navigation.cs
// Sekme/kategori navigasyonu, UpdateView, arama
// ============================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow
    {
        // ─── Debounce timer (arama) ───────────────────────────────────
        private DispatcherTimer? _searchDebounceTimer;

        // Favori sekmesinden canlı TV kategorisine girildiğinde
        // hangi türde içerik gösterileceğini tutar ("Canlı" / "VOD" / "Dizi")
        private string _favoriCategoryType = "Canlı";

        // ─────────────────────────────────────────────────────────────
        // Yardımcı: ObservableCollection'u toplu güncelle (performans)
        // ─────────────────────────────────────────────────────────────
        private void ReplaceCollection<T>(ObservableCollection<T> col, IEnumerable<T> items)
        {
            col.Clear();
            foreach (var item in items) col.Add(item);
        }

        // ─────────────────────────────────────────────────────────────
        // Grid görünürlük yönetimi
        // ─────────────────────────────────────────────────────────────
        private void SetGridVisibility(bool categories, bool content, bool settings)
        {
            CategoriesGrid.IsVisible  = categories;
            SettingsPanel.IsVisible   = settings;
            FavoriPanel.IsVisible     = false;
            SeriesContentGrid.IsVisible = false;

            bool showAsList = _currentTab == "Canlı" ||
                              (_currentTab == "Favori" && _favoriCategoryType == "Canlı");

            bool isVodContent = content && !showAsList;
            ContentItemsGrid.IsVisible = content && !isVodContent;
            VodContentGrid.IsVisible   = isVodContent;
        }

        // ─────────────────────────────────────────────────────────────
        // Ana menü filtre butonları (Canlı / VOD / Dizi / Favori / Ayarlar)
        // ─────────────────────────────────────────────────────────────
        private void MenuFilter_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag == null) return;

            BtnLive.Classes.Remove("Active");
            BtnVOD.Classes.Remove("Active");
            BtnSeries.Classes.Remove("Active");
            BtnFav.Classes.Remove("Active");
            BtnSettings.Classes.Remove("Active");
            btn.Classes.Add("Active");

            string tab = btn.Tag.ToString()!;

            if (tab == "Ayarlar")
            {
                _viewState = "Settings";
                UpdateView();
                return;
            }

            _currentTab      = tab;
            _currentCategory = "";
            _viewState       = "Categories";
            UpdateView();
        }

        // ─────────────────────────────────────────────────────────────
        // Kategori tıklaması (normal kategori kartları)
        // ─────────────────────────────────────────────────────────────
        private void Category_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag == null) return;

            string clicked = btn.Tag.ToString()!;

            if (_viewState == "Categories")
            {
                _currentCategory = clicked;
                _viewState = _currentTab == "Dizi" ? "Shows" : "Content";
            }

            UpdateView();
        }

        // ─────────────────────────────────────────────────────────────
        // Favori Panel – Canlı TV kategorisi tıklaması
        // ─────────────────────────────────────────────────────────────
        private void FavoriCategory_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag == null) return;

            _currentCategory    = btn.Tag.ToString()!;
            _favoriCategoryType = "Canlı";
            _viewState          = "Content";
            UpdateView();
        }

        // ─────────────────────────────────────────────────────────────
        // Geri butonu
        // ─────────────────────────────────────────────────────────────
        private void Back_Click(object? sender, RoutedEventArgs e)
        {
            _viewState = "Categories";
            _currentCategory = "";

            // Kanal / VOD / Dizi listelerini önceden temizle ki ScrollViewer'ın
            // içerik yüksekliği (Extent) hemen küçülsün ve eski scroll konumuna
            // göre yeniden konumlandırma yapılmasın.
            _displayContents.Clear();
            ContentItemsGrid.IsVisible  = false;
            VodContentGrid.ItemsSource  = null;
            VodContentGrid.IsVisible    = false;
            SeriesContentGrid.ItemsSource = null;
            SeriesContentGrid.IsVisible = false;

            UpdateView();

            ResetScrollToTop();
        }

        // ─────────────────────────────────────────────────────────────
        // Arama kutusu – 300ms debounce
        // ─────────────────────────────────────────────────────────────
        private void SearchBox_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
        {
            _searchDebounceTimer?.Stop();
            if (_searchDebounceTimer == null)
            {
                _searchDebounceTimer = new DispatcherTimer
                    { Interval = System.TimeSpan.FromMilliseconds(300) };
                _searchDebounceTimer.Tick += (s, args) =>
                {
                    _searchDebounceTimer!.Stop();
                    UpdateView();
                };
            }
            _searchDebounceTimer.Start();
        }

        // ─────────────────────────────────────────────────────────────
        // Kategori dropdown (içerik taşıma)
        // ─────────────────────────────────────────────────────────────
        private void CategoryDropdown_Opened(object? sender, System.EventArgs e)
        {
            if (sender is ComboBox combo)
            {
                var allGroups = _allChannels.Select(c => c.Group).Distinct().OrderBy(g => g).ToList();
                combo.ItemsSource = allGroups;
            }
        }

        private void CategoryDropdown_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.Tag is Channel channel && combo.SelectedItem != null)
            {
                string newGroup = combo.SelectedItem.ToString()!;
                if (channel.Group != newGroup)
                {
                    channel.Group = newGroup;

                    var activeSource = _sources.FirstOrDefault(s => s.IsActive);
                    if (activeSource != null) SaveChannelsForSource(activeSource.Id);

                    UpdateView();
                    ShowToast($"İçerik '{newGroup}' kategorisine taşındı.");
                }
                combo.SelectedItem = null;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // UpdateView – tüm state'leri yönetir
        // ─────────────────────────────────────────────────────────────
        private void UpdateView()
        {
            try { UpdateViewInternal(); }
            catch { }
        }

        private void UpdateViewInternal()
        {
            if (_watchHistoryByUrlCache == null)
            {
                _watchHistoryByUrlCache = _watchHistory
                    .GroupBy(h => h.Url)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.LastWatched).First());
            }
            var historyByUrl = _watchHistoryByUrlCache;

            string searchText = SearchBox.Text?.ToLower() ?? "";

            // ── Global arama ──────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(searchText) && _viewState != "Settings")
            {
                PageTitle.Text = $"Arama: \"{SearchBox.Text}\"";
                BackBtn.IsVisible = false;
                CategoriesGrid.IsVisible    = false;
                ContentItemsGrid.IsVisible  = false;
                VodContentGrid.IsVisible    = true;
                SettingsPanel.IsVisible     = false;
                FavoriPanel.IsVisible       = false;

                var searchResults = _allChannels
                    .Where(c => !c.IsHidden && c.Name.ToLower().Contains(searchText))
                    .Take(500)
                    .ToList();

                var seriesResults = searchResults
                    .Where(c => c.Type == "Dizi" && !string.IsNullOrEmpty(c.ShowName))
                    .ToList();
                var nonSeriesResults = searchResults
                    .Where(c => c.Type != "Dizi" || string.IsNullOrEmpty(c.ShowName))
                    .ToList();

                foreach (var ch in nonSeriesResults)
                    ch.HasResume = historyByUrl.TryGetValue(ch.Url, out var h) && h.Position > 5000;

                if (seriesResults.Count > 0)
                {
                    var showNames = seriesResults.Select(c => c.ShowName).Distinct().Take(50).ToList();
                    var seriesCards = new List<SeriesCard>();
                    foreach (var sn in showNames)
                    {
                        var eps = _allChannels
                            .Where(c => !c.IsHidden && c.Type == "Dizi" && c.ShowName == sn)
                            .ToList();
                        seriesCards.Add(BuildSeriesCard(sn, eps, historyByUrl));
                    }
                    SeriesContentGrid.ItemsSource = seriesCards;
                    SeriesContentGrid.IsVisible   = seriesCards.Count > 0;
                    _ = LoadTmdbPostersForCards(seriesCards);
                    VodContentGrid.ItemsSource = nonSeriesResults;
                    VodContentGrid.IsVisible   = nonSeriesResults.Count > 0;
                    ContentItemsGrid.IsVisible = false;
                    _ = LoadLogosForSeriesCards(seriesCards, seriesResults);
                    _ = LoadLogosForChannelsAsync(nonSeriesResults);
                }
                else
                {
                    SeriesContentGrid.IsVisible = false;
                    VodContentGrid.ItemsSource  = nonSeriesResults;
                    _ = LoadLogosForChannelsAsync(nonSeriesResults);
                }
                return;
            }

            // ── Settings ──────────────────────────────────────────────
            if (_viewState == "Settings")
            {
                PageTitle.Text    = "Ayarlar";
                BackBtn.IsVisible = false;
                SetGridVisibility(false, false, true);
                return;
            }

            // ── Categories ────────────────────────────────────────────
            if (_viewState == "Categories")
            {
                // ── FAVORİLER – özel 3 bölümlü panel ─────────────────
                if (_currentTab == "Favori")
                {
                    PageTitle.Text    = "Favoriler";
                    BackBtn.IsVisible = false;

                    CategoriesGrid.IsVisible    = false;
                    ContentItemsGrid.IsVisible  = false;
                    VodContentGrid.IsVisible    = false;
                    SeriesContentGrid.IsVisible = false;
                    SettingsPanel.IsVisible     = false;
                    FavoriPanel.IsVisible       = true;

                    var liveFavGroups = _allChannels
                        .Where(c => !c.IsHidden && c.IsFavorite && c.Type == "Canlı")
                        .Select(c => c.Group)
                        .Distinct()
                        .OrderBy(g => g)
                        .ToList();

                    FavoriLiveSection.IsVisible = liveFavGroups.Count > 0;
                    FavoriLiveGrid.ItemsSource  = liveFavGroups;

                    var vodFavs = _allChannels
                        .Where(c => !c.IsHidden && c.IsFavorite && c.Type == "VOD")
                        .ToList();

                    foreach (var ch in vodFavs)
                        ch.HasResume = historyByUrl.TryGetValue(ch.Url, out var h) && h.Position > 5000;

                    FavoriVodSection.IsVisible = vodFavs.Count > 0;
                    FavoriVodGrid.ItemsSource  = vodFavs;
                    SafeRun(() => LoadLogosForChannelsAsync(vodFavs));
                    SafeRun(() => LoadTmdbPostersForChannels(vodFavs));

                    var seriesFavEps = _allChannels
                        .Where(c => !c.IsHidden && c.IsFavorite && c.Type == "Dizi"
                                 && !string.IsNullOrEmpty(c.ShowName))
                        .ToList();

                    var favShowNames = seriesFavEps
                        .Select(c => c.ShowName)
                        .Distinct()
                        .OrderBy(s => s)
                        .ToList();

                    var favSeriesCards = new List<SeriesCard>();
                    foreach (var sn in favShowNames)
                    {
                        var allEps = _allChannels
                            .Where(c => !c.IsHidden && c.Type == "Dizi" && c.ShowName == sn)
                            .ToList();
                        favSeriesCards.Add(BuildSeriesCard(sn, allEps, historyByUrl));
                    }

                    FavoriSeriesSection.IsVisible = favSeriesCards.Count > 0;
                    FavoriSeriesGrid.ItemsSource  = favSeriesCards;
                    SafeRun(() => LoadTmdbPostersForCards(favSeriesCards));

                    return;
                }

                // ── Normal kategori listesi (Canlı / VOD / Dizi) ──────
                PageTitle.Text = _currentTab switch
                {
                    "Canlı" => "Canlı TV",
                    "VOD"   => "Filmler",
                    _       => "Diziler"
                };
                BackBtn.IsVisible = false;
                SetGridVisibility(true, false, false);

                _displayCategories.Clear();
                var filteredList = _allChannels
                    .Where(c => !c.IsHidden && c.Type == _currentTab)
                    .ToList();

                var groups = filteredList
                    .Select(c => c.Group)
                    .Distinct()
                    .OrderBy(g => g);

                foreach (var g in groups)
                    _displayCategories.Add(g);

                return;
            }

            // ── Content ───────────────────────────────────────────────
            if (_viewState == "Content")
            {
                PageTitle.Text    = _currentCategory;
                BackBtn.IsVisible = true;
                SetGridVisibility(false, true, false);
                _displayContents.Clear();

                bool isFavoriLive = _currentTab == "Favori" && _favoriCategoryType == "Canlı";
                bool isCanlı      = _currentTab == "Canlı" || isFavoriLive;

                string cacheKey = $"{_currentTab}_{_favoriCategoryType}_{_currentCategory}";

                if (_contentCache.TryGetValue(cacheKey, out var cachedList) &&
                    cachedList.Count > 0 && string.IsNullOrEmpty(searchText))
                {
                    foreach (var ch in cachedList)
                        ch.HasResume = historyByUrl.TryGetValue(ch.Url, out var h) && h.Position > 5000;

                    _allFilteredContents = cachedList;
                    _loadedCount         = cachedList.Count;

                    if (isCanlı) ReplaceCollection(_displayContents, cachedList);
                    else         VodContentGrid.ItemsSource = cachedList;

                    ContentScrollViewer.Offset = new Avalonia.Vector(0, 0);

                    var needsPoster = cachedList.Where(c => c.LogoBitmap == null).ToList();
                    if (needsPoster.Count > 0)
                    {
                        _ = LoadLogosForChannelsAsync(needsPoster);
                        if (!isCanlı) _ = LoadTmdbPostersForChannels(needsPoster);
                    }
                }
                else
                {
                    ContentScrollViewer.Offset = new Avalonia.Vector(0, 0);

                    IEnumerable<Channel> filteredContents = _allChannels
                        .Where(c => !c.IsHidden && c.Group == _currentCategory);

                    if (_currentTab == "Favori")
                        filteredContents = filteredContents
                            .Where(c => c.IsFavorite && c.Type == _favoriCategoryType);
                    else
                        filteredContents = filteredContents.Where(c => c.Type == _currentTab);

                    var allContents = filteredContents
                        .Where(c => string.IsNullOrEmpty(searchText) ||
                                    c.Name.ToLower().Contains(searchText))
                        .ToList();

                    _allFilteredContents = allContents;
                    _loadedCount         = 0;

                    foreach (var ch in allContents)
                        ch.HasResume = historyByUrl.TryGetValue(ch.Url, out var h) && h.Position > 5000;

                    var firstBatch = _allFilteredContents.Take(PAGE_SIZE).ToList();
                    _loadedCount   = firstBatch.Count;

                    if (isCanlı) ReplaceCollection(_displayContents, firstBatch);
                    else         VodContentGrid.ItemsSource = firstBatch;

                    _ = LoadLogosForChannelsAsync(firstBatch);
                    if (!isCanlı) _ = LoadTmdbPostersForChannels(firstBatch);

                    if (_loadedCount >= _allFilteredContents.Count)
                        _contentCache[cacheKey] = _allFilteredContents;
                }
                return;
            }

            // ── Shows (Dizi poster kartları) ──────────────────────────
            if (_viewState == "Shows")
            {
                PageTitle.Text = _currentCategory;
                BackBtn.IsVisible = true;
                CategoriesGrid.IsVisible    = false;
                ContentItemsGrid.IsVisible  = false;
                VodContentGrid.IsVisible    = false;
                SeriesContentGrid.IsVisible = true;
                SettingsPanel.IsVisible     = false;
                FavoriPanel.IsVisible       = false;

                string cacheKey = $"Dizi_{_currentCategory}";

                if (_seriesCardCache.TryGetValue(cacheKey, out var cachedCards) &&
                    cachedCards.Count > 0 && string.IsNullOrEmpty(searchText))
                {
                    foreach (var card in cachedCards)
                    {
                        var ep = card.SelectedEpisode;
                        if (ep != null)
                            card.HasResume = historyByUrl.TryGetValue(ep.Url, out var h) && h.Position > 5000;
                    }
                    _allFilteredCards = cachedCards;
                    _loadedCount      = cachedCards.Count;
                    SeriesContentGrid.ItemsSource = cachedCards;
                    ContentScrollViewer.Offset = new Avalonia.Vector(0, 0);

                    var needsPoster = cachedCards.Where(c => c.LogoBitmap == null).ToList();
                    if (needsPoster.Count > 0) _ = LoadTmdbPostersForCards(needsPoster);
                }
                else
                {
                    ContentScrollViewer.Offset = new Avalonia.Vector(0, 0);

                    var seriesEpisodes = _allChannels
                        .Where(c => !c.IsHidden && c.Type == "Dizi" && c.Group == _currentCategory)
                        .ToList();

                    var showNames = seriesEpisodes
                        .Select(c => c.ShowName)
                        .Distinct()
                        .Where(s => string.IsNullOrEmpty(searchText) || s.ToLower().Contains(searchText))
                        .OrderBy(s => s)
                        .ToList();

                    var allCards = new List<SeriesCard>();
                    foreach (var showName in showNames)
                    {
                        var episodes = seriesEpisodes.Where(c => c.ShowName == showName).ToList();
                        allCards.Add(BuildSeriesCard(showName, episodes, historyByUrl));
                    }

                    _allFilteredCards = allCards;
                    _loadedCount      = 0;
                    var firstBatch    = _allFilteredCards.Take(PAGE_SIZE).ToList();
                    _loadedCount      = firstBatch.Count;
                    SeriesContentGrid.ItemsSource = firstBatch;
                    _ = LoadTmdbPostersForCards(firstBatch);

                    if (_loadedCount >= _allFilteredCards.Count)
                        _seriesCardCache[$"Dizi_{_currentCategory}"] = _allFilteredCards;
                }
            }
        }

        /// <summary>
        /// Async Task döndüren metodları fire-and-forget olarak çalıştırır.
        /// </summary>
        private static async void SafeRun(Func<System.Threading.Tasks.Task> action)
        {
            try { await action(); }
            catch { }
        }
    }
}
