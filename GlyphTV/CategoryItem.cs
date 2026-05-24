namespace GlyphTV
{
    /// <summary>
    /// Kategori adı ve içerik sayısını birlikte taşır.
    /// CategoriesGrid DataTemplate'inde kullanılır.
    /// </summary>
    public class CategoryItem
    {
        public string Name  { get; set; } = "";
        public int    Count { get; set; } = 0;

        /// <summary>"Vizyon  (567)" formatında gösterim metni</summary>
        public string DisplayText => $"{Name}  ({Count})";
    }
}
