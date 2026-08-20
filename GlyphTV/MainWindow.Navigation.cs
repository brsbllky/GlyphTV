// ============================================================
// MainWindow.Navigation.cs
// Sekme/kategori navigasyonu, UpdateView, arama, filtreleme & Debug
// ============================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
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

        private string? _draggingCategory = null;

        // ─── Kategori içerik önbellek limitleri (RAM optimizasyonu) ────
        private const int MAX_CATEGORY_CACHE = 8;
        private static readonly Queue<string> _contentCacheOrder = new();
        private static readonly Queue<string> _seriesCardCacheOrder = new();

        private static void SetContentCache(string key, List<Channel> channels)
        {
            if (_contentCache.ContainsKey(key))
            {
                _contentCache[key] = channels;
                return;
            }
            while (_contentCacheOrder.Count >= MAX_CATEGORY_CACHE && _contentCacheOrder.Count > 0)
            {
                string oldest = _contentCacheOrder.Dequeue();
                _contentCache.Remove(oldest);
            }
            _contentCache[key] = channels;
            _contentCacheOrder.Enqueue(key);
        }

        private static void SetSeriesCardCache(string key, List<SeriesCard> cards)
        {
            if (_seriesCardCache.ContainsKey(key))
            {
                _seriesCardCache[key] = cards;
                return;
            }
            while (_seriesCardCacheOrder.Count >= MAX_CATEGORY_CACHE && _seriesCardCacheOrder.Count > 0)
            {
                string oldest = _seriesCardCacheOrder.Dequeue();
                _seriesCardCache.Remove(oldest);
            }
            _seriesCardCache[key] = cards;
            _seriesCardCacheOrder.Enqueue(key);
        }

        // ─────────────────────────────────────────────────────────────
        // Yardımcı: ObservableCollection'u güncelle
        // ─────────────────────────────────────────────────────────────
        private void ReplaceCollection<T>(ObservableCollection<T> col, IEnumerable<T> items)
        {
            if (col == null) return;
            var list = items is IList<T> l ? l : items.ToList();

            // RowGroupedCollection ve Avalonia UI'ın sıralama değişimini
            // kesin olarak algılaması için temizleyip yeniden ekliyoruz:
            col.Clear();
            foreach (var item in list)
            {
                col.Add(item);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Grid görünürlük yönetimi
        // ─────────────────────────────────────────────────────────────
        private void SetGridVisibility(bool categories, bool content)
        {
            CategoryListPanel.IsVisible = categories;
            FavoriPanel.IsVisible = false;
            HomePanel.IsVisible = false;
            SeriesContentGrid.IsVisible = false;

            bool isCanlıList = _currentTab == "Canlı" ||
                               (_currentTab == "Favori" && _favoriCategoryType == "Canlı");

            ContentItemsGrid.IsVisible = content && isCanlıList;
            VodContentGrid.IsVisible = content && !isCanlıList;

            SortOptionsPanel.IsVisible = content && !isCanlıList;
        }

        // ─────────────────────────────────────────────────────────────
        // Ana menü filtre butonları (Anasayfa / Canlı / VOD / Dizi / Favori)
        // ─────────────────────────────────────────────────────────────
        private void MenuFilter_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag == null) return;

            BtnHome.Classes.Remove("Active");
            BtnLive.Classes.Remove("Active");
            BtnVOD.Classes.Remove("Active");
            BtnSeries.Classes.Remove("Active");
            BtnFav.Classes.Remove("Active");
            btn.Classes.Add("Active");

            string tab = btn.Tag.ToString()!;
            Debug.WriteLine($"[NAV] Menü Sekmesi Değişti: {tab}");

            _currentTab = tab;
            _currentCategory = "";
            _viewState = "Categories";

            // Arama modundaysa arama kutusunu temizle ve zamanlayıcıyı durdur
            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                _searchDebounceTimer?.Stop();
                SearchBox.Text = "";
            }

            Dispatcher.UIThread.Post(() => TrimProcessMemoryLight(), Avalonia.Threading.DispatcherPriority.Background);
            UpdateView();
            ResetScrollToTop();
        }

        private void SettingsButton_Click(object? sender, RoutedEventArgs e)
        {
            HidePlayerLayerForModal();
            InitializeMpvEqSliderValues();
            UpdateAutoRefreshButtonsActiveState();
            UpdatePlayerEngineButtonsActiveState();
            UpdateScalingQualityButtonsActiveState();
            UpdateThemeButtonsActiveState(_appSettings.ThemeMode);
            SwitchSettingsTab("Sources");
            SettingsModalOverlay.IsVisible = true;
        }

        // ─────────────────────────────────────────────────────────────
        // Kategori tıklaması (normal kategori kartları)
        // ─────────────────────────────────────────────────────────────
        private void Category_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag == null) return;

            string clicked = btn.Tag.ToString()!;
            _currentCategory = clicked;
            Debug.WriteLine($"[NAV] Kategori Seçildi: '{clicked}', Sekme: '{_currentTab}'");

            if (_viewState == "Categories")
                _viewState = _currentTab == "Dizi" ? "Shows" : "Content";

            UpdateView();
        }

        // ─────────────────────────────────────────────────────────────
        // Favori Panel – Canlı TV kategorisi tıklaması
        // ─────────────────────────────────────────────────────────────
        private void FavoriCategory_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag == null) return;

            _currentCategory = btn.Tag.ToString()!;
            _favoriCategoryType = "Canlı";
            _viewState = "Content";
            UpdateView();
        }

        // ─────────────────────────────────────────────────────────────
        // Geri butonu
        // ─────────────────────────────────────────────────────────────
        private void Back_Click(object? sender, RoutedEventArgs e)
        {
            _viewState = "Categories";
            _currentCategory = "";

            _displayContents.Clear();
            _displayVodContents.Clear();
            _displaySeriesCards.Clear();

            ContentItemsGrid.IsVisible = false;
            VodContentGrid.IsVisible = false;
            SeriesContentGrid.IsVisible = false;

            UpdateView();
            ResetScrollToTop();
        }

        // ─────────────────────────────────────────────────────────────
        // Arama kutusu – 300ms debounce
        // ─────────────────────────────────────────────────────────────
        private void SearchBox_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
        {
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

            _searchDebounceTimer.Stop();
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
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] UpdateView Hatası: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void UpdateViewInternal()
        {
            VodContentGrid.Classes.Remove("SearchMode");
            SeriesContentGrid.Classes.Remove("SearchMode");
            SearchLiveLabel.IsVisible = false;
            SearchVodLabel.IsVisible = false;
            SearchSeriesLabel.IsVisible = false;

            var historyByUrl = GetWatchHistoryByUrlCache();

            string searchText = SearchBox.Text?.Trim() ?? "";

            // ── Global arama ──────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(searchText) && _viewState != "Settings")
            {
                PageTitlePanel.IsVisible = true;
                PageTitle.Text = $"Arama: \"{SearchBox.Text}\"";
                PageTitle.IsVisible = true;
                BackBtn.IsVisible = false;
                CategoryListPanel.IsVisible = false;
                SortOptionsPanel.IsVisible = false;
                FavoriPanel.IsVisible = false;
                HomePanel.IsVisible = false;

                VodContentGrid.Classes.Add("SearchMode");
                SeriesContentGrid.Classes.Add("SearchMode");

                var searchResults = _allChannels
                    .Where(c => !c.IsHidden && c.Name.Contains(searchText, StringComparison.CurrentCultureIgnoreCase))
                    .Take(500)
                    .ToList();

                var seriesResults = searchResults
                    .Where(c => c.Type == "Dizi" && !string.IsNullOrEmpty(c.ShowName))
                    .ToList();
                var liveResults = searchResults
                    .Where(c => c.Type == "Canlı")
                    .ToList();
                var vodResults = searchResults
                    .Where(c => c.Type == "VOD")
                    .ToList();

                foreach (var ch in liveResults) ch.HasResume = false;
                foreach (var ch in vodResults)
                    ch.HasResume = historyByUrl.TryGetValue(ch.Url, out var h) && h.Position > 5000;

                ReplaceCollection(_displayContents, liveResults);
                ContentItemsGrid.IsVisible = liveResults.Count > 0;
                SearchLiveLabel.IsVisible = liveResults.Count > 0;
                if (liveResults.Count > 0) _ = LoadLogosForChannelsAsync(liveResults);

                ReplaceCollection(_displayVodContents, vodResults);
                VodContentGrid.IsVisible = vodResults.Count > 0;
                SearchVodLabel.IsVisible = vodResults.Count > 0;
                if (vodResults.Count > 0)
                {
                    _ = LoadLogosForChannelsAsync(vodResults);
                    _ = LoadTmdbPostersForChannels(vodResults);
                }

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
                    ReplaceCollection(_displaySeriesCards, seriesCards);
                    SeriesContentGrid.IsVisible = true;
                    SearchSeriesLabel.IsVisible = true;
                    _ = LoadLogosForSeriesCards(seriesCards, seriesResults);
                    _ = LoadTmdbPostersForCards(seriesCards);
                }
                else
                {
                    ReplaceCollection(_displaySeriesCards, new List<SeriesCard>());
                    SeriesContentGrid.IsVisible = false;
                    SearchSeriesLabel.IsVisible = false;
                }
                return;
            }

            // ── Categories ────────────────────────────────────────────
            if (_viewState == "Categories")
            {
                if (_currentTab == "Anasayfa")
                {
                    PageTitlePanel.IsVisible = false;
                    PageTitle.Text = "Anasayfa";
                    PageTitle.IsVisible = false;
                    BackBtn.IsVisible = false;

                    CategoryListPanel.IsVisible = false;
                    SortOptionsPanel.IsVisible = false;
                    ContentItemsGrid.IsVisible = false;
                    VodContentGrid.IsVisible = false;
                    SeriesContentGrid.IsVisible = false;
                    FavoriPanel.IsVisible = false;
                    HomePanel.IsVisible = true;

                    RefreshHomeView();
                    return;
                }

                HomePanel.IsVisible = false;
                PageTitlePanel.IsVisible = true;
                PageTitle.IsVisible = true;

                if (_currentTab == "Favori")
                {
                    PageTitle.Text = "Favoriler";
                    BackBtn.IsVisible = false;

                    CategoryListPanel.IsVisible = false;
                    SortOptionsPanel.IsVisible = false;
                    ContentItemsGrid.IsVisible = false;
                    VodContentGrid.IsVisible = false;
                    SeriesContentGrid.IsVisible = false;
                    FavoriPanel.IsVisible = true;

                    var liveFavGroups = _allChannels
                        .Where(c => !c.IsHidden && c.IsFavorite && c.Type == "Canlı")
                        .Select(c => c.Group)
                        .Distinct()
                        .OrderBy(g => g)
                        .ToList();

                    FavoriLiveSection.IsVisible = liveFavGroups.Count > 0;
                    FavoriLiveGrid.ItemsSource = liveFavGroups;

                    var vodFavs = _allChannels
                        .Where(c => !c.IsHidden && c.IsFavorite && c.Type == "VOD")
                        .ToList();

                    foreach (var ch in vodFavs)
                        ch.HasResume = historyByUrl.TryGetValue(ch.Url, out var h) && h.Position > 5000;

                    _allFavoriVod = vodFavs;
                    _favoriVodLoadedCount = Math.Min(FAVORI_PAGE_SIZE, vodFavs.Count);
                    var vodFirstBatch = vodFavs.Take(_favoriVodLoadedCount).ToList();

                    FavoriVodSection.IsVisible = vodFavs.Count > 0;
                    ReplaceCollection(_displayFavoriVod, vodFirstBatch);
                    SafeRun(() => LoadLogosForChannelsAsync(vodFirstBatch));
                    SafeRun(() => LoadTmdbPostersForChannels(vodFirstBatch));

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

                    _allFavoriSeriesCards = favSeriesCards;
                    _favoriSeriesLoadedCount = Math.Min(FAVORI_PAGE_SIZE, favSeriesCards.Count);
                    var seriesFirstBatch = favSeriesCards.Take(_favoriSeriesLoadedCount).ToList();

                    FavoriSeriesSection.IsVisible = favSeriesCards.Count > 0;
                    ReplaceCollection(_displayFavoriSeriesCards, seriesFirstBatch);
                    SafeRun(() => LoadTmdbPostersForCards(seriesFirstBatch));

                    Dispatcher.UIThread.Post(() =>
                    {
                        UpdateHArrowVisibility(FavoriLiveScrollViewer, FavoriLivePrevBtn, FavoriLiveNextBtn);
                        UpdateHArrowVisibility(FavoriVodScrollViewer, FavoriVodPrevBtn, FavoriVodNextBtn);
                        UpdateHArrowVisibility(FavoriSeriesScrollViewer, FavoriSeriesPrevBtn, FavoriSeriesNextBtn);
                    }, DispatcherPriority.Loaded);

                    return;
                }

                // ── Normal kategori listesi (Canlı / VOD / Dizi) ──────
                PageTitle.Text = _currentTab switch
                {
                    "Canlı" => "Canlı TV",
                    "VOD" => "Filmler",
                    _ => "Diziler"
                };
                BackBtn.IsVisible = false;
                SetGridVisibility(true, false);

                var filteredList = _allChannels
                    .Where(c => !c.IsHidden && c.Type == _currentTab)
                    .ToList();

                var groups = filteredList
                    .Select(c => c.Group)
                    .Distinct()
                    .ToList();

                groups = ApplyCategoryOrder(_currentTab, groups);

                ReplaceCollection(_displayCategories, groups);
                UpdateCategorySelectionVisual();

                return;
            }

            // ── Content ───────────────────────────────────────────────
            if (_viewState == "Content")
            {
                PageTitlePanel.IsVisible = true;
                PageTitle.Text = _currentCategory;
                PageTitle.IsVisible = true;
                BackBtn.IsVisible = true;
                SetGridVisibility(true, true);
                _displayContents.Clear();

                bool isFavoriLive = _currentTab == "Favori" && _favoriCategoryType == "Canlı";
                bool isCanlı = _currentTab == "Canlı" || isFavoriLive;

                string cacheKey = $"{_currentTab}_{_favoriCategoryType}_{_currentCategory}";

                if (_contentCache.TryGetValue(cacheKey, out var cachedList) &&
                    cachedList.Count > 0 && string.IsNullOrEmpty(searchText))
                {
                    var sortedCached = isCanlı ? cachedList : ApplyContentSort(_currentTab, cachedList).ToList();

                    foreach (var ch in sortedCached)
                        ch.HasResume = historyByUrl.TryGetValue(ch.Url, out var h) && h.Position > 5000;

                    _allFilteredContents = sortedCached;
                    _loadedCount = sortedCached.Count;

                    if (isCanlı) ReplaceCollection(_displayContents, sortedCached);
                    else ReplaceCollection(_displayVodContents, sortedCached);

                    ContentScrollViewer.Offset = new Avalonia.Vector(0, 0);

                    var needsPoster = sortedCached.Where(c => c.LogoBitmap == null).ToList();
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
                                    c.Name.Contains(searchText, StringComparison.CurrentCultureIgnoreCase))
                        .ToList();

                    if (!isCanlı) allContents = ApplyContentSort(_currentTab, allContents).ToList();

                    _allFilteredContents = allContents;
                    _loadedCount = 0;

                    foreach (var ch in allContents)
                        ch.HasResume = historyByUrl.TryGetValue(ch.Url, out var h) && h.Position > 5000;

                    var firstBatch = _allFilteredContents.Take(PAGE_SIZE).ToList();
                    _loadedCount = firstBatch.Count;

                    if (isCanlı) ReplaceCollection(_displayContents, firstBatch);
                    else ReplaceCollection(_displayVodContents, firstBatch);

                    _ = LoadLogosForChannelsAsync(firstBatch);
                    if (!isCanlı) _ = LoadTmdbPostersForChannels(firstBatch);

                    if (_loadedCount >= _allFilteredContents.Count)
                        SetContentCache(cacheKey, _allFilteredContents);
                }
                UpdateCategorySelectionVisual();
                UpdateSortButtons();
                return;
            }

            // ── Shows (Dizi poster kartları) ──────────────────────────
            if (_viewState == "Shows")
            {
                PageTitlePanel.IsVisible = true;
                PageTitle.Text = _currentCategory;
                PageTitle.IsVisible = true;
                BackBtn.IsVisible = true;
                CategoryListPanel.IsVisible = true;
                SortOptionsPanel.IsVisible = true;
                ContentItemsGrid.IsVisible = false;
                VodContentGrid.IsVisible = false;
                SeriesContentGrid.IsVisible = true;
                FavoriPanel.IsVisible = false;

                string cacheKey = $"Dizi_{_currentCategory}";
                Debug.WriteLine($"[SHOWS] Dizi Görünümü Yükleniyor: Kategori='{_currentCategory}', Sekme='{_currentTab}', Sıralama='{GetContentSortMode(_currentTab)}'");

                if (_seriesCardCache.TryGetValue(cacheKey, out var cachedCards) &&
                    cachedCards.Count > 0 && string.IsNullOrEmpty(searchText))
                {
                    Debug.WriteLine($"[SHOWS] Önbellekten {cachedCards.Count} kart çekildi, sıralanıyor...");
                    var sorted = SortSeriesCards(_currentTab, cachedCards);

                    foreach (var card in sorted)
                    {
                        var ep = card.SelectedEpisode;
                        if (ep != null)
                            card.HasResume = historyByUrl.TryGetValue(ep.Url, out var h) && h.Position > 5000;
                    }
                    _allFilteredCards = sorted;
                    _loadedCount = sorted.Count;
                    ReplaceCollection(_displaySeriesCards, sorted);
                    ContentScrollViewer.Offset = new Avalonia.Vector(0, 0);

                    var needsPoster = sorted.Where(c => c.LogoBitmap == null).ToList();
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
                        .Where(s => string.IsNullOrEmpty(searchText) ||
                                    s.Contains(searchText, StringComparison.CurrentCultureIgnoreCase))
                        .ToList();

                    Debug.WriteLine($"[SHOWS] Toplam Dizi Sayısı: {showNames.Count}, Toplam Bölüm: {seriesEpisodes.Count}");

                    var allCards = new List<SeriesCard>();
                    foreach (var showName in showNames)
                    {
                        var episodes = seriesEpisodes.Where(c => c.ShowName == showName).ToList();
                        if (episodes.Count > 0)
                        {
                            allCards.Add(BuildSeriesCard(showName, episodes, historyByUrl));
                        }
                    }

                    // Doğrudan oluşturulan kartları seçili moda göre sıralıyoruz
                    allCards = SortSeriesCards(_currentTab, allCards);

                    _allFilteredCards = allCards;
                    _loadedCount = 0;
                    var firstBatch = _allFilteredCards.Take(PAGE_SIZE).ToList();
                    _loadedCount = firstBatch.Count;
                    ReplaceCollection(_displaySeriesCards, firstBatch);
                    _ = LoadTmdbPostersForCards(firstBatch);

                    if (_loadedCount >= _allFilteredCards.Count)
                        SetSeriesCardCache(cacheKey, _allFilteredCards);
                }
                UpdateCategorySelectionVisual();
                UpdateSortButtons();
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

        // ─────────────────────────────────────────────────────────────
        // Favoriler paneli – yatay scroll tetikli sayfalama
        // ─────────────────────────────────────────────────────────────
        private void FavoriVodScroll_ScrollChanged(object? sender, Avalonia.Controls.ScrollChangedEventArgs e)
        {
            UpdateHArrowVisibility(sender as ScrollViewer, FavoriVodPrevBtn, FavoriVodNextBtn);

            if (_isLoadingMoreFavoriVod) return;
            if (sender is not ScrollViewer sv) return;

            double scrollPos = sv.Offset.X + sv.Viewport.Width;
            double totalWidth = sv.Extent.Width;
            if (totalWidth <= 0 || scrollPos < totalWidth * 0.8) return;
            if (_favoriVodLoadedCount >= _allFavoriVod.Count) return;

            _isLoadingMoreFavoriVod = true;
            try
            {
                var nextBatch = _allFavoriVod.Skip(_favoriVodLoadedCount).Take(FAVORI_PAGE_SIZE).ToList();
                _favoriVodLoadedCount += nextBatch.Count;

                foreach (var item in nextBatch) _displayFavoriVod.Add(item);

                _ = LoadLogosForChannelsAsync(nextBatch);
                _ = LoadTmdbPostersForChannels(nextBatch);
            }
            finally { _isLoadingMoreFavoriVod = false; }
        }

        private void FavoriSeriesScroll_ScrollChanged(object? sender, Avalonia.Controls.ScrollChangedEventArgs e)
        {
            UpdateHArrowVisibility(sender as ScrollViewer, FavoriSeriesPrevBtn, FavoriSeriesNextBtn);

            if (_isLoadingMoreFavoriSeries) return;
            if (sender is not ScrollViewer sv) return;

            double scrollPos = sv.Offset.X + sv.Viewport.Width;
            double totalWidth = sv.Extent.Width;
            if (totalWidth <= 0 || scrollPos < totalWidth * 0.8) return;
            if (_favoriSeriesLoadedCount >= _allFavoriSeriesCards.Count) return;

            _isLoadingMoreFavoriSeries = true;
            try
            {
                var nextBatch = _allFavoriSeriesCards.Skip(_favoriSeriesLoadedCount).Take(FAVORI_PAGE_SIZE).ToList();
                _favoriSeriesLoadedCount += nextBatch.Count;

                foreach (var item in nextBatch) _displayFavoriSeriesCards.Add(item);

                _ = LoadTmdbPostersForCards(nextBatch);
            }
            finally { _isLoadingMoreFavoriSeries = false; }
        }

        private void ScrollHorizontally(ScrollViewer? sv, double direction)
        {
            if (sv == null) return;
            double delta = sv.Viewport.Width * 0.9 * direction;
            double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
            double newX = Math.Clamp(sv.Offset.X + delta, 0, maxX);
            sv.Offset = new Avalonia.Vector(newX, sv.Offset.Y);
        }

        private void UpdateHArrowVisibility(ScrollViewer? sv, Border? prevBtn, Border? nextBtn)
        {
            if (sv == null || prevBtn == null || nextBtn == null) return;
            double maxX = sv.Extent.Width - sv.Viewport.Width;
            bool canScroll = maxX > 1;
            prevBtn.IsVisible = canScroll && sv.Offset.X > 2;
            nextBtn.IsVisible = canScroll && sv.Offset.X < maxX - 2;
        }

        private void FavoriLiveScroll_ScrollChanged(object? sender, Avalonia.Controls.ScrollChangedEventArgs e) =>
            UpdateHArrowVisibility(sender as ScrollViewer, FavoriLivePrevBtn, FavoriLiveNextBtn);

        private void FavoriLivePrev_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ScrollHorizontally(FavoriLiveScrollViewer, -1);
        }
        private void FavoriLiveNext_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ScrollHorizontally(FavoriLiveScrollViewer, 1);
        }

        private void FavoriVodPrev_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ScrollHorizontally(FavoriVodScrollViewer, -1);
        }
        private void FavoriVodNext_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ScrollHorizontally(FavoriVodScrollViewer, 1);
        }

        private void FavoriSeriesPrev_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ScrollHorizontally(FavoriSeriesScrollViewer, -1);
        }
        private void FavoriSeriesNext_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ScrollHorizontally(FavoriSeriesScrollViewer, 1);
        }

        private void SetActiveMenuButton(string tab)
        {
            BtnHome.Classes.Remove("Active");
            BtnLive.Classes.Remove("Active");
            BtnVOD.Classes.Remove("Active");
            BtnSeries.Classes.Remove("Active");
            BtnFav.Classes.Remove("Active");
            switch (tab)
            {
                case "Anasayfa": BtnHome.Classes.Add("Active"); break;
                case "Canlı": BtnLive.Classes.Add("Active"); break;
                case "VOD": BtnVOD.Classes.Add("Active"); break;
                case "Dizi": BtnSeries.Classes.Add("Active"); break;
                case "Favori": BtnFav.Classes.Add("Active"); break;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Manuel kategori sıralaması — sürükle-bırak
        // ─────────────────────────────────────────────────────────────
        private void CategoryDragStart_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control c || c.Tag is not string cat) return;
            _draggingCategory = cat;
#pragma warning disable CS0618
            var data = new DataObject();
            data.Set("Category", cat);
            DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
        }

        private void CategoryItem_DragOver(object? sender, DragEventArgs e)
        {
            if (sender is Button btn)
            {
                e.DragEffects = DragDropEffects.Move;
                btn.Classes.Add("DragOver");
            }
        }

        private void CategoryItem_DragLeave(object? sender, DragEventArgs e)
        {
            if (sender is Button btn) btn.Classes.Remove("DragOver");
        }

        private void CategoryItem_Drop(object? sender, DragEventArgs e)
        {
            if (sender is Button btn) btn.Classes.Remove("DragOver");
            if (sender is not Control c || c.Tag is not string target) return;
            if (string.IsNullOrEmpty(_draggingCategory)) return;

            string from = _draggingCategory;
            _draggingCategory = null;
            if (from == target) return;

            MoveCategory(from, target);
        }

        private void MoveCategory(string from, string to)
        {
            var current = _displayCategories.ToList();
            int fromIdx = current.IndexOf(from);
            int toIdx = current.IndexOf(to);
            if (fromIdx < 0 || toIdx < 0) return;

            current.RemoveAt(fromIdx);
            toIdx = current.IndexOf(to);
            current.Insert(toIdx, from);

            ReplaceCollection(_displayCategories, current);

            if (!_appSettings.CategoryOrder.TryGetValue(_currentTab, out var order))
            {
                order = new List<string>();
                _appSettings.CategoryOrder[_currentTab] = order;
            }
            order.Clear();
            order.AddRange(current);
            SaveAppSettings();

            UpdateCategorySelectionVisual();
            ShowToast($"Kategori sırası güncellendi.");
        }

        private List<string> ApplyCategoryOrder(string tab, List<string> groups)
        {
            if (_appSettings.CategoryOrder.TryGetValue(tab, out var order) && order.Count > 0)
            {
                var ordered = order.Where(g => groups.Contains(g)).ToList();
                var remaining = groups.Where(g => !ordered.Contains(g)).OrderBy(g => g).ToList();
                return ordered.Concat(remaining).ToList();
            }
            return groups.OrderBy(g => g).ToList();
        }

        private void UpdateCategorySelectionVisual()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (CategoriesGrid.ItemContainerGenerator == null) return;
                Control? selectedButton = null;
                for (int i = 0; i < CategoriesGrid.ItemCount; i++)
                {
                    var container = CategoriesGrid.ContainerFromIndex(i);
                    if (container is not Control c) continue;
                    var btn = c.FindDescendantOfType<Button>();
                    if (btn == null) continue;
                    bool selected = btn.Tag?.ToString() == _currentCategory;
                    if (selected) btn.Classes.Add("Selected");
                    else btn.Classes.Remove("Selected");
                    if (selected) selectedButton = btn;
                }
                if (selectedButton != null && !string.IsNullOrEmpty(_currentCategory))
                    selectedButton.BringIntoView();
            }, DispatcherPriority.Loaded);
        }

        // ─────────────────────────────────────────────────────────────
        // İçerik sıralama — A-Z / Z-A / Son Eklenenler
        // ─────────────────────────────────────────────────────────────
        private void SortMode_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string mode) return;

            Debug.WriteLine($"[SORT_CLICK] Tıklanan Buton: '{mode}', Mevcut Sekme: '{_currentTab}'");

            _appSettings.ContentSortMode[_currentTab] = mode;
            SaveAppSettings();

            _contentCache.Clear();
            _seriesCardCache.Clear();

            UpdateView();
            UpdateSortButtons();
        }

        private string GetContentSortMode(string tab)
        {
            if (_appSettings.ContentSortMode.TryGetValue(tab, out var mode) &&
                (mode == "AZ" || mode == "ZA" || mode == "New"))
                return mode;
            return "New";
        }

        private IEnumerable<Channel> ApplyContentSort(string tab, IEnumerable<Channel> items)
        {
            return GetContentSortMode(tab) switch
            {
                "ZA" => items.OrderByDescending(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
                "New" => items.OrderByDescending(c => c.AddedDate),
                _ => items.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            };
        }

        private static DateTime GetLatestEpisodeAddedDate(SeriesCard card)
        {
            if (card == null || card.EpisodesBySeason == null || card.EpisodesBySeason.Count == 0)
                return DateTime.MinValue;

            return card.EpisodesBySeason.Values
                .Where(eps => eps != null)
                .SelectMany(eps => eps)
                .Where(e => e != null)
                .Select(e => e.AddedDate)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
        }

        // ─────────────────────────────────────────────────────────────
        // Dizi Kartları Sıralama Fonksiyonu (STREAM ID ÖNCELİKLİ KESİN ÇÖZÜM)
        // ─────────────────────────────────────────────────────────────
        private List<SeriesCard> SortSeriesCards(string tab, List<SeriesCard> cards)
        {
            if (cards == null || cards.Count == 0)
                return new List<SeriesCard>();

            string sortMode = GetContentSortMode(tab);
            Debug.WriteLine($"[SORT_CARDS] Sekme='{tab}', Mod='{sortMode}', Toplam Kart Sayısı={cards.Count}");

            var sortedList = sortMode switch
            {
                "ZA" => cards
                    .OrderByDescending(c => c.ShowName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),

                "New" => cards
                    // 1. KESİN VE DOĞRU SIRALAMA:
                    // Sağlayıcının atadığı en son Stream ID'ye göre sırala (En son eklenen dizinin ID'si her zaman en büyüktür!)
                    .OrderByDescending(c => c.LatestStreamId)
                    // 2. Stream ID eşitse veya 0 ise tarihe bak
                    .ThenByDescending(c => c.AddedDate != default ? c.AddedDate : GetLatestEpisodeAddedDate(c))
                    .ThenBy(c => c.ShowName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),

                _ => cards
                    .OrderBy(c => c.ShowName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
            };

            Debug.WriteLine($"[SORT_CARDS] Sıralama Tamamlandı. İlk 3 Dizi:");
            foreach (var c in sortedList.Take(3))
            {
                Debug.WriteLine($"  => '{c.ShowName}' (Stream ID: {c.LatestStreamId}, Tarih: {c.AddedDate})");
            }

            return sortedList;
        }

        private void UpdateSortButtons()
        {
            string mode = GetContentSortMode(_currentTab);
            foreach (var btn in SortOptionsPanel.Children.OfType<Button>())
            {
                bool selected = btn.Tag?.ToString() == mode;
                if (selected) btn.Classes.Add("Selected");
                else btn.Classes.Remove("Selected");
            }
        }
    }
}