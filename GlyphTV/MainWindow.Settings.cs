// ============================================================
// MainWindow.Settings.cs
// Uygulama ayarları UI: tema toggle, otomatik yenileme, oynatıcı
// motoru seçimi, pencere kontrolleri (kapat/küçült/büyüt), title bar
// ============================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System;

namespace GlyphTV
{
    public partial class MainWindow
    {
        // ─────────────────────────────────────────────────────────────
        // "Active" class'ını (seçili durumu belirten vurgulanmış görünüm)
        // ekler/kaldırır. Sidebar menü butonlarında (MenuFilter_Click,
        // MainWindow.Navigation.cs) zaten kullanılan Add/Remove deseniyle
        // aynı — burada tek bir yardımcı metoda toplanarak Tema, Otomatik
        // Yenileme VE Oynatıcı Motoru grupları arasında tekrar önlenir.
        // ─────────────────────────────────────────────────────────────
        private static void SetActiveClass(Button? btn, bool active)
        {
            if (btn == null) return;
            if (active) { if (!btn.Classes.Contains("Active")) btn.Classes.Add("Active"); }
            else btn.Classes.Remove("Active");
        }

        // ─────────────────────────────────────────────────────────────
        // Otomatik yenileme — Açık / Kapalı (iki ayrı buton)
        // ─────────────────────────────────────────────────────────────
        private void AutoRefreshOn_Click(object? sender, RoutedEventArgs e) => SetAutoRefresh(true);
        private void AutoRefreshOff_Click(object? sender, RoutedEventArgs e) => SetAutoRefresh(false);

        private void SetAutoRefresh(bool enabled)
        {
            if (_appSettings.AutoRefreshOnStartup == enabled)
            {
                UpdateAutoRefreshButtonsActiveState();
                return;
            }

            _appSettings.AutoRefreshOnStartup = enabled;
            SaveAppSettings();
            UpdateAutoRefreshButtonsActiveState();
            ShowToast(enabled
                ? "Otomatik yenileme aktif - her açılışta kaynak yenilenecek."
                : "Otomatik yenileme kapatıldı.");
        }

        private void UpdateAutoRefreshButtonsActiveState()
        {
            SetActiveClass(AutoRefreshOnBtn, _appSettings.AutoRefreshOnStartup);
            SetActiveClass(AutoRefreshOffBtn, !_appSettings.AutoRefreshOnStartup);
        }

        // ─────────────────────────────────────────────────────────────
        // Açılışta Çevrimiçi Güncellemeleri Denetle — Açık / Kapalı
        // ─────────────────────────────────────────────────────────────
        private void CheckUpdatesOn_Click(object? sender, RoutedEventArgs e) => SetCheckUpdates(true);
        private void CheckUpdatesOff_Click(object? sender, RoutedEventArgs e) => SetCheckUpdates(false);

        private void SetCheckUpdates(bool enabled)
        {
            if (_appSettings.CheckUpdatesOnStartup == enabled)
            {
                UpdateCheckUpdatesButtonsActiveState();
                return;
            }

            _appSettings.CheckUpdatesOnStartup = enabled;
            SaveAppSettings();
            UpdateCheckUpdatesButtonsActiveState();
            ShowToast(enabled
                ? "Açılışta otomatik güncelleme denetimi aktif edildi."
                : "Açılışta otomatik güncelleme denetimi kapatıldı.");
        }

        private void UpdateCheckUpdatesButtonsActiveState()
        {
            SetActiveClass(CheckUpdatesOnBtn, _appSettings.CheckUpdatesOnStartup);
            SetActiveClass(CheckUpdatesOffBtn, !_appSettings.CheckUpdatesOnStartup);
            UpdateAboutTabUpdateStatusText();
        }

