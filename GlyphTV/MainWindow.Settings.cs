// ============================================================
// MainWindow.Settings.cs
// Uygulama ayarları UI: tema toggle, otomatik yenileme,
// pencere kontrolleri (kapat/küçült/büyüt), title bar
// ============================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;

namespace GlyphTV
{
    public partial class MainWindow
    {
        // ─────────────────────────────────────────────────────────────
        // Otomatik yenileme toggle
        // ─────────────────────────────────────────────────────────────
        private void AutoRefreshToggle_Click(object? sender, RoutedEventArgs e)
        {
            _appSettings.AutoRefreshOnStartup = !_appSettings.AutoRefreshOnStartup;
            SaveAppSettings();
            UpdateAutoRefreshButtonText();
            ShowToast(_appSettings.AutoRefreshOnStartup
                ? "Otomatik yenileme aktif - her açılışta kaynak yenilenecek."
                : "Otomatik yenileme kapatıldı.");
        }

        private void UpdateAutoRefreshButtonText()
        {
            if (AutoRefreshBtn != null)
                AutoRefreshBtn.Content = _appSettings.AutoRefreshOnStartup ? "✓ Açık" : "✕ Kapalı";
        }

        // ─────────────────────────────────────────────────────────────
        // Tema toggle (Koyu / Açık)
        // ─────────────────────────────────────────────────────────────
        private void ThemeToggle_Click(object? sender, RoutedEventArgs e)
        {
            _isDarkMode = !_isDarkMode;

            if (_isDarkMode)
            {
                if (SettingsThemeBtn != null) SettingsThemeBtn.Content = "☀️ Açık";
                this.Resources["Bg"]        = Brush.Parse("#1a1a1f");
                this.Resources["BgSidebar"] = Brush.Parse("#1e1e24");
                this.Resources["BgCard"]    = Brush.Parse("#242428");
                this.Resources["BgHover"]   = Brush.Parse("#2e2e34");
                this.Resources["BgActive"]  = Brush.Parse("#2a2a32");
                this.Resources["Border"]    = Brush.Parse("#3a3a42");
                this.Resources["Text"]      = Brush.Parse("#e4e4e7");
                this.Resources["TextSec"]   = Brush.Parse("#9b9ba8");

                // DÜZELTME: VOD/Dizi poster kartlarında logo yokken gösterilen
                // placeholder ve poster üzerine binen overlay rengi karanlık
                // temada BgCard'a (#18181b) çok yakın kalıp posterleri okunmaz
                // yapıyordu. Placeholder artık accent tonunda daha belirgin,
                // overlay ise saydam — poster zaten yeterince kontrastlı.
                this.Resources["PosterPlaceholderBg"] = Brush.Parse("#4DA855F7");
                this.Resources["PosterOverlayBg"]     = Brushes.Transparent;

                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            }
            else
            {
                if (SettingsThemeBtn != null) SettingsThemeBtn.Content = "🌙 Koyu";
                this.Resources["Bg"]        = Brush.Parse("#f5f5f7");
                this.Resources["BgSidebar"] = Brush.Parse("#f0f0f2");
                this.Resources["BgCard"]    = Brush.Parse("#ffffff");
                this.Resources["BgHover"]   = Brush.Parse("#e8e8ec");
                this.Resources["BgActive"]  = Brush.Parse("#e2e2e8");
                this.Resources["Border"]    = Brush.Parse("#d4d4d8");
                this.Resources["Text"]      = Brush.Parse("#18181b");
                this.Resources["TextSec"]   = Brush.Parse("#6b6b73");

                this.Resources["PosterPlaceholderBg"] = Brush.Parse("#1A4f8bff");
                this.Resources["PosterOverlayBg"]     = Brush.Parse("#0A000000");

                Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // İzleme geçmişini temizle
        // ─────────────────────────────────────────────────────────────
        private void ClearHistory_Click(object? sender, RoutedEventArgs e)
        {
            _watchHistory.Clear();
            _watchHistoryByUrlCache = null;

            // Açık içerik kartlarındaki "Devam Et" butonlarını gizle
            foreach (var ch in _allChannels)
                ch.HasResume = false;

            SaveWatchHistory();
            ShowToast("İzleme geçmişi temizlendi.");
        }

        // ─────────────────────────────────────────────────────────────
        // Özel başlık çubuğu kontrolleri
        // ─────────────────────────────────────────────────────────────
        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                this.BeginMoveDrag(e);
        }

        private void CloseWindow_Click(object? sender, RoutedEventArgs e)
        {
            SaveCurrentWatchPosition();
            this.Close();
        }

        private void MinimizeWindow_Click(object? sender, RoutedEventArgs e) =>
            this.WindowState = Avalonia.Controls.WindowState.Minimized;

        private void MaximizeWindow_Click(object? sender, RoutedEventArgs e) =>
            this.WindowState = this.WindowState == Avalonia.Controls.WindowState.Maximized
                ? Avalonia.Controls.WindowState.Normal
                : Avalonia.Controls.WindowState.Maximized;
    }
}
