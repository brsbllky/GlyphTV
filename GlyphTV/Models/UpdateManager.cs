using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GlyphTV
{
    /// <summary>
    /// GitHub Release'ten çekilen sürüm detayları
    /// </summary>
    public class UpdateInfo
    {
        public string Version { get; set; } = "";
        public string TagName { get; set; } = "";
        public string Title { get; set; } = "";
        public string Changelog { get; set; } = "";
        public DateTime PublishedAt { get; set; }
        public string DownloadUrl { get; set; } = "";
        public string FileName { get; set; } = "";
        public long FileSize { get; set; } = 0;
        public string HtmlUrl { get; set; } = "";
        public bool IsPrerelease { get; set; } = false;
    }

    /// <summary>
    /// GitHub Releases API tabanlı çevrimiçi güncelleme yöneticisi
    /// </summary>
    public static class UpdateManager
    {
        public const string GITHUB_OWNER = "brsbllky";
        public const string GITHUB_REPO = "GlyphTV";
        public const string CURRENT_VERSION = "2.1.0";

        private static readonly HttpClient _httpClient = new HttpClient();

        static UpdateManager()
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("GlyphTV-Updater", CURRENT_VERSION));
            _httpClient.Timeout = TimeSpan.FromSeconds(20);
        }

        /// <summary>
        /// GitHub Releases API (/releases/latest) üzerinden en son sürümü sorgular
        /// </summary>
        public static async Task<(bool hasUpdate, UpdateInfo? info, string? errorMessage)> CheckForUpdatesAsync(
            string currentVersion = CURRENT_VERSION,
            CancellationToken ct = default)
        {
            try
            {
                string apiUrl = $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases/latest";
                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        return (false, null, "GitHub üzerinde henüz yayınlanmış bir sürüm (release) bulunamadı.");
                    }
                    return (false, null, $"GitHub API hatası: {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                string json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
                string releaseTitle = root.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? "" : "";
                string body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";
                string htmlUrl = root.TryGetProperty("html_url", out var htmlElem) ? htmlElem.GetString() ?? "" : "";
                bool isPrerelease = root.TryGetProperty("prerelease", out var preElem) && preElem.GetBoolean();

                DateTime publishedAt = DateTime.MinValue;
                if (root.TryGetProperty("published_at", out var pubElem) && pubElem.TryGetDateTime(out var dt))
                {
                    publishedAt = dt;
                }

                // Sürüm numarasını normalize et (v2.1.0 -> 2.1.0)
                string remoteVersionStr = tagName.TrimStart('v', 'V').Trim();
                if (string.IsNullOrEmpty(remoteVersionStr))
                {
                    remoteVersionStr = releaseTitle.TrimStart('v', 'V').Trim();
                }

                // İndirilebilir varlıkları (.exe veya .zip) tara
                string downloadUrl = "";
                string fileName = "";
                long fileSize = 0;

                if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsElem.EnumerateArray())
                    {
                        string aName = asset.TryGetProperty("name", out var aNameElem) ? aNameElem.GetString() ?? "" : "";
                        string aUrl = asset.TryGetProperty("browser_download_url", out var aUrlElem) ? aUrlElem.GetString() ?? "" : "";
                        long aSize = asset.TryGetProperty("size", out var aSizeElem) ? aSizeElem.GetInt64() : 0;

                        if (aName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            fileName = aName;
                            downloadUrl = aUrl;
                            fileSize = aSize;
                            break; // .exe her zaman önceliklidir
                        }
                        else if (aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(downloadUrl))
                        {
                            fileName = aName;
                            downloadUrl = aUrl;
                            fileSize = aSize;
                        }
                    }
                }

                // Varlık doğrudan bulunamadıysa html release URL'ini indirme linki olarak al
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    downloadUrl = htmlUrl;
                    fileName = $"GlyphTV-{remoteVersionStr}.exe";
                }

                var updateInfo = new UpdateInfo
                {
                    Version = remoteVersionStr,
                    TagName = tagName,
                    Title = string.IsNullOrWhiteSpace(releaseTitle) ? $"GlyphTV v{remoteVersionStr}" : releaseTitle,
                    Changelog = string.IsNullOrWhiteSpace(body) ? "Bu sürüm için detaylı sürüm notu girilmedi." : body,
                    PublishedAt = publishedAt,
                    DownloadUrl = downloadUrl,
                    FileName = fileName,
                    FileSize = fileSize,
                    HtmlUrl = htmlUrl,
                    IsPrerelease = isPrerelease
                };

                bool isNewer = IsNewerVersion(currentVersion, remoteVersionStr);
                return (isNewer, updateInfo, null);
            }
            catch (Exception ex)
            {
                MainWindow.LogError("CheckForUpdatesAsync", ex);
                return (false, null, $"Güncelleme denetimi başarısız oldu: {ex.Message}");
            }
        }

        /// <summary>
        /// Semantik sürüm karşılaştırması yapar (örn: 2.1.0 > 2.0.0)
        /// </summary>
        public static bool IsNewerVersion(string currentVersionStr, string remoteVersionStr)
        {
            if (string.IsNullOrWhiteSpace(remoteVersionStr)) return false;

            if (Version.TryParse(currentVersionStr, out var currVer) &&
                Version.TryParse(remoteVersionStr, out var remoteVer))
            {
                return remoteVer > currVer;
            }

            // Fallback semver karşılaştırması
            string[] currParts = currentVersionStr.Split('.', '-', '+');
            string[] remoteParts = remoteVersionStr.Split('.', '-', '+');

            int maxLen = Math.Max(currParts.Length, remoteParts.Length);
            for (int i = 0; i < maxLen; i++)
            {
                int currNum = i < currParts.Length && int.TryParse(currParts[i], out int c) ? c : 0;
                int remoteNum = i < remoteParts.Length && int.TryParse(remoteParts[i], out int r) ? r : 0;

                if (remoteNum > currNum) return true;
                if (remoteNum < currNum) return false;
            }

            return false;
        }

        /// <summary>
        /// Güncelleme paketini ilerleme raporu ile birlikte diske (%TEMP%) indirir
        /// </summary>
        public static async Task<string> DownloadUpdateAsync(
            string downloadUrl,
            string targetFileName,
            IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "GlyphTV_Updates");
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            string destinationPath = Path.Combine(tempDir, targetFileName);

            // Daha önceden indirilmiş yarım veya eski dosyayı temizle
            if (File.Exists(destinationPath))
            {
                try { File.Delete(destinationPath); } catch { }
            }

            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1L;

            using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read, ct);
                totalRead += read;

                if (totalBytes > 0)
                {
                    double percentage = (double)totalRead / totalBytes;
                    progress?.Report(percentage);
                }
            }

            progress?.Report(1.0);
            return destinationPath;
        }

        /// <summary>
        /// İndirilen kurulum paketini veya güncelleme betiğini çalıştırıp mevcut GlyphTV'yi kapatır
        /// </summary>
        public static void ApplyUpdateAndRestart(string downloadedFilePath)
        {
            try
            {
                string currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                string currentExeDir = Path.GetDirectoryName(currentExePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                int currentPid = Process.GetCurrentProcess().Id;

                // Eğer indirilen dosya doğrudan bir setup installer ise doğrudan çalıştır
                if (downloadedFilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Bağımsız güncelleme taşıyıcı betiği (PowerShell / CMD) oluştur:
                    // 1. Mevcut GlyphTV PID'sinin kapanmasını bekle
                    // 2. Yeni .exe'yi mevcut klasöre kopyala (veya installer ise sessizce/normal çalıştır)
                    // 3. Uygulamayı yeniden başlat
                    string updateScriptPath = Path.Combine(Path.GetTempPath(), "glyphtv_updater.cmd");
                    string scriptContent = $@"@echo off
timeout /t 1 /nobreak >nul
:waitloop
tasklist /fi ""pid eq {currentPid}"" | find ""{currentPid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto waitloop
)
start """" ""{downloadedFilePath}""
";
                    File.WriteAllText(updateScriptPath, scriptContent);

                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{updateScriptPath}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    Process.Start(psi);
                }
                else
                {
                    // Tarayıcı veya dosya açılışı
                    Process.Start(new ProcessStartInfo(downloadedFilePath) { UseShellExecute = true });
                }

                // Mevcut uygulamayı güvenle sonlandır
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                MainWindow.LogError("ApplyUpdateAndRestart", ex);
                // Fallback olarak doğrudan çalıştır
                try
                {
                    Process.Start(new ProcessStartInfo(downloadedFilePath) { UseShellExecute = true });
                    Environment.Exit(0);
                }
                catch { }
            }
        }
    }
}
