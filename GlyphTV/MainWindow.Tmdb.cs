// ============================================================
// MainWindow.Tmdb.cs
// TMDb API entegrasyonu: arama, poster yükleme, disk cache,
// önbellek ısıtma (preload)
// ============================================================

using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow
    {
        // ─────────────────────────────────────────────────────────────
        // Önceden derlenmiş Regex'ler — CleanNameForSearch
        // ─────────────────────────────────────────────────────────────
        private static readonly Regex _rxTmdbYear       = new(@"\((\d{4})\)|\[(\d{4})\]",   RegexOptions.Compiled);
        private static readonly Regex _rxTmdbBrackets   = new(@"\[.*?\]",                    RegexOptions.Compiled);
        private static readonly Regex _rxTmdbTechTags   = new(@"\b(4K|UHD|HDR|FHD|HD|SD|HEVC|H\.?265|H\.?264|BluRay|BRRip|WEB-?DL|WEBRip|DVDRip|HDTV|Dolby|Vision|Atmos|DTS|AAC|10bit|REMUX|DUAL|TR|ENG|Multi|Raw)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxTmdbEpCode     = new(@"\bS\d{1,2}E\d{1,2}\b",      RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxTmdbEpCode2    = new(@"\b\d{1,2}x\d{1,2}\b",       RegexOptions.Compiled);
        private static readonly Regex _rxTmdbSeasonWord = new(@"\b(Season|Sezon|Episode|Bölüm)\s*\d+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxTmdbSpaces     = new(@"\s+",                        RegexOptions.Compiled);

        // ─────────────────────────────────────────────────────────────
        // İsim temizleme (TMDb araması için)
        // ─────────────────────────────────────────────────────────────
        private (string name, int? year) CleanNameForSearch(string rawName)
        {
            string name  = rawName;
            int?   year  = null;

            var yearMatch = _rxTmdbYear.Match(name);
            if (yearMatch.Success)
            {
                year = int.Parse(yearMatch.Groups[1].Success ? yearMatch.Groups[1].Value : yearMatch.Groups[2].Value);
                name = name[..yearMatch.Index].Trim();
            }

            name = _rxTmdbBrackets.Replace(name, "").Trim();
            name = _rxTmdbTechTags.Replace(name, "").Trim();
            name = _rxTmdbEpCode.Replace(name, "").Trim();
            name = _rxTmdbEpCode2.Replace(name, "").Trim();
            name = _rxTmdbSeasonWord.Replace(name, "").Trim();
            name = _rxTmdbSpaces.Replace(name, " ").Trim();
            name = name.TrimEnd('-', '.', ',', ':', ' ');

            return (name, year);
        }

        // ─────────────────────────────────────────────────────────────
        // TMDb'den film/dizi bilgisi ve poster çek
        // ─────────────────────────────────────────────────────────────
        private async Task FetchTmdbInfo(string rawName, string contentType, SeriesCard? seriesCard = null)
        {
            try
            {
                var (searchName, year) = CleanNameForSearch(rawName);
                if (string.IsNullOrEmpty(searchName) || searchName.Length < 2) return;

                string cacheKey = $"{searchName}_{contentType}";
                JsonElement? cached;
                lock (_posterCacheLock) // _tmdbCache de aynı kilit altında (JsonElement? değer tipi değil ama güvenli)
                {
                    if (_tmdbCache.TryGetValue(cacheKey, out cached))
                    {
                        if (cached.HasValue)
                        {
                            _ = ApplyTmdbData(cached.Value, contentType, seriesCard);
                            return;
                        }
                    }
                }

                string type = contentType == "Dizi" ? "tv" : "movie";
                string yearParam = year.HasValue
                    ? (contentType == "Dizi" ? $"&first_air_date_year={year.Value}" : $"&year={year.Value}")
                    : "";

                string searchUrl = $"{TMDB_BASE}/search/{type}?api_key={TMDB_API_KEY}&language=tr-TR&query={Uri.EscapeDataString(searchName)}{yearParam}";

                EnsureTmdbHttpClient();
                string searchJson = await _tmdbHttpClient!.GetStringAsync(searchUrl);

                using var searchDoc = JsonDocument.Parse(searchJson);
                var results = searchDoc.RootElement.GetProperty("results");

                if (results.GetArrayLength() == 0 && year.HasValue)
                {
                    searchUrl = $"{TMDB_BASE}/search/{type}?api_key={TMDB_API_KEY}&language=tr-TR&query={Uri.EscapeDataString(searchName)}";
                    searchJson = await _tmdbHttpClient!.GetStringAsync(searchUrl);
                    using var doc2 = JsonDocument.Parse(searchJson);
                    results = doc2.RootElement.GetProperty("results");
                    if (results.GetArrayLength() == 0)
                    {
                        SetTmdbCache(cacheKey, null);
                        return;
                    }
                }
                else if (results.GetArrayLength() == 0)
                {
                    SetTmdbCache(cacheKey, null);
                    return;
                }

                int tmdbId = results[0].GetProperty("id").GetInt32();
                string detailUrl = $"{TMDB_BASE}/{type}/{tmdbId}?api_key={TMDB_API_KEY}&language=tr-TR&append_to_response=credits";

                string detailJson = await _tmdbHttpClient!.GetStringAsync(detailUrl);

                using var detailDoc = JsonDocument.Parse(detailJson);
                var detail = detailDoc.RootElement;

                SetTmdbCache(cacheKey, detail.Clone());

                await ApplyTmdbData(detail, contentType, seriesCard);
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // TMDb verisini UI'ya uygula
        // ─────────────────────────────────────────────────────────────
        private async Task ApplyTmdbData(JsonElement detail, string contentType, SeriesCard? seriesCard)
        {
            try
            {
                string SafeTmdb(JsonElement el, string key)
                {
                    if (!el.TryGetProperty(key, out var val)) return "";
                    return val.ValueKind switch
                    {
                        JsonValueKind.String => val.GetString() ?? "",
                        JsonValueKind.Number => val.GetRawText(),
                        _ => ""
                    };
                }

                string genres = "";
                if (detail.TryGetProperty("genres", out var genresArr) && genresArr.ValueKind == JsonValueKind.Array)
                    genres = string.Join(", ", genresArr.EnumerateArray()
                        .Where(g => g.TryGetProperty("name", out _))
                        .Select(g => g.GetProperty("name").GetString()));

                string cast = "";
                if (detail.TryGetProperty("credits", out var credits) &&
                    credits.TryGetProperty("cast", out var castArr) && castArr.ValueKind == JsonValueKind.Array)
                    cast = string.Join(", ", castArr.EnumerateArray()
                        .Take(5)
                        .Where(c => c.TryGetProperty("name", out _))
                        .Select(c => c.GetProperty("name").GetString()));

                string director = "";
                if (detail.TryGetProperty("credits", out var credits2) &&
                    credits2.TryGetProperty("crew", out var crewArr) && crewArr.ValueKind == JsonValueKind.Array)
                {
                    director = string.Join(", ", crewArr.EnumerateArray()
                        .Where(c => c.TryGetProperty("job", out var j) && j.GetString() == "Director")
                        .Take(2)
                        .Where(c => c.TryGetProperty("name", out _))
                        .Select(c => c.GetProperty("name").GetString()));

                    if (string.IsNullOrEmpty(director) &&
                        detail.TryGetProperty("created_by", out var creators) && creators.ValueKind == JsonValueKind.Array)
                        director = string.Join(", ", creators.EnumerateArray()
                            .Take(2)
                            .Where(c => c.TryGetProperty("name", out _))
                            .Select(c => c.GetProperty("name").GetString()));
                }

                string runtime = "";
                if (contentType == "Dizi")
                {
                    if (detail.TryGetProperty("episode_run_time", out var ert) &&
                        ert.ValueKind == JsonValueKind.Array && ert.GetArrayLength() > 0)
                        runtime = ert[0].GetRawText();
                }
                else runtime = SafeTmdb(detail, "runtime");

                string titleKey   = contentType == "Dizi" ? "name"             : "title";
                string origKey    = contentType == "Dizi" ? "original_name"    : "original_title";
                string dateKey    = contentType == "Dizi" ? "first_air_date"   : "release_date";

                string origName  = SafeTmdb(detail, origKey);
                string relDate   = SafeTmdb(detail, dateKey);
                string plot      = SafeTmdb(detail, "overview");
                string ratingStr = SafeTmdb(detail, "vote_average");
                string poster    = SafeTmdb(detail, "poster_path");

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!string.IsNullOrEmpty(genres))  VodInfoGenre.Text = genres;
                    if (!string.IsNullOrEmpty(origName)) { VodInfoOrigName.Text = origName; VodInfoOrigRow.IsVisible = true; }
                    if (!string.IsNullOrEmpty(director)) { VodInfoDirector.Text = director; VodInfoDirRow.IsVisible  = true; }
                    if (!string.IsNullOrEmpty(cast))     { VodInfoCast.Text     = cast;     VodInfoCastRow.IsVisible = true; }
                    if (!string.IsNullOrEmpty(runtime) && runtime != "0") { VodInfoDuration.Text = runtime + " dk"; VodInfoDurRow.IsVisible = true; }
                    if (!string.IsNullOrEmpty(relDate))  { VodInfoDate.Text     = relDate;  VodInfoDateRow.IsVisible = true; }
                    if (!string.IsNullOrEmpty(ratingStr) && ratingStr != "0")
                    {
                        if (double.TryParse(ratingStr, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out double rating) && rating > 0)
                        { VodInfoAge.Text = $"⭐ {rating:F1}/10"; VodInfoAgeRow.IsVisible = true; }
                    }
                    if (!string.IsNullOrEmpty(plot)) { VodInfoPlot.Text = plot; VodInfoPlotRow.IsVisible = true; }
                });

                // Poster indir
                if (!string.IsNullOrEmpty(poster))
                {
                    try
                    {
                        string posterUrl = TMDB_IMG + poster;
                        EnsureTmdbHttpClient();
                        var posterBytes = await _tmdbHttpClient!.GetByteArrayAsync(posterUrl);
                        using var ms = new MemoryStream(posterBytes);
                        var bitmap = Bitmap.DecodeToWidth(ms, 300);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            VodInfoPoster.Background = Avalonia.Media.Brushes.Transparent;
                            VodInfoPoster.Child = new Avalonia.Controls.Image
                                { Source = bitmap, Stretch = Avalonia.Media.Stretch.UniformToFill };

                            if (seriesCard != null)
                            {
                                seriesCard.LogoBitmap = bitmap;
                                SetPosterCache(seriesCard.ShowName, bitmap);
                            }
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // Poster yükleme – SeriesCard listesi
        // Daha önce tamamen seri (tek tek, aralarda sabit gecikmeyle)
        // çalışıyordu. Logo yüklemedeki gibi sınırlı eşzamanlılık
        // (SemaphoreSlim) kullanılarak büyük dizi kataloglarında poster
        // yükleme süresi belirgin şekilde kısaltılıyor.
        // ─────────────────────────────────────────────────────────────
        private async Task LoadTmdbPostersForCards(List<SeriesCard> cards)
        {
            var semaphore = new SemaphoreSlim(4, 4);
            try
            {
            var tasks = cards.Select(async card =>
            {
                bool memHit;
                Bitmap? memCached = null;
                lock (_posterCacheLock)
                    memHit = _tmdbPosterCache.TryGetValue(card.ShowName, out memCached);

                if (memHit)
                {
                    if (memCached != null && card.LogoBitmap != memCached)
                        await Dispatcher.UIThread.InvokeAsync(() => card.LogoBitmap = memCached);
                    return;
                }

                await semaphore.WaitAsync();
                try
                {
                    string diskPath = GetPosterDiskPath(card.ShowName);
                    if (File.Exists(diskPath))
                    {
                        try
                        {
                            var bytes = await Task.Run(() => File.ReadAllBytes(diskPath));
                            using var ms = new MemoryStream(bytes);
                            var bmp = Bitmap.DecodeToWidth(ms, 300);
                            SetPosterCache(card.ShowName, bmp);
                            await Dispatcher.UIThread.InvokeAsync(() => card.LogoBitmap = bmp);
                            return;
                        }
                        catch { /* disk cache bozuksa TMDb'den tekrar çekilecek */ }
                    }

                    var posterUrl = await SearchTmdbPosterUrl(card.ShowName, "tv");
                    if (!string.IsNullOrEmpty(posterUrl))
                    {
                        EnsureTmdbHttpClient();
                        var posterBytes = await _tmdbHttpClient!.GetByteArrayAsync(posterUrl);
                        await File.WriteAllBytesAsync(GetPosterDiskPath(card.ShowName), posterBytes);
                        using var ms = new MemoryStream(posterBytes);
                        var bitmap = Bitmap.DecodeToWidth(ms, 300);
                        SetPosterCache(card.ShowName, bitmap);
                        await Dispatcher.UIThread.InvokeAsync(() => card.LogoBitmap = bitmap);
                    }
                    else
                    {
                        SetPosterCache(card.ShowName, null);
                    }
                }
                catch { /* bu kart için poster alınamadı, devam */ }
                finally { semaphore.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);
            }
            finally { semaphore.Dispose(); }
        }

        // ─────────────────────────────────────────────────────────────
        // Poster yükleme – Channel listesi (VOD/Film)
        // Sınırlı eşzamanlılık (SemaphoreSlim) ile paralelleştirildi.
        // ─────────────────────────────────────────────────────────────
        private async Task LoadTmdbPostersForChannels(List<Channel> channels)
        {
            var semaphore = new SemaphoreSlim(4, 4);
            try
            {
            var tasks = channels.Select(async ch =>
            {
                string searchKey = !string.IsNullOrEmpty(ch.ShowName) ? ch.ShowName : ch.Name;
                string type = ch.Type == "Dizi" ? "tv" : "movie";

                bool memHit;
                Bitmap? cached = null;
                lock (_posterCacheLock)
                    memHit = _tmdbPosterCache.TryGetValue(searchKey, out cached);

                if (memHit)
                {
                    if (cached != null && ch.LogoBitmap != cached)
                        await Dispatcher.UIThread.InvokeAsync(() => ch.LogoBitmap = cached);
                    return;
                }

                await semaphore.WaitAsync();
                try
                {
                    string diskPath = GetPosterDiskPath(searchKey);
                    if (File.Exists(diskPath))
                    {
                        try
                        {
                            var bytes = await File.ReadAllBytesAsync(diskPath);
                            using var ms = new MemoryStream(bytes);
                            var bmp = Bitmap.DecodeToWidth(ms, 300);
                            SetPosterCache(searchKey, bmp);
                            await Dispatcher.UIThread.InvokeAsync(() => ch.LogoBitmap = bmp);
                            return;
                        }
                        catch { /* disk cache bozuksa TMDb'den tekrar çekilecek */ }
                    }

                    var posterUrl = await SearchTmdbPosterUrl(searchKey, type);
                    if (!string.IsNullOrEmpty(posterUrl))
                    {
                        EnsureTmdbHttpClient();
                        var posterBytes = await _tmdbHttpClient!.GetByteArrayAsync(posterUrl);
                        await File.WriteAllBytesAsync(diskPath, posterBytes);
                        using var ms = new MemoryStream(posterBytes);
                        var bitmap = Bitmap.DecodeToWidth(ms, 300);
                        SetPosterCache(searchKey, bitmap);
                        await Dispatcher.UIThread.InvokeAsync(() => ch.LogoBitmap = bitmap);
                    }
                    else
                    {
                        SetPosterCache(searchKey, null);
                    }
                }
                catch { /* bu içerik için poster alınamadı, devam */ }
                finally { semaphore.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);
            }
            finally { semaphore.Dispose(); }
        }

        // ─────────────────────────────────────────────────────────────
        // TMDb poster URL arama (sadece search – detay çekmez)
        // ─────────────────────────────────────────────────────────────
        private async Task<string> SearchTmdbPosterUrl(string name, string type)
        {
            try
            {
                var (searchName, year) = CleanNameForSearch(name);
                if (string.IsNullOrEmpty(searchName) || searchName.Length < 2) return "";

                string yearParam = year.HasValue
                    ? (type == "tv" ? $"&first_air_date_year={year.Value}" : $"&year={year.Value}")
                    : "";

                string url = $"{TMDB_BASE}/search/{type}?api_key={TMDB_API_KEY}&language=tr-TR&query={Uri.EscapeDataString(searchName)}{yearParam}";

                EnsureTmdbHttpClient();
                var json = await _tmdbHttpClient!.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var results = doc.RootElement.GetProperty("results");

                if (results.GetArrayLength() == 0 && year.HasValue)
                {
                    url = $"{TMDB_BASE}/search/{type}?api_key={TMDB_API_KEY}&language=tr-TR&query={Uri.EscapeDataString(searchName)}";
                    json = await _tmdbHttpClient!.GetStringAsync(url);
                    using var doc2 = JsonDocument.Parse(json);
                    results = doc2.RootElement.GetProperty("results");
                }

                if (results.GetArrayLength() > 0 &&
                    results[0].TryGetProperty("poster_path", out var pp) && pp.ValueKind == JsonValueKind.String)
                {
                    string path = pp.GetString() ?? "";
                    return !string.IsNullOrEmpty(path) ? TMDB_IMG + path : "";
                }
            }
            catch { }
            return "";
        }
    }
}