using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace GlyphTV
{
    public class Channel : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
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
