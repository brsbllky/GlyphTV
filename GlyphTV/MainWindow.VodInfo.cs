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
        // DÜZELTME (elle TMDb eşleşme düzeltme özelliği için): Detay modalı
        // bir dizi için açıldığında (SeriesInfo_Click), o dizinin ekrandaki
        // SeriesCard referansı burada saklanır. Otomatik isimle-arama hiçbir
        // zaman %100 kapsam sağlayamayacağından (yazım hataları, TMDb'de
        // farklı/uzun başlık, transliterasyon farkları vb.), kullanıcının
        // modalden doğrudan doğru TMDb ID'sini girip KALICI olarak
        // eşleyebilmesi gerekiyor — bu alan, ID uygulandığında hangi kartın
        // posterinin yenileneceğini bilmek için kullanılır.
        // ─────────────────────────────────────────────────────────────
        private SeriesCard? _currentVodInfoSeriesCard = null;

        // ─────────────────────────────────────────────────────────────
        // VOD detay modalını aç
        // ─────────────────────────────────────────────────────────────
        private async void VodInfo_Click(object? sender, RoutedEventArgs e)
        {
            var channel = (sender as Button)?.Tag as Channel ?? sender as Channel;
            if (channel == null) return;

            _currentVodInfo = channel;
            _currentVodInfoSeriesCard = null;

            VodInfoOrigRow.IsVisible    = false;
            VodInfoDirRow.IsVisible     = false;
            VodInfoCastRow.IsVisible    = false;
            VodInfoDurRow.IsVisible     = false;
            VodInfoDateRow.IsVisible    = false;
            VodInfoCountryRow.IsVisible = false;
            VodInfoAgeRow.IsVisible     = false;
            VodInfoPlotRow.IsVisible    = false;
            VodInfoPlot.Text            = "";

            // HD Backdrop ve Poster silüetini anında ve kesintisiz göstermek için cache'den en hızlı şekilde al
            var posterBmp = GetBestAvailablePosterForChannel(channel);
            if (posterBmp != null && channel.LogoBitmap == null)
            {
                channel.LogoBitmap = posterBmp;
            }

            var backdropBmp = GetBestAvailableBackdropForChannel(channel);
            VodInfoBackdropImage.Source = backdropBmp ?? posterBmp;

            VodInfoTitle.Text      = channel.Name;
            VodInfoCategory.Text   = channel.Group;
            VodInfoGenre.Text      = channel.Group;
            VodInfoModalTitle.Text = "Film Detayları";

            var activeSource = _sources.FirstOrDefault(s => s.IsActive);
            VodInfoSource.Text  = activeSource?.Name ?? "Bilinmeyen Kaynak";
            VodInfoFavText.Text = channel.IsFavorite ? "❤️ Favorilerde" : "♡ Favori";

            // Poster
            VodInfoPoster.Child = null;
            if (posterBmp != null)
            {
                VodInfoPoster.Background = Avalonia.Media.Brushes.Transparent;
                VodInfoPoster.Child = new Image
                {
                    Source = posterBmp,
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
            {
                // DÜZELTME (Türkçe isimli içeriklerde poster/detay hiç
                // bulunamaması): Xtream sağlayıcıları VOD içeriklerini
                // genellikle kendi panelinde zaten TMDb ile eşleştirmiş olur
                // (get_vod_info → tmdb_id / o_name). Bu bilgi varsa isimle
                // arama tamamen atlanır — Türkçe/İngilizce ad uyuşmazlığı,
                // apostrof vb. hiçbir şey sonucu etkilemez. Sağlayıcı bu
                // alanları vermiyorsa (M3U/Link kaynakları ya da eşleşmesi
                // olmayan içerikler) mevcut isimle-arama akışına sorunsuzca
                // düşülür.
                int? knownTmdbId = null;
                string? knownOriginalName = null;
                int? knownYear = null;

                if (channel.Type == "VOD" && activeSource != null && activeSource.Type == "Xtream")
                {
                    // YENİ: get_vod_info artık her zaman çağrılıyor (sonuç
                    // bellek içi cache'lendiğinden ikinci açılışta bedava) —
                    // sadece tmdb_id için değil, sağlayıcının kendi verdiği
                    // plot/cast/director/genre/süre/tarih/puan/poster için de.
                    // Bu, TMDb'de HİÇ kaydı olmayan içeriklerde (örn. yerel
                    // belgeseller) modalın artık tamamen boş kalmamasını sağlar
                    // — bkz. ApplyProviderFallbackInfo (MainWindow.Tmdb.cs).
                    var providerInfo = await GetXtreamVodInfoAsync(activeSource, channel);

                    if (providerInfo.TmdbId > 0) { knownTmdbId = providerInfo.TmdbId; channel.TmdbId = providerInfo.TmdbId; }
                    else if (channel.TmdbId > 0) { knownTmdbId = channel.TmdbId; }

                    if (!string.IsNullOrEmpty(providerInfo.OriginalName))
                    {
                        knownOriginalName = providerInfo.OriginalName;
                        channel.OriginalName = providerInfo.OriginalName;
                    }

                    // DÜZELTME: kısa/jenerik isimli içeriklerde (aynı isimle
                    // TMDb'de birden fazla kayıt olduğunda) PickConfidentMatch
                    // yıl bilgisi olmadan reddediyor — bkz. MainWindow.Tmdb.cs
                    // → FetchTmdbInfo notu.
                    knownYear = ParseYearFromProviderDate(providerInfo.ReleaseDate);

                    // Sağlayıcı verisini TABAN olarak hemen göster; aşağıdaki
                    // FetchTmdbInfo (TMDb eşleşmesi bulursa) sadece dolu
                    // alanları üzerine yazacak — boş bırakılan alanlar
                    // sağlayıcı değerinde kalır.
                    ApplyProviderFallbackInfo(
                        !string.IsNullOrEmpty(providerInfo.Genre) ? providerInfo.Genre : channel.Group,
                        providerInfo.Director, providerInfo.Cast, providerInfo.Duration,
                        providerInfo.ReleaseDate, providerInfo.Rating, providerInfo.Plot);

                    // Poster: tvg-logo/stream_icon zaten yüklenmiş değilse,
                    // sağlayıcının get_vod_info ile verdiği movie_image/
                    // cover_big'i dene (mevcut logo indirme/cache altyapısı
                    // yeniden kullanılıyor — ikinci bir indirme mekanizması
                    // eklenmedi).
                    if (channel.LogoBitmap == null && !string.IsNullOrEmpty(providerInfo.PosterUrl))
                    {
                        try
                        {
                            var bmp = await GetOrLoadLogoBitmap(providerInfo.PosterUrl, GetLogoCacheDir());
                            if (bmp != null)
                            {
                                channel.LogoBitmap = bmp;
                                if (ReferenceEquals(_currentVodInfo, channel))
                                {
                                    VodInfoPoster.Background = Avalonia.Media.Brushes.Transparent;
                                    VodInfoPoster.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
                                }
                            }
                        }
                        catch { /* poster indirilemedi — mevcut placeholder kalır */ }
                    }
                }
                else if (channel.TmdbId > 0)
                {
                    knownTmdbId = channel.TmdbId;
                }

                await FetchTmdbInfo(channel.Name, channel.Type ?? "VOD", null, knownTmdbId, knownOriginalName, knownYear);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Modalı kapat
        // ─────────────────────────────────────────────────────────────
        private void CloseVodInfo_Click(object? sender, RoutedEventArgs e)
        {
            VodInfoOverlay.IsVisible = false;
            VodInfoBackdropImage.Source = null;
            _currentVodInfo = null;
            _currentVodInfoSeriesCard = null;
        }

        // ─────────────────────────────────────────────────────────────
        // VOD oynat
        // ─────────────────────────────────────────────────────────────
        private async void VodInfoPlay_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentVodInfo == null) return;
            if (string.IsNullOrEmpty(_currentVodInfo.Url))
            {
                ShowToast("Bu içerik aktif olan kaynakta mevcut değil.");
                return;
            }
            VodInfoOverlay.IsVisible = false;
            await StartPlayingChannel(_currentVodInfo, resume: false);
        }

        // ─────────────────────────────────────────────────────────────
        // VOD favori durumu
        // ─────────────────────────────────────────────────────────────
        private void VodInfoFav_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentVodInfo == null) return;

            if (string.IsNullOrEmpty(_currentVodInfo.Url) && _currentVodInfoSeriesCard == null)
            {
                ShowToast("Bu içerik henüz oynatma listenizde bulunmuyor.");
                return;
            }

            if (_currentVodInfo.Type == "Dizi" && _currentVodInfoSeriesCard != null)
            {
                bool newState = ToggleSeriesFavorite(_currentVodInfoSeriesCard.ShowName);
                VodInfoFavText.Text = newState ? "❤️ Favorilerde" : "♡ Favori";
                ShowToast(newState ? "Favorilere eklendi" : "Favorilerden çıkarıldı");
            }
            else
            {
                _currentVodInfo.IsFavorite = !_currentVodInfo.IsFavorite;
                VodInfoFavText.Text = _currentVodInfo.IsFavorite ? "❤️ Favorilerde" : "♡ Favori";

                var activeSource = _sources.FirstOrDefault(s => s.IsActive);
                if (activeSource != null) SaveChannelsForSource(activeSource.Id);

                if (_currentTab == "Favori" && _viewState == "Categories")
                    RefreshFavoriGrids();

                ShowToast(_currentVodInfo.IsFavorite ? "Favorilere eklendi" : "Favorilerden çıkarıldı");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Bellek (RAM) ve disk poster cache'inden tek bir anahtarı siler —
        // manuel düzeltme/sıfırlama sonrası eski (artık geçersiz) posterin
        // önbellekten tekrar servis edilmesini engeller.
        // ─────────────────────────────────────────────────────────────
        private void ClearPosterCacheEntry(string cacheKey)
        {
            lock (_posterCacheLock) { _tmdbPosterCache.Remove(cacheKey); }
            try
            {
                string diskPath = GetPosterDiskPath(cacheKey);
                if (System.IO.File.Exists(diskPath)) System.IO.File.Delete(diskPath);
            }
            catch { /* diskten silinemedi — bir sonraki indirme yine de üzerine yazar */ }
        }

        // ─────────────────────────────────────────────────────────────
        // Manuel düzeltme/sıfırlama sonrası posteri yeniden çeker ve hem
        // açık olan detay modalına hem de (dizi ise) ekrandaki/önbellekteki
        // İLGİLİ TÜM SeriesCard kopyalarına anında yansıtır — SyncSeriesCard
        // Selection (MainWindow.Series.cs) ile aynı "tüm kopyaları gez"
        // deseni.
        // ─────────────────────────────────────────────────────────────
        private async Task RefreshPosterAfterManualMatch(string cacheKey)
        {
            if (_currentVodInfo == null) return;

            if (_currentVodInfo.Type == "Dizi" && !string.IsNullOrEmpty(_currentVodInfo.ShowName))
            {
                void ClearCard(SeriesCard c) { if (c.ShowName == cacheKey) c.LogoBitmap = null; }
                foreach (var c in _displaySeriesCards) ClearCard(c);
                foreach (var c in _displayFavoriSeriesCards) ClearCard(c);
                foreach (var c in _allFilteredCards) ClearCard(c);
                foreach (var c in _allFavoriSeriesCards) ClearCard(c);
                foreach (var cacheList in _seriesCardCache.Values)
                    foreach (var c in cacheList) ClearCard(c);

                var cardsToRefresh = new System.Collections.Generic.List<SeriesCard>();
                if (_currentVodInfoSeriesCard != null) cardsToRefresh.Add(_currentVodInfoSeriesCard);
                cardsToRefresh.AddRange(_displaySeriesCards.Where(c => c.ShowName == cacheKey && !ReferenceEquals(c, _currentVodInfoSeriesCard)));
                cardsToRefresh.AddRange(_displayFavoriSeriesCards.Where(c => c.ShowName == cacheKey && !cardsToRefresh.Contains(c)));

                if (cardsToRefresh.Count > 0) await LoadTmdbPostersForCards(cardsToRefresh);

                // Modal posterini, tazelenen karttan (varsa) uygula.
                var refreshed = cardsToRefresh.FirstOrDefault(c => c.LogoBitmap != null);
                if (refreshed != null)
                {
                    VodInfoPoster.Background = Avalonia.Media.Brushes.Transparent;
                    VodInfoPoster.Child = new Image { Source = refreshed.LogoBitmap, Stretch = Stretch.UniformToFill };
                }
            }
            else
            {
                _currentVodInfo.LogoBitmap = null;
                await LoadTmdbPostersForChannels(new System.Collections.Generic.List<Channel> { _currentVodInfo });
                if (_currentVodInfo.LogoBitmap != null)
                {
                    VodInfoPoster.Background = Avalonia.Media.Brushes.Transparent;
                    VodInfoPoster.Child = new Image { Source = _currentVodInfo.LogoBitmap, Stretch = Stretch.UniformToFill };
                }
            }
        }
    }
}
