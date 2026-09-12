// ============================================================
// PlayerOverlayWindow.axaml.cs
//
// Bu pencerenin TEK görevi: içindeki kontrolleri (üst/alt bar, popup'lar,
// rozetler) MainWindow'daki video alanının üzerine, video her zaman önde
// kalsa bile GÖRÜNÜR ve TIKLANABİLİR şekilde göstermek (bkz. XAML dosyasının
// başındaki "airspace" açıklaması).
//
// Bu pencere KENDİ İŞ MANTIĞINI TAŞIMAZ — her XAML olayı (Click/
// PointerPressed/ValueChanged/PointerEntered/PointerExited/KeyDown), aynı
// isimli, MainWindow.Player.cs içinde artık "internal" olan bir metoda
// birebir yönlendirilir. Böylece PopulatePlayerChannelList/PopulateAudioTracks/
// PopulateSubtitles/PopulateAspectRatioOptions gibi metodların oluşturduğu
// tüm C# tabanlı alt kontroller (Border/TextBlock vb.) ve onların lambda
// event handler'ları da DEĞİŞİKLİK GEREKTİRMEDEN çalışmaya devam eder —
// çünkü bu lambda'lar zaten MainWindow'un kendi metodlarının içinde tanımlı
// ve "this" (MainWindow) üzerinden kapanış (closure) yapıyorlar; hangi
// pencerenin görsel ağacına ekledikleri (artık bu pencere) onlar için önemli
// değildir.
// ============================================================

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace GlyphTV
{
    public partial class PlayerOverlayWindow : Window
    {
        private readonly MainWindow _owner;

        // DÜZELTME (derleme uyarısı — zararsızdı ama giderildi): "AVLN3001:
        // XAML resource ... won't be reachable via runtime loader, as no
        // public constructor was found". Avalonia'nın "avares://" XAML
        // çalışma zamanı yükleyicisi (ör. tasarım zamanı önizleme araçları),
        // bir Window türünü örnekleyebilmek için PUBLIC PARAMETRESİZ bir
        // constructor arar. Normal çalışma zamanında bu yol HİÇ
        // kullanılmıyor — MainWindow her zaman aşağıdaki parametreli
        // constructor'ı (owner ile) çağırıyor; bu parametresiz olan sadece
        // derleyiciyi/araçları memnun etmek için var ve _owner'ı bilinçli
        // olarak null! ile geçirir (gerçek kullanımda asla tetiklenmez).
        public PlayerOverlayWindow() : this(null!) { }

        public PlayerOverlayWindow(MainWindow owner)
        {
            _owner = owner;
            // NOT: InitializeComponent() burada elle tanımlanmıyor — Avalonia
            // XAML derleyicisi (XamlIl), x:Class="GlyphTV.PlayerOverlayWindow"
            // eşleşmesine göre bu metodu otomatik üretir (bkz. MainWindow.axaml.cs
            // → constructor'ın da InitializeComponent()'i hiç tanımlamadan
            // aynen çağırdığı aynı desen).
            InitializeComponent();

            this.PositionChanged += (s, e) =>
            {
                if (_owner != null && _owner._isPipMode && !_owner._isSyncingPipPosition)
                {
                    if (this.Position.X < -10000 || this.Position.Y < -10000) return;
                    _owner._isSyncingPipPosition = true;
                    try
                    {
                        if (_owner.Position != this.Position)
                            _owner.Position = this.Position;
                    }
                    finally
                    {
                        _owner._isSyncingPipPosition = false;
                    }
                }
            };

            this.PropertyChanged += (s, e) =>
            {
                if (e.Property == Window.BoundsProperty && _owner != null && _owner._isPipMode && !_owner._isSyncingPipPosition)
                {
                    var b = this.Bounds;
                    if (b.Width >= 320 && b.Height >= 180)
                    {
                        _owner._isSyncingPipPosition = true;
                        try
                        {
                            if (Math.Abs(_owner.Width - b.Width) > 1.0) _owner.Width = b.Width;
                            if (Math.Abs(_owner.Height - b.Height) > 1.0) _owner.Height = b.Height;
                            if (this.Position.X > -10000 && this.Position.Y > -10000 && _owner.Position != this.Position)
                                _owner.Position = this.Position;
                        }
                        finally
                        {
                            _owner._isSyncingPipPosition = false;
                        }
                    }
                }
            };

            ApplyLanguage(Localization.CurrentLanguage);
        }

        public void ApplyLanguage(string lang)
        {
            try
            {
                if (PlayerLiveBadgeText != null) PlayerLiveBadgeText.Text = Localization.Get("Player_Live");
                if (PlayerAudioTrackBtnText != null) PlayerAudioTrackBtnText.Text = Localization.Get("Player_AudioTrack");
                if (PlayerChannelsBtnText != null) PlayerChannelsBtnText.Text = Localization.Get("Player_Channels");
                if (PlayerSubtitlesBtnText != null) PlayerSubtitlesBtnText.Text = Localization.Get("Player_Subtitles");
                if (PlayerEnhancedBtnText != null) PlayerEnhancedBtnText.Text = Localization.Get("Player_Enhanced");

                // Tooltips
                if (PrevChannelBtn != null) ToolTip.SetTip(PrevChannelBtn, lang == "en" ? "Previous Channel" : "Önceki Kanal");
                if (SkipBackBtnBorder != null) ToolTip.SetTip(SkipBackBtnBorder, lang == "en" ? "10 seconds back" : "10 saniye geri");
                if (NextChannelBtn != null) ToolTip.SetTip(NextChannelBtn, lang == "en" ? "Next Channel" : "Sonraki Kanal");
                if (SkipForwardBtnBorder != null) ToolTip.SetTip(SkipForwardBtnBorder, lang == "en" ? "10 seconds forward" : "10 saniye ileri");
                if (NextEpisodeBtn != null) ToolTip.SetTip(NextEpisodeBtn, lang == "en" ? "Next Episode" : "Sonraki Bölüm");
                if (SpeedBtn != null) ToolTip.SetTip(SpeedBtn, Localization.Get("Player_SpeedTooltip"));
                if (MuteBtn != null) ToolTip.SetTip(MuteBtn, Localization.Get("Player_MuteTooltip"));
                if (PipBtn != null) ToolTip.SetTip(PipBtn, Localization.Get("Player_PipTooltip"));
                if (FullscreenBtn != null) ToolTip.SetTip(FullscreenBtn, Localization.Get("Player_FullscreenTooltip"));
                if (ClosePlayerBorder != null) ToolTip.SetTip(ClosePlayerBorder, Localization.Get("Btn_Close"));

                // Popups
                if (PlayerPopupChannelsTitle != null) PlayerPopupChannelsTitle.Text = Localization.Get("Player_Channels");
                if (PlayerPopupAudioTrackTitle != null) PlayerPopupAudioTrackTitle.Text = Localization.Get("Player_AudioTrack");
                if (PlayerPopupAudioSyncTitle != null) PlayerPopupAudioSyncTitle.Text = Localization.Get("Player_AudioSync");
                if (PlayerPopupAudioSyncResetText != null) PlayerPopupAudioSyncResetText.Text = Localization.Get("Player_AudioSyncReset");

                if (PlayerPopupSubtitlesTitle != null) PlayerPopupSubtitlesTitle.Text = lang == "en" ? "Subtitles" : "Altyazı";
                if (PlayerPopupSubSyncTitle != null) PlayerPopupSubSyncTitle.Text = Localization.Get("Player_SubtitleSync");
                if (PlayerPopupSubSyncResetText != null) PlayerPopupSubSyncResetText.Text = Localization.Get("Player_SubtitleSyncReset");
                if (PlayerPopupVodAudioTrackTitle != null) PlayerPopupVodAudioTrackTitle.Text = Localization.Get("Player_AudioTrack");

                if (PlayerPopupAspectRatioTitle != null) PlayerPopupAspectRatioTitle.Text = Localization.Get("Player_AspectRatio");

                if (PlayerPopupEnhancedTitle != null) PlayerPopupEnhancedTitle.Text = Localization.Get("Player_Enhanced_Title");
                if (PlayerPopupMpvResetText != null) PlayerPopupMpvResetText.Text = Localization.Get("Player_Enhanced_Reset");
                if (PlayerPopupShaderTitle != null) PlayerPopupShaderTitle.Text = Localization.Get("Player_ShaderTitle");
                if (PlayerPopupShaderOffText != null) PlayerPopupShaderOffText.Text = Localization.Get("Player_ShaderOff");
                if (PlayerPopupShaderDesc != null) PlayerPopupShaderDesc.Text = Localization.Get("Player_ShaderDesc");
                if (PlayerPopupZappingTitle != null) PlayerPopupZappingTitle.Text = Localization.Get("Player_FastZapTitle");
                if (PlayerPopupZappingFastText != null) PlayerPopupZappingFastText.Text = Localization.Get("Player_FastZapUltra");
                if (PlayerPopupZappingStableText != null) PlayerPopupZappingStableText.Text = Localization.Get("Player_FastZapStd");
                if (PlayerPopupZappingDesc != null) PlayerPopupZappingDesc.Text = Localization.Get("Player_FastZapDesc");
                if (PlayerPopupAudioEnhanceTitle != null) PlayerPopupAudioEnhanceTitle.Text = Localization.Get("Player_AudioEnhTitle");
                if (PlayerPopupAudioEnhanceOffText != null) PlayerPopupAudioEnhanceOffText.Text = lang == "en" ? "Standard" : "Standart";
                if (PlayerPopupAudioEnhanceNightText != null) PlayerPopupAudioEnhanceNightText.Text = lang == "en" ? "Night Mode" : "Gece Modu";
                if (PlayerPopupAudioEnhanceDesc != null) PlayerPopupAudioEnhanceDesc.Text = lang == "en" ? "Balances volume spikes between channels/ads or clarifies dialogue for night viewing." : "Kanal/reklam arası ani ses patlamalarını dengeler veya gece izlemede diyalogları netleştirir.";

                if (PlayerPopupVideoTitle != null) PlayerPopupVideoTitle.Text = lang == "en" ? "VIDEO" : "VİDEO";
                if (PlayerPopupPictureModeTitle != null) PlayerPopupPictureModeTitle.Text = Localization.Get("Player_PresetTitle");
                if (PlayerPopupPresetNaturalText != null) PlayerPopupPresetNaturalText.Text = Localization.Get("Player_PresetNatural");
                if (PlayerPopupPresetVividText != null) PlayerPopupPresetVividText.Text = Localization.Get("Player_PresetVivid");
                if (PlayerPopupPresetSportsText != null) PlayerPopupPresetSportsText.Text = Localization.Get("Player_PresetSports");
                if (PlayerPopupPresetCinemaText != null) PlayerPopupPresetCinemaText.Text = Localization.Get("Player_PresetCinema");

                if (PlayerPopupBrightnessTitle != null) PlayerPopupBrightnessTitle.Text = Localization.Get("Player_Brightness");
                if (PlayerPopupContrastTitle != null) PlayerPopupContrastTitle.Text = Localization.Get("Player_Contrast");
                if (PlayerPopupSaturationTitle != null) PlayerPopupSaturationTitle.Text = Localization.Get("Player_Saturation");
                if (PlayerPopupGammaTitle != null) PlayerPopupGammaTitle.Text = Localization.Get("Player_Gamma");

                if (PlayerPopupHdrToneMapTitle != null) PlayerPopupHdrToneMapTitle.Text = lang == "en" ? "HDR TONE MAPPING" : "HDR TON EŞLEME";
                if (PlayerPopupHdrAutoText != null) PlayerPopupHdrAutoText.Text = Localization.Get("Player_AspectAuto");
                if (PlayerPopupHdrTargetPeakTitle != null) PlayerPopupHdrTargetPeakTitle.Text = lang == "en" ? "TARGET DISPLAY PEAK BRIGHTNESS" : "HEDEF EKRAN TEPE PARLAKLIĞI";
                if (PlayerPopupHdrPeakAutoText != null) PlayerPopupHdrPeakAutoText.Text = Localization.Get("Player_AspectAuto");

                if (PlayerPopupQualityTitle != null) PlayerPopupQualityTitle.Text = lang == "en" ? "RENDERING QUALITY" : "İŞLEME KALİTESİ";
                if (ScalingQualityDefaultText != null) ScalingQualityDefaultText.Text = lang == "en" ? "Default" : "Varsayılan";
                if (ScalingQualityHighText != null) ScalingQualityHighText.Text = lang == "en" ? "Quality (sharper scaling)" : "Kalite (daha keskin ölçekleme)";
                if (PlayerPopupQualityDesc != null) PlayerPopupQualityDesc.Text = lang == "en" ? "Tone mapping affects HDR content on SDR displays. Quality scaling uses more GPU." : "Ton eşleme, SDR ekranlarda HDR içeriği etkiler. Kalite ölçekleme daha fazla GPU kullanır.";

                if (PlayerPopupHwDecodeTitle != null) PlayerPopupHwDecodeTitle.Text = Localization.Get("Player_HwTitle");
                if (PlayerPopupHwDecodeAutoText != null) PlayerPopupHwDecodeAutoText.Text = lang == "en" ? "Auto / Recommended" : "Otomatik / Önerilen";
                if (PlayerPopupHwDecodeOffText != null) PlayerPopupHwDecodeOffText.Text = Localization.Get("Player_DeintOff");
                if (PlayerPopupDeinterlaceTitle != null) PlayerPopupDeinterlaceTitle.Text = Localization.Get("Player_DeintTitle");
                if (PlayerPopupDeinterlaceDesc != null) PlayerPopupDeinterlaceDesc.Text = Localization.Get("Player_DeintDesc");
                if (PlayerPopupDeinterlaceAlgoTitle != null) PlayerPopupDeinterlaceAlgoTitle.Text = lang == "en" ? "DEINTERLACE ALGORITHM" : "DEINTERLACE ALGORİTMASI";
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // Klavye — MainWindow ShowActivated="False" ile odağı MainWindow'da
        // tutmaya çalışsa da (bkz. XAML), OS bazen yine de bu pencereye
        // odak verebilir. Güvenlik amaçlı: bu pencerede de aynı kısayollar
        // (Space/F/Esc/M/Ok tuşları) çalışsın diye MainWindow.Window_KeyDown
        // buraya da yönlendirilir.
        // ─────────────────────────────────────────────────────────────
        private void Window_KeyDown(object? sender, KeyEventArgs e) => _owner.Window_KeyDown(sender, e);

        // ─────────────────────────────────────────────────────────────
        // Kök Grid — hover/çift tık (tam ekran) ve popup dışına tıklama
        // ─────────────────────────────────────────────────────────────
        private void PlayerContainer_PointerMoved(object? sender, PointerEventArgs e) => _owner.PlayerContainer_PointerMoved(sender, e);
        private void PlayerOverlay_PointerPressed(object? sender, PointerPressedEventArgs e) => _owner.PlayerOverlay_PointerPressed(sender, e);

        // ─────────────────────────────────────────────────────────────
        // Üst bar pill butonları & Altyazı Senkronizasyonu
        // ─────────────────────────────────────────────────────────────
        private void BtnAudioTrack_Click(object? sender, PointerPressedEventArgs e) => _owner.BtnAudioTrack_Click(sender, e);
        private void BtnChannelList_Click(object? sender, PointerPressedEventArgs e) => _owner.BtnChannelList_Click(sender, e);
        private void BtnAspectRatio_Click(object? sender, PointerPressedEventArgs e) => _owner.BtnAspectRatio_Click(sender, e);
        private void BtnSubtitle_Click(object? sender, PointerPressedEventArgs e) => _owner.BtnSubtitle_Click(sender, e);

        private void SubDelayMinus500_Click(object? sender, PointerPressedEventArgs e) => _owner.SubDelayMinus500_Click(sender, e);
        private void SubDelayMinus100_Click(object? sender, PointerPressedEventArgs e) => _owner.SubDelayMinus100_Click(sender, e);
        private void SubDelayMinus50_Click(object? sender, PointerPressedEventArgs e) => _owner.SubDelayMinus50_Click(sender, e);
        private void SubDelayPlus50_Click(object? sender, PointerPressedEventArgs e) => _owner.SubDelayPlus50_Click(sender, e);
        private void SubDelayPlus100_Click(object? sender, PointerPressedEventArgs e) => _owner.SubDelayPlus100_Click(sender, e);
        private void SubDelayPlus500_Click(object? sender, PointerPressedEventArgs e) => _owner.SubDelayPlus500_Click(sender, e);
        private void SubDelayReset_Click(object? sender, PointerPressedEventArgs e) => _owner.SubDelayReset_Click(sender, e);

        private void AudioDelayMinus500_Click(object? sender, PointerPressedEventArgs e) => _owner.AudioDelayMinus500_Click(sender, e);
        private void AudioDelayMinus100_Click(object? sender, PointerPressedEventArgs e) => _owner.AudioDelayMinus100_Click(sender, e);
        private void AudioDelayMinus50_Click(object? sender, PointerPressedEventArgs e) => _owner.AudioDelayMinus50_Click(sender, e);
        private void AudioDelayPlus50_Click(object? sender, PointerPressedEventArgs e) => _owner.AudioDelayPlus50_Click(sender, e);
        private void AudioDelayPlus100_Click(object? sender, PointerPressedEventArgs e) => _owner.AudioDelayPlus100_Click(sender, e);
        private void AudioDelayPlus500_Click(object? sender, PointerPressedEventArgs e) => _owner.AudioDelayPlus500_Click(sender, e);
        private void AudioDelayReset_Click(object? sender, PointerPressedEventArgs e) => _owner.AudioDelayReset_Click(sender, e);

        // ─────────────────────────────────────────────────────────────
        // Alt bar kontroller
        // ─────────────────────────────────────────────────────────────
        private void PlayPause_Click(object? sender, PointerPressedEventArgs e) => _owner.PlayPause_Click(sender, e);
        private void Mute_Click(object? sender, PointerPressedEventArgs e) => _owner.Mute_Click(sender, e);
        private void SkipBack_Click(object? sender, PointerPressedEventArgs e) => _owner.SkipBack_Click(sender, e);
        private void SkipForward_Click(object? sender, PointerPressedEventArgs e) => _owner.SkipForward_Click(sender, e);
        private void PrevChannel_Click(object? sender, PointerPressedEventArgs e) => _owner.PrevChannel_Click(sender, e);
        private void NextChannel_Click(object? sender, PointerPressedEventArgs e) => _owner.NextChannel_Click(sender, e);
        private void NextEpisode_Click(object? sender, PointerPressedEventArgs e) => _owner.NextEpisode_Click(sender, e);
        private void Speed_Click(object? sender, PointerPressedEventArgs e) => _owner.Speed_Click(sender, e);
        private void Pip_Click(object? sender, PointerPressedEventArgs e) => _owner.Pip_Click(sender, e);
        private void Fullscreen_Click(object? sender, PointerPressedEventArgs e) => _owner.Fullscreen_Click(sender, e);

        private void TimeSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e) => _owner.TimeSlider_ValueChanged(sender, e);
        private void VolumeSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e) => _owner.VolumeSlider_ValueChanged(sender, e);

        private void ClosePlayer_Click(object? sender, PointerPressedEventArgs e) => _owner.ClosePlayer_Click(sender, e);
        private void CloseBtn_PointerEntered(object? sender, PointerEventArgs e) => _owner.CloseBtn_PointerEntered(sender, e);
        private void CloseBtn_PointerExited(object? sender, PointerEventArgs e) => _owner.CloseBtn_PointerExited(sender, e);

        // ─────────────────────────────────────────────────────────────
        // Kanal listesi popup'ı kapama butonu (Button.Click)
        // ─────────────────────────────────────────────────────────────
        private void CloseChannelList_Click(object? sender, RoutedEventArgs e) => _owner.CloseChannelList_Click(sender, e);

        // ─────────────────────────────────────────────────────────────
        // YENİ: mpv Ayarları (⚙️) popup'ı — gear butonu, kapatma butonu,
        // HDR ton eşleme/hedef tepe parlaklığı/işleme kalitesi liste
        // öğeleri ve 4 ince görüntü ayarı slider'ı. Mantığın tamamı
        // (SetHdrToneMapping vb.) MainWindow.Settings.cs'te tanımlı;
        // burada sadece isim eşleşmesiyle yönlendirme yapılıyor.
        // ─────────────────────────────────────────────────────────────
        private void MpvSettingsBtn_Click(object? sender, PointerPressedEventArgs e) => _owner.MpvSettingsBtn_Click(sender, e);
        private void CloseMpvSettingsPopup_Click(object? sender, RoutedEventArgs e) => _owner.CloseMpvSettingsPopup_Click(sender, e);
        private void HdrToneMappingItem_PointerPressed(object? sender, PointerPressedEventArgs e) => _owner.HdrToneMappingItem_PointerPressed(sender, e);
        private void HdrTargetPeakItem_PointerPressed(object? sender, PointerPressedEventArgs e) => _owner.HdrTargetPeakItem_PointerPressed(sender, e);
        private void ScalingQualityDefault_Click(object? sender, RoutedEventArgs e) => _owner.ScalingQualityDefault_Click(sender, e);
        private void ScalingQualityHigh_Click(object? sender, RoutedEventArgs e) => _owner.ScalingQualityHigh_Click(sender, e);

        private void ShaderItem_PointerPressed(object? sender, PointerPressedEventArgs e) => _owner.ShaderItem_PointerPressed(sender, e);
        private void ZappingItem_PointerPressed(object? sender, PointerPressedEventArgs e) => _owner.ZappingItem_PointerPressed(sender, e);
        private void AudioEnhanceItem_PointerPressed(object? sender, PointerPressedEventArgs e) => _owner.AudioEnhanceItem_PointerPressed(sender, e);

        private void MpvEqBrightness_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e) => _owner.MpvEqBrightness_ValueChanged(sender, e);
        private void MpvEqContrast_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e) => _owner.MpvEqContrast_ValueChanged(sender, e);
        private void MpvEqSaturation_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e) => _owner.MpvEqSaturation_ValueChanged(sender, e);
        private void MpvEqGamma_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e) => _owner.MpvEqGamma_ValueChanged(sender, e);

        // ─────────────────────────────────────────────────────────────
        // YENİ: mpv Ayarları popup'ına eklenen Donanım Çözümlemesi /
        // İnterlacing kopya kontrolleri (bkz. XAML → "PopupHwDecode..."/
        // "PopupInterlaceItem"). Mantığın tamamı MainWindow.Settings.cs'te
        // (artık internal) tanımlı; burada sadece isim eşleşmesiyle
        // yönlendirme yapılıyor — Ayarlar sekmesindeki orijinal kontrollerle
        // AYNI metodları çağırırlar, bu yüzden iki yer de her zaman
        // birbiriyle senkron kalır.
        // ─────────────────────────────────────────────────────────────
        private void HwDecodeItem_PointerPressed(object? sender, PointerPressedEventArgs e) => _owner.HwDecodeItem_PointerPressed(sender, e);
        private void InterlaceToggle_PointerPressed(object? sender, PointerPressedEventArgs e) => _owner.InterlaceToggle_PointerPressed(sender, e);
        private void DeinterlaceModeItem_PointerPressed(object? sender, PointerPressedEventArgs e) => _owner.DeinterlaceModeItem_PointerPressed(sender, e);

        private void PresetNatural_Click(object? sender, PointerPressedEventArgs e) => _owner.PresetNatural_Click(sender, e);
        private void PresetVivid_Click(object? sender, PointerPressedEventArgs e) => _owner.PresetVivid_Click(sender, e);
        private void PresetSports_Click(object? sender, PointerPressedEventArgs e) => _owner.PresetSports_Click(sender, e);
        private void PresetCinema_Click(object? sender, PointerPressedEventArgs e) => _owner.PresetCinema_Click(sender, e);

        // ─────────────────────────────────────────────────────────────
        // YENİ: mpv Ayarları popup'ı başlığındaki "Sıfırla" butonu.
        // ─────────────────────────────────────────────────────────
        private void MpvResetSettings_Click(object? sender, PointerPressedEventArgs e) => _owner.MpvResetSettings_Click(sender, e);

        // ─────────────────────────────────────────────────────────────
        // PiP Modu Kenar / Köşe Yeniden Boyutlandırma & Popup Tıklama Koruması
        // ─────────────────────────────────────────────────────────────
        private void ResizeEdge_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_owner != null && _owner._isPipMode && sender is Border b && b.Tag is string tag)
            {
                WindowEdge edge = tag switch
                {
                    "NW" => WindowEdge.NorthWest,
                    "N" => WindowEdge.North,
                    "NE" => WindowEdge.NorthEast,
                    "W" => WindowEdge.West,
                    "E" => WindowEdge.East,
                    "SW" => WindowEdge.SouthWest,
                    "S" => WindowEdge.South,
                    "SE" => WindowEdge.SouthEast,
                    _ => WindowEdge.SouthEast
                };
                this.BeginResizeDrag(edge, e);
                e.Handled = true;
            }
        }

        private void Popup_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
        }
    }
}
