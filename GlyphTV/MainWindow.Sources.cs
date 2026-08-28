// ============================================================
// MainWindow.Sources.cs
// Kaynak yönetimi: ekleme, silme, seçme, yenileme
// M3U parse, Xtream Code player_api.php, URL indirme, içerik türü tespiti
// ============================================================

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow
    {
        private static readonly Regex _rxGroupTitle = new(@"group-title=""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex _rxTvgLogo = new(@"tvg-logo=""([^""]*)""", RegexOptions.Compiled);
        private static readonly Regex _rxXuiId = new(@"xui-id=""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex _rxTvgId = new(@"tvg-id=""([^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxVodInGroup = new(@"\bvod\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rx4kInGroup = new(@"\b4k\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxSeasonEp = new(@"\bS\d{1,2}\s*[.\-_]?\s*E\d{1,3}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxShowInfo = new(@"^(.+?)[\s\._\-]+S(\d{1,3})[\s\._\-]*E(\d{1,3})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxShowNameEnd = new(@"[:\-_\.]+$", RegexOptions.Compiled);

        private void SaveSources()
        {
            try
            {
                foreach (var s in _sources)
                {
                    s.PathOrUrlEncrypted = ProtectString(s.PathOrUrl);
                    s.UsernameEncrypted = ProtectString(s.Username);
                    s.PasswordEncrypted = ProtectString(s.Password);
                }
                File.WriteAllText(GetSourcesPath(), JsonSerializer.Serialize(_sources, JsonOptions));
            }
            catch (Exception ex) { LogError("SaveSources", ex); }
        }

        private async Task LoadSourcesAsync()
        {
            try
            {
                string path = GetSourcesPath();
                if (File.Exists(path))
                {
                    var loaded = await Task.Run(() =>
                    {
                        var json = File.ReadAllText(path);
                        return JsonSerializer.Deserialize<List<TvSource>>(json, JsonOptions);
                    });

                    if (loaded != null)
                    {
                        bool needsMigration = false;
                        foreach (var s in loaded)
                        {
                            if (!string.IsNullOrEmpty(s.PathOrUrlEncrypted))
                            {
                                s.PathOrUrl = UnprotectString(s.PathOrUrlEncrypted);
                                s.Username = UnprotectString(s.UsernameEncrypted);
                                s.Password = UnprotectString(s.PasswordEncrypted);
                            }
                            else if (!string.IsNullOrEmpty(s.LegacyPathOrUrl) ||
                                     !string.IsNullOrEmpty(s.LegacyPassword))
                            {
                                s.PathOrUrl = s.LegacyPathOrUrl ?? "";
                                s.Username = s.LegacyUsername ?? "";
                                s.Password = s.LegacyPassword ?? "";
                                needsMigration = true;
                            }

                            s.LegacyPathOrUrl = null;
                            s.LegacyUsername = null;
                            s.LegacyPassword = null;
                        }

                        if (needsMigration) SaveSources();

                        var active = loaded.FirstOrDefault(s => s.IsActive);
                        if (active != null)
                        {
                            LoadCategoriesCacheFromDisk(active.Id);
                        }

                        Dispatcher.UIThread.Post(() =>
                        {
                            _sources.Clear();
                            foreach (var s in loaded)
                            {
                                _sources.Add(s);
                                if (s.Type == "Xtream" && !s.ExpiryDate.HasValue)
                                {
                                    _ = FetchXtreamAccountExpiryDateAsync(s);
                                }
                            }
                        });

                        if (active != null)
                        {
                            await LoadChannelsForSourceAsync(active.Id);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex) { LogError("LoadSourcesAsync", ex); }

            Dispatcher.UIThread.Post(() => { if (_sources.Count == 0) UpdateView(); });
        }

        private void LoadSources()
        {
            try
            {
                string path = GetSourcesPath();
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<List<TvSource>>(File.ReadAllText(path), JsonOptions);
                    if (loaded != null)
                    {
                        bool needsMigration = false;
                        foreach (var s in loaded)
                        {
                            if (!string.IsNullOrEmpty(s.PathOrUrlEncrypted))
                            {
                                s.PathOrUrl = UnprotectString(s.PathOrUrlEncrypted);
                                s.Username = UnprotectString(s.UsernameEncrypted);
                                s.Password = UnprotectString(s.PasswordEncrypted);
                            }
                            else if (!string.IsNullOrEmpty(s.LegacyPathOrUrl) ||
                                     !string.IsNullOrEmpty(s.LegacyPassword))
                            {
                                s.PathOrUrl = s.LegacyPathOrUrl ?? "";
                                s.Username = s.LegacyUsername ?? "";
                                s.Password = s.LegacyPassword ?? "";
                                needsMigration = true;
                            }

                            s.LegacyPathOrUrl = null;
                            s.LegacyUsername = null;
                            s.LegacyPassword = null;
                        }

                        _sources.Clear();
                        foreach (var s in loaded)
                        {
                            _sources.Add(s);
                            // YENİ: Xtream kaynağının henüz bitiş tarihi çekilmemişse arka planda güncelle
                            if (s.Type == "Xtream" && !s.ExpiryDate.HasValue)
                            {
                                _ = FetchXtreamAccountExpiryDateAsync(s);
                            }
                        }

                        if (needsMigration) SaveSources();

                        var active = _sources.FirstOrDefault(s => s.IsActive);
                        if (active != null) { LoadChannelsForSourceSync(active.Id); return; }
                    }
                }
            }
            catch (Exception ex) { LogError("LoadSources", ex); }

            if (_sources.Count == 0) UpdateView();
        }

        private static string ProtectString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            try
            {
                var bytes = Encoding.UTF8.GetBytes(plainText);
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encrypted);
            }
            catch (Exception ex) { LogError("ProtectString", ex); return ""; }
        }

        internal static string UnprotectString(string encryptedBase64)
        {
            if (string.IsNullOrEmpty(encryptedBase64)) return "";
            try
            {
                var encrypted = Convert.FromBase64String(encryptedBase64);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) { LogError("UnprotectString", ex); return ""; }
        }

        private static readonly Dictionary<string, CancellationTokenSource> _saveDebounceTokens = new();
        private static readonly Dictionary<string, (List<Channel> Snapshot, string Path)> _pendingChannelSaves = new();
        private static readonly object _saveDebounceLock = new object();
        private static readonly Dictionary<string, List<Channel>> _decryptedChannelsCache = new();

        private void SaveChannelsForSource(string sourceId)
        {
            var snapshot = _allChannels.ToList();
            var path = GetChannelsPath(sourceId);

            CancellationTokenSource cts;
            lock (_saveDebounceLock)
            {
                if (_saveDebounceTokens.TryGetValue(sourceId, out var existing))
                {
                    try { existing.Cancel(); } catch { }
                }

                cts = new CancellationTokenSource();
                _saveDebounceTokens[sourceId] = cts;
                _pendingChannelSaves[sourceId] = (snapshot, path);
            }

            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(400, token);
                    WritePendingChannelSave(sourceId);
                }
                catch (TaskCanceledException) { }
                catch { }
            }, token);
        }

        private static void WritePendingChannelSave(string sourceId)
        {
            (List<Channel> Snapshot, string Path)? entry = null;
            lock (_saveDebounceLock)
            {
                if (_pendingChannelSaves.TryGetValue(sourceId, out var pending))
                {
                    entry = pending;
                    _pendingChannelSaves.Remove(sourceId);
                    _saveDebounceTokens.Remove(sourceId);
                }
            }
            if (entry == null) return;

            try
            {
                foreach (var ch in entry.Value.Snapshot)
                {
                    if (string.IsNullOrEmpty(ch.UrlEncrypted) && !string.IsNullOrEmpty(ch.Url))
                        ch.UrlEncrypted = ProtectString(ch.Url);
                }

                string tempPath = entry.Value.Path + ".tmp";
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                {
                    JsonSerializer.Serialize(fs, entry.Value.Snapshot, JsonOptions);
                }
                File.Move(tempPath, entry.Value.Path, overwrite: true);
            }
            catch (Exception ex) { LogError($"WritePendingChannelSave({sourceId})", ex); }
        }

        internal static void FlushPendingChannelSaves()
        {
            List<string> sourceIds;
            lock (_saveDebounceLock)
                sourceIds = _pendingChannelSaves.Keys.ToList();

            foreach (var id in sourceIds)
            {
                lock (_saveDebounceLock)
                {
                    if (_saveDebounceTokens.TryGetValue(id, out var cts))
                    {
                        try { cts.Cancel(); } catch { }
                    }
                }
                WritePendingChannelSave(id);
            }
        }

        private List<Channel>? ReadAndParseChannelsFromDisk(string sourceId, out string? migrationPath)
        {
            migrationPath = null;
            string path = GetChannelsPath(sourceId);
            if (!File.Exists(path)) return null;

            try
            {
                using var fs = File.OpenRead(path);
                var loadedList = JsonSerializer.Deserialize<List<Channel>>(fs, JsonOptions);
                if (loadedList == null) return null;

                bool needsMigration = false;
                var migrationLock = new object();

                System.Threading.Tasks.Parallel.ForEach(loadedList, ch =>
                {
                    if (!string.IsNullOrEmpty(ch.LegacyUrl))
                    {
                        ch.Url = ch.LegacyUrl;
                        lock (migrationLock) { needsMigration = true; }
                    }
                    ch.LegacyUrl = null;

                    // RAM Tasarrufu: Onbinlerce kanal arasındaki tekrarlayan string'leri havuzlayarak
                    // LOH ve Gen2 bellek ayak izini %40-60 oranında hafifletiyoruz
                    if (!string.IsNullOrEmpty(ch.Group)) ch.Group = string.Intern(ch.Group);
                    if (!string.IsNullOrEmpty(ch.Type)) ch.Type = string.Intern(ch.Type);
                });

                System.Threading.Tasks.Parallel.ForEach(
                    loadedList.Where(c => c.Type == "Dizi" && string.IsNullOrEmpty(c.ShowName)),
                    ch =>
                    {
                        var (showName, season, episode) = ParseShowInfo(ch.Name);
                        ch.ShowName = showName;
                        ch.Season = !string.IsNullOrEmpty(season) ? string.Intern(season) : season;
                        ch.EpisodeNumber = episode;
                    });

                if (needsMigration) migrationPath = path;
                return loadedList;
            }
            catch (Exception ex)
            {
                LogError($"ReadAndParseChannelsFromDisk({sourceId})", ex);
                return null;
            }
        }

        private void LoadChannelsForSourceSync(string sourceId)
        {
            LoadCategoriesCacheFromDisk(sourceId);

            if (_decryptedChannelsCache.TryGetValue(sourceId, out var cachedChannels))
            {
                _allChannels = cachedChannels;
                _contentCache.Clear();
                _seriesCardCache.Clear();
                _seriesSelections.Clear();
                RebuildCategoriesCache(sourceId, saveDisk: false);
                UpdateView();
                TriggerBackgroundEpgLoad(sourceId);
                return;
            }

            var list = ReadAndParseChannelsFromDisk(sourceId, out var migrationPath);

            if (list == null)
            {
                _allChannels = new List<Channel>();
                _contentCache.Clear();
                _seriesCardCache.Clear();
                _seriesSelections.Clear();
                UpdateView();
                return;
            }

            _allChannels = list;
            _contentCache.Clear();
            _seriesCardCache.Clear();
            _seriesSelections.Clear();
            if (list.Count > 0) _decryptedChannelsCache[sourceId] = list;

            RebuildCategoriesCache(sourceId, saveDisk: true);
            UpdateView();
            TriggerBackgroundEpgLoad(sourceId);
            _ = MatchPopularItemsWithChannelsAsync(_displayPopularItems.ToList());

            if (migrationPath != null)
            {
                var snapshot = list;
                var savePath = migrationPath;
                _ = Task.Run(() =>
                {
                    try
                    {
                        foreach (var ch in snapshot)
                            ch.UrlEncrypted = ProtectString(ch.Url);
                        File.WriteAllText(savePath, JsonSerializer.Serialize(snapshot, JsonOptions));
                    }
                    catch (Exception ex) { LogError("ChannelMigration", ex); }
                });
            }
        }

        private async Task LoadChannelsForSourceAsync(string sourceId)
        {
            // 1. Kategorileri anında (1 ms) diskten RAM'e yükle — Kullanıcı sekmelere tıkladığında anında kategorileri görsün
            LoadCategoriesCacheFromDisk(sourceId);

            if (_decryptedChannelsCache.TryGetValue(sourceId, out var cachedChannels))
            {
                _allChannels = cachedChannels;
                _contentCache.Clear();
                _seriesCardCache.Clear();
                _seriesSelections.Clear();
                RebuildCategoriesCache(sourceId, saveDisk: false);
                UpdateView();
                TriggerBackgroundEpgLoad(sourceId);
                return;
            }

            string? migrationPath = null;
            var list = await Task.Run(() => ReadAndParseChannelsFromDisk(sourceId, out migrationPath));

            if (list == null)
            {
                _allChannels = new List<Channel>();
                _contentCache.Clear();
                _seriesCardCache.Clear();
                _seriesSelections.Clear();
                UpdateView();
                return;
            }

            _allChannels = list;
            _contentCache.Clear();
            _seriesCardCache.Clear();
            _seriesSelections.Clear();
            if (list.Count > 0) _decryptedChannelsCache[sourceId] = list;

            RebuildCategoriesCache(sourceId, saveDisk: true);
            UpdateView();

            // Arka plan ağır işlerini açılış arayüzünü kilitlememesi için hafif ertelemeli başlat
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                TriggerBackgroundEpgLoad(sourceId);
                await MatchPopularItemsWithChannelsAsync(_displayPopularItems.ToList());
            });

            if (migrationPath != null)
            {
                var snapshot = list;
                var savePath = migrationPath;
                _ = Task.Run(() =>
                {
                    try
                    {
                        foreach (var ch in snapshot)
                            ch.UrlEncrypted = ProtectString(ch.Url);
                        File.WriteAllText(savePath, JsonSerializer.Serialize(snapshot, JsonOptions));
                    }
                    catch (Exception ex) { LogError("ChannelMigration", ex); }
                });
            }

            Dispatcher.UIThread.Post(() => TrimProcessMemory(), DispatcherPriority.Background);
        }

        private async void SelectSource_Click(object? sender, RoutedEventArgs e)
        {
            if (_isSwitchingSource) return;
            if (sender is not Button btn || btn.Tag is not TvSource source) return;

            _isSwitchingSource = true;
            try
            {
                foreach (var s in _sources) s.IsActive = false;
                source.IsActive = true;
                SaveSources();
                ShowToast($"'{source.Name}' yükleniyor...");
                await LoadChannelsForSourceAsync(source.Id);
                ShowToast($"'{source.Name}' kaynağı aktifleştirildi.");
            }
            finally { _isSwitchingSource = false; }
        }

        private async void DeleteSource_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not TvSource source) return;
            _sources.Remove(source);
            try { File.Delete(GetChannelsPath(source.Id)); } catch { }

            _decryptedChannelsCache.Remove(source.Id);

            if (source.IsActive && _sources.Count > 0)
            {
                _sources[0].IsActive = true;
                await LoadChannelsForSourceAsync(_sources[0].Id);
            }
            else if (_sources.Count == 0)
            {
                _allChannels.Clear();
                UpdateView();
            }
            SaveSources();
            ShowToast("Kaynak silindi.");
        }

        private async void RefreshSource_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not TvSource source) return;
            await RefreshSourceInternal(source);
        }

        private static readonly HashSet<string> _refreshingSourceIds = new();

        private async Task RefreshSourceInternal(TvSource source)
        {
            lock (_refreshingSourceIds)
            {
                if (!_refreshingSourceIds.Add(source.Id))
                {
                    ShowToast($"'{source.Name}' zaten yenileniyor, lütfen bekleyin...");
                    return;
                }
            }

            try
            {
                await RefreshSourceCore(source);
            }
            finally
            {
                lock (_refreshingSourceIds) _refreshingSourceIds.Remove(source.Id);
            }
        }

        private async Task RefreshSourceCore(TvSource source)
        {
            ShowToast($"'{source.Name}' yenileniyor, lütfen bekleyin...");

            var oldStates = new Dictionary<string, (bool isFavorite, bool isHidden)>();
            var oldEpisodeAddedDates = new Dictionary<string, DateTime>();

            if (source.IsActive)
            {
                foreach (var ch in _allChannels)
                {
                    if (ch.IsFavorite || ch.IsHidden)
                        oldStates[ch.Url] = (ch.IsFavorite, ch.IsHidden);
                    if (ch.Type == "Dizi")
                        oldEpisodeAddedDates[$"{ch.ShowName}|{ch.Season}|{ch.EpisodeNumber}"] = ch.AddedDate;
                }
            }
            else
            {
                try
                {
                    List<Channel>? relevant = null;
                    if (_decryptedChannelsCache.TryGetValue(source.Id, out var cachedList))
                    {
                        relevant = cachedList.Where(c => c.IsFavorite || c.IsHidden).ToList();
                        foreach (var ch in cachedList.Where(c => c.Type == "Dizi"))
                            oldEpisodeAddedDates[$"{ch.ShowName}|{ch.Season}|{ch.EpisodeNumber}"] = ch.AddedDate;
                    }
                    else
                    {
                        string path = GetChannelsPath(source.Id);
                        if (File.Exists(path))
                        {
                            relevant = await Task.Run(() =>
                            {
                                using var fs = File.OpenRead(path);
                                var list = JsonSerializer.Deserialize<List<Channel>>(fs, JsonOptions);
                                if (list == null) return null;

                                foreach (var ch in list.Where(c => c.Type == "Dizi"))
                                    oldEpisodeAddedDates[$"{ch.ShowName}|{ch.Season}|{ch.EpisodeNumber}"] = ch.AddedDate;

                                var subset = list.Where(c => c.IsFavorite || c.IsHidden).ToList();
                                System.Threading.Tasks.Parallel.ForEach(subset, ch =>
                                {
                                    ch.Url = !string.IsNullOrEmpty(ch.UrlEncrypted)
                                        ? UnprotectString(ch.UrlEncrypted)
                                        : (ch.LegacyUrl ?? "");
                                    ch.LegacyUrl = null;
                                });

                                return subset;
                            });
                        }
                    }

                    if (relevant != null)
                    {
                        foreach (var ch in relevant)
                            oldStates[ch.Url] = (ch.IsFavorite, ch.IsHidden);
                    }
                }
                catch { }
            }

            try
            {
                var newChannels = await FetchChannelsForSource(source);
                source.LastRefreshedDate = DateTime.Now;

                int restoredFav = 0, restoredHidden = 0;
                foreach (var ch in newChannels)
                {
                    if (!oldStates.TryGetValue(ch.Url, out var state)) continue;
                    ch.IsFavorite = state.isFavorite;
                    ch.IsHidden = state.isHidden;
                    if (state.isFavorite) restoredFav++;
                    if (state.isHidden) restoredHidden++;
                }

                _decryptedChannelsCache[source.Id] = newChannels;
                if (source.IsActive)
                {
                    _allChannels = newChannels;
                    _contentCache.Clear();
                    _seriesCardCache.Clear();
                    _seriesSelections.Clear();
                    UpdateView();
                }

                SaveChannelsForSource(source.Id);
                SaveSources();

                ShowToast($"'{source.Name}' yenilendi: {newChannels.Count} içerik ({restoredFav} favori korundu).");

                bool m3uAlreadyHasSeries = newChannels.Any(c => c.Type == "Dizi" || c.Url.Contains("/series/"));
                if (source.Type == "Xtream" && !m3uAlreadyHasSeries)
                    _ = LoadXtreamSeriesInBackground(source, oldStates, oldEpisodeAddedDates);

                if (source.Type == "Xtream")
                {
                    _ = FetchXtreamAccountExpiryDateAsync(source);
                    _ = LoadXtreamVodAddedDatesInBackground(source);
                    _ = LoadXtreamSeriesAddedDatesInBackground(source);
                }

                Dispatcher.UIThread.Post(() => TrimProcessMemory(), DispatcherPriority.Background);
            }
            catch (HttpRequestException hre) { ShowToast($"Yenileme hatası: {hre.Message}"); }
            catch (TaskCanceledException) { ShowToast("Yenileme zaman aşımına uğradı."); }
            catch (Exception ex) { ShowToast($"Yenileme hatası: {ex.Message}"); }
        }

        private async Task<List<Channel>> FetchChannelsForSource(TvSource source)
        {
            switch (source.Type)
            {
                case "M3U":
                    {
                        if (!File.Exists(source.PathOrUrl))
                            throw new FileNotFoundException("Kaynak dosyası mevcut değil.");
                        return await Task.Run(() =>
                        {
                            using var stream = File.OpenRead(source.PathOrUrl);
                            using var reader = new StreamReader(stream, Encoding.UTF8);
                            return ParseM3uFromReader(reader);
                        });
                    }
                case "Link":
                    {
                        EnsureDownloadHttpClient();
                        using var response = await _downloadHttpClient!.GetAsync(source.PathOrUrl, HttpCompletionOption.ResponseHeadersRead);
                        response.EnsureSuccessStatusCode();
                        using var stream = await response.Content.ReadAsStreamAsync();
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        return await Task.Run(() => ParseM3uFromReader(reader));
                    }
                case "Xtream":
                    {
                        EnsureDownloadHttpClient();
                        string url = $"{source.PathOrUrl.TrimEnd('/')}/get.php" +
                                     $"?username={Uri.EscapeDataString(source.Username)}" +
                                     $"&password={Uri.EscapeDataString(source.Password)}" +
                                     $"&type=m3u_plus&output=ts";
                        using var response = await _downloadHttpClient!.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                        response.EnsureSuccessStatusCode();
                        using var stream = await response.Content.ReadAsStreamAsync();
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        return await Task.Run(() => ParseM3uFromReader(reader));
                    }
                default:
                    throw new InvalidOperationException("Bilinmeyen kaynak türü.");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // GÜÇLENDİRİLMİŞ: Xtream kullanıcı hesap bitiş tarihini (exp_date) çeker
        // ─────────────────────────────────────────────────────────────
        private async Task FetchXtreamAccountExpiryDateAsync(TvSource source)
        {
            if (source.Type != "Xtream") return;
            try
            {
                string server = source.PathOrUrl.TrimEnd('/');
                string userEnc = Uri.EscapeDataString(source.Username);
                string passEnc = Uri.EscapeDataString(source.Password);
                string url = $"{server}/player_api.php?username={userEnc}&password={passEnc}";

                EnsureDownloadHttpClient();
                string json = await _downloadHttpClient!.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("user_info", out var userInfo) &&
                    userInfo.ValueKind == JsonValueKind.Object)
                {
                    string expRaw = XtreamStr(userInfo, "exp_date");
                    if (string.IsNullOrEmpty(expRaw) || expRaw == "null") expRaw = XtreamStr(userInfo, "expire_date");
                    if (string.IsNullOrEmpty(expRaw) || expRaw == "null") expRaw = XtreamStr(userInfo, "expiration_date");

                    if (!string.IsNullOrEmpty(expRaw) && expRaw != "null")
                    {
                        // 1. Unix timestamp (örn: 1766620800)
                        if (long.TryParse(expRaw, out var unixExp) && unixExp > 0)
                        {
                            source.ExpiryDate = DateTimeOffset.FromUnixTimeSeconds(unixExp).LocalDateTime;
                        }
                        // 2. Standart tarih string (örn: "2026-12-25" veya "2026-12-25 23:59:59")
                        else if (DateTime.TryParse(expRaw, out var parsedDate))
                        {
                            source.ExpiryDate = parsedDate;
                        }
                        else
                        {
                            source.ExpiryDate = null;
                        }
                    }
                    else
                    {
                        source.ExpiryDate = null;
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        SaveSources();
                    });
                }
            }
            catch (Exception ex)
            {
                LogError("FetchXtreamAccountExpiryDateAsync", ex);
            }
        }

        private static readonly HashSet<string> _loadingXtreamSeriesIds = new();

        private async Task LoadXtreamSeriesInBackground(
            TvSource source,
            Dictionary<string, (bool isFavorite, bool isHidden)>? oldStates = null,
            Dictionary<string, DateTime>? oldEpisodeAddedDates = null)
        {
            lock (_loadingXtreamSeriesIds)
            {
                if (!_loadingXtreamSeriesIds.Add(source.Id)) return;
            }

            try
            {
                List<Channel> seriesChannels;
                try
                {
                    seriesChannels = await FetchXtreamSeriesChannels(source, oldEpisodeAddedDates);
                }
                catch (Exception ex)
                {
                    LogError("LoadXtreamSeriesInBackground.Fetch", ex);
                    return;
                }

                if (seriesChannels.Count == 0) return;

                if (oldStates != null)
                {
                    foreach (var ch in seriesChannels)
                    {
                        if (oldStates.TryGetValue(ch.Url, out var state))
                        {
                            ch.IsFavorite = state.isFavorite;
                            ch.IsHidden = state.isHidden;
                        }
                    }
                }

                var stillExists = _sources.FirstOrDefault(s => s.Id == source.Id);
                if (stillExists == null) return;

                List<Channel> finalListToSave;

                if (stillExists.IsActive)
                {
                    _allChannels.AddRange(seriesChannels);
                    _contentCache.Clear();
                    _seriesCardCache.Clear();
                    finalListToSave = _allChannels;
                    _decryptedChannelsCache[source.Id] = _allChannels;

                    Dispatcher.UIThread.Post(UpdateView);
                }
                else
                {
                    if (_decryptedChannelsCache.TryGetValue(source.Id, out var cachedList))
                    {
                        cachedList.AddRange(seriesChannels);
                        finalListToSave = cachedList;
                    }
                    else
                    {
                        finalListToSave = seriesChannels;
                    }
                    _decryptedChannelsCache[source.Id] = finalListToSave;
                }

                SaveChannelsForSource(source.Id);
                Dispatcher.UIThread.Post(() => TrimProcessMemory(), DispatcherPriority.Background);
            }
            finally
            {
                lock (_loadingXtreamSeriesIds) { _loadingXtreamSeriesIds.Remove(source.Id); }
            }
        }

        private async Task<List<Channel>> FetchXtreamSeriesChannels(
            TvSource source, Dictionary<string, DateTime>? oldEpisodeAddedDates = null)
        {
            var result = new List<Channel>();

            string server = source.PathOrUrl.TrimEnd('/');
            string userEnc = Uri.EscapeDataString(source.Username);
            string passEnc = Uri.EscapeDataString(source.Password);
            string baseApi = $"{server}/player_api.php?username={userEnc}&password={passEnc}";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("User-Agent", "GlyphTV/1.1.0");

            var seriesCats = await GetXtreamCategoryMap(client, baseApi, "get_series_categories");

            string seriesJson;
            try
            {
                seriesJson = await client.GetStringAsync($"{baseApi}&action=get_series");
            }
            catch { return result; }

            List<JsonElement> seriesList;
            try
            {
                using var seriesDoc = JsonDocument.Parse(seriesJson);
                if (seriesDoc.RootElement.ValueKind != JsonValueKind.Array) return result;
                seriesList = seriesDoc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            }
            catch { return result; }

            if (seriesList.Count == 0) return result;

            // Disk önbelleğinde kayıtlı bölümleri oku (Delta-sync)
            string detailsCachePath = GetSeriesDetailsDiskPath(source.Id);
            var cachedChannelsBySeriesId = new Dictionary<string, List<Channel>>();
            if (File.Exists(detailsCachePath))
            {
                try
                {
                    var cachedJson = await File.ReadAllTextAsync(detailsCachePath);
                    var cachedList = JsonSerializer.Deserialize<List<Channel>>(cachedJson, JsonOptions);
                    if (cachedList != null)
                    {
                        foreach (var ch in cachedList)
                        {
                            string sKey = ch.XuiId ?? ch.ShowName ?? "";
                            if (!cachedChannelsBySeriesId.TryGetValue(sKey, out var list))
                            {
                                list = new List<Channel>();
                                cachedChannelsBySeriesId[sKey] = list;
                            }
                            list.Add(ch);
                        }
                    }
                }
                catch { }
            }

            var semaphore = new SemaphoreSlim(10);
            using var epClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            epClient.DefaultRequestHeaders.Add("User-Agent", "GlyphTV/1.1.0");

            var tasks = seriesList.Select(async series =>
            {
                string seriesId = XtreamStr(series, "series_id");
                if (string.IsNullOrEmpty(seriesId)) return;

                string showName = XtreamStr(series, "name");
                string catId = XtreamStr(series, "category_id");
                string catName = seriesCats.TryGetValue(catId, out var cn) ? cn : "Diğer";
                string logoUrl = XtreamStr(series, "cover");

                // Disk önbelleğinde bu dizi varsa doğrudan kullan
                if (cachedChannelsBySeriesId.TryGetValue(seriesId, out var cachedEpisodes) && cachedEpisodes.Count > 0)
                {
                    lock (result)
                    {
                        result.AddRange(cachedEpisodes);
                    }
                    return;
                }

                await semaphore.WaitAsync();
                try
                {

                    try
                    {
                        var infoJson = await epClient.GetStringAsync(
                            $"{baseApi}&action=get_series_info&series_id={seriesId}");
                        using var infoDoc = JsonDocument.Parse(infoJson);

                        int seriesTmdbId = 0;
                        string seriesOriginalName = "";
                        string seriesPlot = "", seriesCast = "", seriesDirector = "", seriesGenre = "", seriesRating = "", seriesReleaseDate = "";
                        if (infoDoc.RootElement.TryGetProperty("info", out var seriesInfo) &&
                            seriesInfo.ValueKind == JsonValueKind.Object)
                        {
                            string tmdbRaw = XtreamStr(seriesInfo, "tmdb_id");
                            if (string.IsNullOrEmpty(tmdbRaw)) tmdbRaw = XtreamStr(seriesInfo, "tmdb");
                            int.TryParse(tmdbRaw, out seriesTmdbId);
                            seriesOriginalName = XtreamStr(seriesInfo, "o_name");

                            seriesPlot = XtreamStr(seriesInfo, "plot");
                            if (string.IsNullOrEmpty(seriesPlot)) seriesPlot = XtreamStr(seriesInfo, "description");
                            seriesCast = XtreamStr(seriesInfo, "cast");
                            seriesDirector = XtreamStr(seriesInfo, "director");
                            seriesGenre = XtreamStr(seriesInfo, "genre");
                            seriesRating = XtreamStr(seriesInfo, "rating");
                            seriesReleaseDate = XtreamStr(seriesInfo, "releaseDate");
                            if (string.IsNullOrEmpty(seriesReleaseDate)) seriesReleaseDate = XtreamStr(seriesInfo, "release_date");
                        }

                        if (infoDoc.RootElement.TryGetProperty("episodes", out var episodes) &&
                            episodes.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var seasonProp in episodes.EnumerateObject())
                            {
                                string season = $"S{seasonProp.Name.PadLeft(2, '0')}";

                                foreach (var ep in seasonProp.Value.EnumerateArray())
                                {
                                    string epId = XtreamStr(ep, "id", "0");
                                    string epNum = XtreamStr(ep, "episode_num", "0");
                                    string ext = XtreamStr(ep, "container_extension", "mkv");
                                    string title = XtreamStr(ep, "title");

                                    string epName = $"{showName} {season}E{epNum.PadLeft(2, '0')}";
                                    if (!string.IsNullOrEmpty(title)) epName += $" - {title}";

                                    int epNumForSig = int.TryParse(epNum, out var enSig) ? enSig : 0;
                                    string epSignature = $"{showName}|{season}|{epNumForSig}";

                                    DateTime epAdded;
                                    if (oldEpisodeAddedDates != null &&
                                        oldEpisodeAddedDates.TryGetValue(epSignature, out var knownAdded) &&
                                        knownAdded != DateTime.MinValue)
                                    {
                                        epAdded = knownAdded;
                                    }
                                    else
                                    {
                                        DateTime providerEpAdded = ParseXtreamAddedDate(ep);
                                        epAdded = providerEpAdded != DateTime.MinValue ? providerEpAdded : DateTime.Now;
                                    }

                                    lock (result)
                                    {
                                        result.Add(new Channel
                                        {
                                            Name = epName,
                                            Url = $"{server}/series/{source.Username}/{source.Password}/{epId}.{ext}",
                                            Group = catName,
                                            Type = "Dizi",
                                            LogoUrl = logoUrl,
                                            ShowName = showName,
                                            Season = season,
                                            EpisodeNumber = int.TryParse(epNum, out var en) ? en : 0,
                                            TmdbId = seriesTmdbId,
                                            OriginalName = seriesOriginalName,
                                            ProviderPlot = seriesPlot,
                                            ProviderCast = seriesCast,
                                            ProviderDirector = seriesDirector,
                                            ProviderGenre = seriesGenre,
                                            ProviderRating = seriesRating,
                                            ProviderReleaseDate = seriesReleaseDate,
                                            AddedDate = epAdded
                                        });
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);

            if (result.Count > 0)
            {
                try
                {
                    await File.WriteAllTextAsync(detailsCachePath, JsonSerializer.Serialize(result, JsonOptions));
                }
                catch { }
            }

            return result;
        }

        private async Task<Dictionary<string, string>> GetXtreamCategoryMap(
            HttpClient client, string baseApi, string action)
        {
            var map = new Dictionary<string, string>();
            try
            {
                var json = await client.GetStringAsync($"{baseApi}&action={action}");
                using var doc = JsonDocument.Parse(json);
                foreach (var cat in doc.RootElement.EnumerateArray())
                {
                    string id = XtreamStr(cat, "category_id");
                    string name = XtreamStr(cat, "category_name", "Diğer");
                    if (!string.IsNullOrEmpty(id)) map[id] = name;
                }
            }
            catch { }
            return map;
        }

        private static string XtreamStr(JsonElement el, string key, string fallback = "")
        {
            if (!el.TryGetProperty(key, out var val)) return fallback;
            return val.ValueKind switch
            {
                JsonValueKind.String => val.GetString() ?? fallback,
                JsonValueKind.Number => val.GetRawText(),
                _ => fallback
            };
        }

        private static DateTime ParseXtreamAddedDate(JsonElement el)
        {
            string raw = XtreamStr(el, "added");
            if (string.IsNullOrEmpty(raw)) return DateTime.MinValue;
            if (long.TryParse(raw, out var unixSeconds) && unixSeconds > 0)
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime; }
                catch { return DateTime.MinValue; }
            }
            return DateTime.MinValue;
        }

        private sealed class XtreamProviderInfo
        {
            public int TmdbId;
            public string OriginalName = "";
            public string Plot = "";
            public string Cast = "";
            public string Director = "";
            public string Genre = "";
            public string Duration = "";
            public string ReleaseDate = "";
            public string Rating = "";
            public string PosterUrl = "";
        }

        private static readonly Dictionary<string, XtreamProviderInfo> _xtreamVodInfoCache = new();
        private static readonly object _xtreamVodInfoCacheLock = new object();

        private async Task<Dictionary<string, DateTime>> FetchXtreamVodAddedDatesAsync(TvSource source)
        {
            var result = new Dictionary<string, DateTime>();
            try
            {
                string server = source.PathOrUrl.TrimEnd('/');
                string userEnc = Uri.EscapeDataString(source.Username);
                string passEnc = Uri.EscapeDataString(source.Password);
                string url = $"{server}/player_api.php?username={userEnc}&password={passEnc}&action=get_vod_streams";

                EnsureDownloadHttpClient();
                using var response = await _downloadHttpClient!.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

                foreach (var s in doc.RootElement.EnumerateArray())
                {
                    string streamId = XtreamStr(s, "stream_id");
                    if (string.IsNullOrEmpty(streamId)) continue;
                    result[streamId] = ParseXtreamAddedDate(s);
                }
            }
            catch (Exception ex) { LogError("FetchXtreamVodAddedDatesAsync", ex); }
            return result;
        }

        private static readonly HashSet<string> _loadingXtreamVodDatesIds = new();

        private async Task LoadXtreamVodAddedDatesInBackground(TvSource source)
        {
            lock (_loadingXtreamVodDatesIds)
            {
                if (!_loadingXtreamVodDatesIds.Add(source.Id)) return;
            }

            try
            {
                var dates = await FetchXtreamVodAddedDatesAsync(source);
                if (dates.Count == 0) return;

                var stillExists = _sources.FirstOrDefault(s => s.Id == source.Id);
                if (stillExists == null) return;

                List<Channel>? targetList = stillExists.IsActive
                    ? _allChannels
                    : (_decryptedChannelsCache.TryGetValue(source.Id, out var cached) ? cached : null);
                if (targetList == null) return;

                bool changed = false;
                foreach (var ch in targetList)
                {
                    if (ch.Type != "VOD") continue;
                    string? streamId = ExtractXtreamStreamId(ch);
                    if (streamId == null) continue;
                    if (dates.TryGetValue(streamId, out var added) &&
                        added != DateTime.MinValue && ch.AddedDate != added)
                    {
                        ch.AddedDate = added;
                        changed = true;
                    }
                }

                if (!changed) return;

                _contentCache.Clear();
                SaveChannelsForSource(source.Id);

                if (stillExists.IsActive)
                    Dispatcher.UIThread.Post(UpdateView);

                Dispatcher.UIThread.Post(() => TrimProcessMemory(), DispatcherPriority.Background);
            }
            finally
            {
                lock (_loadingXtreamVodDatesIds) { _loadingXtreamVodDatesIds.Remove(source.Id); }
            }
        }

        private async Task<Dictionary<string, DateTime>> FetchXtreamSeriesAddedDatesAsync(TvSource source)
        {
            var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string server = source.PathOrUrl.TrimEnd('/');
                string userEnc = Uri.EscapeDataString(source.Username);
                string passEnc = Uri.EscapeDataString(source.Password);
                string url = $"{server}/player_api.php?username={userEnc}&password={passEnc}&action=get_series";

                EnsureDownloadHttpClient();
                using var response = await _downloadHttpClient!.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

                foreach (var s in doc.RootElement.EnumerateArray())
                {
                    string showName = XtreamStr(s, "name").Trim();
                    if (string.IsNullOrEmpty(showName)) continue;

                    DateTime date = DateTime.MinValue;
                    string lastModRaw = XtreamStr(s, "last_modified");
                    if (!string.IsNullOrEmpty(lastModRaw) && long.TryParse(lastModRaw, out var unixMod) && unixMod > 0)
                    {
                        try { date = DateTimeOffset.FromUnixTimeSeconds(unixMod).LocalDateTime; } catch { }
                    }

                    if (date == DateTime.MinValue)
                    {
                        date = ParseXtreamAddedDate(s);
                    }

                    if (date != DateTime.MinValue)
                    {
                        result[showName] = date;
                    }
                }
            }
            catch (Exception ex) { LogError("FetchXtreamSeriesAddedDatesAsync", ex); }
            return result;
        }

        private static readonly HashSet<string> _loadingXtreamSeriesDatesIds = new();

        private async Task LoadXtreamSeriesAddedDatesInBackground(TvSource source)
        {
            lock (_loadingXtreamSeriesDatesIds)
            {
                if (!_loadingXtreamSeriesDatesIds.Add(source.Id)) return;
            }

            try
            {
                var dates = await FetchXtreamSeriesAddedDatesAsync(source);
                if (dates.Count == 0) return;

                var stillExists = _sources.FirstOrDefault(s => s.Id == source.Id);
                if (stillExists == null) return;

                List<Channel>? targetList = stillExists.IsActive
                    ? _allChannels
                    : (_decryptedChannelsCache.TryGetValue(source.Id, out var cached) ? cached : null);
                if (targetList == null) return;

                bool changed = false;
                foreach (var ch in targetList)
                {
                    if (ch.Type != "Dizi" || string.IsNullOrEmpty(ch.ShowName)) continue;

                    if (dates.TryGetValue(ch.ShowName, out var added) &&
                        added != DateTime.MinValue && ch.AddedDate != added)
                    {
                        ch.AddedDate = added;
                        changed = true;
                    }
                }

                if (!changed) return;

                _seriesCardCache.Clear();
                _contentCache.Clear();
                SaveChannelsForSource(source.Id);

                if (stillExists.IsActive)
                    Dispatcher.UIThread.Post(UpdateView);

                Dispatcher.UIThread.Post(() => TrimProcessMemory(), DispatcherPriority.Background);
            }
            finally
            {
                lock (_loadingXtreamSeriesDatesIds) { _loadingXtreamSeriesDatesIds.Remove(source.Id); }
            }
        }

        private static string? ExtractXtreamStreamId(Channel channel)
        {
            if (!string.IsNullOrEmpty(channel.XuiId) && long.TryParse(channel.XuiId, out _))
                return channel.XuiId;

            if (string.IsNullOrEmpty(channel.Url)) return null;

            var m = Regex.Match(channel.Url, @"/(\d+)\.[A-Za-z0-9]+(?:\?.*)?$");
            return m.Success ? m.Groups[1].Value : null;
        }

        private async Task<XtreamProviderInfo> GetXtreamVodInfoAsync(TvSource source, Channel channel)
        {
            if (source.Type != "Xtream") return new XtreamProviderInfo();

            string? streamId = ExtractXtreamStreamId(channel);
            if (string.IsNullOrEmpty(streamId)) return new XtreamProviderInfo();

            string cacheKey = $"{source.Id}:{streamId}";
            lock (_xtreamVodInfoCacheLock)
            {
                if (_xtreamVodInfoCache.TryGetValue(cacheKey, out var cachedVal))
                    return cachedVal;
            }

            var result = new XtreamProviderInfo();
            try
            {
                string server = source.PathOrUrl.TrimEnd('/');
                string userEnc = Uri.EscapeDataString(source.Username);
                string passEnc = Uri.EscapeDataString(source.Password);
                string url = $"{server}/player_api.php?username={userEnc}&password={passEnc}&action=get_vod_info&vod_id={streamId}";

                EnsureDownloadHttpClient();
                string json = await _downloadHttpClient!.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
                {
                    string tmdbRaw = XtreamStr(info, "tmdb_id");
                    if (string.IsNullOrEmpty(tmdbRaw)) tmdbRaw = XtreamStr(info, "tmdb");
                    int.TryParse(tmdbRaw, out int tmdbId);
                    result.TmdbId = tmdbId > 0 ? tmdbId : 0;

                    result.OriginalName = XtreamStr(info, "o_name");
                    result.Plot = XtreamStr(info, "plot");
                    if (string.IsNullOrEmpty(result.Plot)) result.Plot = XtreamStr(info, "description");
                    result.Cast = XtreamStr(info, "cast");
                    if (string.IsNullOrEmpty(result.Cast)) result.Cast = XtreamStr(info, "actors");
                    result.Director = XtreamStr(info, "director");
                    result.Genre = XtreamStr(info, "genre");
                    result.ReleaseDate = XtreamStr(info, "releasedate");
                    if (string.IsNullOrEmpty(result.ReleaseDate)) result.ReleaseDate = XtreamStr(info, "release_date");
                    result.Rating = XtreamStr(info, "rating");
                    if (string.IsNullOrEmpty(result.Rating) || result.Rating == "0")
                        result.Rating = XtreamStr(info, "rating_imdb");

                    string durationRaw = XtreamStr(info, "duration");
                    if (!string.IsNullOrEmpty(durationRaw))
                    {
                        if (durationRaw.Contains(':'))
                        {
                            var parts = durationRaw.Split(':');
                            if (parts.Length == 3 &&
                                int.TryParse(parts[0], out int hh) &&
                                int.TryParse(parts[1], out int mm))
                                result.Duration = (hh * 60 + mm).ToString();
                        }
                        else if (int.TryParse(durationRaw, out int durMin))
                        {
                            result.Duration = durMin.ToString();
                        }
                    }
                    if (string.IsNullOrEmpty(result.Duration))
                    {
                        string durSecs = XtreamStr(info, "duration_secs");
                        if (int.TryParse(durSecs, out int secs) && secs > 0)
                            result.Duration = (secs / 60).ToString();
                    }

                    string poster = XtreamStr(info, "movie_image");
                    if (string.IsNullOrEmpty(poster)) poster = XtreamStr(info, "cover_big");
                    result.PosterUrl = poster;
                }
            }
            catch { }

            lock (_xtreamVodInfoCacheLock) { _xtreamVodInfoCache[cacheKey] = result; }
            return result;
        }

        private bool _isXtreamPasswordRevealed = false;

        private void ShowChangeSource_Click(object? sender, RoutedEventArgs e)
        {
            SourceNameInput.Text = "";
            M3uUrlInput.Text = "";
            M3uEpgUrlInput.Text = "";
            XtreamUrlInput.Text = "";
            XtreamUserInput.Text = "";
            XtreamPassInput.Text = "";
            _isXtreamPasswordRevealed = false;
            XtreamPassInput.PasswordChar = '*';
            XtreamPassToggleIcon.Text = "👁️";
            ToolTip.SetTip(XtreamTogglePassBtn, "Şifreyi Göster");
            SelectedFilePath.Text = "";
            SelectedFileName.IsVisible = false;

            SetSourceType("M3U");
            ChangeSourceOverlay.IsVisible = true;
        }

        private void XtreamTogglePassword_Click(object? sender, RoutedEventArgs e)
        {
            _isXtreamPasswordRevealed = !_isXtreamPasswordRevealed;
            XtreamPassInput.PasswordChar = _isXtreamPasswordRevealed ? '\0' : '*';
            XtreamPassToggleIcon.Text = _isXtreamPasswordRevealed ? "🙈" : "👁️";
            ToolTip.SetTip(XtreamTogglePassBtn, _isXtreamPasswordRevealed ? "Şifreyi Gizle" : "Şifreyi Göster");
        }

        private void CancelChangeSource_Click(object? sender, RoutedEventArgs e) =>
            ChangeSourceOverlay.IsVisible = false;

        private void SourceType_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null)
                SetSourceType(btn.Tag.ToString()!);
        }

        private void SetSourceType(string type)
        {
            _selectedSourceType = type;

            BtnTypeM3U.Foreground = (Avalonia.Media.IBrush)this.Resources["TextSec"]!;
            BtnTypeM3U.BorderBrush = (Avalonia.Media.IBrush)this.Resources["Border"]!;
            BtnTypeLink.Foreground = (Avalonia.Media.IBrush)this.Resources["TextSec"]!;
            BtnTypeLink.BorderBrush = (Avalonia.Media.IBrush)this.Resources["Border"]!;
            BtnTypeXtream.Foreground = (Avalonia.Media.IBrush)this.Resources["TextSec"]!;
            BtnTypeXtream.BorderBrush = (Avalonia.Media.IBrush)this.Resources["Border"]!;

            InputAreaM3U.IsVisible = false;
            InputAreaXtream.IsVisible = false;

            switch (type)
            {
                case "M3U":
                    BtnTypeM3U.Foreground = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    BtnTypeM3U.BorderBrush = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    InputAreaM3U.IsVisible = true;
                    M3uFilePickerPanel.IsVisible = true;
                    M3uUrlInput.IsVisible = false;
                    break;
                case "Link":
                    BtnTypeLink.Foreground = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    BtnTypeLink.BorderBrush = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    InputAreaM3U.IsVisible = true;
                    M3uFilePickerPanel.IsVisible = false;
                    M3uUrlInput.IsVisible = true;
                    break;
                case "Xtream":
                    BtnTypeXtream.Foreground = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    BtnTypeXtream.BorderBrush = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    InputAreaXtream.IsVisible = true;
                    break;
            }
        }

        private async void SelectM3uFile_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
            if (files.Count >= 1)
            {
                SelectedFilePath.Text = files[0].Path.LocalPath;
                SelectedFileName.Text = "Seçilen Dosya: " + files[0].Name;
                SelectedFileName.IsVisible = true;
            }
        }

        private async void ConfirmAddSource_Click(object? sender, RoutedEventArgs e)
        {
            string sourceName = SourceNameInput.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(sourceName)) sourceName = "Yeni Kaynak";

            var newSource = new TvSource { Name = sourceName, Type = _selectedSourceType, IsActive = true };
            ChangeSourceOverlay.IsVisible = false;
            ShowToast("Kaynak ekleniyor, lütfen bekleyin...");

            try
            {
                string epgUrlInput = M3uEpgUrlInput.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(epgUrlInput) &&
                    !epgUrlInput.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !epgUrlInput.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    epgUrlInput = "http://" + epgUrlInput;
                newSource.EpgUrl = epgUrlInput;

                if (_selectedSourceType == "M3U")
                {
                    string path = SelectedFilePath.Text ?? "";
                    if (string.IsNullOrEmpty(path)) { ShowToast("Lütfen bir M3U dosyası seçin."); return; }
                    if (!File.Exists(path)) { ShowToast("Seçilen dosya bulunamadı."); return; }
                    newSource.PathOrUrl = path;
                    var channels = await FetchChannelsForSource(newSource);
                    _allChannels = channels;
                    FinishAddingSource(newSource);
                }
                else if (_selectedSourceType == "Link")
                {
                    string url = M3uUrlInput.Text?.Trim() ?? "";
                    if (string.IsNullOrEmpty(url)) { ShowToast("Lütfen bir link girin."); return; }
                    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        url = "http://" + url;
                    newSource.PathOrUrl = url;
                    var channels = await FetchChannelsForSource(newSource);
                    _allChannels = channels;
                    FinishAddingSource(newSource);
                }
                else if (_selectedSourceType == "Xtream")
                {
                    string server = XtreamUrlInput.Text?.Trim().TrimEnd('/') ?? "";
                    string user = XtreamUserInput.Text?.Trim() ?? "";
                    string pass = XtreamPassInput.Text?.Trim() ?? "";

                    if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(user))
                    { ShowToast("Sunucu URL ve kullanıcı adı zorunludur."); return; }

                    if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        server = "http://" + server;

                    newSource.PathOrUrl = server;
                    newSource.Username = user;
                    newSource.Password = pass;

                    var channels = await FetchChannelsForSource(newSource);
                    _allChannels = channels;
                    FinishAddingSource(newSource);

                    bool m3uAlreadyHasSeries = channels.Any(c => c.Type == "Dizi" || c.Url.Contains("/series/"));
                    if (!m3uAlreadyHasSeries)
                        _ = LoadXtreamSeriesInBackground(newSource);

                    _ = FetchXtreamAccountExpiryDateAsync(newSource);
                    _ = LoadXtreamVodAddedDatesInBackground(newSource);
                    _ = LoadXtreamSeriesAddedDatesInBackground(newSource);
                }
            }
            catch (HttpRequestException hre) { ShowToast($"Bağlantı hatası: {hre.Message}"); }
            catch (TaskCanceledException) { ShowToast("İstek zaman aşımına uğradı."); }
            catch (UriFormatException) { ShowToast("Geçersiz URL formatı."); }
            catch (Exception ex) { ShowToast($"Hata: {ex.Message}"); }
        }

        private List<Channel> ParseM3u(string content)
        {
            using var reader = new StringReader(content);
            return ParseM3uFromReader(reader);
        }

        private static string ExtractAttributeValue(string line, string attrKey)
        {
            int keyIdx = line.IndexOf(attrKey, StringComparison.OrdinalIgnoreCase);
            if (keyIdx == -1) return "";
            int valStart = keyIdx + attrKey.Length;
            int valEnd = line.IndexOf('"', valStart);
            if (valEnd == -1) return "";
            return line[valStart..valEnd];
        }

        private List<Channel> ParseM3uFromReader(TextReader reader)
        {
            var result = new List<Channel>();
            string? line;
            string currentName = "Bilinmeyen Kanal", currentGroup = "Diğer",
                   currentLogo = "", currentXuiId = "", currentTvgId = "";

            var groupPool = new Dictionary<string, string>(StringComparer.Ordinal);
            groupPool["Diğer"] = "Diğer";

            string InternGroup(string g)
            {
                if (string.IsNullOrEmpty(g)) return "Diğer";
                if (groupPool.TryGetValue(g, out var cached)) return cached;
                groupPool[g] = g;
                return g;
            }

            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    string grp = ExtractAttributeValue(line, "group-title=\"");
                    currentGroup = !string.IsNullOrEmpty(grp) ? InternGroup(grp) : "Diğer";

                    currentLogo = ExtractAttributeValue(line, "tvg-logo=\"");
                    currentXuiId = ExtractAttributeValue(line, "xui-id=\"");
                    currentTvgId = ExtractAttributeValue(line, "tvg-id=\"");

                    int ci = line.LastIndexOf(',');
                    if (ci != -1 && ci < line.Length - 1)
                        currentName = line[(ci + 1)..].Trim();
                }
                else if (!line.StartsWith("#"))
                {
                    string type = DetermineContentType(currentName, currentGroup, line);
                    type = string.Intern(type);

                    var channel = new Channel
                    {
                        Name = currentName,
                        Url = line,
                        Group = currentGroup,
                        Type = type,
                        LogoUrl = currentLogo,
                        XuiId = currentXuiId,
                        TvgId = currentTvgId
                    };

                    if (type == "Dizi")
                    {
                        var (showName, season, episode) = ParseShowInfo(currentName);
                        channel.ShowName = showName;
                        channel.Season = !string.IsNullOrEmpty(season) ? string.Intern(season) : season;
                        channel.EpisodeNumber = episode;
                    }

                    result.Add(channel);
                    currentName = "Bilinmeyen Kanal";
                    currentGroup = "Diğer";
                    currentLogo = "";
                    currentXuiId = "";
                    currentTvgId = "";
                }
            }

            for (int i = 0; i < result.Count; i++)
                result[i].AddedDate = DateTime.Now.AddSeconds(-(result.Count - i));

            return result;
        }

        private string DetermineContentType(string channelName, string groupTitle, string url)
        {
            string lowerUrl = url.ToLower();
            string lowerGroup = groupTitle.ToLower();

            if (lowerUrl.Contains("/series/")) return "Dizi";
            if (lowerUrl.Contains("/movie/") || lowerUrl.Contains("/movies/")) return "VOD";
            if (lowerUrl.Contains("/live/")) return "Canlı";

            if (lowerGroup.Contains("series") || lowerGroup.Contains("dizi") ||
                lowerGroup.Contains("sezon") || lowerGroup.Contains("season"))
                return "Dizi";

            if (lowerGroup.Contains("live") || lowerGroup.Contains("canlı") ||
                lowerGroup.Contains("news") || lowerGroup.Contains("haber") ||
                lowerGroup.Contains("spor") || lowerGroup.Contains("sport") ||
                lowerGroup.Contains("kids") || lowerGroup.Contains("çocuk") ||
                lowerGroup.Contains("music") || lowerGroup.Contains("müzik") ||
                lowerGroup.Contains("belgesel") || lowerGroup.Contains("documentary"))
                return "Canlı";

            if (lowerGroup.Contains("movie") || lowerGroup.Contains("film") ||
                lowerGroup.Contains("cinema") || lowerGroup.Contains("sinema") ||
                _rxVodInGroup.IsMatch(lowerGroup) ||
                _rx4kInGroup.IsMatch(lowerGroup))
                return "VOD";

            if (_rxSeasonEp.IsMatch(channelName))
                return "Dizi";

            if (lowerUrl.EndsWith(".mp4") || lowerUrl.EndsWith(".mkv") ||
                lowerUrl.EndsWith(".avi") || lowerUrl.EndsWith(".mov"))
                return "VOD";

            return "Canlı";
        }

        private (string showName, string season, int episode) ParseShowInfo(string channelName)
        {
            var match = _rxShowInfo.Match(channelName);
            if (match.Success)
            {
                string showName = match.Groups[1].Value.Trim();
                showName = _rxShowNameEnd.Replace(showName, "").Trim();
                if (string.IsNullOrEmpty(showName)) showName = channelName;

                string season = "S" + match.Groups[2].Value.PadLeft(2, '0');
                int episode = int.TryParse(match.Groups[3].Value, out var ep) ? ep : 0;
                return (showName, season, episode);
            }
            return (channelName, "Bilinmeyen Sezon", 0);
        }

        private void FinishAddingSource(TvSource newSource)
        {
            newSource.LastRefreshedDate = DateTime.Now;

            foreach (var s in _sources) s.IsActive = false;
            _sources.Add(newSource);

            if (_allChannels.Count > 0) _decryptedChannelsCache[newSource.Id] = _allChannels;

            SaveChannelsForSource(newSource.Id);
            SaveSources();
            RebuildCategoriesCache(newSource.Id, saveDisk: true);
            UpdateView();
            ShowToast("Kaynak başarıyla eklendi.");
            _ = MatchPopularItemsWithChannelsAsync(_displayPopularItems.ToList());

            // Kaynak eklendikten sonra anlık bellek birikimini temizle
            Dispatcher.UIThread.Post(() => TrimProcessMemory(), DispatcherPriority.Background);
        }
    }
}