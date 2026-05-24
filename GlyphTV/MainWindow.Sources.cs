// ============================================================
// MainWindow.Sources.cs
// Kaynak yönetimi: ekleme, silme, seçme, yenileme, dışa aktarma
// M3U parse, Xtream Code, URL indirme, içerik türü tespiti
// ============================================================

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow
    {
        // ─────────────────────────────────────────────────────────────
        // Kaynaklar – kaydet / yükle
        // ─────────────────────────────────────────────────────────────
        private void SaveSources()
        {
            try { File.WriteAllText(GetSourcesPath(), JsonSerializer.Serialize(_sources)); } catch { }

            var temp = _sources.ToList();
            _sources.Clear();
            foreach (var s in temp) _sources.Add(s);
        }

        private void LoadSources()
        {
            try
            {
                string path = GetSourcesPath();
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<List<TvSource>>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        _sources.Clear();
                        foreach (var s in loaded) _sources.Add(s);

                        var active = _sources.FirstOrDefault(s => s.IsActive);
                        if (active != null) { LoadChannelsForSource(active.Id); return; }
                    }
                }
            }
            catch { }

            if (_sources.Count == 0) UpdateView();
        }

        // ─────────────────────────────────────────────────────────────
        // Kanallar – kaydet / yükle
        // ─────────────────────────────────────────────────────────────
        private void SaveChannelsForSource(string sourceId)
        {
            var snapshot = _allChannels.ToList();
            var path     = GetChannelsPath(sourceId);
            Task.Run(() =>
            {
                try { File.WriteAllText(path, JsonSerializer.Serialize(snapshot)); } catch { }
            });
        }

        private void LoadChannelsForSource(string sourceId)
        {
            _allChannels.Clear();
            try
            {
                string path = GetChannelsPath(sourceId);
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<List<Channel>>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        _allChannels = loaded;

                        // Eski JSON'larda ShowName boş olabilir – parse et
                        foreach (var ch in _allChannels.Where(c => c.Type == "Dizi" && string.IsNullOrEmpty(c.ShowName)))
                        {
                            var (showName, season, episode) = ParseShowInfo(ch.Name);
                            ch.ShowName      = showName;
                            ch.Season        = season;
                            ch.EpisodeNumber = episode;
                        }
                    }
                }
            }
            catch { }

            // Cache'leri sıfırla (yeni kanal listesine göre yeniden oluşacak)
            _contentCache.Clear();
            _seriesCardCache.Clear();

            UpdateView();
        }

        // ─────────────────────────────────────────────────────────────
        // Kaynak işlemleri (seç / sil / yenile)
        // ─────────────────────────────────────────────────────────────
        private void SelectSource_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not TvSource source) return;
            foreach (var s in _sources) s.IsActive = false;
            source.IsActive = true;
            SaveSources();
            LoadChannelsForSource(source.Id);
            ShowToast($"'{source.Name}' kaynağı aktifleştirildi.");
        }

        private void DeleteSource_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not TvSource source) return;
            _sources.Remove(source);
            try { File.Delete(GetChannelsPath(source.Id)); } catch { }

            if (source.IsActive && _sources.Count > 0)
            {
                _sources[0].IsActive = true;
                LoadChannelsForSource(_sources[0].Id);
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

        /// <summary>
        /// Kaynağı yeniden indir ve parse et. Favori/gizli durumlarını URL bazlı korur.
        /// </summary>
        private async Task RefreshSourceInternal(TvSource source)
        {
            ShowToast($"'{source.Name}' yenileniyor, lütfen bekleyin...");

            var oldStates = new Dictionary<string, (bool isFavorite, bool isHidden)>();

            if (source.IsActive)
            {
                foreach (var ch in _allChannels.Where(c => c.IsFavorite || c.IsHidden))
                    oldStates[ch.Url] = (ch.IsFavorite, ch.IsHidden);
            }
            else
            {
                try
                {
                    string path = GetChannelsPath(source.Id);
                    if (File.Exists(path))
                    {
                        var loaded = JsonSerializer.Deserialize<List<Channel>>(File.ReadAllText(path));
                        if (loaded != null)
                            foreach (var ch in loaded.Where(c => c.IsFavorite || c.IsHidden))
                                oldStates[ch.Url] = (ch.IsFavorite, ch.IsHidden);
                    }
                }
                catch { }
            }

            try
            {
                string content = source.Type switch
                {
                    "M3U" => File.Exists(source.PathOrUrl)
                                ? await File.ReadAllTextAsync(source.PathOrUrl)
                                : throw new FileNotFoundException("Kaynak dosyası mevcut değil."),
                    "Link"   => await DownloadM3uContent(source.PathOrUrl),
                    "Xtream" => await DownloadM3uContent(
                                    $"{source.PathOrUrl}/get.php?username={Uri.EscapeDataString(source.Username)}&password={Uri.EscapeDataString(source.Password)}&type=m3u_plus&output=ts"),
                    _ => throw new InvalidOperationException("Bilinmeyen kaynak türü.")
                };

                var newChannels = ParseM3u(content);

                int restoredFav = 0, restoredHidden = 0;
                foreach (var ch in newChannels)
                {
                    if (!oldStates.TryGetValue(ch.Url, out var state)) continue;
                    ch.IsFavorite = state.isFavorite;
                    ch.IsHidden   = state.isHidden;
                    if (state.isFavorite) restoredFav++;
                    if (state.isHidden)   restoredHidden++;
                }

                if (source.IsActive)
                {
                    _allChannels = newChannels;
                    SaveChannelsForSource(source.Id);
                    UpdateView();
                }
                else
                {
                    try { File.WriteAllText(GetChannelsPath(source.Id), JsonSerializer.Serialize(newChannels)); } catch { }
                }

                ShowToast($"'{source.Name}' yenilendi: {newChannels.Count} içerik ({restoredFav} favori, {restoredHidden} gizli korundu).");
            }
            catch (HttpRequestException hre) { ShowToast($"Yenileme hatası: {hre.Message}"); }
            catch (TaskCanceledException)    { ShowToast("Yenileme zaman aşımına uğradı."); }
            catch (Exception ex)             { ShowToast($"Yenileme hatası: {ex.Message}"); }
        }

        // ─────────────────────────────────────────────────────────────
        // Uygulama sıfırlama
        // ─────────────────────────────────────────────────────────────
        private void ResetApp_Click(object? sender, RoutedEventArgs e)
        {
            _sources.Clear();
            _allChannels.Clear();
            _watchHistory.Clear();
            _contentCache.Clear();
            _seriesCardCache.Clear();

            string appData = AppDataDir();
            try
            {
                foreach (var f in new[] { "sources.json", "history.json" })
                {
                    var p = Path.Combine(appData, f);
                    if (File.Exists(p)) File.Delete(p);
                }
                foreach (var f in Directory.GetFiles(appData, "channels_*.json"))
                    File.Delete(f);
            }
            catch { }

            LoadSources();
            ShowToast("Uygulama sıfırlandı.");
        }

        // ─────────────────────────────────────────────────────────────
        // Favorileri M3U olarak dışa aktar
        // ─────────────────────────────────────────────────────────────
        private async void ExportJson_Click(object? sender, RoutedEventArgs e)
        {
            var favorites = _allChannels.Where(c => c.IsFavorite).ToList();
            if (favorites.Count == 0) { ShowToast("Dışa aktarılacak favori içerik yok."); return; }

            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title             = "Favorileri Kaydet",
                    SuggestedFileName = $"GlyphTV_Favoriler_{DateTime.Now:yyyyMMdd}.m3u",
                    DefaultExtension  = "m3u",
                    FileTypeChoices   = new[]
                    {
                        new FilePickerFileType("M3U Playlist") { Patterns = new[] { "*.m3u", "*.m3u8" } }
                    }
                });

                if (file == null) return;

                var sb = new StringBuilder();
                sb.AppendLine("#EXTM3U");
                foreach (var ch in favorites)
                {
                    sb.AppendLine($"#EXTINF:-1 group-title=\"{ch.Group}\",{ch.Name}");
                    sb.AppendLine(ch.Url);
                }

                await using var stream = await file.OpenWriteAsync();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(sb.ToString());

                ShowToast($"{favorites.Count} favori içerik dışa aktarıldı.");
            }
            catch (Exception ex) { ShowToast($"Dışa aktarma hatası: {ex.Message}"); }
        }

        // ─────────────────────────────────────────────────────────────
        // Kaynak ekleme modalı
        // ─────────────────────────────────────────────────────────────
        private void ShowChangeSource_Click(object? sender, RoutedEventArgs e)
        {
            SourceNameInput.Text = "";
            M3uUrlInput.Text     = "";
            XtreamUrlInput.Text  = "";
            XtreamUserInput.Text = "";
            XtreamPassInput.Text = "";
            SelectedFilePath.Text = "";
            SelectedFileName.IsVisible = false;

            SetSourceType("M3U");
            ChangeSourceOverlay.IsVisible = true;
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

            BtnTypeM3U.Foreground    = (Avalonia.Media.IBrush)this.Resources["TextSec"]!;
            BtnTypeM3U.BorderBrush   = (Avalonia.Media.IBrush)this.Resources["Border"]!;
            BtnTypeLink.Foreground   = (Avalonia.Media.IBrush)this.Resources["TextSec"]!;
            BtnTypeLink.BorderBrush  = (Avalonia.Media.IBrush)this.Resources["Border"]!;
            BtnTypeXtream.Foreground = (Avalonia.Media.IBrush)this.Resources["TextSec"]!;
            BtnTypeXtream.BorderBrush= (Avalonia.Media.IBrush)this.Resources["Border"]!;

            InputAreaM3U.IsVisible    = false;
            InputAreaXtream.IsVisible = false;

            switch (type)
            {
                case "M3U":
                    BtnTypeM3U.Foreground    = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    BtnTypeM3U.BorderBrush   = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    InputAreaM3U.IsVisible   = true;
                    M3uFilePickerPanel.IsVisible = true;
                    M3uUrlInput.IsVisible        = false;
                    break;
                case "Link":
                    BtnTypeLink.Foreground   = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    BtnTypeLink.BorderBrush  = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
                    InputAreaM3U.IsVisible   = true;
                    M3uFilePickerPanel.IsVisible = false;
                    M3uUrlInput.IsVisible        = true;
                    break;
                case "Xtream":
                    BtnTypeXtream.Foreground  = (Avalonia.Media.IBrush)this.Resources["Accent"]!;
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
                SelectedFilePath.Text     = files[0].Path.LocalPath;
                SelectedFileName.Text     = "Seçilen Dosya: " + files[0].Name;
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
                if (_selectedSourceType == "M3U")
                {
                    string path = SelectedFilePath.Text ?? "";
                    if (string.IsNullOrEmpty(path))     { ShowToast("Lütfen bir M3U dosyası seçin."); return; }
                    if (!File.Exists(path))              { ShowToast("Seçilen dosya bulunamadı.");     return; }
                    newSource.PathOrUrl = path;
                    ParseAndLoadM3u(await File.ReadAllTextAsync(path), newSource);
                }
                else if (_selectedSourceType == "Link")
                {
                    string url = M3uUrlInput.Text?.Trim() ?? "";
                    if (string.IsNullOrEmpty(url))       { ShowToast("Lütfen bir link girin."); return; }
                    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        url = "http://" + url;
                    newSource.PathOrUrl = url;
                    ParseAndLoadM3u(await DownloadM3uContent(url), newSource);
                }
                else if (_selectedSourceType == "Xtream")
                {
                    string server = XtreamUrlInput.Text?.Trim().TrimEnd('/') ?? "";
                    string user   = XtreamUserInput.Text?.Trim() ?? "";
                    string pass   = XtreamPassInput.Text?.Trim() ?? "";

                    if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(user))
                    { ShowToast("Sunucu URL ve kullanıcı adı zorunludur."); return; }

                    if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        server = "http://" + server;

                    newSource.PathOrUrl = server;
                    newSource.Username  = user;
                    newSource.Password  = pass;

                    string apiUrl = $"{server}/get.php?username={Uri.EscapeDataString(user)}&password={Uri.EscapeDataString(pass)}&type=m3u_plus&output=ts";
                    ParseAndLoadM3u(await DownloadM3uContent(apiUrl), newSource);
                }
            }
            catch (HttpRequestException hre) { ShowToast($"Bağlantı hatası: {hre.Message}"); }
            catch (TaskCanceledException)    { ShowToast("İstek zaman aşımına uğradı."); }
            catch (UriFormatException)       { ShowToast("Geçersiz URL formatı."); }
            catch (Exception ex)             { ShowToast($"Hata: {ex.Message}"); }
        }

        // ─────────────────────────────────────────────────────────────
        // HTTP indirici (M3U playlist için)
        // ─────────────────────────────────────────────────────────────
        private async Task<string> DownloadM3uContent(string url)
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
            client.DefaultRequestHeaders.Add("User-Agent", "VLC/3.0.20 LibVLC/3.0.20");
            return await client.GetStringAsync(url);
        }

        // ─────────────────────────────────────────────────────────────
        // M3U parse → Channel listesi
        // ─────────────────────────────────────────────────────────────
        private void ParseAndLoadM3u(string content, TvSource newSource)
        {
            _allChannels = ParseM3u(content);
            FinishAddingSource(newSource);
        }

        private List<Channel> ParseM3u(string content)
        {
            var lines  = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<Channel>();
            string currentName = "Bilinmeyen Kanal", currentGroup = "Diğer",
                   currentLogo = "", currentXuiId = "";

            foreach (var line in lines)
            {
                if (line.StartsWith("#EXTINF:"))
                {
                    var groupMatch = Regex.Match(line, @"group-title=""([^""]+)""");
                    currentGroup = groupMatch.Success ? groupMatch.Groups[1].Value : "Diğer";

                    var logoMatch = Regex.Match(line, @"tvg-logo=""([^""]*)""");
                    currentLogo  = logoMatch.Success ? logoMatch.Groups[1].Value : "";

                    var xuiMatch  = Regex.Match(line, @"xui-id=""([^""]+)""");
                    currentXuiId = xuiMatch.Success ? xuiMatch.Groups[1].Value : "";

                    int ci = line.LastIndexOf(',');
                    if (ci != -1 && ci < line.Length - 1)
                        currentName = line[(ci + 1)..].Trim();
                }
                else if (!line.StartsWith("#"))
                {
                    string type = DetermineContentType(currentName, currentGroup, line);
                    var channel = new Channel
                    {
                        Name     = currentName,
                        Url      = line.Trim(),
                        Group    = currentGroup,
                        Type     = type,
                        LogoUrl  = currentLogo,
                        XuiId    = currentXuiId
                    };

                    if (type == "Dizi")
                    {
                        var (showName, season, episode) = ParseShowInfo(currentName);
                        channel.ShowName      = showName;
                        channel.Season        = season;
                        channel.EpisodeNumber = episode;
                    }

                    result.Add(channel);
                    currentName = "Bilinmeyen Kanal";
                    currentGroup = "Diğer";
                    currentLogo  = "";
                    currentXuiId = "";
                }
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────
        // İçerik türü tespiti (öncelik bazlı)
        // 1) Xtream URL path  2) S##E## regex  3) group-title  4) dosya uzantısı  5) default: Canlı
        // ─────────────────────────────────────────────────────────────
        private string DetermineContentType(string channelName, string groupTitle, string url)
        {
            string lowerUrl   = url.ToLower();
            string lowerGroup = groupTitle.ToLower();

            if (lowerUrl.Contains("/series/"))                                   return "Dizi";
            if (lowerUrl.Contains("/movie/") || lowerUrl.Contains("/movies/"))  return "VOD";
            if (lowerUrl.Contains("/live/"))                                     return "Canlı";

            if (Regex.IsMatch(channelName, @"\bS\d{1,2}\s*[.\-_]?\s*E\d{1,3}\b", RegexOptions.IgnoreCase))
                return "Dizi";

            if (lowerGroup.Contains("series") || lowerGroup.Contains("dizi")  ||
                lowerGroup.Contains("sezon")  || lowerGroup.Contains("season"))
                return "Dizi";

            if (lowerGroup.Contains("movie") || lowerGroup.Contains("film") ||
                lowerGroup.Contains("cinema") || lowerGroup.Contains("vod") ||
                lowerGroup.Contains("sinema") || lowerGroup.Contains("4k"))
                return "VOD";

            if (lowerGroup.Contains("live")   || lowerGroup.Contains("canlı") ||
                lowerGroup.Contains("news")   || lowerGroup.Contains("haber") ||
                lowerGroup.Contains("spor")   || lowerGroup.Contains("sport") ||
                lowerGroup.Contains("kids")   || lowerGroup.Contains("çocuk") ||
                lowerGroup.Contains("music")  || lowerGroup.Contains("müzik") ||
                lowerGroup.Contains("belgesel")|| lowerGroup.Contains("documentary"))
                return "Canlı";

            if (lowerUrl.EndsWith(".mp4") || lowerUrl.EndsWith(".mkv") ||
                lowerUrl.EndsWith(".avi") || lowerUrl.EndsWith(".mov"))
                return "VOD";

            return "Canlı";
        }

        // ─────────────────────────────────────────────────────────────
        // Dizi adından show / sezon / bölüm parse
        // ─────────────────────────────────────────────────────────────
        private (string showName, string season, int episode) ParseShowInfo(string channelName)
        {
            var match = Regex.Match(channelName, @"^(.+?)[\s\._\-]+S(\d{1,3})[\s\._\-]*E(\d{1,3})", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string showName = match.Groups[1].Value.Trim();
                showName = Regex.Replace(showName, @"[:\-_\.]+$", "").Trim();
                if (string.IsNullOrEmpty(showName)) showName = channelName;

                string season = "S" + match.Groups[2].Value.PadLeft(2, '0');
                int episode   = int.TryParse(match.Groups[3].Value, out var ep) ? ep : 0;
                return (showName, season, episode);
            }
            return (channelName, "Bilinmeyen Sezon", 0);
        }

        private void FinishAddingSource(TvSource newSource)
        {
            foreach (var s in _sources) s.IsActive = false;
            _sources.Add(newSource);
            SaveChannelsForSource(newSource.Id);
            SaveSources();
            UpdateView();
            ShowToast("Kaynak başarıyla eklendi.");
        }
    }
}