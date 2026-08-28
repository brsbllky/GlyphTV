using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace GlyphTV
{
    public class Channel : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";

        // ─────────────────────────────────────────────────────────────
        // GÜVENLİK DÜZELTMESİ: Önceden [JsonIgnore(WhenWritingDefault)]
        // kullanılıyordu; bu koşul yalnızca değer null ise atlama yapar.
        // Url hiçbir zaman null olmadığından (her zaman "" veya gerçek bir
        // stream adresi) bu koşul asla tetiklenmiyor, yani Url düz metin
        // olarak UrlEncrypted'ın YANINDA channels_*.json'a yazılmaya devam
        // ediyordu. Şimdi koşulsuz [JsonIgnore] kullanılıyor; Url hiçbir
        // durumda JSON'a yazılmaz/okunmaz. Runtime'da her yerde ch.Url
        // kullanılmaya devam eder; diske yazılırken ProtectString ile
        // UrlEncrypted'a, okunurken UnprotectString ile geri Url'ye aktarılır.
        // LegacyUrl ise sadece şifreleme-öncesi eski dosyalardan düz metin
        // URL'yi okuyabilmek için var; okunur okunmaz null'a çekilir ve
        // bir daha asla yazılmaz.
        // ─────────────────────────────────────────────────────────────
        private string _url = "";

        [JsonIgnore]
        public string Url
        {
            get
            {
                if (string.IsNullOrEmpty(_url) && !string.IsNullOrEmpty(UrlEncrypted))
                {
                    _url = MainWindow.UnprotectString(UrlEncrypted);
                }
                return _url;
            }
            set => _url = value;
        }

        [JsonPropertyName("Url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? LegacyUrl { get; set; }

        public string UrlEncrypted { get; set; } = "";

        public string Group { get; set; } = "Diğer";
        public string Type { get; set; } = "Canlı";

        // ─────────────────────────────────────────────────────────────
        // YENİ (içerik sıralama — "Son Eklenenler"): İçeriğin kaynağa
        // eklendiği tarih. Xtream kaynaklarında sağlayıcının verdiği
        // "added" (Unix zaman damgası) alanından, M3U kaynaklarında ise
        // dosyadaki sıradan türetilen sentetik bir değerden doldurulur
        // (bkz. MainWindow.Sources.cs → ParseM3u / ParseXtreamApi /
        // FetchXtreamSeriesChannels). Diske yazılır; "Son Eklenenler"
        // sıralaması bu alana göre yapılır. Eski kayıtlarda (alan yokken)
        // DateTime.MinValue kalır — bu içerikler sıralamada en sona düşer.
        // ─────────────────────────────────────────────────────────────
        public DateTime AddedDate { get; set; } = DateTime.MinValue;

        private bool _isFavorite = false;
        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                _isFavorite = value;
                OnPropertyChanged(nameof(IsFavorite));
                OnPropertyChanged(nameof(FavoriteIcon));
                OnPropertyChanged(nameof(FavoriteBrush));
            }
        }

        public bool IsHidden { get; set; } = false;

        // Dizi metadata - sadece Type="Dizi" için
        public string ShowName { get; set; } = "";
        public string Season { get; set; } = "";
        public int EpisodeNumber { get; set; } = 0;

        // Logo URL (M3U tvg-logo özelliğinden)
        public string LogoUrl { get; set; } = "";
        public string XuiId { get; set; } = "";

        // ─────────────────────────────────────────────────────────────
        // YENİ (EPG desteği): M3U/Xtream #EXTINF satırındaki tvg-id="..."
        // özelliği. XMLTV dosyalarındaki <programme channel="..."> alanı
        // ile birebir eşleşir — bu sayede bir kanalın yayın akışı (EPG)
        // doğru bulunabilir. Boşsa (sağlayıcı tvg-id vermiyorsa) o kanal
        // için EPG gösterilemez, ama uygulamanın geri kalanı etkilenmez.
        // Bkz. MainWindow.Sources.cs → ParseM3u, MainWindow.Epg.cs.
        // ─────────────────────────────────────────────────────────────
        public string TvgId { get; set; } = "";

        // ─────────────────────────────────────────────────────────────
        // YENİ (EPG "şu an oynuyor" rozeti): Kategori listelerinde
        // (ContentItemsGrid → Canlı sekmesi) kanal adının altında o an
        // yayında olan programı göstermek için. MainWindow.Epg.cs →
        // UpdateLiveChannelsEpgNowInfo tarafından EPG verisi her
        // yenilendiğinde/dakikada bir güncellenir. JsonIgnore: diske asla
        // yazılmaz, sadece o oturumda hesaplanan geçici bir görüntü verisi.
        // ─────────────────────────────────────────────────────────────
        private string _epgNowTitle = "";
        [JsonIgnore]
        public string EpgNowTitle
        {
            get => _epgNowTitle;
            set
            {
                if (_epgNowTitle == value) return;
                _epgNowTitle = value;
                OnPropertyChanged(nameof(EpgNowTitle));
                OnPropertyChanged(nameof(HasEpgNow));
                OnPropertyChanged(nameof(EpgNowDisplayText));
            }
        }

        private string _epgNowTimeRange = "";
        [JsonIgnore]
        public string EpgNowTimeRange
        {
            get => _epgNowTimeRange;
            set
            {
                if (_epgNowTimeRange == value) return;
                _epgNowTimeRange = value;
                OnPropertyChanged(nameof(EpgNowTimeRange));
                OnPropertyChanged(nameof(EpgNowDisplayText));
            }
        }

        [JsonIgnore] public bool HasEpgNow => !string.IsNullOrEmpty(_epgNowTitle);

        // YENİ: Kategori listesinde tek satırda gösterilecek birleşik metin
        // — örn. "Güzel Köylü - 14:00-15:30". Başlangıç/bitiş saati boşsa
        // (nadiren olur — programın kendisi var ama süresi çözülemediyse)
        // sadece başlığa düşer, tire eklenmez.
        [JsonIgnore]
        public string EpgNowDisplayText => string.IsNullOrEmpty(_epgNowTimeRange)
            ? _epgNowTitle
            : $"{_epgNowTitle} - {_epgNowTimeRange}";

        // ─────────────────────────────────────────────────────────────
        // DÜZELTME (Türkçe isimli içeriklerde poster/detay bulunamaması):
        // Xtream sağlayıcıları çoğunlukla VOD içeriklerini kendi panelinde
        // zaten TMDb ile eşleştirmiş olur ve bunu get_vod_info uç noktasının
        // "tmdb_id" alanında döndürür. Bu ID bir kez (detay açıldığında veya
        // poster yüklenirken) sağlayıcıdan çekilip buraya kaydedilir ki aynı
        // kanal için bir daha network isteği yapılmasın ve isimle arama
        // (dolayısıyla Türkçe/İngilizce isim uyuşmazlığı) tamamen atlanabilsin.
        // 0 veya boş = henüz sorgulanmadı ya da sağlayıcı sağlamadı.
        // ─────────────────────────────────────────────────────────────
        public int TmdbId { get; set; } = 0;

        // Sağlayıcının (varsa) verdiği orijinal/İngilizce ad — TMDb'de
        // arama yaparken Türkçe görünen ada göre çok daha güvenilir bir
        // adaydır. Xtream get_vod_info yanıtındaki "o_name" alanından gelir.
        public string OriginalName { get; set; } = "";

        // ─────────────────────────────────────────────────────────────
        // YENİ: Sağlayıcının (Xtream get_series_info) kendi verdiği
        // plot/cast/director/genre/rating/tarih bilgisi — sadece Type="Dizi"
        // için, FetchXtreamSeriesChannels tarafından doldurulur. TMDb'de
        // hiç kaydı olmayan diziler (örn. yerel TRT belgeselleri) için
        // detay modalının tamamen boş kalmaması amacıyla eklendi — bkz.
        // MainWindow.Series.cs → SeriesInfo_Click, MainWindow.Tmdb.cs →
        // ApplyProviderFallbackInfo. TMDb eşleşmesi bulunursa bu alanların
        // yerini TMDb verisi alır (sağlayıcı verisi sadece TABAN'dır).
        // ─────────────────────────────────────────────────────────────
        public string ProviderPlot { get; set; } = "";
        public string ProviderCast { get; set; } = "";
        public string ProviderDirector { get; set; } = "";
        public string ProviderGenre { get; set; } = "";
        public string ProviderRating { get; set; } = "";
        public string ProviderReleaseDate { get; set; } = "";

        private bool _hasResume = false;
        [JsonIgnore]
        public bool HasResume
        {
            get => _hasResume;
            set { _hasResume = value; OnPropertyChanged(nameof(HasResume)); }
        }

        private Bitmap? _logoBitmap;
        [JsonIgnore]
        public Bitmap? LogoBitmap
        {
            get => _logoBitmap;
            set
            {
                _logoBitmap = value;
                OnPropertyChanged(nameof(LogoBitmap));
                OnPropertyChanged(nameof(HasLogo));
                OnPropertyChanged(nameof(NoLogo));
            }
        }

        [JsonIgnore] public bool HasLogo => _logoBitmap != null;
        [JsonIgnore] public bool NoLogo => _logoBitmap == null;

        public string FavoriteIcon => IsFavorite ? "❤️" : "🤍";
        public IBrush FavoriteBrush => IsFavorite
            ? Brush.Parse("#dc2626")
            : Brush.Parse("#8b8b95");

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
