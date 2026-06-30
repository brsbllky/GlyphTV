using System;

namespace GlyphTV
{
    /// <summary>
    /// İzleme geçmişi kaydı
    /// </summary>
    public class WatchHistory
    {
        public string Url { get; set; } = "";
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
