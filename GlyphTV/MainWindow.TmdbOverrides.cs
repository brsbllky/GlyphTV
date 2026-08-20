// ============================================================
// MainWindow.TmdbOverrides.cs
// Geliştirici/admin TMDb eşleştirme genel liste (override) mekanizması.
//
// SORUN: MainWindow.VodInfo.cs'deki "TMDb Eşleşmesini Düzelt" paneli bir
// düzeltmeyi (channel.TmdbId) sadece o kullanıcının kendi bilgisayarındaki
// channels_{sourceId}.json dosyasına yazıyordu — yani düzeltme SADECE o
// kişide, SADECE o IPTV kaynağında kalıcıydı. GitHub'da yeni bir sürüm
// yayınlansa bile bu düzeltme oraya hiç yansımıyordu; her kullanıcı aynı
// içerik için aynı düzeltmeyi kendi başına tekrar yapmak zorunda kalıyordu.
//
// ÇÖZÜM: Assets/tmdb-overrides.json adında, derleme sırasında .exe İÇİNE
// GÖMÜLEN (embedded resource) bir genel eşleştirme tablosu. Uygulama
// başlarken bu tabloyu bir kez okur. FetchTmdbInfo/SearchTmdbPosterUrl,
// sağlayıcının kendi tmdb_id'sinden sonra, isimle aramaya başvurmadan
// ÖNCE bu tabloya bakar — tabloda bir kayıt varsa artık hiçbir arama
// yapılmaz, doğrudan o ID kullanılır (yani sağlayıcı tmdb_id'si kadar
// güvenilir). Bu dosya git'e commit edilip yeni bir sürüm yayınlandığında,
// düzeltme TÜM kullanıcılara otomatik olarak gider — hiçbirinin panele
// dokunmasına gerek kalmaz.
//
// AKIŞ (geliştirici/admin için):
//   1) Uygulamayı normal şekilde kullan, yanlış/eksik bir poster gördüğünde
//      "TMDb Eşleşmesini Düzelt" panelinden doğru ID'yi bul ve Uygula'ya bas.
//   2) Bu işlem hem o anki görünümü düzeltir HEM DE otomatik olarak
//      %AppData%\GlyphTV\tmdb_overrides_staging.json dosyasına bir satır
//      ekler (AppendOverrideStaging).
//   3) Test bittiğinde bu staging dosyasının içeriğini (entries dizisini)
//      proje kökündeki Assets/tmdb-overrides.json dosyasına kopyala, git'e
//      commit et, yeni sürümü yayınla. Artık bu düzeltme herkeste var.
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlyphTV
{
    public partial class MainWindow
    {
        // Gömülü kaynağın adı — GlyphTV.csproj'daki <LogicalName> ile birebir
        // aynı olmalı.
        private const string TMDB_OVERRIDES_RESOURCE_NAME = "GlyphTV.tmdb-overrides.json";

        // Anahtar: (type, NormalizeTmdbTitle sonucu) → TMDb ID.
        // Lazy + thread-safe tek seferlik yükleme.
        private static Dictionary<(string Type, string Key), int>? _tmdbOverrides;
        private static readonly object _tmdbOverridesLock = new object();

        private static Dictionary<(string Type, string Key), int> GetTmdbOverrides()
        {
            if (_tmdbOverrides != null) return _tmdbOverrides;

            lock (_tmdbOverridesLock)
            {
                if (_tmdbOverrides != null) return _tmdbOverrides;

                var result = new Dictionary<(string, string), int>();
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using var stream = asm.GetManifestResourceStream(TMDB_OVERRIDES_RESOURCE_NAME);
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        string json = reader.ReadToEnd();
                        using var doc = JsonDocument.Parse(json);

                        if (doc.RootElement.TryGetProperty("entries", out var entries) &&
                            entries.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var entry in entries.EnumerateArray())
                            {
                                string type = entry.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                                string key  = entry.TryGetProperty("key",  out var k) ? (k.GetString()  ?? "") : "";
                                int id      = entry.TryGetProperty("tmdbId", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                                    ? idEl.GetInt32() : 0;

                                // tmdbId=0 olan satırlar bilinçli olarak atlanır —
                                // örnek/yer tutucu satırların yanlışlıkla
                                // uygulanmasını engeller (bkz. Assets/tmdb-overrides.json
                                // içindeki örnek satır).
                                if (id <= 0 || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(key))
                                    continue;

                                result[(type, key)] = id;
                            }
                        }
                    }
                }
                catch (Exception ex) { LogError("LoadTmdbOverrides", ex); }

                _tmdbOverrides = result;
                return result;
            }
        }

        /// <summary>
        /// Genel eşleştirme tablosunda bu başlık için kayıtlı bir TMDb ID
        /// var mı diye bakar. Karşılaştırma NormalizeTmdbTitle ile aynı
        /// normalizasyonu kullanır — böylece küçük yazım/boşluk/noktalama
        /// farkları eşleşmeyi bozmaz.
        /// </summary>
        private static int? TryGetTmdbOverrideId(string rawTitle, string type)
        {
            if (string.IsNullOrWhiteSpace(rawTitle)) return null;
            string key = NormalizeTmdbTitle(rawTitle);
            if (string.IsNullOrEmpty(key)) return null;

            var overrides = GetTmdbOverrides();
            return overrides.TryGetValue((type, key), out var id) ? id : null;
        }

        // ─────────────────────────────────────────────────────────────
        // Yerel "staging" dosyası — VodInfoManualIdApply_Click çağırır.
        // Bu dosya Assets/tmdb-overrides.json İLE AYNI FORMATTADIR; admin
        // test sırasında yaptığı her elle düzeltmeyi burada birikmiş
        // bulur ve tek seferde kopyalayıp genel listeye ekleyebilir.
        // Aynı (type, key) çifti tekrar eklenirse üzerine yazılır
        // (yinelenen satır birikmesin diye).
        // ─────────────────────────────────────────────────────────────
        private static readonly object _tmdbOverrideStagingLock = new object();

        private string GetTmdbOverrideStagingPath() => Path.Combine(AppDataDir(), "tmdb_overrides_staging.json");

        internal void AppendOverrideStaging(string rawTitle, string type, int tmdbId)
        {
            if (tmdbId <= 0) return;
            string key = NormalizeTmdbTitle(rawTitle);
            if (string.IsNullOrEmpty(key)) return;

            lock (_tmdbOverrideStagingLock)
            {
                try
                {
                    string path = GetTmdbOverrideStagingPath();
                    JsonObject root;

                    if (File.Exists(path))
                    {
                        try { root = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); }
                        catch { root = new JsonObject(); }
                    }
                    else
                    {
                        root = new JsonObject();
                    }

                    if (root["entries"] is not JsonArray entries)
                    {
                        entries = new JsonArray();
                        root["entries"] = entries;
                    }

                    // Aynı (type, key) zaten varsa güncelle, yoksa ekle.
                    JsonObject? existing = null;
                    foreach (var node in entries)
                    {
                        if (node is JsonObject obj &&
                            (string?)obj["type"] == type &&
                            (string?)obj["key"] == key)
                        {
                            existing = obj;
                            break;
                        }
                    }

                    if (existing != null)
                    {
                        existing["tmdbId"] = tmdbId;
                        existing["title"] = rawTitle;
                    }
                    else
                    {
                        entries.Add(new JsonObject
                        {
                            ["type"] = type,
                            ["key"] = key,
                            ["tmdbId"] = tmdbId,
                            ["title"] = rawTitle
                        });
                    }

                    File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) { LogError("AppendOverrideStaging", ex); }
            }
        }
    }
}
