using Avalonia;
using System;
using System.IO;
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
                    string? targetExe = FindInstalledGlyphTvExe(installPathFile);

                    if (!string.IsNullOrEmpty(targetExe) &&
                        !string.Equals(currentExe, targetExe, StringComparison.OrdinalIgnoreCase))
                    {
                        // Hedef dosyanın üzerindeki kilitlerin kalkması için yeniden deneme döngüsü
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

                        if (copied)
                        {
                            try { File.WriteAllText(installPathFile, targetExe); } catch { }

                            var psi = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = targetExe,
                                WorkingDirectory = Path.GetDirectoryName(targetExe) ?? "",
                                UseShellExecute = true
                            };
                            System.Diagnostics.Process.Start(psi);
                            Environment.Exit(0);
                            return;
                        }
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

        private static string? FindInstalledGlyphTvExe(string installPathFile)
        {
            // 1. Daha önceden kaydedilmiş kurulum konumu
            try
            {
                if (File.Exists(installPathFile))
                {
                    string saved = File.ReadAllText(installPathFile).Trim();
                    if (File.Exists(saved) && !saved.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                    {
                        return saved;
                    }
                }
            }
            catch { }

            // 2. Standart C:\GlyphTV\GlyphTV.exe konumu
            string defaultPath = @"C:\GlyphTV\GlyphTV.exe";
            if (File.Exists(defaultPath))
            {
                return defaultPath;
            }

            // 3. Görev Çubuğu, Masaüstü ve Başlat Menüsü Kısayolları
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
                                return target;
                            }
                        }
                    }
                    catch { }
                }
            }

            return null;
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
