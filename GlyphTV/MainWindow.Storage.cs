// ============================================================
// MainWindow.Storage.cs
// Dosya yolları, ayarlar, izleme geçmişi, logo yükleme,
// hash yardımcısı, HTTP istemcisi, cache boyut limitleri
// ============================================================

using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow
    {
        // ─── Cache boyut sabitleri ────────────────────────────────────
        private const int MAX_LOGO_CACHE   = 300;
        private const int MAX_POSTER_CACHE = 150;

        // ─── Eşzamanlılık kilit nesneleri ─────────────────────────────
        private static readonly object _logoCacheLock   = new object();
        private static readonly object _posterCacheLock = new object();

        // Logo cache'in ekleme sırasını takip etmek için (FIFO/LRU)
        private static readonly List<string> _logoCacheOrder   = new List<string>();
        private static readonly List<string> _posterCacheOrder = new List<string>();

        // ─────────────────────────────────────────────────────────────
        // Logo cache – boyut korumalı ekleme (thread‑safe)
        // ─────────────────────────────────────────────────────────────
        private static void SetLogoCache(string key, Bitmap? bitmap)
        {
            lock (_logoCacheLock)
            {
                if (_logoCache.ContainsKey(key))
                {
                    _logoCache[key] = bitmap;
                    return;
                }

                // Limit aşıldıysa en eski girdileri sadece referans olarak sil,
                // bitmap'i dispose etme (hâlâ kullanılıyor olabilir!)
                while (_logoCacheOrder.Count >= MAX_LOGO_CACHE && _logoCacheOrder.Count > 0)
                {
                    string oldest = _logoCacheOrder[0];
                    _logoCacheOrder.RemoveAt(0);
                    if (_logoCache.TryGetValue(oldest, out _))
                    {
                        _logoCache.Remove(oldest);
                    }
                }

                _logoCache[key] = bitmap;
                _logoCacheOrder.Add(key);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // TMDb poster cache – boyut korumalı ekleme (thread‑safe)
        // ─────────────────────────────────────────────────────────────
        private static void SetPosterCache(string key, Bitmap? bitmap)
        {
            lock (_posterCacheLock)
            {
                if (_tmdbPosterCache.ContainsKey(key))
                {
                    _tmdbPosterCache[key] = bitmap;
                    return;
                }

                while (_posterCacheOrder.Count >= MAX_POSTER_CACHE && _posterCacheOrder.Count > 0)
                {
                    string oldest = _posterCacheOrder[0];
                    _posterCacheOrder.RemoveAt(0);
                    if (_tmdbPosterCache.TryGetValue(oldest, out _))
                    {
                        _tmdbPosterCache.Remove(oldest);
                    }
                }

                _tmdbPosterCache[key] = bitmap;
                _posterCacheOrder.Add(key);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Dosya yolları
        // ─────────────────────────────────────────────────────────────
        private string AppDataDir()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlyphTV");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        private string GetSourcesPath()     => Path.Combine(AppDataDir(), "sources.json");
        private string GetSettingsPath()    => Path.Combine(AppDataDir(), "settings.json");
        private string GetHistoryPath()     => Path.Combine(AppDataDir(), "history.json");
        private string GetChannelsPath(string sourceId) => Path.Combine(AppDataDir(), $"channels_{sourceId}.json");

        private string GetTmdbPosterDir()
        {
            string dir = Path.Combine(AppDataDir(), "tmdb_posters");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        private string GetPosterDiskPath(string name) =>
            Path.Combine(GetTmdbPosterDir(), GetUrlHash(name) + ".jpg");

        private string GetLogoCacheDir()
        {
            string dir = Path.Combine(AppDataDir(), "logos");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        // ─────────────────────────────────────────────────────────────
        // MD5 hash
        // ─────────────────────────────────────────────────────────────
        private string GetUrlHash(string input)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash).ToLower();
        }

        // ─────────────────────────────────────────────────────────────
        // HTTP istemcileri
        // ─────────────────────────────────────────────────────────────
        private void EnsureLogoHttpClient()
        {
            if (_logoHttpClient != null) return;
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            _logoHttpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            _logoHttpClient.DefaultRequestHeaders.Add("User-Agent", "VLC/3.0.20 LibVLC/3.0.20");
        }

        private static void EnsureTmdbHttpClient()
        {
            if (_tmdbHttpClient != null) return;
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            _tmdbHttpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            _tmdbHttpClient.DefaultRequestHeaders.Add("User-Agent", "GlyphTV/1.2.1");
        }

        // ─────────────────────────────────────────────────────────────
        // Ayarlar
        // ─────────────────────────────────────────────────────────────
        private void LoadAppSettings()
        {
            try
            {
                string path = GetSettingsPath();
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                    if (loaded != null) _appSettings = loaded;
                }
            }
            catch { }
        }

        private void SaveAppSettings()
        {
            try { File.WriteAllText(GetSettingsPath(), JsonSerializer.Serialize(_appSettings)); } catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // İzleme geçmişi
        // ─────────────────────────────────────────────────────────────
        private void LoadWatchHistory()
        {
            try
            {
                string path = GetHistoryPath();
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<List<WatchHistory>>(File.ReadAllText(path));
                    if (loaded != null) _watchHistory = loaded;
                }
            }
            catch { }
            _watchHistoryByUrlCache = null;
        }

        private void SaveWatchHistory()
        {
            try { File.WriteAllText(GetHistoryPath(), JsonSerializer.Serialize(_watchHistory)); } catch { }
        }

        private void UpsertWatchHistory(Channel channel, long position, long duration)
        {
            if (position < 5000) return;

            if (duration > 0 && (double)position / duration > 0.95)
            {
                _watchHistory.RemoveAll(h => h.Url == channel.Url);
                SaveWatchHistory();
                return;
            }

            var existing = _watchHistory.FirstOrDefault(h => h.Url == channel.Url);
            if (existing != null)
            {
                existing.Position    = position;
                existing.Duration    = duration;
                existing.LastWatched = DateTime.Now;
            }
            else
            {
                _watchHistory.Add(new WatchHistory
                {
                    Url           = channel.Url,
                    Name          = channel.Name,
                    Group         = channel.Group,
                    Type          = channel.Type,
                    Position      = position,
                    Duration      = duration,
                    LastWatched   = DateTime.Now,
                    ShowName      = channel.ShowName,
                    Season        = channel.Season,
                    EpisodeNumber = channel.EpisodeNumber
                });
            }

            if (_watchHistory.Count > 100)
                _watchHistory = _watchHistory.OrderByDescending(h => h.LastWatched).Take(100).ToList();

            _watchHistoryByUrlCache = null;
            SaveWatchHistory();
        }

        // ─────────────────────────────────────────────────────────────
        // Logo yükleme (M3U tvg-logo'ları)
        // Paralel yükleme: en fazla 4 aynı anda, disk cache destekli
        // Cache boyutu MAX_LOGO_CACHE ile sınırlanmış
        // ─────────────────────────────────────────────────────────────
        private async Task LoadLogosForChannelsAsync(IEnumerable<Channel> channels)
        {
            var list = channels.Where(c => !string.IsNullOrEmpty(c.LogoUrl) && c.LogoBitmap == null)
                .Where(c =>
                {
                    Bitmap? dummy;
                    lock (_posterCacheLock)
                    {
                        if (_tmdbPosterCache.TryGetValue(c.Name,    out dummy) && dummy != null) return false;
                        if (_tmdbPosterCache.TryGetValue(c.ShowName ?? "", out dummy) && dummy != null) return false;
                    }
                    return true;
                }).ToList();

            if (list.Count == 0) return;

            EnsureLogoHttpClient();
            string cacheDir = GetLogoCacheDir();

            var semaphore = new SemaphoreSlim(6);
            var tasks = list.Select(async ch =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // Thread‑safe cache okuması
                    Bitmap? cached = null;
                    lock (_logoCacheLock)
                    {
                        _logoCache.TryGetValue(ch.LogoUrl, out cached);
                    }
                    if (cached != null)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => ch.LogoBitmap = cached);
                        await Task.Delay(1); // UI'a nefes aldır
                        return;
                    }

                    string hash      = GetUrlHash(ch.LogoUrl);
                    string cachePath = Path.Combine(cacheDir, hash);
                    Bitmap? bitmap   = null;

                    if (File.Exists(cachePath))
                    {
                        try
                        {
                            await using var fs = File.OpenRead(cachePath);
                            bitmap = Bitmap.DecodeToWidth(fs, 300);
                        }
                        catch { bitmap = null; }
                    }
                    else
                    {
                        try
                        {
                            var bytes = await _logoHttpClient!.GetByteArrayAsync(ch.LogoUrl);
                            await File.WriteAllBytesAsync(cachePath, bytes);
                            using var ms = new MemoryStream(bytes);
                            bitmap = Bitmap.DecodeToWidth(ms, 300);
                        }
                        catch { bitmap = null; }
                    }

                    // Thread‑safe cache ekleme (dispose içermez)
                    SetLogoCache(ch.LogoUrl, bitmap);

                    if (bitmap != null)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => ch.LogoBitmap = bitmap);
                        await Task.Delay(1); // UI'a nefes aldır
                    }
                }
                catch { }
                finally { semaphore.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);
        }
    }
}