        private void UpdateAboutTabUpdateStatusText()
        {
            var lastCheckText = this.FindControl<TextBlock>("SettingsAboutLastCheckText");
            if (lastCheckText == null) return;

            var statusBadge = this.FindControl<Border>("SettingsAboutStatusBadge");
            var statusBadgeText = this.FindControl<TextBlock>("SettingsAboutStatusBadgeText");
            var updateBadge = this.FindControl<Border>("SettingsAboutUpdateBadge");
            var updateBadgeText = this.FindControl<TextBlock>("SettingsAboutUpdateBadgeText");

            bool hasNewerUpdate = _latestUpdateInfo != null &&
                                  UpdateManager.IsNewerVersion(UpdateManager.CURRENT_VERSION, _latestUpdateInfo.Version);

            if (hasNewerUpdate)
            {
                if (statusBadge != null)
                {
                    statusBadge.IsVisible = false;
                }

                if (updateBadge != null && updateBadgeText != null)
                {
                    updateBadge.IsVisible = true;
                    updateBadgeText.Text = $"Yeni Sürüm Mevcut: v{_latestUpdateInfo!.Version}";
                    updateBadge.Background = new SolidColorBrush(Color.Parse("#2222c55e"));
                    updateBadgeText.Foreground = new SolidColorBrush(Color.Parse("#16a34a"));
                }
                lastCheckText.IsVisible = false;
                return;
            }

            if (statusBadge != null && statusBadgeText != null)
            {
                statusBadge.IsVisible = true;
                statusBadgeText.Text = "Sürüm Güncel";
                statusBadge.Background = new SolidColorBrush(Color.Parse("#223b82f6"));
                statusBadgeText.Foreground = (IBrush)this.FindResource("Accent")!;
            }

            if (updateBadge != null)
            {
                updateBadge.IsVisible = false;
            }
            lastCheckText.IsVisible = true;

            if (string.IsNullOrEmpty(_appSettings.LastUpdateCheckTime))
            {
                lastCheckText.Text = _appSettings.CheckUpdatesOnStartup
                    ? "Otomatik denetim aktif"
                    : "Otomatik denetim kapalı";
            }
            else
            {
                lastCheckText.Text = $"Son denetim: {_appSettings.LastUpdateCheckTime}";
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Hakkında sekmesi: Güncellemeleri Denetle butonu
        // ─────────────────────────────────────────────────────────────
        private async void CheckForUpdatesManual_Click(object? sender, RoutedEventArgs e)
        {
            await CheckForUpdatesAsync(manualTrigger: true);
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: Oynatıcı Motoru — VLC / mpv (iki ayrı buton)
        //
        // Tema/Otomatik Yenileme ile birebir aynı desen: iki buton, biri
        // her zaman "Active". Motor değişimi ANINDA uygulanır — eğer o
        // sırada bir video oynatılıyorsa mevcut oynatma durdurulup player
        // kapatılır (yeni motorla kaldığı yerden DEVAM ETMEZ; bu bilinçli
        // bir tercih, çünkü iki motor arası "seamless" geçiş native
        // kaynakların anlık olarak birbirinin yerini alması gerektirir ve
        // bu riskli bir işlemdir — kullanıcı sadece yeniden oynat/devam et
        // butonuna basar).
        //
        // NOT: Bu metodun çalışması için MainWindow.axaml.cs içindeki
        // InitializePlayer()/PlayChannel() akışının IPlayerEngine
        // soyutlamasını kullanacak şekilde güncellenmiş olması gerekir
        // (bkz. sohbetteki "MainWindow.axaml.cs entegrasyon rehberi").
        // Bu dosya tek başına derlenmez; _currentEngine alanı ve
        // SwitchPlayerEngine(...) metodu o entegrasyonun bir parçasıdır.
        // ─────────────────────────────────────────────────────────────
        private void PlayerEngineVlc_Click(object? sender, RoutedEventArgs e) => SetPlayerEngine("Vlc");
        private void PlayerEngineMpv_Click(object? sender, RoutedEventArgs e) => SetPlayerEngine("Mpv");

        private void SetPlayerEngine(string engineName)
        {
            if (_appSettings.PlayerEngine == engineName)
            {
                UpdatePlayerEngineButtonsActiveState();
                return;
            }

            // Oynatma sürüyorsa güvenli tarafta kal: motoru değiştirmeden
            // önce player'ı kapat (native kaynaklar iç içe geçmesin).
            if (PlayerContainer.Height > 0)
            {
                ClosePlayer_Click(null, new RoutedEventArgs());
            }

            _appSettings.PlayerEngine = engineName;
            SaveAppSettings();
            UpdatePlayerEngineButtonsActiveState();
            UpdateMpvSettingsButtonVisibility();

            // Entegrasyon rehberindeki SwitchPlayerEngine(...) — mevcut
            // motoru Dispose edip yeni motoru (henüz Initialize
            // ÇAĞRILMADAN, ilk oynatmada lazy başlatılacak şekilde) kurar.
            SwitchPlayerEngine(engineName);

            ShowToast(engineName == "Mpv"
                ? "Oynatıcı motoru mpv olarak ayarlandı."
                : "Oynatıcı motoru VLC olarak ayarlandı.");
        }

        private void UpdatePlayerEngineButtonsActiveState()
        {
            SetActiveClass(PlayerEngineVlcBtn, _appSettings.PlayerEngine != "Mpv");
            SetActiveClass(PlayerEngineMpvBtn, _appSettings.PlayerEngine == "Mpv");
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: Donanım Çözümlemesi — Otomatik/Önerilen, Windows D3D11VA,
        // NVIDIA NVDEC, NVIDIA NVDEC Copy, Kapalı (5 seçenekli, tek
        // seçilebilir liste — Tema/Otomatik Yenileme'deki iki-buton
        // desenin genişletilmiş hâli). Motor değişimindekinin aksine
        // player kapatılmaz: hem mpv hem VLC değişikliği "sonraki
        // oynatmaya" uygular (VLC için bu zorunlu — bkz. VlcPlayerEngine
        // yorumu; mpv aslında anında uygulayabiliyor ama tutarlılık için
        // aynı davranış korunuyor), bu yüzden burada oynatma kesintiye
        // uğratılmaz.
        // ─────────────────────────────────────────────────────────────
        // YENİ: internal — bu handler artık hem Ayarlar sekmesindeki
        // "Oynatıcı Performansı" kartından hem de PlayerOverlayWindow.axaml
        // → MpvSettingsPopup içindeki kopya kontrol grubundan (forwarding
        // ile, bkz. PlayerOverlayWindow.axaml.cs) çağrılabiliyor.
        internal void HwDecodeItem_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Border b || b.Tag is not string mode) return;
            SetHwDecodeMode(mode);
        }

        private void SetHwDecodeMode(string mode)
        {
            if (_appSettings.HwDecodeMode == mode)
            {
                UpdateHwDecodeItemsActiveState();
                return;
            }

            _appSettings.HwDecodeMode = mode;
            SaveAppSettings();
            UpdateHwDecodeItemsActiveState();
            _engine?.SetHardwareDecoding(mode);
            ShowToast($"Donanım çözümlemesi: {HwDecodeLabel(mode)}");
        }

        private static string HwDecodeLabel(string mode) => mode switch
        {
            "d3d11va" => "Windows D3D11VA",
            "nvdec" => "NVIDIA NVDEC",
            "nvdec-copy" => "NVIDIA NVDEC Copy",
            "no" => "Kapalı",
            _ => "Otomatik / Önerilen"
        };

        // DÜZELTME: "Oynatıcı Performansı" kartı Ayarlar sekmesinden
        // tamamen kaldırıldı (bkz. MainWindow.axaml) — bu seçenekler artık
        // SADECE mpv Ayarları (⚙️) popup'ında yaşıyor (bkz.
        // PlayerOverlayWindow.axaml → MpvSettingsPopup). Bu yüzden burada
        // artık eski Settings-sekmesi kontrollerine (HwDecodeAutoItem vb.)
        // değil, doğrudan Popup* kontrollerine yazılıyor.
        private void UpdateHwDecodeItemsActiveState()
        {
            string mode = _appSettings.HwDecodeMode;
            SetSelectListItemActive(PopupHwDecodeAutoItem, PopupHwDecodeAutoCheck, mode == "auto");
            SetSelectListItemActive(PopupHwDecodeD3D11Item, PopupHwDecodeD3D11Check, mode == "d3d11va");
            SetSelectListItemActive(PopupHwDecodeNvdecItem, PopupHwDecodeNvdecCheck, mode == "nvdec");
            SetSelectListItemActive(PopupHwDecodeNvdecCopyItem, PopupHwDecodeNvdecCopyCheck, mode == "nvdec-copy");
            SetSelectListItemActive(PopupHwDecodeOffItem, PopupHwDecodeOffCheck, mode == "no");
            UpdateMpvPopupStatusText();
        }

        private static void SetSelectListItemActive(Border? item, TextBlock? check, bool active)
        {
            if (item != null)
            {
                if (active) { if (!item.Classes.Contains("Active")) item.Classes.Add("Active"); }
                else item.Classes.Remove("Active");
            }
            if (check != null) check.IsVisible = active;
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: İnterlacing'i Kaldır — tek satırlık açık/kapalı toggle.
        // Diğer "iki ayrı buton" desenlerinden farklı olarak burada tek
        // bir satıra tıklanır, sağdaki onay işareti (✓) mevcut durumu
        // gösterir.
        // ─────────────────────────────────────────────────────────────
        // YENİ: internal — bkz. HwDecodeItem_PointerPressed'teki aynı notun
        // açıklaması (artık MpvSettingsPopup'taki kopya kontrolden de
        // çağrılabiliyor).
        internal void InterlaceToggle_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            _appSettings.RemoveInterlacing = !_appSettings.RemoveInterlacing;
            SaveAppSettings();
            UpdateInterlaceToggleState();
            _engine?.SetDeinterlace(_appSettings.RemoveInterlacing, _appSettings.DeinterlaceMode);
            ShowToast(_appSettings.RemoveInterlacing
                ? $"İnterlacing kaldırma aktif ({DeinterlaceModeLabel(_appSettings.DeinterlaceMode)})."
                : "İnterlacing kaldırma kapatıldı.");
        }

        internal void DeinterlaceModeItem_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Border b || b.Tag is not string mode) return;
            SetDeinterlaceMode(mode);
        }

