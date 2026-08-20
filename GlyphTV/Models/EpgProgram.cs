using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GlyphTV
{
    /// <summary>
    /// XMLTV'den (Xtream'in xmltv.php uç noktası veya M3U/Link kaynakları
    /// için kullanıcının verdiği harici XMLTV linki) ayrıştırılan tek bir
    /// program/yayın kaydı.
    /// </summary>
    public class EpgProgram
    {
        /// <summary>
        /// XMLTV &lt;programme channel="..."&gt; değeri — Channel.TvgId ile
        /// (bkz. MainWindow.Sources.cs → ParseM3u) eşleştirilir.
        /// </summary>
        public string ChannelId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime Start { get; set; }
        public DateTime Stop { get; set; }

        [JsonIgnore]
        public TimeSpan Duration => Stop > Start ? Stop - Start : TimeSpan.Zero;

        [JsonIgnore]
        public bool IsNow => DateTime.Now >= Start && DateTime.Now < Stop;
    }

    /// <summary>
    /// Diske kaydedilen EPG önbelleği. XMLTV indirme + ayrıştırma (özellikle
    /// büyük dosyalarda) pahalı bir işlem olduğundan sonuç bir süre
    /// (bkz. MainWindow.Epg.cs → EPG_CACHE_HOURS) diskte tutulur; aynı
    /// kaynağa her geçişte/pencere açılışında yeniden indirilmez.
    /// SourceSignature, kaynağın EPG adresi (Xtream: sunucu+kullanıcı,
    /// M3U/Link: EpgUrl) değiştiyse eski önbelleğin sessizce geçersiz
    /// sayılması için kullanılır.
    /// </summary>
    public class EpgCache
    {
        public DateTime FetchedAt { get; set; }
        public string SourceSignature { get; set; } = "";
        public List<EpgProgram> Programs { get; set; } = new();

        // YENİ: XMLTV'deki <channel id="X"><display-name>Ad</display-name>
        // eşlemesi. tvg-id eksik/uyuşmuyorsa isim bazlı eşleştirme
        // (bkz. MainWindow.Epg.cs → NormalizeEpgName/FindEpgProgramsForChannel)
        // için kullanılır — önbellekten okunduğunda da bu eşleşme yeniden
        // kurulabilsin diye burada da saklanır.
        public Dictionary<string, string> ChannelNames { get; set; } = new();
    }
}
