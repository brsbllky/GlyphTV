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

            // VLC başlat
            try
            {
                Core.Initialize();
            }
            catch (Exception ex)
            {
                Console.WriteLine("VLC Başlatılamadı: " + ex.Message);
            }

            try
            {
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

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
