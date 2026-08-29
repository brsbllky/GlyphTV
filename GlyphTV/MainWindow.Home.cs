// ============================================================
// MainWindow.Home.cs
// Anasayfa: TMDb Popüler İçerikler (bu hafta) & Devam Et
// ============================================================

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow
    {
        private ObservableCollection<PopularMediaItem> _displayPopularItems = new();
        private ObservableCollection<HeroDotItem> _heroDots = new();
        private ObservableCollection<ResumeWatchItem> _displayResumeItems = new();
        private List<ResumeWatchItem> _allResumeItems = new();
        private string _currentResumeFilter = "All";
        private bool _isPopularLoaded = false;
        private bool _isPopularLoading = false;
        private int _currentHeroIndex = 0;
        private DispatcherTimer? _heroCarouselTimer;

        // TMDb Tür Haritası
        private static readonly Dictionary<int, string> _tmdbGenreMap = new()
        {
            { 28, "Aksiyon" }, { 12, "Macera" }, { 16, "Animasyon" }, { 35, "Komedi" },
            { 80, "Suç" }, { 99, "Belgesel" }, { 18, "Dram" }, { 10751, "Aile" },
            { 14, "Fantastik" }, { 36, "Tarih" }, { 27, "Korku" }, { 10402, "Müzik" },
            { 9648, "Gizem" }, { 10749, "Romantik" }, { 878, "Bilim Kurgu" },
            { 10770, "TV Filmi" }, { 53, "Gerilim" }, { 10752, "Savaş" }, { 37, "Vahşi Batı" },
            { 10759, "Aksiyon & Macera" }, { 10762, "Çocuk" }, { 10763, "Haber" },
            { 10764, "Reality" }, { 10765, "Bilim Kurgu & Fantazi" }, { 10766, "Pembe Dizi" },
            { 10767, "Talk Show" }, { 10768, "Savaş & Politik" }
        };

        // ─────────────────────────────────────────────────────────────
        // Bellek Poster & Backdrop Önbelleği Erişimi
        // ─────────────────────────────────────────────────────────────
        private static Bitmap? GetPosterFromCache(string key)
        {
            lock (_posterCacheLock)
            {
                if (_tmdbPosterCache.TryGetValue(key, out var bmp))
                    return bmp;
            }
            return null;
        }

        private static Bitmap? GetBackdropFromCache(string key)
        {
            lock (_posterCacheLock)
            {
                if (_tmdbBackdropCache.TryGetValue(key, out var bmp))
                    return bmp;
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────
        // Anasayfa Görünümünü Yenile
        // ─────────────────────────────────────────────────────────────
        private void RefreshHomeView()
        {
            RefreshHomeResumeSection();

            if (!_isPopularLoaded && !_isPopularLoading)
            {
                LoadCachedTmdbPopular();
                _ = LoadWeeklyPopularFromTmdbAsync();
            }
            else
            {
                UpdateHeroBannerDisplay();
            }

            StartHeroCarouselTimer();
        }

        // ─────────────────────────────────────────────────────────────
        // 1. Popüler Bölümü (TMDb Haftalık Trendler - Hero Banner)
        // ─────────────────────────────────────────────────────────────
        private void LoadCachedTmdbPopular()
        {
            try
            {
                string cachePath = GetTmdbPopularCachePath();
                if (!File.Exists(cachePath)) return;
                var json = File.ReadAllText(cachePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

                var items = new List<PopularMediaItem>();
                int rank = 1;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string title = el.TryGetProperty("Title", out var tProp) ? tProp.GetString() ?? "" : "";
                    string origTitle = el.TryGetProperty("OriginalTitle", out var otProp) ? otProp.GetString() ?? "" : "";
                    string mediaType = el.TryGetProperty("MediaType", out var mtProp) ? mtProp.GetString() ?? "movie" : "movie";
                    string posterPath = el.TryGetProperty("PosterPath", out var ppProp) ? ppProp.GetString() ?? "" : "";
                    string backdropPath = el.TryGetProperty("BackdropPath", out var bpProp) ? bpProp.GetString() ?? "" : "";
                    string tagline = el.TryGetProperty("Tagline", out var tgProp) ? tgProp.GetString() ?? "" : "";
                    int tmdbId = el.TryGetProperty("TmdbId", out var idProp) ? idProp.GetInt32() : 0;
                    double vote = el.TryGetProperty("VoteAverage", out var vProp) ? vProp.GetDouble() : 0.0;
                    string releaseYear = el.TryGetProperty("ReleaseYear", out var ryProp) ? ryProp.GetString() ?? "" : "";
                    string overview = el.TryGetProperty("Overview", out var ovProp) ? ovProp.GetString() ?? "" : "";

                    var item = new PopularMediaItem
                    {
                        Rank = rank++,
                        TmdbId = tmdbId,
                        Title = title,
                        OriginalTitle = origTitle,
                        MediaType = mediaType,
                        PosterPath = posterPath,
                        BackdropPath = backdropPath,
                        Tagline = tagline,
                        VoteAverage = vote,
                        ReleaseYear = releaseYear,
                        Overview = overview,
                        MatchRateText = $"%{(int)Math.Clamp(vote * 10 + 12, 85, 99)} Eşleşme"
                    };

                    if (el.TryGetProperty("Genres", out var gArr) && gArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var g in gArr.EnumerateArray())
                        {
                            var gName = g.GetString();
                            if (!string.IsNullOrEmpty(gName)) item.Genres.Add(gName);
                        }
                    }

                    items.Add(item);
                    if (items.Count >= 20) break; // Maksimum 20 içerik
                }

                if (items.Count > 0)
                {
                    foreach (var it in items)
                    {
                        PreloadCachedImagesForPopularItem(it);
                    }

                    _isPopularLoaded = true;

                    Dispatcher.UIThread.Post(() =>
                    {
                        _displayPopularItems.Clear();
                        foreach (var it in items)
                        {
                            _displayPopularItems.Add(it);
                        }
                        SetupHeroDots(items.Count);
                        UpdateHeroBannerDisplay();
                    }, DispatcherPriority.Normal);

                    _ = Task.Run(() => MatchPopularItemsWithChannelsAsync(items));
                    _ = LoadTmdbPostersAndBackdropsForPopularItemsAsync(items);
                }
            }
            catch (Exception ex)
            {
                LogError("LoadCachedTmdbPopular", ex);
            }
        }

        private void SaveCachedTmdbPopular(List<PopularMediaItem> items)
        {
            try
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartArray();
                    foreach (var item in items.Take(20))
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("TmdbId", item.TmdbId);
                        writer.WriteString("Title", item.Title);
                        writer.WriteString("OriginalTitle", item.OriginalTitle);
                        writer.WriteString("MediaType", item.MediaType);
                        writer.WriteString("PosterPath", item.PosterPath);
                        writer.WriteString("BackdropPath", item.BackdropPath);
                        writer.WriteString("Tagline", item.Tagline);
                        writer.WriteNumber("VoteAverage", item.VoteAverage);
                        writer.WriteString("ReleaseYear", item.ReleaseYear);
                        writer.WriteString("Overview", item.Overview);
                        
                        writer.WriteStartArray("Genres");
                        foreach (var g in item.Genres)
                        {
                            writer.WriteStringValue(g);
                        }
                        writer.WriteEndArray();

                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                File.WriteAllBytes(GetTmdbPopularCachePath(), stream.ToArray());
            }
            catch (Exception ex)
            {
                LogError("SaveCachedTmdbPopular", ex);
            }
        }

        private async Task LoadWeeklyPopularFromTmdbAsync()
        {
            if (_isPopularLoading) return;
            _isPopularLoading = true;

            try
            {
                EnsureTmdbHttpClient();
                string url = $"{TMDB_BASE}/trending/all/week?api_key={TMDB_API_KEY}&language=tr-TR";
                var json = await TmdbApiGetAsync(url, "TrendingPopularTr");

                if (string.IsNullOrEmpty(json))
                {
                    url = $"{TMDB_BASE}/trending/all/week?api_key={TMDB_API_KEY}";
                    json = await TmdbApiGetAsync(url, "TrendingPopularEn");
                }

                if (string.IsNullOrEmpty(json)) return;

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                    return;

                var items = new List<PopularMediaItem>();
                int rank = 1;

                foreach (var el in results.EnumerateArray())
                {
                    string mediaType = el.TryGetProperty("media_type", out var mt) ? mt.GetString() ?? "movie" : "movie";
                    if (mediaType != "movie" && mediaType != "tv") continue;

                    string title = "";
                    if (el.TryGetProperty("title", out var tProp)) title = tProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(title) && el.TryGetProperty("name", out var nProp)) title = nProp.GetString() ?? "";

                    string origTitle = "";
                    if (el.TryGetProperty("original_title", out var otProp)) origTitle = otProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(origTitle) && el.TryGetProperty("original_name", out var onProp)) origTitle = onProp.GetString() ?? "";

                    string posterPath = el.TryGetProperty("poster_path", out var pp) ? pp.GetString() ?? "" : "";
                    string backdropPath = el.TryGetProperty("backdrop_path", out var bp) ? bp.GetString() ?? "" : "";

                    int tmdbId = el.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                    double vote = el.TryGetProperty("vote_average", out var vProp) ? vProp.GetDouble() : 0.0;
                    string overview = el.TryGetProperty("overview", out var ovProp) ? ovProp.GetString() ?? "" : "";

                    string releaseDate = "";
                    if (el.TryGetProperty("release_date", out var rdProp)) releaseDate = rdProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(releaseDate) && el.TryGetProperty("first_air_date", out var fdProp)) releaseDate = fdProp.GetString() ?? "";

                    string releaseYear = "";
                    if (!string.IsNullOrEmpty(releaseDate) && releaseDate.Length >= 4)
                    {
                        releaseYear = releaseDate[..4];
                    }

                    var item = new PopularMediaItem
                    {
                        Rank = rank++,
                        TmdbId = tmdbId,
                        Title = string.IsNullOrEmpty(title) ? origTitle : title,
                        OriginalTitle = origTitle,
                        MediaType = mediaType,
                        PosterPath = posterPath,
                        BackdropPath = string.IsNullOrEmpty(backdropPath) ? posterPath : backdropPath,
                        VoteAverage = vote,
                        ReleaseYear = releaseYear,
                        Overview = overview,
                        MatchRateText = $"%{(int)Math.Clamp(vote * 10 + 12, 85, 99)} Eşleşme"
                    };

                    // Türleri haritalandır
                    if (el.TryGetProperty("genre_ids", out var gIds) && gIds.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var gId in gIds.EnumerateArray())
                        {
                            int id = gId.GetInt32();
                            if (_tmdbGenreMap.TryGetValue(id, out var gName) && !item.Genres.Contains(gName))
                            {
                                item.Genres.Add(gName);
                                if (item.Genres.Count >= 4) break;
                            }
                        }
                    }

                    items.Add(item);
                    if (items.Count >= 20) break; // 20 adet içerikle sınırlandır
                }

                // TMDb üzerinden her içerik için detaylı Türkçe slogan (tagline) çek
                await FetchHeroDetailsAsync(items);

                if (items.Count > 0)
                {
                    SaveCachedTmdbPopular(items);
                }

                foreach (var it in items)
                {
                    PreloadCachedImagesForPopularItem(it);
                }

                // Mevcut yüklü görselleri ve eşleşmeleri koru
                var existingMap = _displayPopularItems.Where(x => x.TmdbId > 0).ToDictionary(x => x.TmdbId, x => x);
                foreach (var it in items)
                {
                    if (it.TmdbId > 0 && existingMap.TryGetValue(it.TmdbId, out var existing))
                    {
                        if (it.BackdropBitmap == null && existing.BackdropBitmap != null)
                            it.BackdropBitmap = existing.BackdropBitmap;
                        if (it.PosterBitmap == null && existing.PosterBitmap != null)
                            it.PosterBitmap = existing.PosterBitmap;
                        if (it.MatchedChannel == null && existing.MatchedChannel != null)
                            it.MatchedChannel = existing.MatchedChannel;
                    }
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (i < _displayPopularItems.Count)
                            _displayPopularItems[i] = items[i];
                        else
                            _displayPopularItems.Add(items[i]);
                    }
                    while (_displayPopularItems.Count > items.Count)
                    {
                        _displayPopularItems.RemoveAt(_displayPopularItems.Count - 1);
                    }
                    _isPopularLoaded = true;

                    SetupHeroDots(items.Count);
                    UpdateHeroBannerDisplay();
                });

                _ = MatchPopularItemsWithChannelsAsync(items);

                // Görselleri (Backdrop ve Poster) arka planda paralel indir
                _ = LoadTmdbPostersAndBackdropsForPopularItemsAsync(items);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HOME] TMDb popüler yükleme hatası: {ex.Message}");
            }
            finally
            {
                _isPopularLoading = false;
            }
        }

        private async Task FetchHeroDetailsAsync(List<PopularMediaItem> items)
        {
            var sem = new SemaphoreSlim(4, 4);
            var tasks = items.Select(async item =>
            {
                await sem.WaitAsync();
                try
                {
                    EnsureTmdbHttpClient();
                    string detailUrl = $"{TMDB_BASE}/{item.MediaType}/{item.TmdbId}?api_key={TMDB_API_KEY}&language=tr-TR";
                    var json = await TmdbApiGetAsync(detailUrl, $"HeroDetail_{item.TmdbId}");
                    if (!string.IsNullOrEmpty(json))
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("tagline", out var tg) && !string.IsNullOrWhiteSpace(tg.GetString()))
                        {
                            item.Tagline = tg.GetString()!.Trim();
                        }
                        else if (root.TryGetProperty("overview", out var ov) && !string.IsNullOrWhiteSpace(ov.GetString()) && string.IsNullOrEmpty(item.Overview))
                        {
                            item.Overview = ov.GetString()!.Trim();
                        }

                        if (item.Genres.Count == 0 && root.TryGetProperty("genres", out var gList) && gList.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var gObj in gList.EnumerateArray())
                            {
                                if (gObj.TryGetProperty("name", out var gn) && !string.IsNullOrEmpty(gn.GetString()))
                                {
                                    item.Genres.Add(gn.GetString()!);
                                }
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    sem.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);
        }

        private async Task MatchPopularItemsWithChannelsAsync(List<PopularMediaItem> items)
        {
            await Task.Run(() =>
            {
                MatchPopularItemsWithChannels(items);
            });
            await Dispatcher.UIThread.InvokeAsync(UpdateHeroBannerDisplay);
        }

        private void MatchPopularItemsWithChannels(List<PopularMediaItem> items)
        {
            if (_allChannels.Count == 0 || items.Count == 0) return;

            var historyByUrl = GetWatchHistoryByUrlCache();

            // 1. Dizi kanallarını ve VOD kanallarını tek geçişte ön-indeksle (O(N))
            var seriesByTmdbId = new Dictionary<int, Channel>();
            var seriesEpisodesByShow = new Dictionary<string, List<Channel>>(StringComparer.OrdinalIgnoreCase);
            var seriesByNormTitle = new Dictionary<string, (string ShowName, List<Channel> Eps, int? Year)>(StringComparer.OrdinalIgnoreCase);

            var vodByTmdbId = new Dictionary<int, Channel>();
            var vodByNormTitle = new Dictionary<string, (Channel Channel, int? Year)>(StringComparer.OrdinalIgnoreCase);

            foreach (var ch in _allChannels)
            {
                if (ch.IsHidden) continue;

                if (ch.Type == "Dizi" && !string.IsNullOrEmpty(ch.ShowName))
                {
                    if (ch.TmdbId > 0 && !seriesByTmdbId.ContainsKey(ch.TmdbId))
                        seriesByTmdbId[ch.TmdbId] = ch;

                    if (!seriesEpisodesByShow.TryGetValue(ch.ShowName, out var epList))
                    {
                        epList = new List<Channel>();
                        seriesEpisodesByShow[ch.ShowName] = epList;

                        var (cleanShow, showYear) = CleanNameForSearch(ch.ShowName);
                        string normShow = NormalizeTmdbTitle(cleanShow);
                        if (!string.IsNullOrEmpty(normShow) && !seriesByNormTitle.ContainsKey(normShow))
                            seriesByNormTitle[normShow] = (ch.ShowName, epList, showYear);
                    }
                    epList.Add(ch);
                }
                else if (ch.Type == "VOD")
                {
                    if (ch.TmdbId > 0 && !vodByTmdbId.ContainsKey(ch.TmdbId))
                        vodByTmdbId[ch.TmdbId] = ch;

                    var (cleanName, chYear) = CleanNameForSearch(ch.Name);
                    string normCh = NormalizeTmdbTitle(cleanName);
                    if (!string.IsNullOrEmpty(normCh) && !vodByNormTitle.ContainsKey(normCh))
                        vodByNormTitle[normCh] = (ch, chYear);
                }
            }

            // 2. Her popüler öğeyi O(1) indeksler üzerinden anında eşleştir
            foreach (var item in items)
            {
                item.MatchedChannel = null;
                item.MatchedSeries = null;

                string normTitle = NormalizeTmdbTitle(item.Title);
                string normOrig = NormalizeTmdbTitle(item.OriginalTitle);
                int? itemYear = int.TryParse(item.ReleaseYear, out int y) && y > 0 ? y : null;

                if (item.MediaType == "tv")
                {
                    // 1. TmdbId ile doğrudan O(1) eşleşme
                    if (item.TmdbId > 0 && seriesByTmdbId.TryGetValue(item.TmdbId, out var tmdbMatchedCh))
                    {
                        if (seriesEpisodesByShow.TryGetValue(tmdbMatchedCh.ShowName!, out var eps))
                        {
                            item.MatchedSeries = BuildSeriesCard(tmdbMatchedCh.ShowName!, eps, historyByUrl);
                            continue;
                        }
                    }

                    // 2. Normalize başlık ile O(1) eşleşme
                    if (!string.IsNullOrEmpty(normTitle) && seriesByNormTitle.TryGetValue(normTitle, out var matchInfo))
                    {
                        if (!itemYear.HasValue || !matchInfo.Year.HasValue || Math.Abs(itemYear.Value - matchInfo.Year.Value) <= 1)
                        {
                            item.MatchedSeries = BuildSeriesCard(matchInfo.ShowName, matchInfo.Eps, historyByUrl);
                            continue;
                        }
                    }
                    if (!string.IsNullOrEmpty(normOrig) && seriesByNormTitle.TryGetValue(normOrig, out matchInfo))
                    {
                        if (!itemYear.HasValue || !matchInfo.Year.HasValue || Math.Abs(itemYear.Value - matchInfo.Year.Value) <= 1)
                        {
                            item.MatchedSeries = BuildSeriesCard(matchInfo.ShowName, matchInfo.Eps, historyByUrl);
                            continue;
                        }
                    }
                }
                else
                {
                    // 1. TmdbId ile doğrudan O(1) eşleşme
                    if (item.TmdbId > 0 && vodByTmdbId.TryGetValue(item.TmdbId, out var tmdbVodCh))
                    {
                        item.MatchedChannel = tmdbVodCh;
                        continue;
                    }

                    // 2. Normalize başlık ile O(1) eşleşme
                    if (!string.IsNullOrEmpty(normTitle) && vodByNormTitle.TryGetValue(normTitle, out var vodMatch))
                    {
                        if (!itemYear.HasValue || !vodMatch.Year.HasValue || Math.Abs(itemYear.Value - vodMatch.Year.Value) <= 1)
                        {
                            item.MatchedChannel = vodMatch.Channel;
                            continue;
                        }
                    }
                    if (!string.IsNullOrEmpty(normOrig) && vodByNormTitle.TryGetValue(normOrig, out vodMatch))
                    {
                        if (!itemYear.HasValue || !vodMatch.Year.HasValue || Math.Abs(itemYear.Value - vodMatch.Year.Value) <= 1)
                        {
                            item.MatchedChannel = vodMatch.Channel;
                            continue;
                        }
                    }
                }
            }
        }

        private void SetupHeroDots(int count)
        {
            _heroDots.Clear();
            for (int i = 0; i < count; i++)
            {
                _heroDots.Add(new HeroDotItem
                {
                    Index = i,
                    IsActive = (i == _currentHeroIndex)
                });
            }
        }

        private void StartHeroCarouselTimer()
        {
            if (_heroCarouselTimer == null)
            {
                _heroCarouselTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(7.5)
                };
                _heroCarouselTimer.Tick += (s, e) =>
                {
                    if (_displayPopularItems.Count > 1 && _currentTab == "Anasayfa")
                    {
                        SetHeroIndex((_currentHeroIndex + 1) % _displayPopularItems.Count);
                    }
                };
            }
            _heroCarouselTimer.Stop();
            _heroCarouselTimer.Start();
        }

        private void PreloadCachedImagesForPopularItem(PopularMediaItem item)
        {
            bool isCurrentHero = _displayPopularItems.Count == 0 ||
                                 (_currentHeroIndex >= 0 && _currentHeroIndex < _displayPopularItems.Count && _displayPopularItems[_currentHeroIndex] == item);

            if (isCurrentHero && item.BackdropBitmap == null)
            {
                string bkKey = "tmdb_bk_" + (item.TmdbId > 0 ? item.TmdbId.ToString() : item.BackdropPath.Replace("/", "_"));
                var cachedBk = GetBackdropFromCache(bkKey);
                if (cachedBk != null)
                {
                    item.BackdropBitmap = cachedBk;
                }
                else
                {
                    string diskPath = GetBackdropDiskPath(bkKey);
                    if (File.Exists(diskPath))
                    {
                        try
                        {
                            using var fs = File.OpenRead(diskPath);
                            var bmp = Bitmap.DecodeToWidth(fs, 1280);
                            SetBackdropCache(bkKey, bmp);
                            item.BackdropBitmap = bmp;
                        }
                        catch { }
                    }
                }
            }

            if (item.PosterBitmap == null)
            {
                string postKey = "tmdb_pop_" + (item.TmdbId > 0 ? item.TmdbId.ToString() : item.PosterPath.Replace("/", "_"));
                var cachedPost = GetPosterFromCache(postKey);
                if (cachedPost != null)
                {
                    item.PosterBitmap = cachedPost;
                }
                else
                {
                    string diskPostPath = GetPosterDiskPath(postKey);
                    if (File.Exists(diskPostPath))
                    {
                        try
                        {
                            using var fs = File.OpenRead(diskPostPath);
                            var bmp = Bitmap.DecodeToWidth(fs, 250);
                            SetPosterCache(postKey, bmp);
                            item.PosterBitmap = bmp;
                        }
                        catch { }
                    }
                }
            }
        }

        private void SetHeroIndex(int index)
        {
            if (_displayPopularItems.Count == 0) return;
            if (index < 0) index = _displayPopularItems.Count - 1;
            if (index >= _displayPopularItems.Count) index = 0;

            _currentHeroIndex = index;

            for (int i = 0; i < _heroDots.Count; i++)
            {
                _heroDots[i].IsActive = (i == _currentHeroIndex);
            }

            var item = _displayPopularItems[_currentHeroIndex];
            PreloadCachedImagesForPopularItem(item);
            UpdateHeroBannerDisplay();

            // Aktif öğenin backdropları henüz diskte yoksa veya indirilmemişse arka planda indirip uygula
            if (item.BackdropBitmap == null && !string.IsNullOrEmpty(item.BackdropUrl))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string bkKey = "tmdb_bk_" + (item.TmdbId > 0 ? item.TmdbId.ToString() : item.BackdropPath.Replace("/", "_"));
                        string diskPath = GetBackdropDiskPath(bkKey);
                        if (!File.Exists(diskPath))
                        {
                            EnsureTmdbHttpClient();
                            var bkBytes = await _tmdbHttpClient!.GetByteArrayAsync(item.BackdropUrl);
                            await File.WriteAllBytesAsync(diskPath, bkBytes);
                        }
                        if (File.Exists(diskPath))
                        {
                            await using var fs = File.OpenRead(diskPath);
                            var bmp = Bitmap.DecodeToWidth(fs, 1280);
                            SetBackdropCache(bkKey, bmp);
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                if (_displayPopularItems.Count > _currentHeroIndex && _displayPopularItems[_currentHeroIndex] == item)
                                {
                                    item.BackdropBitmap = bmp;
                                    if (HeroBackdropImg != null) HeroBackdropImg.Source = bmp;
                                }
                            });
                        }
                    }
                    catch { }
                });
            }
        }

        private void UpdateHeroBannerDisplay()
        {
            if (_displayPopularItems.Count == 0 || _currentHeroIndex < 0 || _currentHeroIndex >= _displayPopularItems.Count)
                return;

            var item = _displayPopularItems[_currentHeroIndex];

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (HeroTypeBadgeText != null) HeroTypeBadgeText.Text = item.MediaTypeBadge;
                    if (HeroRatingText != null) HeroRatingText.Text = item.RatingFormatted;
                    if (HeroTitleText != null) HeroTitleText.Text = item.Title;
                    if (HeroTaglineText != null)
                    {
                        HeroTaglineText.Text = item.TaglineFormatted;
                        HeroTaglineText.IsVisible = item.HasTagline;
                    }
                    if (HeroOverviewText != null) HeroOverviewText.Text = item.Overview;
                    if (HeroGenresList != null) HeroGenresList.ItemsSource = item.Genres;
                    if (HeroFavIconText != null)
                    {
                        HeroFavIconText.Text = item.FavoriteIcon;
                        HeroFavIconText.Foreground = item.FavoriteBrush;
                    }
                    if (HeroBackdropImg != null)
                    {
                        var targetBmp = item.BackdropBitmap ?? item.PosterBitmap;
                        if (targetBmp != null && HeroBackdropImg.Source != targetBmp)
                        {
                            HeroBackdropImg.Source = targetBmp;
                        }
                    }
                }
                catch { }
            });
        }

        private async Task LoadTmdbPostersAndBackdropsForPopularItemsAsync(List<PopularMediaItem> items)
        {
            var semaphore = new SemaphoreSlim(6, 6);
            var tasks = items.Select(async item =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // 1. Backdrop Yükle (Disk önbelleğine kaydet, sadece aktif banner öğesi için decode et)
                    string bkKey = "tmdb_bk_" + (item.TmdbId > 0 ? item.TmdbId.ToString() : item.BackdropPath.Replace("/", "_"));
                    string diskPath = GetBackdropDiskPath(bkKey);
                    if (!File.Exists(diskPath) && !string.IsNullOrEmpty(item.BackdropUrl))
                    {
                        EnsureTmdbHttpClient();
                        var bkBytes = await _tmdbHttpClient!.GetByteArrayAsync(item.BackdropUrl);
                        await File.WriteAllBytesAsync(diskPath, bkBytes);
                    }

                    bool isCurrentHero = _displayPopularItems.Count > 0 &&
                                         _currentHeroIndex >= 0 &&
                                         _currentHeroIndex < _displayPopularItems.Count &&
                                         _displayPopularItems[_currentHeroIndex] == item;

                    if (isCurrentHero && item.BackdropBitmap == null && File.Exists(diskPath))
                    {
                        try
                        {
                            await using var fs = File.OpenRead(diskPath);
                            var bmp = Bitmap.DecodeToWidth(fs, 1280);
                            SetBackdropCache(bkKey, bmp);
                            item.BackdropBitmap = bmp;
                        }
                        catch { }
                    }

                    // 2. Poster Yükle (250px)
                    string postKey = "tmdb_pop_" + (item.TmdbId > 0 ? item.TmdbId.ToString() : item.PosterPath.Replace("/", "_"));
                    var cachedPost = GetPosterFromCache(postKey);
                    if (cachedPost != null)
                    {
                        item.PosterBitmap = cachedPost;
                    }
                    else
                    {
                        string diskPostPath = GetPosterDiskPath(postKey);
                        if (File.Exists(diskPostPath))
                        {
                            try
                            {
                                await using var fs = File.OpenRead(diskPostPath);
                                var bmp = Bitmap.DecodeToWidth(fs, 250);
                                SetPosterCache(postKey, bmp);
                                item.PosterBitmap = bmp;
                            }
                            catch { }
                        }
                        else if (!string.IsNullOrEmpty(item.PosterUrl))
                        {
                            EnsureTmdbHttpClient();
                            var postBytes = await _tmdbHttpClient!.GetByteArrayAsync(item.PosterUrl);
                            await File.WriteAllBytesAsync(diskPostPath, postBytes);
                            using var ms = new MemoryStream(postBytes);
                            var netBmp = Bitmap.DecodeToWidth(ms, 250);
                            SetPosterCache(postKey, netBmp);
                            item.PosterBitmap = netBmp;
                        }
                    }

                    if (_displayPopularItems.Count > 0 && _displayPopularItems[_currentHeroIndex] == item)
                    {
                        UpdateHeroBannerDisplay();
                    }
                }
                catch { }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);
        }

        // ─────────────────────────────────────────────────────────────
        // Hero Banner Kontrolleri ve Tıklamaları
        // ─────────────────────────────────────────────────────────────
        private void HeroPrev_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            SetHeroIndex(_currentHeroIndex - 1);
            StartHeroCarouselTimer(); // Manuel tıklamada süreyi sıfırla
        }

        private void HeroNext_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            SetHeroIndex(_currentHeroIndex + 1);
            StartHeroCarouselTimer();
        }

        private void HeroDot_Click(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control c || c.Tag is not HeroDotItem dot) return;
            e.Handled = true;
            SetHeroIndex(dot.Index);
            StartHeroCarouselTimer();
        }

        private async void HeroPlay_Click(object? sender, RoutedEventArgs e)
        {
            if (_displayPopularItems.Count == 0 || _currentHeroIndex >= _displayPopularItems.Count) return;
            var item = _displayPopularItems[_currentHeroIndex];

            // 1. Doğrudan eşleşen VOD kanalı varsa oynatmayı başlat
            if (item.MatchedChannel != null)
            {
                await StartPlayingChannel(item.MatchedChannel, resume: false);
                return;
            }

            // 2. Doğrudan eşleşen Dizi varsa seçili/ilk bölümü oynat
            if (item.MatchedSeries != null)
            {
                var ep = item.MatchedSeries.SelectedEpisode 
                      ?? item.MatchedSeries.EpisodesBySeason.Values.SelectMany(x => x).FirstOrDefault();
                if (ep != null)
                {
                    SaveSeriesSelection(item.MatchedSeries);
                    await StartPlayingChannel(ep, resume: false);
                    return;
                }
            }

            ShowToast("İçerik mevcut oynatma listenizde bulunamadı.");
        }

        private void HeroDetail_Click(object? sender, RoutedEventArgs e)
        {
            if (_displayPopularItems.Count == 0 || _currentHeroIndex >= _displayPopularItems.Count) return;
            var item = _displayPopularItems[_currentHeroIndex];

            if (item.MatchedChannel != null)
            {
                VodInfo_Click(item.MatchedChannel, new RoutedEventArgs());
                return;
            }

            if (item.MatchedSeries != null)
            {
                SeriesInfo_Click(item.MatchedSeries, new RoutedEventArgs());
                return;
            }

            // Oynatma listesinde eşleşmeyen popüler TMDb içeriği için doğrudan TMDb verileri ile detay modalını aç
            ShowPopularItemTmdbDetail(item);
        }

        /// <summary>
        /// Oynatma listesinde bulunmayan popüler TMDb içerikleri için detay modalını açar
        /// </summary>
        private async void ShowPopularItemTmdbDetail(PopularMediaItem item)
        {
            if (item == null) return;

            PreloadCachedImagesForPopularItem(item);

            var dummyChannel = new Channel
            {
                Name = item.Title,
                OriginalName = item.OriginalTitle,
                Group = item.Genres.FirstOrDefault() ?? (item.MediaType == "tv" ? "Dizi" : "Sinema"),
                Type = item.MediaType == "tv" ? "Dizi" : "VOD",
                TmdbId = item.TmdbId,
                LogoBitmap = item.PosterBitmap,
                IsFavorite = false,
                Url = ""
            };

            _currentVodInfo = dummyChannel;
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

            VodInfoBackdropImage.Source = item.BackdropBitmap ?? item.PosterBitmap;

            VodInfoTitle.Text      = item.Title;
            VodInfoCategory.Text   = item.Genres.Count > 0 ? string.Join(", ", item.Genres) : (item.MediaType == "tv" ? "Dizi" : "Sinema");
            VodInfoGenre.Text      = item.Genres.Count > 0 ? string.Join(", ", item.Genres) : (item.MediaType == "tv" ? "Dizi" : "Sinema");
            VodInfoModalTitle.Text = item.MediaType == "tv" ? "Dizi Detayları" : "Film Detayları";

            var activeSource = _sources.FirstOrDefault(s => s.IsActive);
            VodInfoSource.Text  = activeSource?.Name ?? "TMDb Keşif";
            VodInfoFavText.Text = "♡ Favori";

            // Poster
            VodInfoPoster.Child = null;
            if (item.PosterBitmap != null)
            {
                VodInfoPoster.Background = Avalonia.Media.Brushes.Transparent;
                VodInfoPoster.Child = new Avalonia.Controls.Image
                {
                    Source = item.PosterBitmap,
                    Stretch = Avalonia.Media.Stretch.UniformToFill
                };
            }
            else
            {
                VodInfoPoster.Background = Avalonia.Media.Brush.Parse("#1A4f8bff");
                VodInfoPoster.Child = new Avalonia.Controls.TextBlock
                {
                    Text = item.MediaType == "tv" ? "🎞️" : "🎬",
                    FontSize = 40,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
                    Opacity = 0.5
                };
            }

            if (!string.IsNullOrEmpty(item.OriginalTitle) && !item.OriginalTitle.Equals(item.Title, StringComparison.OrdinalIgnoreCase))
            {
                VodInfoOrigName.Text = item.OriginalTitle;
                VodInfoOrigRow.IsVisible = true;
            }

            if (!string.IsNullOrEmpty(item.Overview))
            {
                VodInfoPlot.Text = item.Overview;
                VodInfoPlotRow.IsVisible = true;
            }

            if (item.VoteAverage > 0)
            {
                VodInfoAge.Text = $"⭐ {item.VoteAverage:F1}/10";
                VodInfoAgeRow.IsVisible = true;
            }

            if (!string.IsNullOrEmpty(item.ReleaseYear))
            {
                VodInfoDate.Text = item.ReleaseYear;
                VodInfoDateRow.IsVisible = true;
            }

            VodInfoOverlay.IsVisible = true;

            // TMDb'den tam detayları çek
            int? knownYear = int.TryParse(item.ReleaseYear, out int y) ? y : null;
            await FetchTmdbInfo(
                item.Title,
                item.MediaType == "tv" ? "Dizi" : "VOD",
                null,
                item.TmdbId > 0 ? item.TmdbId : null,
                item.OriginalTitle,
                knownYear
            );
        }

        private void HeroFavorite_Click(object? sender, RoutedEventArgs e)
        {
            if (_displayPopularItems.Count == 0 || _currentHeroIndex >= _displayPopularItems.Count) return;
            var item = _displayPopularItems[_currentHeroIndex];

            if (item.MatchedChannel != null)
            {
                item.MatchedChannel.IsFavorite = !item.MatchedChannel.IsFavorite;
                var activeSource = _sources.FirstOrDefault(s => s.IsActive);
                if (activeSource != null) SaveChannelsForSource(activeSource.Id);
                item.NotifyFavoriteChanged();
                UpdateHeroBannerDisplay();
                ShowToast(item.MatchedChannel.IsFavorite ? "Favorilere eklendi" : "Favorilerden çıkarıldı");
            }
            else if (item.MatchedSeries != null)
            {
                bool newState = ToggleSeriesFavorite(item.MatchedSeries.ShowName);
                item.NotifyFavoriteChanged();
                UpdateHeroBannerDisplay();
                ShowToast(newState ? "Dizi favorilere eklendi" : "Dizi favorilerden çıkarıldı");
            }
            else
            {
                ShowToast("Bu içerik henüz oynatma listenizde bulunmuyor.");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 2. Devam Et Bölümü (İzleme Geçmişi)
        // ─────────────────────────────────────────────────────────────
        private void RefreshHomeResumeSection()
        {
            var resumeList = new List<ResumeWatchItem>();
            var seenSeries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenVods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var historyByUrl = GetWatchHistoryByUrlCache();

            // En son izlenenden eskiye doğru sıralı
            var validHistories = _watchHistory
                .Where(h => h.Position > 5000 && (h.Duration <= 0 || (double)h.Position / h.Duration < 0.95))
                .OrderByDescending(h => h.LastWatched)
                .ToList();

            if (validHistories.Count == 0)
            {
                _allResumeItems.Clear();
                _displayResumeItems.Clear();
                HomeResumeEmptyNotice.IsVisible = true;
                return;
            }

            // 1. Performans & 0 ms Donma: Dizi bölümlerini ve kanalları O(N) tek geçişte adlarına göre indeksle
            var seriesEpisodesByShow = new Dictionary<string, List<Channel>>(StringComparer.OrdinalIgnoreCase);
            var channelsByName = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in _allChannels)
            {
                if (c.IsHidden) continue;
                if (!string.IsNullOrEmpty(c.ShowName))
                {
                    if (!seriesEpisodesByShow.TryGetValue(c.ShowName, out var epList))
                    {
                        epList = new List<Channel>();
                        seriesEpisodesByShow[c.ShowName] = epList;
                    }
                    epList.Add(c);
                }
                if (!string.IsNullOrEmpty(c.Name) && !channelsByName.ContainsKey(c.Name))
                {
                    channelsByName[c.Name] = c;
                }
            }

            // 2. Geçmiş kayıtlarını O(1) doğrudan indeksler üzerinden bağla (Lazy DPAPI taraması tetiklenmez)
            foreach (var h in validHistories)
            {
                Channel? ch = null;
                if (!string.IsNullOrEmpty(h.Name) && channelsByName.TryGetValue(h.Name, out var foundByName))
                {
                    ch = foundByName;
                }
                else if (!string.IsNullOrEmpty(h.ShowName) && seriesEpisodesByShow.TryGetValue(h.ShowName, out var epList) && epList.Count > 0)
                {
                    ch = epList[0];
                }

                string type = string.IsNullOrEmpty(h.Type) ? (ch?.Type ?? "VOD") : h.Type;
                if (type == "Canlı") continue; // Canlı TV için devam et mantığı olmaz

                string title = !string.IsNullOrEmpty(h.Name) ? h.Name : (ch?.Name ?? "İçerik");
                string subtitle = "";

                SeriesCard? sCard = null;

                if (type == "Dizi" || !string.IsNullOrEmpty(h.ShowName))
                {
                    type = "Dizi";
                    string showName = !string.IsNullOrEmpty(h.ShowName) ? h.ShowName : (ch?.ShowName ?? title);

                    // Tekrar eden dizi kontrolü: Aynı diziden yalnızca en son izlenen bölüm gösterilir
                    if (seenSeries.Contains(showName)) continue;
                    seenSeries.Add(showName);

                    title = showName;

                    if (seriesEpisodesByShow.TryGetValue(showName, out var eps) && eps.Count > 0)
                    {
                        sCard = BuildSeriesCard(showName, eps, historyByUrl);
                        subtitle = sCard.Group;
                    }
                    else
                    {
                        subtitle = !string.IsNullOrEmpty(h.Group) ? h.Group : (ch?.Group ?? "Dizi");
                    }
                }
                else
                {
                    type = "VOD";

                    // Tekrar eden VOD kontrolü: Aynı film/içerikten yalnızca bir adet gösterilir
                    string vodKey = !string.IsNullOrEmpty(h.Url) ? h.Url : title;
                    if (seenVods.Contains(vodKey)) continue;
                    seenVods.Add(vodKey);

                    subtitle = !string.IsNullOrEmpty(h.Group) ? h.Group : (ch?.Group ?? "Film");
                }

                var item = new ResumeWatchItem
                {
                    History = h,
                    Channel = ch,
                    SeriesCard = sCard,
                    Title = title,
                    Subtitle = subtitle,
                    Type = type,
                    Position = h.Position,
                    Duration = h.Duration,
                    LogoBitmap = ch?.LogoBitmap ?? sCard?.LogoBitmap
                };

                resumeList.Add(item);
            }

            _allResumeItems = resumeList;

            // Filtre uygula
            var filtered = _allResumeItems;
            if (_currentResumeFilter == "Movies")
            {
                filtered = _allResumeItems.Where(i => i.Type == "VOD").ToList();
            }
            else if (_currentResumeFilter == "Series")
            {
                filtered = _allResumeItems.Where(i => i.Type == "Dizi").ToList();
            }

            // Akıllı ve pürüzsüz (flicker-free) koleksiyon senkronizasyonu
            // Önceden yüklenmiş LogoBitmap'leri ve mevcut öğeleri korur, ekran kırpışmasını (blink) önler
            for (int i = 0; i < filtered.Count; i++)
            {
                var newItem = filtered[i];
                if (i < _displayResumeItems.Count)
                {
                    var existing = _displayResumeItems[i];
                    bool sameItem = (existing.Type == newItem.Type && 
                                    ((existing.Type == "Dizi" && !string.IsNullOrEmpty(existing.SeriesCard?.ShowName) && existing.SeriesCard?.ShowName == newItem.SeriesCard?.ShowName) ||
                                     (!string.IsNullOrEmpty(existing.History?.Url) && existing.History?.Url == newItem.History?.Url)));

                    if (sameItem)
                    {
                        // Mevcut öğenin alanlarını güncelle, varsa mevcut posteri koru
                        existing.Channel = newItem.Channel;
                        existing.SeriesCard = newItem.SeriesCard;
                        existing.Title = newItem.Title;
                        existing.Subtitle = newItem.Subtitle;
                        existing.Position = newItem.Position;
                        existing.Duration = newItem.Duration;
                        existing.History = newItem.History;
                        if (existing.LogoBitmap == null && newItem.LogoBitmap != null)
                        {
                            existing.LogoBitmap = newItem.LogoBitmap;
                        }
                        filtered[i] = existing;
                        continue;
                    }
                    else
                    {
                        _displayResumeItems[i] = newItem;
                    }
                }
                else
                {
                    _displayResumeItems.Add(newItem);
                }
            }

            while (_displayResumeItems.Count > filtered.Count)
            {
                _displayResumeItems.RemoveAt(_displayResumeItems.Count - 1);
            }

            HomeResumeEmptyNotice.IsVisible = _displayResumeItems.Count == 0;

            // Eksik posterleri TMDb / Cache üzerinden paralel yükle
            _ = LoadPostersForResumeItemsAsync(filtered);
        }

        private async Task LoadPostersForResumeItemsAsync(List<ResumeWatchItem> items)
        {
            var list = items.Where(i => i.LogoBitmap == null).ToList();
            if (list.Count == 0) return;

            string logoCacheDir = GetLogoCacheDir();
            var semaphore = new SemaphoreSlim(4, 4);

            var tasks = list.Select(async item =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // 1. Channel veya SeriesCard hazırsa bitmap'i al
                    if (item.Channel?.LogoBitmap != null)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => item.LogoBitmap = item.Channel.LogoBitmap);
                        return;
                    }
                    if (item.SeriesCard?.LogoBitmap != null)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => item.LogoBitmap = item.SeriesCard.LogoBitmap);
                        return;
                    }

                    string searchKey = item.Type == "Dizi" && item.SeriesCard != null 
                        ? item.SeriesCard.ShowName 
                        : item.Title;

                    // 2. Bellek cache kontrolü (TMDb / Logo)
                    var cached = GetPosterFromCache(searchKey);
                    if (cached != null)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => item.LogoBitmap = cached);
                        return;
                    }

                    // 3. Disk cache kontrolü (TMDb)
                    string diskPath = GetPosterDiskPath(searchKey);
                    if (File.Exists(diskPath))
                    {
                        try
                        {
                            await using var fs = File.OpenRead(diskPath);
                            var bmp = Bitmap.DecodeToWidth(fs, 300);
                            SetPosterCache(searchKey, bmp);
                            await Dispatcher.UIThread.InvokeAsync(() => item.LogoBitmap = bmp);
                            return;
                        }
                        catch { }
                    }

                    // 4. Kanalın LogoUrl'i varsa onu dene
                    if (item.Channel != null && !string.IsNullOrEmpty(item.Channel.LogoUrl))
                    {
                        var bmp = await GetOrLoadLogoBitmap(item.Channel.LogoUrl, logoCacheDir);
                        if (bmp != null)
                        {
                            await Dispatcher.UIThread.InvokeAsync(() => item.LogoBitmap = bmp);
                            return;
                        }
                    }

                    // 5. TMDb üzerinden ara ve indir
                    string tmdbType = item.Type == "Dizi" ? "tv" : "movie";
                    var posterUrl = await SearchTmdbPosterUrl(searchKey, tmdbType);
                    if (!string.IsNullOrEmpty(posterUrl))
                    {
                        EnsureTmdbHttpClient();
                        var posterBytes = await _tmdbHttpClient!.GetByteArrayAsync(posterUrl);
                        await File.WriteAllBytesAsync(diskPath, posterBytes);
                        using var ms = new MemoryStream(posterBytes);
                        var bitmap = Bitmap.DecodeToWidth(ms, 300);
                        SetPosterCache(searchKey, bitmap);
                        await Dispatcher.UIThread.InvokeAsync(() => item.LogoBitmap = bitmap);
                    }
                }
                catch
                {
                    // Ignore
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);
        }

        // ─────────────────────────────────────────────────────────────
        // Devam Et Filtre Butonları (Tümü / Filmler / Diziler)
        // ─────────────────────────────────────────────────────────────
        private void ResumeFilter_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag) return;

            _currentResumeFilter = tag;

            BtnResumeAll.Classes.Remove("Selected");
            BtnResumeMovies.Classes.Remove("Selected");
            BtnResumeSeries.Classes.Remove("Selected");
            btn.Classes.Add("Selected");

            RefreshHomeResumeSection();
        }

        // ─────────────────────────────────────────────────────────────
        // Devam Et Kart Tıklamaları
        // ─────────────────────────────────────────────────────────────
        private async void ResumeCard_Click(object? sender, RoutedEventArgs e)
        {
            var item = (sender as Button)?.Tag as ResumeWatchItem 
                    ?? (sender as Control)?.Tag as ResumeWatchItem;
            if (item == null) return;

            Channel? targetChannel = item.Channel;
            if (targetChannel == null)
            {
                targetChannel = _allChannels.FirstOrDefault(ch => ch.Url == item.History?.Url)
                             ?? _allChannels.FirstOrDefault(ch => !string.IsNullOrEmpty(item.History?.ShowName) && ch.ShowName == item.History?.ShowName);
            }

            if (targetChannel != null)
            {
                _scrollOffsetBeforePlayer = ContentScrollViewer.Offset.Y;
                _currentChannel = targetChannel;
                PlayerTitleText.Text = targetChannel.Name;
                PlayerContainer.IsVisible = true;
                PlayerVideoHost.IsVisible = true;
                PlayerContainer.Background = Avalonia.Media.Brushes.Black;
                PlayerContainer.Height = 450;
                _resumePosition = item.Position;
                await PlayChannel(targetChannel.Url);
                var ts = TimeSpan.FromMilliseconds(item.Position);
                ShowToast($"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2} konumundan devam ediliyor");
            }
            else
            {
                ShowToast("İçerik mevcut oynatma listesinde bulunamadı.");
            }
        }

        private async void ResumeRestart_Click(object? sender, RoutedEventArgs e)
        {
            var item = (sender as Button)?.Tag as ResumeWatchItem 
                    ?? (sender as Control)?.Tag as ResumeWatchItem;
            if (item == null) return;

            Channel? targetChannel = item.Channel;
            if (targetChannel == null)
            {
                targetChannel = _allChannels.FirstOrDefault(ch => ch.Url == item.History?.Url)
                             ?? _allChannels.FirstOrDefault(ch => !string.IsNullOrEmpty(item.History?.ShowName) && ch.ShowName == item.History?.ShowName);
            }

            if (targetChannel != null)
            {
                _scrollOffsetBeforePlayer = ContentScrollViewer.Offset.Y;
                _currentChannel = targetChannel;
                PlayerTitleText.Text = targetChannel.Name;
                PlayerContainer.IsVisible = true;
                PlayerVideoHost.IsVisible = true;
                PlayerContainer.Background = Avalonia.Media.Brushes.Black;
                PlayerContainer.Height = 450;
                _resumePosition = 0;
                await PlayChannel(targetChannel.Url);
                ShowToast("Baştan başlatılıyor...");
            }
            else
            {
                ShowToast("İçerik mevcut oynatma listesinde bulunamadı.");
            }
        }

        private void ResumeFavorite_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not ResumeWatchItem item) return;

            if (item.Type == "Dizi")
            {
                string showName = item.SeriesCard?.ShowName 
                               ?? item.History?.ShowName 
                               ?? (item.Channel?.ShowName ?? item.Title);
                bool newState = ToggleSeriesFavorite(showName);
                if (item.SeriesCard != null)
                {
                    item.SeriesCard.OnPropertyChanged("IsFavorite");
                    item.SeriesCard.OnPropertyChanged("FavoriteIcon");
                    item.SeriesCard.OnPropertyChanged("FavoriteBrush");
                }
                item.NotifyFavoriteChanged();
                ShowToast(newState ? "Dizi favorilere eklendi" : "Dizi favorilerden çıkarıldı");
            }
            else
            {
                Channel? targetChannel = item.Channel;
                if (targetChannel == null)
                {
                    targetChannel = _allChannels.FirstOrDefault(ch => ch.Url == item.History?.Url)
                                 ?? _allChannels.FirstOrDefault(ch => !string.IsNullOrEmpty(item.History?.ShowName) && ch.ShowName == item.History?.ShowName);
                    if (targetChannel != null)
                    {
                        item.Channel = targetChannel;
                    }
                }

                if (targetChannel != null)
                {
                    targetChannel.IsFavorite = !targetChannel.IsFavorite;
                    var activeSource = _sources.FirstOrDefault(s => s.IsActive);
                    if (activeSource != null) SaveChannelsForSource(activeSource.Id);
                    if (_currentTab == "Favori" && _viewState == "Categories")
                        RefreshFavoriGrids();
                    item.NotifyFavoriteChanged();
                    ShowToast(targetChannel.IsFavorite ? "Favorilere eklendi" : "Favorilerden çıkarıldı");
                }
                else
                {
                    item.IsFavorite = !item.IsFavorite;
                    ShowToast(item.IsFavorite ? "Favorilere eklendi" : "Favorilerden çıkarıldı");
                }
            }
        }

        private void ResumeDetail_Click(object? sender, RoutedEventArgs e)
        {
            var item = (sender as Button)?.Tag as ResumeWatchItem 
                    ?? (sender as Control)?.Tag as ResumeWatchItem;
            if (item == null) return;

            if (item.Type == "Dizi")
            {
                if (item.SeriesCard != null)
                {
                    SeriesInfo_Click(item.SeriesCard, new RoutedEventArgs());
                    return;
                }

                string showName = (!string.IsNullOrEmpty(item.History?.ShowName) ? item.History!.ShowName : null)
                    ?? item.Channel?.ShowName 
                    ?? item.Title;

                var eps = _allChannels.Where(ch => ch.Type == "Dizi" && ch.ShowName == showName).ToList();
                if (eps.Count > 0)
                {
                    var card = BuildSeriesCard(showName, eps, GetWatchHistoryByUrlCache());
                    SeriesInfo_Click(card, new RoutedEventArgs());
                    return;
                }
            }

            if (item.Channel != null)
            {
                VodInfo_Click(item.Channel, new RoutedEventArgs());
                return;
            }

            var ch = _allChannels.FirstOrDefault(c => c.Url == item.History?.Url);
            if (ch != null)
            {
                VodInfo_Click(ch, new RoutedEventArgs());
            }
            else
            {
                ShowToast("Detay bilgisi yüklenemedi.");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Devam Et Yatay Kaydırma (Scroll & Ok Butonları)
        // ─────────────────────────────────────────────────────────────
        private void HomeResumeScroll_ScrollChanged(object? sender, Avalonia.Controls.ScrollChangedEventArgs e) =>
            UpdateHArrowVisibility(sender as ScrollViewer, HomeResumePrevBtn, HomeResumeNextBtn);

        private void HomeResumePrev_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ScrollHorizontally(HomeResumeScrollViewer, -1);
        }

        private void HomeResumeNext_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ScrollHorizontally(HomeResumeScrollViewer, 1);
        }
    }
}
