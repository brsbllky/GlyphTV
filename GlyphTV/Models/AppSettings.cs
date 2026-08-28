using System.Collections.Generic;

namespace GlyphTV
{
    /// <summary>
    /// Uygulama ayarları
    /// </summary>
    public class AppSettings
    {
        public bool AutoRefreshOnStartup { get; set; } = false;

        // YENİ: Uygulama açılışında otomatik çevrimiçi güncelleme denetimi (Açık / Kapalı)
        public bool CheckUpdatesOnStartup { get; set; } = true;

        // Son güncelleme denetimi zaman damgası (ISO formatında veya yerel gösterim)
        public string LastUpdateCheckTime { get; set; } = "";

        // ─────────────────────────────────────────────────────────────
        // YENİ (manuel kategori sıralaması): Her sekme (Canlı / VOD /
        // Dizi) için kullanıcının belirlediği kategori sırası. Anahtar
        // sekme adı, değer o sekmedeki kategorilerin istenen sırasıdır.
        // Kullanıcı sol taraftaki kategori listesini sürükleyip bırakarak
        // sıralamayı değiştirir; bu sıra burada kalıcı olarak saklanır.
        // Listede olmayan (yeni eklenen) kategoriler, bu listenin sonuna
        // alfabetik olarak eklenir — bkz. MainWindow.Navigation.cs →
        // ApplyCategoryOrder.
        // ─────────────────────────────────────────────────────────────
        public Dictionary<string, List<string>> CategoryOrder { get; set; } = new();

        // ─────────────────────────────────────────────────────────────
        // YENİ (içerik sıralama): Her sekme için seçili sıralama modu.
        // Değerler: "AZ" | "ZA" | "New". Varsayılan "AZ". "New" (Son
        // Eklenenler) içerikleri Channel.AddedDate'e göre en yeni önce
        // olacak şekilde sıralar — bkz. MainWindow.Navigation.cs →
        // ApplyContentSort.
        // ─────────────────────────────────────────────────────────────
        public Dictionary<string, string> ContentSortMode { get; set; } = new();

        // "System" | "Light" | "Dark". Varsayılan "System" — uygulama ilk
        // açılışta işletim sisteminin o anki açık/koyu tema tercihini
        // otomatik uygular (bkz. MainWindow.Settings.cs → ApplyThemeMode).
        public string ThemeMode { get; set; } = "System";

        // YENİ: Oynatıcı motoru — "Vlc" | "Mpv". Varsayılan "Vlc" (mevcut
        // kullanıcılar için davranış değişmez; System.Text.Json eksik alanı
        // bu property initializer'ıyla doldurur, yani eski settings.json
        // dosyaları da sorunsuz okunur). Mpv seçilirse GlyphTV.PlayerEngines.
        // MpvPlayerEngine kullanılır — bkz. MainWindow.axaml.cs →
        // EnsureEngineInitialized.
        public string PlayerEngine { get; set; } = "Vlc";

        // YENİ: Donanım çözümlemesi modu — "auto" | "d3d11va" | "nvdec" |
        // "nvdec-copy" | "no". Varsayılan "auto". Ayrık seçim (nvdec vb.)
        // sadece mpv motorunda gerçek etki yaratır; VLC'de "no" dışındaki
        // her değer aynı ("any"/otomatik) davranışa düşer — bkz.
        // VlcPlayerEngine.MapHwDecodeToVlc.
        public string HwDecodeMode { get; set; } = "auto";

        // YENİ: İnterlacing (taramalı görüntü) giderme açık/kapalı.
        // Varsayılan false — çoğu modern IPTV yayını zaten progressive'dir,
        // gereksiz yere açık bırakmak hafif bir CPU/GPU maliyeti ekler.
        public bool RemoveInterlacing { get; set; } = false;

        // YENİ: Deinterlace Modu ("yadif2x", "yadif", "bob", "linear"). Varsayılan "yadif2x".
        public string DeinterlaceMode { get; set; } = "yadif2x";

        // YENİ: Resim Modu / Preseti ("natural", "vivid", "sports", "cinema", "custom"). Varsayılan "natural".
        public string PicturePreset { get; set; } = "natural";

        // ─────────────────────────────────────────────────────────────
        // YENİ: HDR / Görüntü İşleme ayarları — SADECE mpv motorunda etki
        // eder (VLC'de bu düzeyde ayrıntılı HDR ton eşleme/ölçekleme
        // kontrolü yoktur). Orta seviye GPU'larda (ör. GTX 1650 Ti gibi)
        // HDR ton eşlemenin GPU maliyeti değişkenlik gösterebildiğinden,
        // bu ayarlar kullanıcının kalite/performans dengesini kendi
        // donanımına göre ayarlayabilmesi için eklendi.
        // ─────────────────────────────────────────────────────────────

        // "auto" | "hable" | "mobius" | "bt.2446a". Varsayılan "auto" —
        // mpv içeriğin kendi meta verisine göre en uygun algoritmayı seçer.
        public string HdrToneMapping { get; set; } = "auto";

        // "auto" | "100" | "200" | "400" | "600" | "800" | "1000" (nit).
        // Varsayılan "auto" — mpv, ekranın bildirdiği (ya da HDR içeriğin
        // sahne başına dinamik analiz edilen) tepe parlaklığını kullanır.
        // Sabit bir değer seçilirse (MpvPlayerEngine.SetHdrTargetPeak),
        // sahne başına dinamik analiz (hdr-compute-peak, GPU maliyetli)
        // otomatik olarak kapatılır — çünkü sabit hedefte bu analiz
        // gereksizdir.
        public string HdrTargetPeak { get; set; } = "auto";

        // "default" (hızlı/bilinear) | "quality" (daha keskin/pahalı
        // ölçekleme, ewa_lanczossharp). Varsayılan "default" — 4K/HDR
        // içeriklerde takılma yaşayan orta seviye GPU'larda daha akıcı
        // oynatma sağlar; daha güçlü GPU'su olan kullanıcılar "quality"
        // seçerek daha keskin görüntü elde edebilir.
        public string ScalingQuality { get; set; } = "default";

        // YENİ: İnce görüntü ayarları (video equalizer) — mpv'nin
        // "brightness"/"contrast"/"saturation"/"gamma" property'leri.
        // Aralık: -100..100, varsayılan (nötr/dokunulmamış) değer: 0.
        // Sadece mpv motorunda etkilidir.
        public int Brightness { get; set; } = 0;
        public int Contrast { get; set; } = 0;
        public int Saturation { get; set; } = 0;
        public int Gamma { get; set; } = 0;

        // ─────────────────────────────────────────────────────────────
        // YENİ: İleri Düzey (Enhanced) Oynatıcı Özellikleri
        // ─────────────────────────────────────────────────────────────
        // "off" | "cas" (AMD FidelityFX CAS) | "fsr" (AMD FSR 1.0). Varsayılan "off". (Sadece MPV)
        public string ShaderMode { get; set; } = "off";

        // Canlı TV kanallarında düşük gecikmeli / anlık kanal açılış modu (Ultra-Fast Zapping). Varsayılan true. (MPV & VLC)
        public bool FastZapping { get; set; } = true;

        // "off" | "loudnorm" (EBU R128 Ses Dengeleme) | "night" (Gece Modu / Dinamik Kompresör). Varsayılan "off". (MPV & VLC)
        public string AudioEnhancement { get; set; } = "off";
    }
}
