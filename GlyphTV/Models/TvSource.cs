using Avalonia.Media;
using System;
using System.ComponentModel;

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
        public string PathOrUrl { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";

        // ─────────────────────────────────────────────────────────────
        // DÜZELTME: IsActive önceden basit bir auto-property idi. TvSource
        // INotifyPropertyChanged uygulamadığı için SelectSource_Click /
        // DeleteSource_Click gibi yerlerde IsActive değiştirildiğinde
        // SourcesList ("Aktif"/"Seç" rozeti, StatusBrush rengi) otomatik
        // güncellenmiyordu. Bunun "çözümü" olarak MainWindow.Sources.cs içinde
        // SaveSources() her çağrıldığında _sources koleksiyonu Clear() edilip
        // aynı öğelerle yeniden dolduruluyordu — bu, ObservableCollection için
        // gereksiz bir Reset bildirimi (tüm liste UI'da yeniden oluşturulur,
        // görünür bir "titreme" + gereksiz iş yüküne yol açar).
        // Asıl/doğru çözüm: IsActive set edildiğinde PropertyChanged ile
        // bağımlı (StatusText/StatusBrush) özellikleri de bildir. Bu sayede
        // SaveSources() artık sadece dosyaya yazar; UI, binding üzerinden
        // doğru şekilde kendiliğinden güncellenir.
        // ─────────────────────────────────────────────────────────────
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

        public string StatusText => IsActive ? "Aktif" : "Seç";
        public IBrush StatusBrush => IsActive
            ? Brush.Parse("#28c840")
            : Brush.Parse("#8b8b95");

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
