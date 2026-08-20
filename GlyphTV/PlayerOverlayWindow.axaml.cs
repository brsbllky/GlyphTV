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
        // Üst bar pill butonları
        // ─────────────────────────────────────────────────────────────
        private void BtnAudioTrack_Click(object? sender, PointerPressedEventArgs e) => _owner.BtnAudioTrack_Click(sender, e);
        private void BtnChannelList_Click(object? sender, PointerPressedEventArgs e) => _owner.BtnChannelList_Click(sender, e);
        private void BtnAspectRatio_Click(object? sender, PointerPressedEventArgs e) => _owner.BtnAspectRatio_Click(sender, e);
        private void BtnSubtitle_Click(object? sender, PointerPressedEventArgs e) => _owner.BtnSubtitle_Click(sender, e);

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

        // ─────────────────────────────────────────────────────────────
        // YENİ: mpv Ayarları popup'ı başlığındaki "Sıfırla" butonu.
        // ─────────────────────────────────────────────────────────
        private void MpvResetSettings_Click(object? sender, PointerPressedEventArgs e) => _owner.MpvResetSettings_Click(sender, e);
    }
}
