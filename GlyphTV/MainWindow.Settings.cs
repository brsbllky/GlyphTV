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
            _engine?.SetDeinterlace(_appSettings.RemoveInterlacing);
            ShowToast(_appSettings.RemoveInterlacing
                ? "İnterlacing kaldırma aktif."
                : "İnterlacing kaldırma kapatıldı.");
        }

        private void UpdateInterlaceToggleState()
        {
            // DÜZELTME: bkz. UpdateHwDecodeItemsActiveState'teki aynı notun
            // açıklaması — Settings-sekmesi kontrolü kaldırıldı, sadece
            // popup'taki kopya güncelleniyor.
            SetSelectListItemActive(PopupInterlaceItem, PopupInterlaceCheck, _appSettings.RemoveInterlacing);
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
        // YENİ: İnce görüntü ayarları (Parlaklık/Kontrast/Doygunluk/Gama)
        // — mpv Ayarları popup'ındaki 4 slider.
        // Aralık -100..100, varsayılan 0. Değer değiştikçe ANINDA uygulanır.
        // ─────────────────────────────────────────────────────────────
        internal void MpvEqBrightness_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            int value = (int)e.NewValue;
            _appSettings.Brightness = value;
            if (MpvEqBrightnessText != null) MpvEqBrightnessText.Text = value.ToString();
            if (_engine is GlyphTV.PlayerEngines.MpvPlayerEngine mpvEngine) mpvEngine.SetBrightness(value);
        }

        internal void MpvEqContrast_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            int value = (int)e.NewValue;
            _appSettings.Contrast = value;
            if (MpvEqContrastText != null) MpvEqContrastText.Text = value.ToString();
            if (_engine is GlyphTV.PlayerEngines.MpvPlayerEngine mpvEngine) mpvEngine.SetContrast(value);
        }

        internal void MpvEqSaturation_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            int value = (int)e.NewValue;
            _appSettings.Saturation = value;
            if (MpvEqSaturationText != null) MpvEqSaturationText.Text = value.ToString();
            if (_engine is GlyphTV.PlayerEngines.MpvPlayerEngine mpvEngine) mpvEngine.SetSaturation(value);
        }

        internal void MpvEqGamma_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            int value = (int)e.NewValue;
            _appSettings.Gamma = value;
            if (MpvEqGammaText != null) MpvEqGammaText.Text = value.ToString();
            if (_engine is GlyphTV.PlayerEngines.MpvPlayerEngine mpvEngine) mpvEngine.SetGamma(value);
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
        }

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
            // YENİ: popup'a eklenen Donanım Çözümlemesi / İnterlacing
            // kopya kontrollerinin ✓ işaretleri ve "Aktif: ..." durum
            // metni her açılışta güncel olsun.
            UpdateHwDecodeItemsActiveState();
            UpdateInterlaceToggleState();
            UpdateMpvPopupStatusText();
            MpvSettingsPopup.IsVisible = true;
        }

        internal void CloseMpvSettingsPopup_Click(object? sender, RoutedEventArgs e) =>
            MpvSettingsPopup.IsVisible = false;

        // ─────────────────────────────────────────────────────────────
        // YENİ: Popup başlığının altındaki "Aktif: ..." durum satırı —
        // o an geçerli donanım çözümleme modunu gösterir (bkz. görsel
        // referans). HDR/ölçekleme/interlacing değişikliklerinde de
        // görünürlük açısından bir farkı olmasa da, ileride buraya başka
        // bilgi eklenmek istenirse tek bir yerden güncellenebilsin diye
        // ayrı bir metoda alındı.
        // ─────────────────────────────────────────────────────────────
        private void UpdateMpvPopupStatusText()
        {
            if (MpvPopupStatusText != null)
                MpvPopupStatusText.Text = $"Aktif: {_appSettings.HwDecodeMode}";
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: mpv Ayarları popup'ındaki "Sıfırla" butonu — SADECE bu
        // popup'taki görüntü/performans ayarlarını (Parlaklık/Kontrast/
        // Doygunluk/Gama, HDR ton eşleme/hedef tepe parlaklığı, işleme
        // kalitesi, donanım çözümlemesi, interlacing) varsayılana
        // döndürür. Ayarlar sekmesindeki "Sıfırla" (ResetApp_Click) ile
        // KARIŞTIRILMAMALI — o TÜM uygulamayı (kaynaklar, geçmiş, tema
        // vb.) sıfırlar; bu sadece oynatıcının görüntü ayarlarını.
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
            _appSettings.HdrToneMapping = "auto";
            _appSettings.HdrTargetPeak = "auto";
            _appSettings.ScalingQuality = "default";
            _appSettings.HwDecodeMode = "auto";
            _appSettings.RemoveInterlacing = false;
            SaveAppSettings();

            InitializeMpvEqSliderValues();
            UpdateHdrToneMappingItemsActiveState();
            UpdateHdrTargetPeakItemsActiveState();
            UpdateScalingQualityButtonsActiveState();
            UpdateHwDecodeItemsActiveState();
            UpdateInterlaceToggleState();
            UpdateMpvPopupStatusText();

            if (_engine is GlyphTV.PlayerEngines.MpvPlayerEngine mpvEngine)
            {
                mpvEngine.SetBrightness(0);
                mpvEngine.SetContrast(0);
                mpvEngine.SetSaturation(0);
                mpvEngine.SetGamma(0);
                mpvEngine.SetHdrToneMapping("auto");
                mpvEngine.SetHdrTargetPeak("auto");
                mpvEngine.SetScalingQuality("default");
            }
            _engine?.SetHardwareDecoding("auto");
            _engine?.SetDeinterlace(false);

            ShowToast("mpv görüntü ayarları varsayılana döndürüldü.");
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: ⚙️ butonu SADECE mpv motoru aktifken görünür olmalı (VLC'de
        // bu düzeyde ayrıntılı görüntü/HDR kontrolü yok). MainWindow.
        // Player.cs → ConfigurePlayerUIForContentType (her PlayChannel'da)
        // ve SetPlayerEngine (motor değiştiğinde) tarafından çağrılır.
        // ─────────────────────────────────────────────────────────────
        internal void UpdateMpvSettingsButtonVisibility()
        {
            try
            {
                bool isMpv = _appSettings.PlayerEngine == "Mpv";
                if (MpvSettingsBtn != null) MpvSettingsBtn.IsVisible = isMpv;
                if (!isMpv && MpvSettingsPopup != null) MpvSettingsPopup.IsVisible = false;
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
            if (SettingsTabAbout != null) SettingsTabAbout.IsVisible = tabKey == "About";

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
        // Proje ve topluluk bağlantıları (GitHub / Telegram)
        // ─────────────────────────────────────────────────────────────
        private const string GITHUB_REPO_URL = "https://github.com/brsbllky/GlyphTV";
        private const string TELEGRAM_GROUP_URL = "https://t.me/glyphtv";

        private void OpenGithub_Click(object? sender, RoutedEventArgs e) => OpenExternalLink(GITHUB_REPO_URL);

        private void OpenTelegram_Click(object? sender, RoutedEventArgs e) => OpenExternalLink(TELEGRAM_GROUP_URL);

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
