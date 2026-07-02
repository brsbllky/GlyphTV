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

        // ─────────────────────────────────────────────────────────────
        // GÜVENLİK DÜZELTMESİ:
        // Bu alanlar önceden [JsonIgnore(Condition = WhenWritingDefault)]
        // ile işaretliydi. Bu koşul bir alanı SADECE değeri null ise atlar;
        // string'in "default" değeri null'dır, "" DEĞİLDİR. PathOrUrl/
        // Username/Password hiçbir zaman null olmadığından (hep "" ile
        // başlatılıp gerçek değerle doldurulduklarından) bu koşul asla
        // tetiklenmiyordu — yani şifreli (*Encrypted) alanlar doğru
        // yazılırken, DÜZ METİN halleri de her seferinde sources.json'a
        // yazılmaya devam ediyordu. Şifreleme fiilen hiçbir şey
        // gizlemiyordu.
        //
        // Artık koşulsuz [JsonIgnore] kullanılıyor: bu alanlar hiçbir
        // durumda JSON'a yazılmaz/okunmaz. Eski (şifreleme öncesi)
        // dosyalardan geçişi bozmamak için aşağıda ayrı "Legacy" alanlar
        // tanımlandı — bunlar sadece OKUMA sırasında eski düz metin
        // değerleri yakalamak içindir, okunur okunmaz temizlenirler
        // (bkz. MainWindow.Sources.cs LoadSources) ve bir daha asla
        // diske yazılmazlar.
        // ─────────────────────────────────────────────────────────────
        [JsonIgnore]
        public string PathOrUrl { get; set; } = "";

        [JsonIgnore]
        public string Username { get; set; } = "";

        [JsonIgnore]
        public string Password { get; set; } = "";

        // Sadece eski/şifreleme-öncesi sources.json dosyalarından düz metin
        // değerleri okuyabilmek için geçiş (migration) alanları. JSON'da
        // gerçek alanlarla aynı isimleri kullanırlar (yukarıdakiler
        // [JsonIgnore] olduğu için isim çakışması olmaz). WhenWritingDefault
        // burada doğru çalışır çünkü bu alanlar nullable'dır ve okunduktan
        // hemen sonra null'a çekilirler — dolayısıyla asla tekrar yazılmazlar.
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
