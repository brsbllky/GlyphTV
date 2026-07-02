using System;
using System.Text.Json.Serialization;

namespace GlyphTV
{
    /// <summary>
    /// İzleme geçmişi kaydı
    /// </summary>
    public class WatchHistory
    {
        // ─────────────────────────────────────────────────────────────
        // GÜVENLİK DÜZELTMESİ: Bu Url alanı hiç şifrelenmeden history.json'a
        // düz metin yazılıyordu. Xtream kaynaklarında stream URL'si
        // "sunucu/kullanıcı_adı/şifre/dosya.uzantı" biçimindedir — yani
        // izleme geçmişi dosyası da fiilen kullanıcı adı/şifreyi düz metin
        // olarak taşıyordu. Artık sources.json/channels.json ile aynı
        // desen uygulanıyor: Url [JsonIgnore], gerçek değer UrlEncrypted
        // içinde DPAPI ile şifrelenmiş olarak tutuluyor.
        // ─────────────────────────────────────────────────────────────
        [JsonIgnore]
        public string Url { get; set; } = "";

        [JsonPropertyName("Url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? LegacyUrl { get; set; }

        public string UrlEncrypted { get; set; } = "";

        public string Name { get; set; } = "";
        public string Group { get; set; } = "";
        public string Type { get; set; } = "";
        public long Position { get; set; } = 0;     // milisaniye
        public long Duration { get; set; } = 0;     // milisaniye
        public DateTime LastWatched { get; set; }
        public string ShowName { get; set; } = "";
        public string Season { get; set; } = "";
        public int EpisodeNumber { get; set; } = 0;

    }
}
