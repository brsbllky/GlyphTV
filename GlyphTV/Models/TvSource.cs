using Avalonia.Media;
using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace GlyphTV
{
    /// <summary>
    /// IPTV kaynağı (M3U dosyası, URL linki veya Xtream Code)
    /// </summary>
    public class TvSource : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";    // "M3U" | "Link" | "Xtream"

        [JsonIgnore]
        public string PathOrUrl { get; set; } = "";

        [JsonIgnore]
        public string Username { get; set; } = "";

        [JsonIgnore]
        public string Password { get; set; } = "";

        [JsonPropertyName("PathOrUrl")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? LegacyPathOrUrl { get; set; }

        [JsonPropertyName("Username")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? LegacyUsername { get; set; }

        [JsonPropertyName("Password")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? LegacyPassword { get; set; }

        public string PathOrUrlEncrypted { get; set; } = "";
        public string UsernameEncrypted  { get; set; } = "";
        public string PasswordEncrypted  { get; set; } = "";

        public string EpgUrl { get; set; } = "";
        public DateTime? EpgLastFetchedDate { get; set; }

        private bool _isActive = false;
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBrush));
            }
        }

        public string StatusText => IsActive 
            ? (Localization.CurrentLanguage == "en" ? "ACTIVE" : "AKTİF") 
            : (Localization.CurrentLanguage == "en" ? "SELECT" : "SEÇ");

        public IBrush StatusBrush => IsActive
            ? Brush.Parse("#28c840")
            : Brush.Parse("#8b8b95");

        public DateTime CreatedDate { get; set; } = DateTime.Now;

        private DateTime? _lastRefreshedDate;
        public DateTime? LastRefreshedDate
        {
            get => _lastRefreshedDate;
            set
            {
                if (_lastRefreshedDate == value) return;
                _lastRefreshedDate = value;
                OnPropertyChanged(nameof(LastRefreshedDate));
                OnPropertyChanged(nameof(SourceSummaryText));
            }
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: Satın alınan hesabın bitiş (expiration) tarihi
        // Xtream get_user_info yanıtındaki "exp_date" alanından doldurulur.
        // ─────────────────────────────────────────────────────────────
        private DateTime? _expiryDate;
        public DateTime? ExpiryDate
        {
            get => _expiryDate;
            set
            {
                if (_expiryDate == value) return;
                _expiryDate = value;
                OnPropertyChanged(nameof(ExpiryDate));
                OnPropertyChanged(nameof(SourceSummaryText));
            }
        }

        public string SourceSummaryText
        {
            get
            {
                bool isEn = Localization.CurrentLanguage == "en";
                string refreshStr = LastRefreshedDate.HasValue 
                    ? LastRefreshedDate.Value.ToString("dd.MM.yyyy") 
                    : (isEn ? "None yet" : "Henüz yok");

                string expiryStr = "";
                if (ExpiryDate.HasValue)
                {
                    expiryStr = isEn 
                        ? $" | Expires: {ExpiryDate.Value:dd.MM.yyyy}" 
                        : $" | Bitiş: {ExpiryDate.Value:dd.MM.yyyy}";
                }
                else if (Type == "Xtream")
                {
                    expiryStr = isEn ? " | Expires: Unlimited" : " | Bitiş: Sınırsız";
                }

                string addedLabel = isEn ? "Added" : "Eklendi";
                string updatedLabel = isEn ? "Updated" : "Yenileme";
                return $"{Type} | {addedLabel}: {CreatedDate:dd.MM.yyyy} | {updatedLabel}: {refreshStr}{expiryStr}";
            }
        }

        public void RefreshLocalization()
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(SourceSummaryText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}