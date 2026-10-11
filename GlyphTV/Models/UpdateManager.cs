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
        public const string CURRENT_VERSION = "2.1.2";

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
        /// İndirilen güncelleme paketini veya .exe dosyasını mevcut çalışan GlyphTV'nin üzerine
        /// doğrudan yazıp (in-place replacement) güncellenen uygulamayı yeniden başlatır.
        /// </summary>
        public static void ApplyUpdateAndRestart(string downloadedFilePath)
        {
            try
            {
                string currentExePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

                // dotnet.exe ile çalıştırılıyorsa (geliştirme ortamı) varsayılan GlyphTV.exe yolunu al
                if (string.IsNullOrWhiteSpace(currentExePath) ||
                    Path.GetFileNameWithoutExtension(currentExePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    currentExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GlyphTV.exe");
                }

                // Eğer uygulama daha önceki bir yönlendirme hatası nedeniyle %TEMP% dizininden çalıştırılmışsa,
                // kalıcı bilinen konumu (örneğin C:\GlyphTV\GlyphTV.exe) güncelle
                if (currentExePath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                {
                    string standardInstalledExe = @"C:\GlyphTV\GlyphTV.exe";
                    if (File.Exists(standardInstalledExe))
                    {
                        currentExePath = standardInstalledExe;
                    }
                }

                string currentExeDir = Path.GetDirectoryName(currentExePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                int currentPid = Process.GetCurrentProcess().Id;

                bool isInstaller = downloadedFilePath.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   downloadedFilePath.IndexOf("installer", StringComparison.OrdinalIgnoreCase) >= 0;

                string updateScriptPath = Path.Combine(Path.GetTempPath(), "glyphtv_updater.cmd");

                string scriptContent;
                if (isInstaller)
                {
                    // Installer/Kurulum dosyası ise: Mevcut işlemin bitmesini bekle ve kurulum sihirbazını başlat
                    scriptContent = $@"@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

set ""PID={currentPid}""
set ""INSTALLER={downloadedFilePath}""

:wait_pid
tasklist /fi ""PID eq %PID%"" 2>nul | find ""%PID%"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait_pid
)

timeout /t 1 /nobreak >nul
start """" ""%INSTALLER%""
exit /b
";
                }
                else if (downloadedFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    // Zip arşivi ise: Klasöre çıkart ve ana uygulamayı başlat
                    scriptContent = $@"@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

set ""PID={currentPid}""
set ""TARGET_EXE={currentExePath}""
set ""TARGET_DIR={currentExeDir}""
set ""SOURCE_ZIP={downloadedFilePath}""

:wait_pid
tasklist /fi ""PID eq %PID%"" 2>nul | find ""%PID%"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait_pid
)

timeout /t 1 /nobreak >nul
powershell -NoProfile -Command ""Expand-Archive -LiteralPath '%SOURCE_ZIP%' -DestinationPath '%TARGET_DIR%' -Force"" >nul 2>&1
del /f /q ""%SOURCE_ZIP%"" >nul 2>&1

cd /d ""%TARGET_DIR%""
start """" ""%TARGET_EXE%""
exit /b
";
                }
                else
                {
                    // Taşınabilir/Doğrudan GlyphTV.exe binary dosyası:
                    // Mevcut exe'nin üzerine doğrudan yaz (in-place update)
                    scriptContent = $@"@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

set ""PID={currentPid}""
set ""TARGET_EXE={currentExePath}""
set ""TARGET_DIR={currentExeDir}""
set ""SOURCE_FILE={downloadedFilePath}""

:wait_pid
tasklist /fi ""PID eq %PID%"" 2>nul | find ""%PID%"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait_pid
)

timeout /t 1 /nobreak >nul

set RETRIES=0
:replace_loop
set /a RETRIES+=1

:: 1. Deneme: Doğrudan üzerine kopyalama
copy /y ""%SOURCE_FILE%"" ""%TARGET_EXE%"" >nul 2>&1
if not errorlevel 1 goto replace_success

:: 2. Deneme: Eski dosyayı .old yaparak kilidi kaldırma ve yeni dosyayı kopyalama
if exist ""%TARGET_EXE%.old"" del /f /q ""%TARGET_EXE%.old"" >nul 2>&1
move /y ""%TARGET_EXE%"" ""%TARGET_EXE%.old"" >nul 2>&1
copy /y ""%SOURCE_FILE%"" ""%TARGET_EXE%"" >nul 2>&1
if not errorlevel 1 (
    del /f /q ""%TARGET_EXE%.old"" >nul 2>&1
    goto replace_success
)

if !RETRIES! leq 15 (
    timeout /t 1 /nobreak >nul
    goto replace_loop
)

:: Eğer yetki vb. nedenlerle kopyalanamazsa kullanıcıyı mağdur etmemek için indirilen dosyayı çalıştır
cd /d ""%TARGET_DIR%""
start """" ""%SOURCE_FILE%""
exit /b

:replace_success
del /f /q ""%SOURCE_FILE%"" >nul 2>&1
if exist ""%TARGET_EXE%.old"" del /f /q ""%TARGET_EXE%.old"" >nul 2>&1

cd /d ""%TARGET_DIR%""
start """" ""%TARGET_EXE%""
exit /b
";
                }

                File.WriteAllText(updateScriptPath, scriptContent);

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{updateScriptPath}\"\"",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                };
                Process.Start(psi);

                // Mevcut uygulamayı sonlandır
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
