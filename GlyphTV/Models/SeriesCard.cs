using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace GlyphTV
{
    /// <summary>
    /// Dizi kart modeli - poster + sezon/bölüm navigasyonu
    /// </summary>
    public class SeriesCard : INotifyPropertyChanged
    {
        public string ShowName { get; set; } = "";
        public string Group { get; set; } = "";
        public string LogoUrl { get; set; } = "";

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

        private bool _hasResume = false;
        [JsonIgnore]
        public bool HasResume
        {
            get => _hasResume;
            set { _hasResume = value; OnPropertyChanged(nameof(HasResume)); }
        }

        [JsonIgnore] public List<string> Seasons { get; set; } = new();
        [JsonIgnore] public Dictionary<string, List<Channel>> EpisodesBySeason { get; set; } = new();

        private int _selectedSeasonIndex = 0;
        [JsonIgnore]
        public int SelectedSeasonIndex
        {
            get => _selectedSeasonIndex;
            set
            {
                _selectedSeasonIndex = value;
                OnPropertyChanged(nameof(SeasonText));
                OnPropertyChanged(nameof(EpisodeText));
                _selectedEpisodeIndex = 0;
                OnPropertyChanged(nameof(SelectedEpisodeIndex));
            }
        }

        private int _selectedEpisodeIndex = 0;
        [JsonIgnore]
        public int SelectedEpisodeIndex
        {
            get => _selectedEpisodeIndex;
            set
            {
                _selectedEpisodeIndex = value;
                OnPropertyChanged(nameof(EpisodeText));
                OnPropertyChanged(nameof(HasResume));
            }
        }

        [JsonIgnore]
        public string SeasonText => Seasons.Count > 0 && _selectedSeasonIndex < Seasons.Count
            ? Seasons[_selectedSeasonIndex] : "-";

        [JsonIgnore]
        public List<Channel> CurrentEpisodes =>
            Seasons.Count > 0 &&
            _selectedSeasonIndex < Seasons.Count &&
            EpisodesBySeason.ContainsKey(Seasons[_selectedSeasonIndex])
                ? EpisodesBySeason[Seasons[_selectedSeasonIndex]]
                : new List<Channel>();

        [JsonIgnore]
        public string EpisodeText =>
            CurrentEpisodes.Count > 0 && _selectedEpisodeIndex < CurrentEpisodes.Count
                ? $"Bölüm {CurrentEpisodes[_selectedEpisodeIndex].EpisodeNumber}" : "-";

        [JsonIgnore]
        public Channel? SelectedEpisode =>
            CurrentEpisodes.Count > 0 && _selectedEpisodeIndex < CurrentEpisodes.Count
                ? CurrentEpisodes[_selectedEpisodeIndex] : null;

        [JsonIgnore] public bool IsFavorite => SelectedEpisode?.IsFavorite ?? false;
        [JsonIgnore] public string FavoriteIcon => IsFavorite ? "❤️" : "🤍";
        [JsonIgnore]
        public IBrush FavoriteBrush => IsFavorite
            ? Brush.Parse("#ff5f57")
            : Brush.Parse("#8b8b95");

        /// <summary>
        /// Sezon/bölüm seçimini yan etki olmadan geri yükler (hafızadan restore için)
        /// </summary>
        public void RestoreSelection(int seasonIdx, int episodeIdx)
        {
            if (seasonIdx < Seasons.Count) _selectedSeasonIndex = seasonIdx;
            var eps = CurrentEpisodes;
            if (episodeIdx < eps.Count) _selectedEpisodeIndex = episodeIdx;
            OnPropertyChanged(nameof(SeasonText));
            OnPropertyChanged(nameof(EpisodeText));
            OnPropertyChanged(nameof(HasResume));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