        private void SetDeinterlaceMode(string mode)
        {
            _appSettings.DeinterlaceMode = mode;
            SaveAppSettings();
            UpdateDeinterlaceModesActiveState();
            if (_appSettings.RemoveInterlacing)
            {
                _engine?.SetDeinterlace(true, mode);
            }
            ShowToast($"Deinterlace Modu: {DeinterlaceModeLabel(mode)}");
        }

        private static string DeinterlaceModeLabel(string mode) => mode switch
        {
            "yadif2x" => "Yadif 2X (Çift Kare)",
            "yadif"   => "Yadif (Tek Kare)",
            "bob"     => "Bob (Donanım)",
            "linear"  => "Linear",
            _         => "Yadif 2X"
        };

        private void UpdateInterlaceToggleState()
        {
            SetSelectListItemActive(PopupInterlaceItem, PopupInterlaceCheck, _appSettings.RemoveInterlacing);
            if (DeinterlaceModesContainer != null)
            {
                DeinterlaceModesContainer.Opacity = _appSettings.RemoveInterlacing ? 1.0 : 0.38;
                DeinterlaceModesContainer.IsHitTestVisible = _appSettings.RemoveInterlacing;
            }
            UpdateDeinterlaceModesActiveState();
        }

        private void UpdateDeinterlaceModesActiveState()
        {
            string mode = _appSettings.DeinterlaceMode;
            SetSelectListItemActive(PopupDeintYadif2xItem, null, mode == "yadif2x");
            SetSelectListItemActive(PopupDeintYadifItem, null, mode == "yadif");
            SetSelectListItemActive(PopupDeintBobItem, null, mode == "bob");
            SetSelectListItemActive(PopupDeintLinearItem, null, mode == "linear");
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: HDR Ton Eşleme / Hedef Ekran Tepe Parlaklığı / İşleme
        // Kalitesi — SADECE mpv motorunda etki eder (VLC'de bu düzeyde
        // ayrıntılı HDR/ölçekleme kontrolü yok). Donanım Çözümlemesi/
        // İnterlacing ile AYNI desen: liste öğesine tıklanır, aktif durum
        // (✓ işareti) güncellenir, değişiklik oynatma kesintiye
        // uğratılmadan bir sonraki oynatmaya (ya da mpv'de ANINDA, VO
        // property'leri çalışırken de değiştirilebildiğinden) uygulanır.
        // ─────────────────────────────────────────────────────────────
        internal void HdrToneMappingItem_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Border b || b.Tag is not string mode) return;
            SetHdrToneMapping(mode);
        }

        private void SetHdrToneMapping(string mode)
        {
            if (_appSettings.HdrToneMapping == mode)
            {
                UpdateHdrToneMappingItemsActiveState();
                return;
            }

            _appSettings.HdrToneMapping = mode;
            SaveAppSettings();
            UpdateHdrToneMappingItemsActiveState();
            if (_engine is GlyphTV.PlayerEngines.MpvPlayerEngine mpvEngine)
                mpvEngine.SetHdrToneMapping(mode);
            ShowToast($"HDR ton eşleme: {HdrToneMappingLabel(mode)}");
        }

        private static string HdrToneMappingLabel(string mode) => mode switch
        {
            "hable" => "Hable",
            "mobius" => "Mobius",
            "bt.2446a" => "BT.2446a",
            _ => "Otomatik"
        };

