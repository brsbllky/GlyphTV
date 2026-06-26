using Avalonia.Media;
using System;

namespace GlyphTV
{
    /// <summary>
    /// IPTV kaynağı (M3U dosyası, URL linki veya Xtream Code)
    /// </summary>
    public class TvSource
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";    // "M3U" | "Link" | "Xtream"
        public string PathOrUrl { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public bool IsActive { get; set; } = false;

        public string StatusText => IsActive ? "Aktif" : "Seç";
        public IBrush StatusBrush => IsActive
            ? Brush.Parse("#28c840")
            : Brush.Parse("#8b8b95");
    }
}
