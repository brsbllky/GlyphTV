using Avalonia.Media;
using Avalonia.Media.Imaging;
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
        [JsonIgnore]
        public string Url { get; set; } = "";

        [JsonPropertyName("Url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? LegacyUrl { get; set; }

        public string UrlEncrypted { get; set; } = "";

        public string Group { get; set; } = "Diğer";
        public string Type { get; set; } = "Canlı";

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
            ? Brush.Parse("#ff5f57")
            : Brush.Parse("#8b8b95");

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