        private void UpdateHdrToneMappingItemsActiveState()
        {
            string mode = _appSettings.HdrToneMapping;
            SetSelectListItemActive(HdrToneMapAutoItem, HdrToneMapAutoCheck, mode == "auto");
            SetSelectListItemActive(HdrToneMapHableItem, HdrToneMapHableCheck, mode == "hable");
            SetSelectListItemActive(HdrToneMapMobiusItem, HdrToneMapMobiusCheck, mode == "mobius");
            SetSelectListItemActive(HdrToneMapBt2446aItem, HdrToneMapBt2446aCheck, mode == "bt.2446a");
        }

        internal void HdrTargetPeakItem_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Border b || b.Tag is not string peak) return;
            SetHdrTargetPeak(peak);
        }

        private void SetHdrTargetPeak(string peak)
        {
            if (_appSettings.HdrTargetPeak == peak)
            {
                UpdateHdrTargetPeakItemsActiveState();
                return;
            }

            _appSettings.HdrTargetPeak = peak;
            SaveAppSettings();
            UpdateHdrTargetPeakItemsActiveState();
            if (_engine is GlyphTV.PlayerEngines.MpvPlayerEngine mpvEngine)
                mpvEngine.SetHdrTargetPeak(peak);
            ShowToast($"Hedef tepe parlaklığı: {(peak == "auto" ? "Otomatik" : peak + " nit")}");
        }

        private void UpdateHdrTargetPeakItemsActiveState()
        {
            string peak = _appSettings.HdrTargetPeak;
            SetSelectListItemActive(HdrPeakAutoItem, HdrPeakAutoCheck, peak == "auto");
            SetSelectListItemActive(HdrPeak100Item, HdrPeak100Check, peak == "100");
            SetSelectListItemActive(HdrPeak200Item, HdrPeak200Check, peak == "200");
            SetSelectListItemActive(HdrPeak400Item, HdrPeak400Check, peak == "400");
            SetSelectListItemActive(HdrPeak600Item, HdrPeak600Check, peak == "600");
            SetSelectListItemActive(HdrPeak800Item, HdrPeak800Check, peak == "800");
            SetSelectListItemActive(HdrPeak1000Item, HdrPeak1000Check, peak == "1000");
        }

        internal void ScalingQualityDefault_Click(object? sender, RoutedEventArgs e) => SetScalingQuality("default");
        internal void ScalingQualityHigh_Click(object? sender, RoutedEventArgs e) => SetScalingQuality("quality");

        private void SetScalingQuality(string quality)
        {
            if (_appSettings.ScalingQuality == quality)
            {
                UpdateScalingQualityButtonsActiveState();
                return;
            }

            _appSettings.ScalingQuality = quality;
            SaveAppSettings();
            UpdateScalingQualityButtonsActiveState();
            if (_engine is GlyphTV.PlayerEngines.MpvPlayerEngine mpvEngine)
                mpvEngine.SetScalingQuality(quality);
            ShowToast(quality == "quality"
                ? "İşleme kalitesi: Kalite (daha keskin ölçekleme)"
                : "İşleme kalitesi: Varsayılan");
        }

        private void UpdateScalingQualityButtonsActiveState()
        {
            bool isQuality = _appSettings.ScalingQuality == "quality";
            SetActiveClass(ScalingQualityDefaultBtn, !isQuality);
            SetActiveClass(ScalingQualityHighBtn, isQuality);
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: Resim Modları (Doğal, Canlı, Spor, Sinema) & F1 - F4
        // ─────────────────────────────────────────────────────────────
        internal void PresetNatural_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ApplyPicturePreset("natural");
        }

        internal void PresetVivid_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ApplyPicturePreset("vivid");
        }

        internal void PresetSports_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ApplyPicturePreset("sports");
        }

        internal void PresetCinema_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ApplyPicturePreset("cinema");
        }

        public void ApplyPicturePreset(string preset, bool showToast = true)
        {
            switch (preset.ToLowerInvariant())
            {
                case "vivid":
                    _appSettings.Brightness = 4;
                    _appSettings.Contrast = 16;
                    _appSettings.Saturation = 28;
                    _appSettings.Gamma = 6;
                    _appSettings.PicturePreset = "vivid";
                    break;
                case "sports":
                    _appSettings.Brightness = 8;
                    _appSettings.Contrast = 22;
                    _appSettings.Saturation = 32;
                    _appSettings.Gamma = -4;
                    _appSettings.PicturePreset = "sports";
                    break;
                case "cinema":
                    _appSettings.Brightness = -4;
                    _appSettings.Contrast = 12;
                    _appSettings.Saturation = -4;
                    _appSettings.Gamma = 14;
                    _appSettings.PicturePreset = "cinema";
                    break;
                case "natural":
                default:
                    _appSettings.Brightness = 0;
                    _appSettings.Contrast = 0;
                    _appSettings.Saturation = 0;
                    _appSettings.Gamma = 0;
                    _appSettings.PicturePreset = "natural";
                    break;
            }

            SaveAppSettings();
            InitializeMpvEqSliderValues();
            UpdatePicturePresetActiveState();

            _engine?.SetBrightness(_appSettings.Brightness);
            _engine?.SetContrast(_appSettings.Contrast);
            _engine?.SetSaturation(_appSettings.Saturation);
            _engine?.SetGamma(_appSettings.Gamma);

            if (showToast)
            {
                string name = _appSettings.PicturePreset switch
                {
                    "vivid" => "Canlı [F2]",
                    "sports" => "Spor [F3]",
                    "cinema" => "Sinema [F4]",
                    _ => "Doğal [F1]"
                };
                ShowToast($"Resim Modu: {name}");
            }
        }

        private void UpdatePicturePresetActiveState()
        {
            string preset = _appSettings.PicturePreset;
            SetSelectListItemActive(PresetNaturalItem, null, preset == "natural");
            SetSelectListItemActive(PresetVividItem, null, preset == "vivid");
            SetSelectListItemActive(PresetSportsItem, null, preset == "sports");
            SetSelectListItemActive(PresetCinemaItem, null, preset == "cinema");
        }

        private void CheckCustomPreset()
        {
            int b = _appSettings.Brightness;
            int c = _appSettings.Contrast;
            int s = _appSettings.Saturation;
            int g = _appSettings.Gamma;

            if (b == 0 && c == 0 && s == 0 && g == 0) _appSettings.PicturePreset = "natural";
            else if (b == 4 && c == 16 && s == 28 && g == 6) _appSettings.PicturePreset = "vivid";
            else if (b == 8 && c == 22 && s == 32 && g == -4) _appSettings.PicturePreset = "sports";
            else if (b == -4 && c == 12 && s == -4 && g == 14) _appSettings.PicturePreset = "cinema";
            else _appSettings.PicturePreset = "custom";

            UpdatePicturePresetActiveState();
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: İnce görüntü ayarları (Parlaklık/Kontrast/Doygunluk/Gama)
        // — mpv Ayarları popup'ındaki 4 slider.
        // Aralık -100..100, varsayılan 0. Değer değiştikçe ANINDA uygulanır.
        // ─────────────────────────────────────────────────────────────
        internal void MpvEqBrightness_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            int value = (int)e.NewValue;
            _appSettings.Brightness = value;
            if (MpvEqBrightnessText != null) MpvEqBrightnessText.Text = value.ToString();
            CheckCustomPreset();
            SaveAppSettings();
            _engine?.SetBrightness(value);
        }

        internal void MpvEqContrast_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            int value = (int)e.NewValue;
            _appSettings.Contrast = value;
            if (MpvEqContrastText != null) MpvEqContrastText.Text = value.ToString();
            CheckCustomPreset();
            SaveAppSettings();
            _engine?.SetContrast(value);
        }

        internal void MpvEqSaturation_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            int value = (int)e.NewValue;
            _appSettings.Saturation = value;
            if (MpvEqSaturationText != null) MpvEqSaturationText.Text = value.ToString();
            CheckCustomPreset();
            SaveAppSettings();
            _engine?.SetSaturation(value);
        }

        internal void MpvEqGamma_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            int value = (int)e.NewValue;
            _appSettings.Gamma = value;
            if (MpvEqGammaText != null) MpvEqGammaText.Text = value.ToString();
            CheckCustomPreset();
            SaveAppSettings();
            _engine?.SetGamma(value);
        }

        // Slider'ların ilk değerlerini (kayıtlı AppSettings'ten) uygular —
        // hem C# tarafındaki alan (_brightness vb.) hem de görünen sayı
        // etiketi güncellenir.
        internal void InitializeMpvEqSliderValues()
        {
            if (MpvEqBrightnessSlider != null) MpvEqBrightnessSlider.Value = _appSettings.Brightness;
            if (MpvEqContrastSlider != null) MpvEqContrastSlider.Value = _appSettings.Contrast;
            if (MpvEqSaturationSlider != null) MpvEqSaturationSlider.Value = _appSettings.Saturation;
            if (MpvEqGammaSlider != null) MpvEqGammaSlider.Value = _appSettings.Gamma;
            if (MpvEqBrightnessText != null) MpvEqBrightnessText.Text = _appSettings.Brightness.ToString();
            if (MpvEqContrastText != null) MpvEqContrastText.Text = _appSettings.Contrast.ToString();
            if (MpvEqSaturationText != null) MpvEqSaturationText.Text = _appSettings.Saturation.ToString();
            if (MpvEqGammaText != null) MpvEqGammaText.Text = _appSettings.Gamma.ToString();
            UpdatePicturePresetActiveState();
        }

        // ─────────────────────────────────────────────────────────────
        // ─────────────────────────────────────────────────────────────
        // YENİ: Oynatıcı üzerindeki ⚙️ (mpv Ayarları) butonu — popup'ı
        // aç/kapat. Diğer player popup'larıyla (Ses Dili/Altyazı/EnBoy)
        // aynı "önce diğerlerini kapat" deseni.
        // ─────────────────────────────────────────────────────────────
        internal void MpvSettingsBtn_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            AudioTrackPopup.IsVisible = false;
            SubtitlePopup.IsVisible = false;
            AspectRatioPopup.IsVisible = false;
            ChannelListPanel.IsVisible = false;

            if (MpvSettingsPopup.IsVisible) { MpvSettingsPopup.IsVisible = false; return; }

            InitializeMpvEqSliderValues();
            UpdateHwDecodeItemsActiveState();
            UpdateInterlaceToggleState();
            UpdateShaderItemsActiveState();
            UpdateZappingItemsActiveState();
            UpdateAudioEnhanceItemsActiveState();
            UpdatePicturePresetActiveState();
            UpdateHdrToneMappingItemsActiveState();
            UpdateHdrTargetPeakItemsActiveState();
            UpdateScalingQualityButtonsActiveState();
            UpdateMpvPopupStatusText();

            // Sadece MPV motorunda geçerli olan shader bölümü VLC'de gizlenir
            if (ShaderSectionContainer != null)
                ShaderSectionContainer.IsVisible = (_appSettings.PlayerEngine == "Mpv");

            MpvSettingsPopup.IsVisible = true;
        }

        internal void CloseMpvSettingsPopup_Click(object? sender, RoutedEventArgs e) =>
            MpvSettingsPopup.IsVisible = false;

        // ─────────────────────────────────────────────────────────────
        // YENİ: AMD FidelityFX CAS & FSR GLSL Shader Seçimi
        // ─────────────────────────────────────────────────────────────
        internal void ShaderItem_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Border b || b.Tag is not string mode) return;
            _appSettings.ShaderMode = mode;
            SaveAppSettings();
            UpdateShaderItemsActiveState();
            _engine?.SetShaderMode(mode);
            ShowToast(mode switch
            {
                "cas" => "AMD FidelityFX CAS keskinleştirme aktif",
                "fsr" => "AMD FSR süper çözünürlük aktif",
                _     => "Görüntü shader'ı kapatıldı"
            });
        }

        internal void UpdateShaderItemsActiveState()
        {
            try
            {
                string mode = _appSettings.ShaderMode;
                SetSelectListItemActive(ShaderOffItem, null, mode == "off");
                SetSelectListItemActive(ShaderCasItem, null, mode == "cas");
                SetSelectListItemActive(ShaderFsrItem, null, mode == "fsr");
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: Canlı TV Ultra-Fast Zapping Modu
        // ─────────────────────────────────────────────────────────────
        internal void ZappingItem_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Border b || b.Tag is not string tag) return;
            bool fast = tag == "fast";
            _appSettings.FastZapping = fast;
            SaveAppSettings();
            UpdateZappingItemsActiveState();
            _engine?.SetFastZapping(fast);
            ShowToast(fast
                ? "⚡ Ultra Hızlı Kanal Geçişi (200ms) aktif"
                : "Standart Tamponlu Geçiş Modu aktif");
        }

        internal void UpdateZappingItemsActiveState()
        {
            try
            {
                bool fast = _appSettings.FastZapping;
                SetSelectListItemActive(ZappingFastItem, null, fast);
                SetSelectListItemActive(ZappingStableItem, null, !fast);
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: Akıllı Ses İşleme (EBU R128 Loudnorm & Gece Modu)
        // ─────────────────────────────────────────────────────────────
        internal void AudioEnhanceItem_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Border b || b.Tag is not string mode) return;
            _appSettings.AudioEnhancement = mode;
            SaveAppSettings();
            UpdateAudioEnhanceItemsActiveState();
            _engine?.SetAudioEnhancement(mode);
            ShowToast(mode switch
            {
                "loudnorm" => "EBU R128 Akıllı Ses Dengeleme (Loudnorm) aktif",
                "night"    => "Gece Modu (Dinamik Kompresör) aktif",
                _          => "Ses işleme kapatıldı (Standart ses)"
            });
        }

        internal void UpdateAudioEnhanceItemsActiveState()
        {
            try
            {
                string mode = _appSettings.AudioEnhancement;
                SetSelectListItemActive(AudioEnhanceOffItem, null, mode == "off");
                SetSelectListItemActive(AudioEnhanceLoudnormItem, null, mode == "loudnorm");
                SetSelectListItemActive(AudioEnhanceNightItem, null, mode == "night");
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: Popup başlığının altındaki "Aktif: ..." durum satırı
        // ─────────────────────────────────────────────────────────────
        private void UpdateMpvPopupStatusText()
        {
            if (MpvPopupStatusText != null)
                MpvPopupStatusText.Text = $"Motor: {(_appSettings.PlayerEngine == "Mpv" ? "MPV" : "VLC")} | HW: {_appSettings.HwDecodeMode}";
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: mpv Ayarları popup'ındaki "Sıfırla" butonu
        // ─────────────────────────────────────────────────────────────
        internal void MpvResetSettings_Click(object? sender, RoutedEventArgs e) => ResetMpvSettingsCore();

        internal void MpvResetSettings_Click(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            ResetMpvSettingsCore();
        }

        private void ResetMpvSettingsCore()
        {
            _appSettings.Brightness = 0;
            _appSettings.Contrast = 0;
            _appSettings.Saturation = 0;
            _appSettings.Gamma = 0;
            _appSettings.PicturePreset = "natural";
            _appSettings.HdrToneMapping = "auto";
            _appSettings.HdrTargetPeak = "auto";
            _appSettings.ScalingQuality = "default";
            _appSettings.HwDecodeMode = "auto";
            _appSettings.RemoveInterlacing = false;
            _appSettings.DeinterlaceMode = "yadif2x";
            _appSettings.ShaderMode = "off";
            _appSettings.FastZapping = true;
            _appSettings.AudioEnhancement = "off";
            SaveAppSettings();

            InitializeMpvEqSliderValues();
            UpdatePicturePresetActiveState();
            UpdateHdrToneMappingItemsActiveState();
            UpdateHdrTargetPeakItemsActiveState();
            UpdateScalingQualityButtonsActiveState();
            UpdateHwDecodeItemsActiveState();
            UpdateInterlaceToggleState();
            UpdateShaderItemsActiveState();
            UpdateZappingItemsActiveState();
            UpdateAudioEnhanceItemsActiveState();
            UpdateMpvPopupStatusText();

            _engine?.SetBrightness(0);
            _engine?.SetContrast(0);
            _engine?.SetSaturation(0);
            _engine?.SetGamma(0);
            if (_engine is GlyphTV.PlayerEngines.MpvPlayerEngine mpvEngine)
            {
                mpvEngine.SetHdrToneMapping("auto");
                mpvEngine.SetHdrTargetPeak("auto");
                mpvEngine.SetScalingQuality("default");
            }
            _engine?.SetHardwareDecoding("auto");
            _engine?.SetDeinterlace(false, "yadif2x");
            _engine?.SetShaderMode("off");
            _engine?.SetFastZapping(true);
            _engine?.SetAudioEnhancement("off");

            ShowToast("Gelişmiş ayarlar varsayılana döndürüldü.");
        }

        // ─────────────────────────────────────────────────────────────
        // ⚙️ Gelişmiş video/performans ayarları butonu — VLC ve mpv
        // her iki motor için de gelişmiş görüntü, donanım hızlandırma ve
        // interlacing ayarlarını sunar.
        // ─────────────────────────────────────────────────────────────
        internal void UpdateMpvSettingsButtonVisibility()
        {
            try
            {
                if (MpvSettingsBtn != null) MpvSettingsBtn.IsVisible = true;
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // Tema — Sistem / Açık / Koyu (üç ayrı buton)
        // ─────────────────────────────────────────────────────────────
        private void ThemeSystem_Click(object? sender, RoutedEventArgs e) => SetThemeMode("System");
        private void ThemeLight_Click(object? sender, RoutedEventArgs e)  => SetThemeMode("Light");
        private void ThemeDark_Click(object? sender, RoutedEventArgs e)   => SetThemeMode("Dark");

        private void SetThemeMode(string mode)
        {
            _appSettings.ThemeMode = mode;
            SaveAppSettings();
            ApplyThemeMode(mode);
        }

        private void ApplyThemeMode(string mode)
        {
            bool dark = mode switch
            {
                "Dark"  => true,
                "Light" => false,
                _       => GetSystemThemeVariant() == PlatformThemeVariant.Dark
            };

            _isDarkMode = dark;
            if (dark) ApplyDarkThemeResources(); else ApplyLightThemeResources();
            UpdateThemeButtonsActiveState(mode);
        }

        private static PlatformThemeVariant GetSystemThemeVariant()
        {
            try
            {
                var colorValues = Application.Current?.PlatformSettings?.GetColorValues();
                return colorValues?.ThemeVariant ?? PlatformThemeVariant.Light;
            }
            catch { return PlatformThemeVariant.Light; }
        }

        private void UpdateThemeButtonsActiveState(string mode)
        {
            SetActiveClass(ThemeSystemBtn, mode == "System");
            SetActiveClass(ThemeLightBtn, mode == "Light");
            SetActiveClass(ThemeDarkBtn, mode == "Dark");
        }

        private void ApplyDarkThemeResources()
        {
            this.Resources["Bg"]        = Brush.Parse("#0b0f19");
            this.Resources["BgSidebar"] = Brush.Parse("#0e1322");
            this.Resources["BgCard"]    = Brush.Parse("#131a2e");
            this.Resources["BgHover"]   = Brush.Parse("#1a243e");
            this.Resources["BgActive"]  = Brush.Parse("#243356");
            this.Resources["Border"]    = Brush.Parse("#1e2b47");
            this.Resources["Text"]      = Brush.Parse("#f1f5f9");
            this.Resources["TextSec"]   = Brush.Parse("#94a3b8");

            this.Resources["PosterPlaceholderBg"] = Brush.Parse("#261e2b47");
            this.Resources["PosterOverlayBg"]     = Brushes.Transparent;

            this.Resources["ComboBoxBackground"]                          = Brush.Parse("#131a2e");
            this.Resources["ComboBoxBackgroundPointerOver"]              = Brush.Parse("#1a243e");
            this.Resources["ComboBoxBackgroundPressed"]                  = Brush.Parse("#243356");
            this.Resources["ComboBoxBorderBrush"]                        = Brush.Parse("#1e2b47");
            this.Resources["ComboBoxBorderBrushPointerOver"]             = Brush.Parse("#334155");
            this.Resources["ComboBoxDropDownBackground"]                  = Brush.Parse("#131a2e");
            this.Resources["ComboBoxDropDownBorderBrush"]                = Brush.Parse("#1e2b47");
            this.Resources["ComboBoxItemPresenterBackgroundPointerOver"] = Brush.Parse("#1a243e");
            this.Resources["ComboBoxItemPresenterBackgroundSelected"]    = Brush.Parse("#243356");
            this.Resources["ComboBoxForeground"]                         = Brush.Parse("#f1f5f9");

            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        }

        private void ApplyLightThemeResources()
        {
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

            this.Resources["ComboBoxBackground"]                          = Brush.Parse("#ffffff");
            this.Resources["ComboBoxBackgroundPointerOver"]              = Brush.Parse("#e8e8ec");
            this.Resources["ComboBoxBackgroundPressed"]                  = Brush.Parse("#e2e2e8");
            this.Resources["ComboBoxBorderBrush"]                        = Brush.Parse("#d4d4d8");
            this.Resources["ComboBoxBorderBrushPointerOver"]             = Brush.Parse("#a1a1aa");
            this.Resources["ComboBoxDropDownBackground"]                  = Brush.Parse("#ffffff");
            this.Resources["ComboBoxDropDownBorderBrush"]                = Brush.Parse("#d4d4d8");
            this.Resources["ComboBoxItemPresenterBackgroundPointerOver"] = Brush.Parse("#e8e8ec");
            this.Resources["ComboBoxItemPresenterBackgroundSelected"]    = Brush.Parse("#e2e2e8");
            this.Resources["ComboBoxForeground"]                         = Brush.Parse("#18181b");

            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        }

        // ─────────────────────────────────────────────────────────────
        // PROFESYONEL AYARLAR MODALI YÖNETİMİ & TAB GEÇİŞLERİ
        // ─────────────────────────────────────────────────────────────
        private string _activeSettingsTab = "Sources";

        private void CloseSettings_Click(object? sender, RoutedEventArgs e)
        {
            SettingsModalOverlay.IsVisible = false;
            RestorePlayerLayerAfterModal();
        }

        private void SettingsNav_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tabKey) return;
            SwitchSettingsTab(tabKey);
        }

        private void SwitchSettingsTab(string tabKey)
        {
            _activeSettingsTab = tabKey;

            if (SettingsTabSources != null) SettingsTabSources.IsVisible = tabKey == "Sources";
            if (SettingsTabAppearance != null) SettingsTabAppearance.IsVisible = tabKey == "Appearance";
            if (SettingsTabData != null) SettingsTabData.IsVisible = tabKey == "Data";
            if (SettingsTabShortcuts != null) SettingsTabShortcuts.IsVisible = tabKey == "Shortcuts";
            if (SettingsTabAbout != null)
            {
                SettingsTabAbout.IsVisible = tabKey == "About";
                if (tabKey == "About") UpdateAboutTabUpdateStatusText();
            }

            SetActiveClass(SettingsNavSources, tabKey == "Sources");
            SetActiveClass(SettingsNavAppearance, tabKey == "Appearance");
            SetActiveClass(SettingsNavData, tabKey == "Data");
            SetActiveClass(SettingsNavShortcuts, tabKey == "Shortcuts");
            SetActiveClass(SettingsNavAbout, tabKey == "About");
        }

        private void ClearCache_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                lock (_logoCacheLock) { _logoCache.Clear(); _logoCacheOrder.Clear(); }
                lock (_posterCacheLock) { _tmdbPosterCache.Clear(); _posterCacheOrder.Clear(); }

                string logoDir = GetLogoCacheDir();
                if (System.IO.Directory.Exists(logoDir))
                {
                    foreach (var file in System.IO.Directory.GetFiles(logoDir))
                    {
                        try { System.IO.File.Delete(file); } catch { }
                    }
                }

                ShowToast("Görsel ve logo önbelleği temizlendi.");
            }
            catch (Exception ex)
            {
                LogError("ClearCache_Click", ex);
                ShowToast("Önbellek temizlenirken bir hata oluştu.");
            }
        }

        private void ResetApp_Click(object? sender, RoutedEventArgs e)
        {
            _appSettings = new AppSettings();
            SaveAppSettings();
            ApplyThemeMode(_appSettings.ThemeMode);
            UpdateAutoRefreshButtonsActiveState();
            UpdatePlayerEngineButtonsActiveState();
            UpdateScalingQualityButtonsActiveState();
            UpdateHdrToneMappingItemsActiveState();
            UpdateHdrTargetPeakItemsActiveState();
            UpdateHwDecodeItemsActiveState();
            UpdateInterlaceToggleState();
            UpdateShaderItemsActiveState();
            UpdateZappingItemsActiveState();
            UpdateAudioEnhanceItemsActiveState();
            InitializeMpvEqSliderValues();

            _sources.Clear();
            _allChannels.Clear();
            _watchHistory.Clear();
            _contentCache.Clear();
            _seriesCardCache.Clear();
            _seriesSelections.Clear();
            _decryptedChannelsCache.Clear();

            string appData = AppDataDir();
            try
            {
                foreach (var f in new[] { "sources.json", "history.json" })
                {
                    var p = System.IO.Path.Combine(appData, f);
                    if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
                }
                foreach (var f in System.IO.Directory.GetFiles(appData, "channels_*.json"))
                    System.IO.File.Delete(f);
            }
            catch { }

            LoadSources();
            UpdateView();
            ShowToast("Uygulama ve ayarlar sıfırlandı.");
        }

        // ─────────────────────────────────────────────────────────────
        // İzleme geçmişini temizle
        // ─────────────────────────────────────────────────────────────
        private void ClearHistory_Click(object? sender, RoutedEventArgs e)
        {
            _watchHistory.Clear();
            _watchHistoryByUrlCache = null;

            foreach (var ch in _allChannels)
                ch.HasResume = false;

            _seriesSelections.Clear();

            void ResetCard(SeriesCard card)
            {
                card.RestoreSelection(0, 0);
                card.HasResume = false;
            }

            foreach (var c in _displaySeriesCards) ResetCard(c);
            foreach (var c in _displayFavoriSeriesCards) ResetCard(c);
            foreach (var c in _allFilteredCards) ResetCard(c);
            foreach (var c in _allFavoriSeriesCards) ResetCard(c);
            foreach (var cacheList in _seriesCardCache.Values)
                foreach (var c in cacheList) ResetCard(c);

            SaveWatchHistory();
            RefreshHomeResumeSection();
            ShowToast("İzleme geçmişi temizlendi.");
        }

        // ─────────────────────────────────────────────────────────────
        // Proje bağlantısı (GitHub)
        // ─────────────────────────────────────────────────────────────
        private const string GITHUB_REPO_URL = "https://github.com/brsbllky/GlyphTV";

        private void OpenGithub_Click(object? sender, RoutedEventArgs e) => OpenExternalLink(GITHUB_REPO_URL);

        private void OpenExternalLink(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogError("OpenExternalLink", ex);
                ShowToast("Bağlantı açılamadı.");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Özel başlık çubuğu kontrolleri
        // ─────────────────────────────────────────────────────────────
        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // DÜZELTME (arama kutusuna tıklamak pencereyi sürüklüyordu):
            // Title bar üzerindeki HERHANGİ bir sol tık (butonlar hariç,
            // onlar olayı zaten kendi işliyor) BeginMoveDrag tetikliyordu.
            // SearchBox bu bar'ın içinde olduğundan, kullanıcı arama
            // kutusuna odaklanmak için tıkladığında bu tıklama olayı
            // (TextBox varsayılan olarak PointerPressed'i handled
            // işaretlemediğinden) yukarı, bu handler'a kabarcıklanıyor ve
            // pencere sürükleme moduna giriyordu — arama kutusu görünüşte
            // "tıklanamaz" hale geliyordu. Tıklamanın kaynağı SearchBox'ın
            // görsel ağacının bir parçasıysa (kutunun içi veya üzerindeki
            // herhangi bir alt eleman) sürükleme başlatılmaz.
            if (e.Source is Visual sourceVisual && SearchBox.IsVisualAncestorOf(sourceVisual))
                return;

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

        private void MaximizeWindow_Click(object? sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == Avalonia.Controls.WindowState.Maximized
                ? Avalonia.Controls.WindowState.Normal
                : Avalonia.Controls.WindowState.Maximized;

            ApplyGridColumnsRecalcWithRetries();
        }
    }
}
