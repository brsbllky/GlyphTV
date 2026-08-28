// ============================================================
// MainWindow.Updates.cs
// Çevrimiçi Güncelleme (Online Updates) UI yönetimi, GitHub Releases
// denetimi, güncelleme bildirim modalı ve otomatik indirme akışı
// ============================================================

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow
    {
        private UpdateInfo? _latestUpdateInfo;
        private bool _isCheckingForUpdates = false;
        private bool _isDownloadingUpdate = false;
        private CancellationTokenSource? _updateCts;

        /// <summary>
        /// Güncellemeleri GitHub API üzerinden denetler
        /// </summary>
        /// <param name="manualTrigger">Kullanıcının Ayarlar sekmesindeki butona elle basıp basmadığı</param>
        public async Task CheckForUpdatesAsync(bool manualTrigger = false)
        {
            if (_isCheckingForUpdates) return;

            _isCheckingForUpdates = true;
            UpdateAboutTabCheckingState(true);

            try
            {
                var (hasUpdate, info, errorMessage) = await UpdateManager.CheckForUpdatesAsync(
                    UpdateManager.CURRENT_VERSION);

                _appSettings.LastUpdateCheckTime = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
                SaveAppSettings();

                _latestUpdateInfo = hasUpdate ? info : null;
                UpdateAboutTabCheckingState(false, hasUpdate, errorMessage);

                if (hasUpdate && info != null)
                {
                    // Yeni sürüm bulundu, güncelleme modalını hazırla ve göster
                    ShowUpdateModal(info);
                }
                else if (manualTrigger)
                {
                    if (!string.IsNullOrEmpty(errorMessage))
                    {
                        ShowToast($"Güncelleme kontrolü: {errorMessage}");
                    }
                    else
                    {
                        ShowToast($"GlyphTV güncel. En son sürümü kullanıyorsunuz (v{UpdateManager.CURRENT_VERSION}).");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("CheckForUpdatesAsync", ex);
                UpdateAboutTabCheckingState(false, false, ex.Message);
                if (manualTrigger)
                {
                    ShowToast("Güncelleme sunucusuna bağlanılamadı.");
                }
            }
            finally
            {
                _isCheckingForUpdates = false;
            }
        }

        /// <summary>
        /// Güncelleme modalını doldurur ve ekranda görüntüler
        /// </summary>
        private void ShowUpdateModal(UpdateInfo info)
        {
            if (UpdateModalOverlay == null) return;

            UpdateModalTitleText.Text = string.IsNullOrWhiteSpace(info.Title)
                ? $"GlyphTV v{info.Version} Yayınlandı!"
                : info.Title;

            UpdateModalVersionBadgeText.Text = $"v{info.Version}";
            UpdateModalCurrentVersionText.Text = $"Mevcut: v{UpdateManager.CURRENT_VERSION}";

            string dateStr = info.PublishedAt != DateTime.MinValue
                ? info.PublishedAt.ToString("dd MMMM yyyy")
                : "Yeni Sürüm";
            UpdateModalDateText.Text = dateStr;

            string sizeStr = info.FileSize > 0
                ? $"{info.FileSize / (1024.0 * 1024.0):F1} MB"
                : "Setup Paketi";
            UpdateModalSizeText.Text = sizeStr;

            UpdateModalChangelogText.Text = string.IsNullOrWhiteSpace(info.Changelog)
                ? "Bu sürüm için detaylı sürüm notu girilmedi."
                : info.Changelog;

            // İndirme paneli durumunu sıfırla
            UpdateDownloadPanel.IsVisible = false;
            UpdateActionButtonsPanel.IsVisible = true;
            UpdateProgressBar.Value = 0;
            UpdateProgressStatusText.Text = "İndirmeye Hazır";

            UpdateModalOverlay.IsVisible = true;
        }

        /// <summary>
        /// Modal: "Şimdi Güncelle" butonuna basıldığında otomatik indirme ve kurulum akışını başlatır
        /// </summary>
        private async void UpdateNow_Click(object? sender, RoutedEventArgs e)
        {
            if (_latestUpdateInfo == null || _isDownloadingUpdate) return;

            if (string.IsNullOrEmpty(_latestUpdateInfo.DownloadUrl))
            {
                OpenExternalLink(_latestUpdateInfo.HtmlUrl);
                UpdateModalOverlay.IsVisible = false;
                return;
            }

            _isDownloadingUpdate = true;
            UpdateActionButtonsPanel.IsVisible = false;
            UpdateDownloadPanel.IsVisible = true;
            UpdateProgressBar.Value = 0;
            UpdateProgressStatusText.Text = "İndirme başlatılıyor...";

            _updateCts?.Dispose();
            _updateCts = new CancellationTokenSource();

            var progress = new Progress<double>(percent =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateProgressBar.Value = percent * 100;
                    UpdateProgressStatusText.Text = $"İndiriliyor: %{(int)(percent * 100)}";
                });
            });

            try
            {
                string targetFileName = !string.IsNullOrEmpty(_latestUpdateInfo.FileName)
                    ? _latestUpdateInfo.FileName
                    : $"GlyphTV-v{_latestUpdateInfo.Version}-setup.exe";

                string downloadedFilePath = await UpdateManager.DownloadUpdateAsync(
                    _latestUpdateInfo.DownloadUrl,
                    targetFileName,
                    progress,
                    _updateCts.Token);

                UpdateProgressStatusText.Text = "İndirme tamamlandı! Kurulum başlatılıyor...";
                await Task.Delay(800);

                // Kurulumu başlat ve GlyphTV'yi kapat
                UpdateManager.ApplyUpdateAndRestart(downloadedFilePath);
            }
            catch (OperationCanceledException)
            {
                ShowToast("İndirme iptal edildi.");
                UpdateDownloadPanel.IsVisible = false;
                UpdateActionButtonsPanel.IsVisible = true;
            }
            catch (Exception ex)
            {
                LogError("UpdateNow_Click", ex);
                ShowToast($"İndirme başarısız oldu: {ex.Message}");
                UpdateProgressStatusText.Text = "İndirme hatası. Tarayıcıdan indirmeyi deneyin.";
                UpdateDownloadPanel.IsVisible = false;
                UpdateActionButtonsPanel.IsVisible = true;
            }
            finally
            {
                _isDownloadingUpdate = false;
            }
        }

        /// <summary>
        /// Modal: "GitHub'da Aç" butonu
        /// </summary>
        private void UpdateOpenGithub_Click(object? sender, RoutedEventArgs e)
        {
            string url = _latestUpdateInfo?.HtmlUrl ?? $"https://github.com/{UpdateManager.GITHUB_OWNER}/{UpdateManager.GITHUB_REPO}/releases";
            OpenExternalLink(url);
            UpdateModalOverlay.IsVisible = false;
        }

        /// <summary>
        /// Modal: "Daha Sonra" veya Kapat butonu
        /// </summary>
        private void CloseUpdateModal_Click(object? sender, RoutedEventArgs e)
        {
            if (_isDownloadingUpdate)
            {
                _updateCts?.Cancel();
            }
            UpdateModalOverlay.IsVisible = false;
        }

        /// <summary>
        /// Ayarlar -> Hakkında sekmesindeki güncelleme denetleme butonu ve rozet durumunu günceller
        /// </summary>
        private void UpdateAboutTabCheckingState(bool isChecking, bool hasUpdate = false, string? error = null)
        {
            var checkBtn = this.FindControl<Button>("SettingsAboutCheckUpdatesBtn");
            if (checkBtn == null) return;

            var checkText = this.FindControl<TextBlock>("SettingsAboutCheckUpdatesText");
            var statusBadge = this.FindControl<Border>("SettingsAboutStatusBadge");
            var statusBadgeText = this.FindControl<TextBlock>("SettingsAboutStatusBadgeText");
            var updateBadge = this.FindControl<Border>("SettingsAboutUpdateBadge");
            var updateBadgeText = this.FindControl<TextBlock>("SettingsAboutUpdateBadgeText");
            var lastCheckText = this.FindControl<TextBlock>("SettingsAboutLastCheckText");

            if (isChecking)
            {
                checkBtn.IsEnabled = false;
                if (checkText != null) checkText.Text = "Kontrol ediliyor...";
                if (statusBadge != null && statusBadgeText != null)
                {
                    statusBadge.IsVisible = true;
                    statusBadgeText.Text = "Denetleniyor...";
                    statusBadge.Background = (IBrush)this.FindResource("BgActive")!;
                    statusBadgeText.Foreground = (IBrush)this.FindResource("TextSec")!;
                }
                if (updateBadge != null)
                {
                    updateBadge.IsVisible = false;
                }
                if (lastCheckText != null)
                {
                    lastCheckText.IsVisible = true;
                    lastCheckText.Text = "Denetleniyor...";
                }
            }
            else
            {
                checkBtn.IsEnabled = true;
                if (checkText != null) checkText.Text = "Kontrol Et";

                if (hasUpdate)
                {
                    if (statusBadge != null)
                    {
                        statusBadge.IsVisible = false;
                    }
                    if (lastCheckText != null)
                    {
                        lastCheckText.IsVisible = false;
                    }
                    if (updateBadge != null && updateBadgeText != null)
                    {
                        updateBadge.IsVisible = true;
                        updateBadgeText.Text = $"Yeni Sürüm Mevcut: v{_latestUpdateInfo?.Version}";
                        updateBadge.Background = new SolidColorBrush(Color.Parse("#2222c55e"));
                        updateBadgeText.Foreground = new SolidColorBrush(Color.Parse("#16a34a"));
                    }
                }
                else if (!string.IsNullOrEmpty(error))
                {
                    if (statusBadge != null && statusBadgeText != null)
                    {
                        statusBadge.IsVisible = true;
                        statusBadgeText.Text = "Denetim Başarısız";
                        statusBadge.Background = new SolidColorBrush(Color.Parse("#22ef4444"));
                        statusBadgeText.Foreground = new SolidColorBrush(Color.Parse("#ef4444"));
                    }
                    if (updateBadge != null)
                    {
                        updateBadge.IsVisible = false;
                    }
                    if (lastCheckText != null)
                    {
                        lastCheckText.IsVisible = true;
                        if (!string.IsNullOrEmpty(_appSettings.LastUpdateCheckTime))
                        {
                            lastCheckText.Text = $"Son denetim: {_appSettings.LastUpdateCheckTime}";
                        }
                    }
                }
                else
                {
                    UpdateAboutTabUpdateStatusText();
                }
            }
        }
    }
}
