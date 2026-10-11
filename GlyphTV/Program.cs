using Avalonia;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibVLCSharp.Shared;

namespace GlyphTV
{
    internal class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // ── Global exception handler'lar ──────────────────────────
            // UI thread veya Task pool'dan kaçan yakalanmamış exception'lar
            // uygulamayı çökmeden loglayarak devam ettirir.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    string msg = e.ExceptionObject?.ToString() ?? "Bilinmeyen hata";
                    string logPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "GlyphTV", "crash.log");
                    File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n\n");
                }
                catch { }
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                e.SetObserved(); // Task exception'ı gözlemlendi, uygulama çökmez
            };

            try
            {
                // Eski güncelleyici tarafından %TEMP% dizininden başlatıldıysa asıl dosyanın üzerine taşı
                HandlePostUpdateMigration(args);

                int v = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                // Avalonia ana döngüsünden kaçan kritik hataları yakala
                try
                {
                    string logPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "GlyphTV", "crash.log");
                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FATAL: {ex}\n\n");
                }
                catch { }
            }
        }

        /// <summary>
        /// Eğer uygulama eski bir sürümün güncelleyicisi tarafından %TEMP% klasöründen çalıştırılmışsa,
        /// kullanıcının asıl GlyphTV konumunu (C:\GlyphTV veya kısayol hedefini) bulur, kendini oraya
        /// kopyalar (in-place güncelleme) ve uygulamayı orijinal konumundan yeniden başlatır.
        /// </summary>
        private static void HandlePostUpdateMigration(string[] args)
        {
            try
            {
                string currentExe = Environment.ProcessPath ?? "";
                if (string.IsNullOrWhiteSpace(currentExe) ||
                    Path.GetFileNameWithoutExtension(currentExe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "GlyphTV");

                if (!Directory.Exists(appDataDir))
                {
                    Directory.CreateDirectory(appDataDir);
                }

                string installPathFile = Path.Combine(appDataDir, "install_path.txt");

                bool isRunningFromTemp = currentExe.Contains("GlyphTV_Updates", StringComparison.OrdinalIgnoreCase) ||
                                         currentExe.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);

                if (isRunningFromTemp)
                {
                    var candidates = FindCandidateExecutables(installPathFile);

                    // En son erişilen / üzerinde işlem yapılan dosyayı birincil hedef olarak seçmek için sırala
                    var orderedCandidates = candidates
                        .Where(p => File.Exists(p) && !string.Equals(currentExe, p, StringComparison.OrdinalIgnoreCase))
                        .Select(p => new
                        {
                            Path = p,
                            LastTime = File.GetLastAccessTime(p) > File.GetLastWriteTime(p) ? File.GetLastAccessTime(p) : File.GetLastWriteTime(p)
                        })
                        .OrderByDescending(x => x.LastTime)
                        .ToList();

                    string? primaryTargetToLaunch = null;

                    foreach (var item in orderedCandidates)
                    {
                        string targetExe = item.Path;

                        // Hedef dosyayı güncelle (üzerine yaz)
                        bool copied = false;
                        for (int retry = 0; retry < 10; retry++)
                        {
                            try
                            {
                                string targetDir = Path.GetDirectoryName(targetExe) ?? "";
                                if (!Directory.Exists(targetDir))
                                {
                                    Directory.CreateDirectory(targetDir);
                                }

                                File.Copy(currentExe, targetExe, overwrite: true);
                                copied = true;
                                break;
                            }
                            catch
                            {
                                System.Threading.Thread.Sleep(500);
                            }
                        }

                        if (copied && primaryTargetToLaunch == null)
                        {
                            primaryTargetToLaunch = targetExe;
                        }
                    }

                    if (!string.IsNullOrEmpty(primaryTargetToLaunch))
                    {
                        try { File.WriteAllText(installPathFile, primaryTargetToLaunch); } catch { }

                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = primaryTargetToLaunch,
                            WorkingDirectory = Path.GetDirectoryName(primaryTargetToLaunch) ?? "",
                            UseShellExecute = true
                        };
                        System.Diagnostics.Process.Start(psi);
                        Environment.Exit(0);
                        return;
                    }
                }
                else
                {
                    // Uygulama normal konumunda çalışıyor:
                    // 1. Gelecekteki güncellemeler için asıl konumu kaydet
                    try { File.WriteAllText(installPathFile, currentExe); } catch { }

                    // 2. Geçici indirme dosyalarını arka planda temizle
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            string tempUpdatesDir = Path.Combine(Path.GetTempPath(), "GlyphTV_Updates");
                            if (Directory.Exists(tempUpdatesDir))
                            {
                                Directory.Delete(tempUpdatesDir, true);
                            }
                            string updaterScript = Path.Combine(Path.GetTempPath(), "glyphtv_updater.cmd");
                            if (File.Exists(updaterScript))
                            {
                                File.Delete(updaterScript);
                            }
                        }
                        catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                try
                {
                    string logPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "GlyphTV", "crash.log");
                    File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Migration error: {ex}\n");
                }
                catch { }
            }
        }

        private static List<string> FindCandidateExecutables(string installPathFile)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. install_path.txt
            try
            {
                if (File.Exists(installPathFile))
                {
                    string saved = File.ReadAllText(installPathFile).Trim();
                    if (File.Exists(saved) && !saved.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(saved);
                    }
                }
            }
            catch { }

            // 2. Downloads (İndirilenler) klasörü
            try
            {
                string downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GlyphTV.exe");
                if (File.Exists(downloadsPath))
                {
                    candidates.Add(downloadsPath);
                }
            }
            catch { }

            // 3. Desktop (Masaüstü) klasörü
            try
            {
                string desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "GlyphTV.exe");
                if (File.Exists(desktopPath))
                {
                    candidates.Add(desktopPath);
                }
            }
            catch { }

            // 4. Standart C:\GlyphTV\GlyphTV.exe konumu
            try
            {
                string defaultPath = @"C:\GlyphTV\GlyphTV.exe";
                if (File.Exists(defaultPath))
                {
                    candidates.Add(defaultPath);
                }
            }
            catch { }

            // 5. Görev Çubuğu, Masaüstü ve Başlat Menüsü Kısayolları (.lnk)
            string[] shortcutDirs = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            };

            foreach (var dir in shortcutDirs)
            {
                if (Directory.Exists(dir))
                {
                    try
                    {
                        var lnkFiles = Directory.GetFiles(dir, "*GlyphTV*.lnk", SearchOption.TopDirectoryOnly);
                        foreach (var lnk in lnkFiles)
                        {
                            string? target = ResolveShortcutTarget(lnk);
                            if (!string.IsNullOrEmpty(target) && File.Exists(target) &&
                                !target.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                            {
                                candidates.Add(target);
                            }
                        }
                    }
                    catch { }
                }
            }

            return candidates.ToList();
        }

        private static string? ResolveShortcutTarget(string lnkPath)
        {
            try
            {
                if (!File.Exists(lnkPath)) return null;
                byte[] bytes = File.ReadAllBytes(lnkPath);

                string ascii = System.Text.Encoding.ASCII.GetString(bytes);
                var match = System.Text.RegularExpressions.Regex.Match(
                    ascii,
                    @"[A-Za-z]:\\[^""<>|?*\x00-\x1F]+\.exe",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (match.Success && File.Exists(match.Value))
                {
                    return match.Value;
                }

                string unicode = System.Text.Encoding.Unicode.GetString(bytes);
                match = System.Text.RegularExpressions.Regex.Match(
                    unicode,
                    @"[A-Za-z]:\\[^""<>|?*\x00-\x1F]+\.exe",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (match.Success && File.Exists(match.Value))
                {
                    return match.Value;
                }
            }
            catch { }
            return null;
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
