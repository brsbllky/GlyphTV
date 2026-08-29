// ============================================================
// MainWindow.PlayerOverlay.cs
//
// KÖK SEBEP (VLC'de video oynuyor ama kontrol butonları/rozetler hiç
// görünmüyordu — mpv'de de aynı sorun oluşurdu): VideoView (VLC) ve
// MpvVideoHost (mpv) ikisi de video çıktısını GERÇEK bir native Win32 alt
// pencere (HWND) üzerinden gösterir. Windows'ta bir native alt pencere,
// aynı üst pencere (MainWindow) içindeki Avalonia'nın kendi çizdiği
// (Skia tabanlı, tek bir yüzeye render edilen) içeriğin HER ZAMAN önünde
// görüntülenir — bu, XAML'deki eleman sırası veya ZIndex ile
// DEĞİŞTİRİLEMEYEN bir Windows pencere kompozisyon kısıtıdır ("airspace"
// sorunu olarak bilinir; WPF/WinForms'ta native video kontrolleri için de
// onlarca yıldır aynı sorun yaşanır ve orada da standart çözüm budur).
//
// ÇÖZÜM: Oynatıcı kontrol paneli (üst bar, alt bar, kanal listesi/ses/
// altyazı/enboy popup'ları, medya bilgi rozetleri) MainWindow.axaml'den
// çıkarılıp PlayerOverlayWindow.axaml adında AYRI, şeffaf, çerçevesiz,
// her zaman üstte kalan bir native pencereye taşındı. Bu dosya o pencerenin
// MainWindow tarafındaki yaşam döngüsünü (oluştur/göster/gizle/senkronize
// et/kapat) ve MainWindow.Player.cs/Series.cs/VodInfo.cs/Epg.cs'nin hâlâ
// eskisi gibi "PlayerTopBar", "ChannelListPanel" vb. isimlerle çalışabilmesi
// için gereken YÖNLENDİRME (forwarding) property'lerini içerir — böylece o
// dosyalarda (bu accessibility değişiklikleri hariç) tek satır mantık
// değişikliği yapılmadı.
// ============================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow : Window
    {
        private PlayerOverlayWindow? _playerOverlay;

        // YENİ (bkz. EvaluateOverlayActivation): MainWindow ve overlay
        // penceresinin aktiflik durumları ARTIK AYRI AYRI izleniyor —
        // "uygulamadan çıkıldı" durumu sadece ikisi de aktif değilken kabul
        // edilir.
        private bool _mainWindowActive = true;
        private bool _overlayActive = false;

        // EPG/Yayın Akışı gibi bir modal açıkken oynatıcı overlay penceresinin
        // yeniden gösterilmesini engeller (bkz. EvaluateOverlayActivation).
        private bool _modalOpen = false;

        // ─────────────────────────────────────────────────────────────
        // Kurulum — constructor'dan (MainWindow.axaml.cs) bir kez çağrılır.
        // ─────────────────────────────────────────────────────────────
        //
        // DÜZELTME (NullReferenceException: "_playerOverlay, null idi." —
        // içerik oynatmaya çalışınca çöküyordu): Bu pencere önceden SADECE
        // PlayerContainer.Height ilk kez 0'dan farklı bir değere geçtiğinde
        // (aşağıdaki PropertyChanged aboneliğinde) "lazy" olarak
        // oluşturuluyordu. Ama StartPlayingChannel/PlaySeries_Click/
        // PlaySeriesResume_Click/VodInfoPlay_Click gibi TÜM çağrı
        // noktalarının hepsinde "PlayerTitleText.Text = ..." satırı,
        // "PlayerContainer.Height = 450;" satırından ÖNCE çalışıyor —
        // yani forwarding property (PlayerTitleText → _playerOverlay!.
        // PlayerTitleText) tetiklendiğinde _playerOverlay HÂLÂ null'du.
        //
        // Artık overlay penceresi burada, MainWindow constructor'ı
        // çalışırken HEMEN (ama gizli/Show() çağrılmadan) oluşturuluyor.
        // Böylece _playerOverlay, MainWindow'un ömrü boyunca ASLA null
        // olmuyor — hangi kod satırının PlayerContainer.Height'ı ne zaman
        // değiştirdiği artık hiç önemli değil. PlayerContainer.Height
        // PropertyChanged aboneliği artık SADECE göster/gizle/senkronize
        // et için kullanılıyor, oluşturma için değil.
        // ─────────────────────────────────────────────────────────────
        private void InitPlayerOverlay()
        {
            _playerOverlay = new PlayerOverlayWindow(this);

            PlayerContainer.PropertyChanged += (s, e) =>
            {
                if (e.Property != Layoutable.HeightProperty) return;
                double h = PlayerContainer.Height;
                bool shouldShow = double.IsNaN(h) || h > 0;
                if (shouldShow) ShowPlayerOverlay();
                else HidePlayerOverlay();
            };

            // Pencere sürüklenerek taşındığında veya yeniden boyutlandırıldığında senkronize et
            this.PositionChanged += (s, e) => SyncPlayerOverlayBounds();
            this.PropertyChanged += (s, e) =>
            {
                if (e.Property == Window.BoundsProperty || e.Property == Window.ClientSizeProperty)
                    SyncPlayerOverlayBounds();
            };
            PlayerContainer.PropertyChanged += (s, e) =>
            {
                if (e.Property == Layoutable.BoundsProperty || e.Property == Layoutable.WidthProperty || e.Property == Layoutable.HeightProperty)
                    SyncPlayerOverlayBounds();
            };

            // ─────────────────────────────────────────────────────────
            // DÜZELTME ("hayalet pencere" — GlyphTV'nin önüne başka bir
            // uygulama getirildiğinde oynatıcı kontrol paneli hâlâ en
            // üstte görünmeye devam ediyordu): PlayerOverlayWindow.axaml'de
            // önceden STATİK "Topmost=True" vardı — bu, overlay'i SADECE
            // MainWindow'un değil, SİSTEMDEKİ TÜM pencerelerin üzerinde
            // tutuyordu. Bu ilk düzeltmenin devamı ve NİHAİ hâli için
            // hemen aşağıdaki "DÜZELTME 2" yorumuna bakın — Topmost'u
            // dinamik açıp/kapatmak YETERSİZ kaldığından yerini Hide/Show
            // tabanlı bir yaklaşıma bıraktı.
            // ─────────────────────────────────────────────────────────
            // ─────────────────────────────────────────────────────────
            // DÜZELTME 2 ("hayalet pencere" — sorun kısmen devam ediyordu:
            // özellikle oynatıcı butonları görünür hâle geldiği anda başka
            // bir uygulamanın önüne geçiyordu, butonlar kaybolunca GlyphTV
            // tekrar öne geliyordu): Bir önceki turda Topmost DEĞERİNİ
            // dinamik olarak true/false yapıyorduk. Ancak Win32'de bir
            // pencereye Topmost=true ATAMAK (SetWindowPos + HWND_TOPMOST),
            // sadece "üstte kal" bayrağını açmakla kalmaz, o ANDA pencereyi
            // z-sırasının EN TEPESİNE TAŞIR — yani MainWindow her
            // "Activated" olduğunda (ki bu, GlyphTV'nin KENDİ overlay
            // penceresiyle etkileşim gibi GERÇEKTEN başka bir uygulamaya
            // geçiş olmayan durumlarda da tetiklenebiliyor) overlay
            // yeniden en tepeye zıplayıp o an önde olan başka bir
            // uygulamanın üzerine biniyordu.
            //
            // Artık Topmost=true SADECE bir kez, oynatıcı ilk açıldığında
            // ayarlanıyor ve BİR DAHA DOKUNULMUYOR. "Başka uygulamaların
            // önünde kalmama" garantisi artık Topmost'u false'a çekerek
            // DEĞİL, MainWindow etkinliğini kaybettiğinde overlay
            // penceresini TAMAMEN GİZLEYEREK (Hide) sağlanıyor — gizli bir
            // pencere z-sırasından bağımsız olarak hiçbir şeyin önünde
            // görünemez. MainWindow etkinliğini geri kazandığında (kullanıcı
            // GERÇEKTEN GlyphTV'ye döndüğünde) overlay tekrar gösterilip
            // (Show) konumu yeniden senkronize edilir.
            // ─────────────────────────────────────────────────────────
            // ─────────────────────────────────────────────────────────
            // DÜZELTME (oynatıcı kontrol butonlarına tıklayınca panelin
            // KAYBOLMASI — "hayalet pencere" düzeltmesinin bir yan etkisi):
            // PlayerOverlayWindow "ShowActivated=False" ile gösteriliyor,
            // ama bu SADECE Show() çağrıldığı ANDA pencerenin odak
            // ALMAMASINI garanti eder — kullanıcı pencereye SONRADAN
            // (örn. bir butona) tıkladığında Windows bu pencereyi yine de
            // etkinleştirebilir. Bu olduğunda MainWindow "Deactivated"
            // oluyordu ve eski kod bunu "kullanıcı başka bir uygulamaya
            // geçti" sanıp overlay'i (dolayısıyla o an tıklanan butonun
            // kendisini) ANINDA gizletiyordu.
            //
            // Artık MainWindow'un yanında overlay penceresinin de kendi
            // Activated/Deactivated'ı izleniyor; "uygulamadan çıkıldı"
            // durumu SADECE ikisi de (MainWindow VE overlay) aktif
            // değilken kabul ediliyor — bkz. EvaluateOverlayActivation.
            // ─────────────────────────────────────────────────────────
            this.Activated += (s, e) =>
            {
                _mainWindowActive = true;
                EvaluateOverlayActivation();
            };
            this.Deactivated += (s, e) =>
            {
                _mainWindowActive = false;
                EvaluateOverlayActivation();
            };

            _playerOverlay.Activated += (s, e) =>
            {
                _overlayActive = true;
                EvaluateOverlayActivation();
            };
            _playerOverlay.Deactivated += (s, e) =>
            {
                _overlayActive = false;
                EvaluateOverlayActivation();
            };
        }

        // ─────────────────────────────────────────────────────────────
        // MainWindow VEYA overlay penceresinden biri aktifse "uygulama
        // içindeyiz" sayılır ve overlay görünür tutulur/gösterilir; ikisi
        // de aktif değilse (kullanıcı GERÇEKTEN başka bir uygulamaya
        // geçti) overlay gizlenir.
        //
        // Gizleme kararı küçük bir gecikmeyle (Dispatcher.Post) veriliyor:
        // MainWindow ↔ overlay arasındaki NORMAL geçişlerde (ör. overlay
        // üzerindeki bir butona tıklama) önce bir Deactivated, hemen
        // ardından bir Activated gelir — gecikme olmadan overlay bu ikisi
        // arasındaki anlık boşlukta gizlenip tekrar gösterilerek titrer
        // (ve tam o anda kullanıcının tıkladığı buton görsel olarak
        // "kaybolmuş" gibi görünür). Gösterme kararı ise HER ZAMAN anında
        // verilir — gecikme sadece gizleme tarafında gereklidir.
        // ─────────────────────────────────────────────────────────────
        private void EvaluateOverlayActivation()
        {
            if (_playerOverlay == null) return;

            // EPG/Yayın Akışı gibi bir modal açıkken overlay'i asla yeniden
            // gösterme — aksi hâlde modal açıldıktan sonra gelen bir
            // Activated olayı (bkz. HidePlayerLayerForModal) overlay'i
            // modalın ÜZERİNE çıkarırdı.
            if (_modalOpen) return;

            double h = PlayerContainer.Height;
            bool playerShouldBeOpen = double.IsNaN(h) || h > 0;
            if (!playerShouldBeOpen) return;

            // PiP modundayken kullanıcı başka bir uygulamaya odaklansa bile
            // video ve overlay'i her zaman görünür ve en üstte tut
            if (_isPipMode)
            {
                if (!_playerOverlay.IsVisible)
                    ShowPlayerOverlay();
                this.Topmost = true;
                _playerOverlay.Topmost = false;
                _playerOverlay.Topmost = true;
                return;
            }

            if (_mainWindowActive || _overlayActive)
            {
                ShowPlayerOverlay();
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                // Bu noktaya kadar (gecikme süresince) durum değişip
                // yeniden aktif olunmuş olabilir — son durumu tekrar
                // kontrol etmeden gizleme yapma.
                if (!_mainWindowActive && !_overlayActive && !_isPipMode)
                    HidePlayerOverlay();
            }, DispatcherPriority.Input);
        }

        // ─────────────────────────────────────────────────────────────
        // Göster / gizle
        // ─────────────────────────────────────────────────────────────
        private void ShowPlayerOverlay()
        {
            try
            {
                if (_playerOverlay == null)
                    _playerOverlay = new PlayerOverlayWindow(this);

                PlayerContainer.InvalidateMeasure();

                if (!_playerOverlay.IsVisible)
                {
                    _playerOverlay.Show(this);
                }

                _playerOverlay.Topmost = false;
                _playerOverlay.Topmost = true;
                SetPipButtonActive(_isPipMode);

                // Ekran koordinatlarını ve boyutları kesinlikle yeniden senkronize et
                _lastSyncedOverlayPos = new PixelPoint(-32000, -32000);
                _lastSyncedOverlaySize = new Size(0, 0);

                ShowPlayerControls();
                ResetInactivityTimer();
                SyncPlayerOverlayBounds();

                Dispatcher.UIThread.Post(SyncPlayerOverlayBounds, DispatcherPriority.Loaded);
                Dispatcher.UIThread.Post(SyncPlayerOverlayBounds, DispatcherPriority.Render);

                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await Task.Delay(50);
                        SyncPlayerOverlayBounds();
                    }
                    catch { }
                });
            }
            catch (Exception ex) { LogError("ShowPlayerOverlay", ex); }
        }

        private void HidePlayerOverlay()
        {
            try
            {
                if (_playerOverlay != null)
                {
                    _lastSyncedOverlayPos = new PixelPoint(-32000, -32000);
                    _playerOverlay.Position = _lastSyncedOverlayPos;
                }
                _playerOverlay?.Hide();
            }
            catch (Exception ex) { LogError("HidePlayerOverlay", ex); }
        }

        // ─────────────────────────────────────────────────────────────
        // Konum/boyut senkronu — PlayerOverlayWindow'u MainWindow'daki
        // PlayerContainer'ın o anki EKRAN dikdörtgeniyle birebir çakışacak
        // şekilde taşır/yeniden boyutlandırır. Tam ekran geçişinde
        // PlayerContainer tüm pencereyi kapladığından overlay de otomatik
        // olarak tam ekran boyutuna gelir.
        // ─────────────────────────────────────────────────────────────
        private PixelPoint _lastSyncedOverlayPos = new PixelPoint(-32000, -32000);
        private Size _lastSyncedOverlaySize = new Size(0, 0);

        private void SyncPlayerOverlayBounds()
        {
            if (_playerOverlay == null) return;
            if (_isPipMode && _isSyncingPipPosition) return;
            try
            {
                var size = PlayerContainer.Bounds.Size;
                if (size.Width <= 0 || size.Height <= 0 || !PlayerContainer.IsVisible)
                {
                    if (_lastSyncedOverlayPos.X != -32000 || _lastSyncedOverlayPos.Y != -32000)
                    {
                        _lastSyncedOverlayPos = new PixelPoint(-32000, -32000);
                        _playerOverlay.Position = _lastSyncedOverlayPos;
                    }
                    return;
                }

                var topLeft = PlayerContainer.PointToScreen(new Point(0, 0));

                if (_lastSyncedOverlayPos != topLeft)
                {
                    _lastSyncedOverlayPos = topLeft;
                    _playerOverlay.Position = topLeft;
                }

                if (Math.Abs(_lastSyncedOverlaySize.Width - size.Width) > 0.5 || Math.Abs(_playerOverlay.Width - size.Width) > 0.5)
                {
                    _lastSyncedOverlaySize = new Size(size.Width, _lastSyncedOverlaySize.Height);
                    _playerOverlay.Width = size.Width;
                }
                if (Math.Abs(_lastSyncedOverlaySize.Height - size.Height) > 0.5 || Math.Abs(_playerOverlay.Height - size.Height) > 0.5)
                {
                    _lastSyncedOverlaySize = new Size(_lastSyncedOverlaySize.Width, size.Height);
                    _playerOverlay.Height = size.Height;
                }
            }
            catch { /* pencere henüz ekrana bağlı değil vb. */ }
        }

        private void ClosePlayerOverlayWindow()
        {
            try { _playerOverlay?.Close(); } catch { }
            _playerOverlay = null;
        }

        // ═════════════════════════════════════════════════════════════
        // YÖNLENDİRME (forwarding) PROPERTY'LERİ
        //
        // Eskiden MainWindow.axaml içinde x:Name ile tanımlı olup şimdi
        // PlayerOverlayWindow.axaml'e taşınmış TÜM elemanlar için — isim ve
        // tür birebir aynı tutuldu, bu yüzden MainWindow.Player.cs/Series.cs/
        // VodInfo.cs/Epg.cs içindeki "PlayerTopBar.IsVisible = true" gibi
        // KOD SATIRLARININ HİÇBİRİ DEĞİŞTİRİLMEDİ — sadece bu isimlerin
        // artık nereden geldiği değişti (eskiden compiler-generated alan,
        // şimdi bu property'ler üzerinden _playerOverlay'e yönlendirme).
        //
        // NOT: Bu property'ler sadece oynatıcı açıkken (_playerOverlay
        // dolu) çağrılan kod yollarından erişilir; player kapalıyken bu
        // koda hiç girilmez (bkz. PlayerContainer.Height==0 kontrolleri),
        // bu yüzden null-forgiving (!) kullanımı güvenlidir.
        // ═════════════════════════════════════════════════════════════
        private Grid PlayerOverlayRoot => _playerOverlay!.PlayerOverlayRoot;

        private Border PlayerTopBar => _playerOverlay!.PlayerTopBar;
        private Border LiveBadge => _playerOverlay!.LiveBadge;
        private TextBlock PlayerTitleText => _playerOverlay!.PlayerTitleText;

        private StackPanel MediaInfoBadgesPanel => _playerOverlay!.MediaInfoBadgesPanel;
        private Border ResolutionBadge => _playerOverlay!.ResolutionBadge;
        private TextBlock ResolutionBadgeText => _playerOverlay!.ResolutionBadgeText;
        private Border FpsBadge => _playerOverlay!.FpsBadge;
        private TextBlock FpsBadgeText => _playerOverlay!.FpsBadgeText;
        private Border BitrateBadge => _playerOverlay!.BitrateBadge;
        private TextBlock BitrateBadgeText => _playerOverlay!.BitrateBadgeText;
        private Border VideoCodecBadge => _playerOverlay!.VideoCodecBadge;
        private TextBlock VideoCodecBadgeText => _playerOverlay!.VideoCodecBadgeText;
        private Border AudioCodecBadge => _playerOverlay!.AudioCodecBadge;
        private TextBlock AudioCodecBadgeText => _playerOverlay!.AudioCodecBadgeText;
        private Border AudioChannelsBadge => _playerOverlay!.AudioChannelsBadge;
        private TextBlock AudioChannelsBadgeText => _playerOverlay!.AudioChannelsBadgeText;

        private Border BtnAudioTrack => _playerOverlay!.BtnAudioTrack;
        private Border BtnChannelList => _playerOverlay!.BtnChannelList;
        private Border BtnAspectRatio => _playerOverlay!.BtnAspectRatio;
        private TextBlock AspectRatioText => _playerOverlay!.AspectRatioText;
        private Border BtnSubtitle => _playerOverlay!.BtnSubtitle;

        private Border PlayerBottomBar => _playerOverlay!.PlayerBottomBar;
        private Grid SeekBarContainer => _playerOverlay!.SeekBarContainer;
        private TextBlock CurrentTimeText => _playerOverlay!.CurrentTimeText;
        private Slider TimeSlider => _playerOverlay!.TimeSlider;
        private TextBlock TotalTimeText => _playerOverlay!.TotalTimeText;

        private Border PrevChannelBtn => _playerOverlay!.PrevChannelBtn;
        private StackPanel SkipBackBtn => _playerOverlay!.SkipBackBtn;
        private Border PlayPauseBtn => _playerOverlay!.PlayPauseBtn;
        private Avalonia.Controls.Shapes.Path IconPlay => _playerOverlay!.IconPlay;
        private Avalonia.Controls.Shapes.Path IconPause => _playerOverlay!.IconPause;
        private Border NextChannelBtn => _playerOverlay!.NextChannelBtn;
        private StackPanel SkipForwardBtn => _playerOverlay!.SkipForwardBtn;
        private Border NextEpisodeBtn => _playerOverlay!.NextEpisodeBtn;
        private Border SpeedBtn => _playerOverlay!.SpeedBtn;
        private TextBlock SpeedBtnText => _playerOverlay!.SpeedBtnText;
        private Border PipBtn => _playerOverlay!.PipBtn;

        private Border MuteBtn => _playerOverlay!.MuteBtn;
        private Avalonia.Controls.Shapes.Path IconMuteOn => _playerOverlay!.IconMuteOn;
        private Avalonia.Controls.Shapes.Path IconMuteOff => _playerOverlay!.IconMuteOff;
        private Slider VolumeSlider => _playerOverlay!.VolumeSlider;
        private TextBlock VolumeText => _playerOverlay!.VolumeText;

        private Border ClosePlayerBorder => _playerOverlay!.ClosePlayerBorder;

        private Border ChannelListPanel => _playerOverlay!.ChannelListPanel;
        private StackPanel PlayerChannelListContainer => _playerOverlay!.PlayerChannelListContainer;

        private Border AudioTrackPopup => _playerOverlay!.AudioTrackPopup;
        private StackPanel AudioTrackContainer => _playerOverlay!.AudioTrackContainer;
        private TextBlock AudioDelayText => _playerOverlay!.AudioDelayText;

        private Border SubtitlePopup => _playerOverlay!.SubtitlePopup;
        private StackPanel SubtitleContainer => _playerOverlay!.SubtitleContainer;
        private TextBlock SubtitleDelayText => _playerOverlay!.SubtitleDelayText;
        private StackPanel AudioTrackContainerVod => _playerOverlay!.AudioTrackContainerVod;

        private Border AspectRatioPopup => _playerOverlay!.AspectRatioPopup;
        private StackPanel AspectRatioContainer => _playerOverlay!.AspectRatioContainer;

        // ─────────────────────────────────────────────────────────────
        // YENİ: mpv Ayarları (⚙️) popup'ı — eskiden MainWindow.axaml'deki
        // Ayarlar sekmesinde bir kart olarak duran HDR/görüntü işleme
        // kontrolleri artık burada, bkz. PlayerOverlayWindow.axaml.
        // ─────────────────────────────────────────────────────────────
        private Border MpvSettingsBtn => _playerOverlay!.MpvSettingsBtn;
        private Border MpvSettingsPopup => _playerOverlay!.MpvSettingsPopup;

        private Border HdrToneMapAutoItem => _playerOverlay!.HdrToneMapAutoItem;
        private TextBlock HdrToneMapAutoCheck => _playerOverlay!.HdrToneMapAutoCheck;
        private Border HdrToneMapHableItem => _playerOverlay!.HdrToneMapHableItem;
        private TextBlock HdrToneMapHableCheck => _playerOverlay!.HdrToneMapHableCheck;
        private Border HdrToneMapMobiusItem => _playerOverlay!.HdrToneMapMobiusItem;
        private TextBlock HdrToneMapMobiusCheck => _playerOverlay!.HdrToneMapMobiusCheck;
        private Border HdrToneMapBt2446aItem => _playerOverlay!.HdrToneMapBt2446aItem;
        private TextBlock HdrToneMapBt2446aCheck => _playerOverlay!.HdrToneMapBt2446aCheck;

        private Border HdrPeakAutoItem => _playerOverlay!.HdrPeakAutoItem;
        private TextBlock HdrPeakAutoCheck => _playerOverlay!.HdrPeakAutoCheck;
        private Border HdrPeak100Item => _playerOverlay!.HdrPeak100Item;
        private TextBlock HdrPeak100Check => _playerOverlay!.HdrPeak100Check;
        private Border HdrPeak200Item => _playerOverlay!.HdrPeak200Item;
        private TextBlock HdrPeak200Check => _playerOverlay!.HdrPeak200Check;
        private Border HdrPeak400Item => _playerOverlay!.HdrPeak400Item;
        private TextBlock HdrPeak400Check => _playerOverlay!.HdrPeak400Check;
        private Border HdrPeak600Item => _playerOverlay!.HdrPeak600Item;
        private TextBlock HdrPeak600Check => _playerOverlay!.HdrPeak600Check;
        private Border HdrPeak800Item => _playerOverlay!.HdrPeak800Item;
        private TextBlock HdrPeak800Check => _playerOverlay!.HdrPeak800Check;
        private Border HdrPeak1000Item => _playerOverlay!.HdrPeak1000Item;
        private TextBlock HdrPeak1000Check => _playerOverlay!.HdrPeak1000Check;

        private Button ScalingQualityDefaultBtn => _playerOverlay!.ScalingQualityDefaultBtn;
        private Button ScalingQualityHighBtn => _playerOverlay!.ScalingQualityHighBtn;

        private Slider MpvEqBrightnessSlider => _playerOverlay!.MpvEqBrightnessSlider;
        private TextBlock MpvEqBrightnessText => _playerOverlay!.MpvEqBrightnessText;
        private Slider MpvEqContrastSlider => _playerOverlay!.MpvEqContrastSlider;
        private TextBlock MpvEqContrastText => _playerOverlay!.MpvEqContrastText;
        private Slider MpvEqSaturationSlider => _playerOverlay!.MpvEqSaturationSlider;
        private TextBlock MpvEqSaturationText => _playerOverlay!.MpvEqSaturationText;
        private Slider MpvEqGammaSlider => _playerOverlay!.MpvEqGammaSlider;
        private TextBlock MpvEqGammaText => _playerOverlay!.MpvEqGammaText;

        private Border PresetNaturalItem => _playerOverlay!.PresetNaturalItem;
        private Border PresetVividItem => _playerOverlay!.PresetVividItem;
        private Border PresetSportsItem => _playerOverlay!.PresetSportsItem;
        private Border PresetCinemaItem => _playerOverlay!.PresetCinemaItem;

        // ─────────────────────────────────────────────────────────────
        // YENİ: "Oynatıcı Performansı" kartındaki Donanım Çözümlemesi /
        // İnterlacing seçenekleri artık mpv Ayarları (⚙️) popup'ında da
        // gösteriliyor (Ayarlar sekmesindeki kart olduğu gibi kalıyor,
        // burası ek/kopya bir erişim noktası) — bkz. PlayerOverlayWindow.
        // axaml → MpvSettingsPopup. Mantık (SetHwDecodeMode/
        // InterlaceToggle_PointerPressed) MainWindow.Settings.cs'te
        // DEĞİŞMEDEN kalıyor; sadece bu ek kontrol grubu da aynı state'i
        // yansıtacak şekilde güncelleniyor.
        // ─────────────────────────────────────────────────────────────
        private Border PopupHwDecodeAutoItem => _playerOverlay!.PopupHwDecodeAutoItem;
        private TextBlock PopupHwDecodeAutoCheck => _playerOverlay!.PopupHwDecodeAutoCheck;
        private Border PopupHwDecodeD3D11Item => _playerOverlay!.PopupHwDecodeD3D11Item;
        private TextBlock PopupHwDecodeD3D11Check => _playerOverlay!.PopupHwDecodeD3D11Check;
        private Border PopupHwDecodeNvdecItem => _playerOverlay!.PopupHwDecodeNvdecItem;
        private TextBlock PopupHwDecodeNvdecCheck => _playerOverlay!.PopupHwDecodeNvdecCheck;
        private Border PopupHwDecodeNvdecCopyItem => _playerOverlay!.PopupHwDecodeNvdecCopyItem;
        private TextBlock PopupHwDecodeNvdecCopyCheck => _playerOverlay!.PopupHwDecodeNvdecCopyCheck;
        private Border PopupHwDecodeOffItem => _playerOverlay!.PopupHwDecodeOffItem;
        private TextBlock PopupHwDecodeOffCheck => _playerOverlay!.PopupHwDecodeOffCheck;

        private Border PopupInterlaceItem => _playerOverlay!.PopupInterlaceItem;
        private TextBlock PopupInterlaceCheck => _playerOverlay!.PopupInterlaceCheck;

        private StackPanel DeinterlaceModesContainer => _playerOverlay!.DeinterlaceModesContainer;
        private Border PopupDeintYadif2xItem => _playerOverlay!.PopupDeintYadif2xItem;
        private Border PopupDeintYadifItem => _playerOverlay!.PopupDeintYadifItem;
        private Border PopupDeintBobItem => _playerOverlay!.PopupDeintBobItem;
        private Border PopupDeintLinearItem => _playerOverlay!.PopupDeintLinearItem;

        private StackPanel ShaderSectionContainer => _playerOverlay!.ShaderSectionContainer;
        private Border ShaderOffItem => _playerOverlay!.ShaderOffItem;
        private Border ShaderCasItem => _playerOverlay!.ShaderCasItem;
        private Border ShaderFsrItem => _playerOverlay!.ShaderFsrItem;

        private StackPanel ZappingSectionContainer => _playerOverlay!.ZappingSectionContainer;
        private Border ZappingFastItem => _playerOverlay!.ZappingFastItem;
        private Border ZappingStableItem => _playerOverlay!.ZappingStableItem;

        private StackPanel AudioEnhanceSectionContainer => _playerOverlay!.AudioEnhanceSectionContainer;
        private Border AudioEnhanceOffItem => _playerOverlay!.AudioEnhanceOffItem;
        private Border AudioEnhanceLoudnormItem => _playerOverlay!.AudioEnhanceLoudnormItem;
        private Border AudioEnhanceNightItem => _playerOverlay!.AudioEnhanceNightItem;

        // YENİ: mpv Ayarları popup'ı başlığındaki "Aktif: ..." durum satırı.
        private TextBlock MpvPopupStatusText => _playerOverlay!.MpvPopupStatusText;
    }
}
