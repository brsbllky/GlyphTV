using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;
using System.ComponentModel;
using System.Globalization;

namespace GlyphTV
{
    /// <summary>
    /// TMDb üzerinden haftalık popüler içerik modeli
    /// </summary>
    public class PopularMediaItem : INotifyPropertyChanged
    {
        private int _rank;
        public int Rank
        {
            get => _rank;
            set
            {
                _rank = value;
                _rankGeometry = null;
                OnPropertyChanged(nameof(Rank));
                OnPropertyChanged(nameof(RankText));
                OnPropertyChanged(nameof(RankGeometry));
            }
        }
        public string RankText => Rank.ToString();

        private Geometry? _rankGeometry;
        public Geometry? RankGeometry
        {
            get
            {
                if (_rankGeometry == null && Rank > 0)
                {
                    try
                    {
                        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.ExtraBold);
                        var ft = new FormattedText(
                            Rank.ToString(),
                            CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            typeface,
                            130,
                            Brushes.Transparent);
                        _rankGeometry = ft.BuildGeometry(new Point(0, 0));
                    }
                    catch { }
                }
                return _rankGeometry;
            }
        }
        public int TmdbId { get; set; }
        public string Title { get; set; } = "";
        public string OriginalTitle { get; set; } = "";
        public string MediaType { get; set; } = "movie"; // "movie" veya "tv"
        public string MediaTypeBadge => MediaType == "tv" ? "DİZİ" : "SİNEMA";
        public string PosterPath { get; set; } = "";
        public string PosterUrl => string.IsNullOrEmpty(PosterPath) 
            ? "" 
            : (PosterPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) 
                ? PosterPath 
                : $"https://image.tmdb.org/t/p/w500{PosterPath}");

        public string BackdropPath { get; set; } = "";
        public string BackdropUrl => string.IsNullOrEmpty(BackdropPath)
            ? PosterUrl
            : (BackdropPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? BackdropPath
                : $"https://image.tmdb.org/t/p/w1280{BackdropPath}");

        public string Tagline { get; set; } = "";
        public string TaglineFormatted => string.IsNullOrWhiteSpace(Tagline) ? "" : $"\"{Tagline}\"";
        public bool HasTagline => !string.IsNullOrWhiteSpace(Tagline);

        public string Quality { get; set; } = "4K HDR";
        public string MatchRateText { get; set; } = "%95 Eşleşme";

        private System.Collections.ObjectModel.ObservableCollection<string> _genres = new();
        public System.Collections.ObjectModel.ObservableCollection<string> Genres
        {
            get => _genres;
            set
            {
                _genres = value;
                OnPropertyChanged(nameof(Genres));
                OnPropertyChanged(nameof(HasGenres));
            }
        }
        public bool HasGenres => _genres != null && _genres.Count > 0;

        public double VoteAverage { get; set; }
        public string RatingFormatted => VoteAverage > 0 
            ? VoteAverage.ToString("0.0", CultureInfo.InvariantCulture) 
            : "8.0";
        public string ReleaseYear { get; set; } = "";
        public string Overview { get; set; } = "";

        public Channel? MatchedChannel { get; set; }
        public SeriesCard? MatchedSeries { get; set; }
        public bool IsMatched => MatchedChannel != null || MatchedSeries != null;

        public bool IsFavorite
        {
            get
            {
                if (MatchedChannel != null) return MatchedChannel.IsFavorite;
                if (MatchedSeries != null) return MatchedSeries.IsFavorite;
                return false;
            }
        }
        public string FavoriteIcon => IsFavorite ? "❤️" : "🤍";
        public IBrush FavoriteBrush => IsFavorite 
            ? new SolidColorBrush(Color.Parse("#e81123")) 
            : new SolidColorBrush(Color.Parse("#cbd5e1"));

        public void NotifyFavoriteChanged()
        {
            OnPropertyChanged(nameof(IsFavorite));
            OnPropertyChanged(nameof(FavoriteIcon));
            OnPropertyChanged(nameof(FavoriteBrush));
        }

        private Bitmap? _posterBitmap;
        public Bitmap? PosterBitmap
        {
            get => _posterBitmap;
            set
            {
                _posterBitmap = value;
                OnPropertyChanged(nameof(PosterBitmap));
                OnPropertyChanged(nameof(HasPoster));
                OnPropertyChanged(nameof(NoPoster));
            }
        }

        private Bitmap? _backdropBitmap;
        public Bitmap? BackdropBitmap
        {
            get => _backdropBitmap;
            set
            {
                _backdropBitmap = value;
                OnPropertyChanged(nameof(BackdropBitmap));
                OnPropertyChanged(nameof(HasBackdrop));
                OnPropertyChanged(nameof(NoBackdrop));
            }
        }

        public bool HasPoster => _posterBitmap != null;
        public bool NoPoster => _posterBitmap == null;
        public bool HasBackdrop => _backdropBitmap != null;
        public bool NoBackdrop => _backdropBitmap == null;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }

    /// <summary>
    /// Hero Banner gösterge noktası modeli
    /// </summary>
    public class HeroDotItem : INotifyPropertyChanged
    {
        public int Index { get; set; }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set
            {
                _isActive = value;
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(DotWidth));
                OnPropertyChanged(nameof(DotBrush));
            }
        }

        public double DotWidth => IsActive ? 26.0 : 8.0;
        public IBrush DotBrush => IsActive 
            ? new SolidColorBrush(Color.Parse("#00d2ff")) 
            : new SolidColorBrush(Color.Parse("#475569"));

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }

    /// <summary>
    /// Devam et (yarıda bırakılan) izleme öğesi modeli
    /// </summary>
    public class ResumeWatchItem : INotifyPropertyChanged
    {
        public WatchHistory? History { get; set; }
        public Channel? Channel { get; set; }

        private SeriesCard? _seriesCard;
        public SeriesCard? SeriesCard
        {
            get => _seriesCard;
            set
            {
                _seriesCard = value;
                OnPropertyChanged(nameof(SeriesCard));
                OnPropertyChanged(nameof(IsSeries));
                OnPropertyChanged(nameof(IsMovie));
            }
        }

        public bool IsSeries => Type == "Dizi" && SeriesCard != null;
        public bool IsMovie => !IsSeries;

        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = ""; // "AMAZON DİZİLERİ" veya "Film"
        public string Type { get; set; } = "VOD";   // "VOD" veya "Dizi"
        private long _position;
        public long Position
        {
            get => _position;
            set
            {
                if (_position == value) return;
                _position = value;
                OnPropertyChanged(nameof(Position));
                OnPropertyChanged(nameof(HasResume));
                OnPropertyChanged(nameof(HasProgress));
                OnPropertyChanged(nameof(ProgressRatio));
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressPercentText));
                OnPropertyChanged(nameof(PositionFormatted));
                OnPropertyChanged(nameof(RemainingMilliseconds));
                OnPropertyChanged(nameof(RemainingTimeText));
            }
        }

        private long _duration;
        public long Duration
        {
            get => _duration;
            set
            {
                if (_duration == value) return;
                _duration = value;
                OnPropertyChanged(nameof(Duration));
                OnPropertyChanged(nameof(HasProgress));
                OnPropertyChanged(nameof(ProgressRatio));
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressPercentText));
                OnPropertyChanged(nameof(PositionFormatted));
                OnPropertyChanged(nameof(RemainingMilliseconds));
                OnPropertyChanged(nameof(RemainingTimeText));
            }
        }

        public bool HasResume => Position > 5000;
        public bool HasProgress => Position > 5000;

        private bool? _overrideFavorite;
        public bool IsFavorite
        {
            get
            {
                if (_overrideFavorite.HasValue) return _overrideFavorite.Value;
                if (Channel != null) return Channel.IsFavorite;
                if (SeriesCard != null) return SeriesCard.IsFavorite;
                return false;
            }
            set
            {
                _overrideFavorite = value;
                if (Channel != null) Channel.IsFavorite = value;
                if (SeriesCard != null) SeriesCard.IsFavorite = value;
                NotifyFavoriteChanged();
            }
        }

        public string FavoriteIcon => IsFavorite ? "❤️" : "🤍";

        public IBrush FavoriteBrush => IsFavorite 
            ? new SolidColorBrush(Color.Parse("#e81123")) 
            : new SolidColorBrush(Color.Parse("#6b6b73"));

        public void NotifyFavoriteChanged()
        {
            _overrideFavorite = null;
            OnPropertyChanged(nameof(IsFavorite));
            OnPropertyChanged(nameof(FavoriteIcon));
            OnPropertyChanged(nameof(FavoriteBrush));
        }

        public double ProgressRatio => Duration > 0 
            ? Math.Clamp((double)Position / Duration, 0.0, 1.0) 
            : 0.0;
        public double ProgressPercent => Math.Round(ProgressRatio * 100);
        public string ProgressPercentText => $"%{ProgressPercent:0}";

        public long RemainingMilliseconds => Math.Max(0, Duration - Position);

        public string RemainingTimeText
        {
            get
            {
                if (Duration <= 0)
                {
                    if (Position > 5000)
                    {
                        var ts = TimeSpan.FromMilliseconds(Position);
                        int mins = (int)ts.TotalMinutes;
                        if (mins < 1) return "< 1 dk";
                        if (mins >= 60) return $"{mins / 60} sa {mins % 60} dk";
                        return $"{mins} dk";
                    }
                    return "";
                }

                var rem = TimeSpan.FromMilliseconds(RemainingMilliseconds);
                int totalMinutes = (int)Math.Round(rem.TotalMinutes);
                if (totalMinutes <= 1)
                {
                    return "1 dk kaldı";
                }
                if (totalMinutes >= 60)
                {
                    int hours = totalMinutes / 60;
                    int mins = totalMinutes % 60;
                    if (mins == 0) return $"{hours} sa kaldı";
                    return $"{hours} sa {mins} dk kaldı";
                }
                return $"{totalMinutes} dk kaldı";
            }
        }

        public string PositionFormatted
        {
            get
            {
                var ts = TimeSpan.FromMilliseconds(Position);
                var dur = TimeSpan.FromMilliseconds(Duration);
                if (dur.TotalHours >= 1 || ts.TotalHours >= 1)
                {
                    return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2} / {dur.Hours:D2}:{dur.Minutes:D2}:{dur.Seconds:D2}";
                }
                return $"{ts.Minutes:D2}:{ts.Seconds:D2} / {dur.Minutes:D2}:{dur.Seconds:D2}";
            }
        }

        private Bitmap? _posterBitmap;
        public Bitmap? PosterBitmap
        {
            get => _posterBitmap;
            set
            {
                _posterBitmap = value;
                OnPropertyChanged(nameof(PosterBitmap));
                OnPropertyChanged(nameof(LogoBitmap));
                OnPropertyChanged(nameof(HasPoster));
                OnPropertyChanged(nameof(NoPoster));
                OnPropertyChanged(nameof(HasLogo));
                OnPropertyChanged(nameof(NoLogo));
            }
        }

        public Bitmap? LogoBitmap
        {
            get => _posterBitmap;
            set => PosterBitmap = value;
        }

        public bool HasPoster => _posterBitmap != null;
        public bool NoPoster => _posterBitmap == null;
        public bool HasLogo => _posterBitmap != null;
        public bool NoLogo => _posterBitmap == null;
        public string PlaceholderIcon => Type == "Dizi" ? "🎞️" : "🎬";

        public string Name => Title;
        public string Group => Subtitle;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }
}
