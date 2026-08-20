// ============================================================
// MainWindow.Tmdb.cs
// TMDb API entegrasyonu: arama, poster yükleme, disk cache,
// önbellek ısıtma (preload)
// ============================================================

using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GlyphTV
{
    public partial class MainWindow
    {
        // ─────────────────────────────────────────────────────────────
        // Önceden derlenmiş Regex'ler — CleanNameForSearch
        // ─────────────────────────────────────────────────────────────
        private static readonly Regex _rxTmdbYear        = new(@"\((\d{4})\)|\[(\d{4})\]", RegexOptions.Compiled);
        private static readonly Regex _rxTmdbBrackets    = new(@"\[.*?\]|\(.*?\)", RegexOptions.Compiled);
        private static readonly Regex _rxLeadingIptvTag  = new(@"^\s*(\[.*?\]\s*|\(.*?\)\s*|(TR|TUR|ENG?|DEU?|FRA?|SPA?|ITA?|VOD|FILM|DIZI|SERIES|4K|UHD|FHD|HD|SD|NETFLIX|DISNEY\+?|AMAZON|PRIME|BLUTV|EXXEN|GA[Iİi]N|APPLE\s*TV\+?|TOD|BEIN(?:\s*CONNECT)?|VIP|Sinema\s*TV|LOCAL|PREMIUM)\s*[/|:\-~]\s*)+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxDotSeparator    = new(@"(?<=\w)\.(?=\w)", RegexOptions.Compiled);
        private static readonly Regex _rxUnderscoreSep   = new(@"(?<=\w)_(?=\w)", RegexOptions.Compiled);
        private static readonly Regex _rxTmdbTechTags    = new(@"\b(4K|UHD|HDR|HDR10|HDR10\+|DV|DoVi|Dolby\s*Vision|FHD|1080p|720p|2160p|480p|HD|SD|HEVC|H\.?265|H\.?264|x264|x265|x266|AV1|10bit|8bit|BluRay|BRRip|BDRip|WEB-?DL|WEBRip|DVDRip|HDTV|REMUX|DUAL|DUAL-AUDIO|Multi|Multisub|MULTISUB|Raw|TR|TUR|ENG|EN|TR-EN|TR-ENG|FR|DE|ES|IT|Dublaj|Dublajlı|Dub|Altyazı|Altyazılı|Altyazi|Sub|Extended|Remastered|Unrated|Directors Cut|Director's Cut|Special Edition|Theatrical|IMAX|Sinema Çekimi|CAM|CAMRip|TS|AAC|AAC2\.0|DTS|DTS-HD|TrueHD|Atmos|Dolby|5\.1|7\.1|Yerli Film|Yabancı Dizi|Yerli Dizi|Fragman|Trailer)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxTmdbEpCode      = new(@"\bS\d{1,2}E\d{1,2}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxTmdbEpCode2     = new(@"\b\d{1,2}x\d{1,2}\b", RegexOptions.Compiled);
        private static readonly Regex _rxTmdbSeasonWord  = new(@"\b(Season|Sezon|Episode|Bölüm|Part|Kısım)\s*\d+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxTmdbStandaloneYear = new(@"(?<=[A-Za-z0-9\s]{3,})\b(19\d{2}|20\d{2})\b", RegexOptions.Compiled);
        private static readonly Regex _rxTmdbSpaces      = new(@"\s+", RegexOptions.Compiled);

        // ─────────────────────────────────────────────────────────────
        // YENİ: Sağlayıcının get_vod_info/get_series_info yanıtındaki
        // releasedate/releaseDate metninden (biçimi sağlayıcıya göre
        // "2024-06-19", "2024", vb. değişebilir) bir yıl çıkarır. Bu, TMDb
        // eşleştirmesinde `year` parametresi olarak kullanılır — bkz.
        // FetchTmdbInfo/SearchTmdbPosterUrl'deki "kısa/jenerik isimli
        // içerikler yıl olmadan belirsiz sayılıp reddediliyordu" düzeltmesi.
        // ─────────────────────────────────────────────────────────────
        private static int? ParseYearFromProviderDate(string? dateStr)
        {
            if (string.IsNullOrWhiteSpace(dateStr)) return null;
            var m = Regex.Match(dateStr, @"(19|20)\d{2}");
            return m.Success && int.TryParse(m.Value, out int y) ? y : null;
        }

        // ─────────────────────────────────────────────────────────────
        // İsim temizleme (TMDb araması için)
        // ─────────────────────────────────────────────────────────────
        private static (string name, int? year) CleanNameForSearch(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return ("", null);
            string name = rawName.Trim();
            int? year = null;

            // 1. Baştaki IPTV sağlayıcı / kategori / kalite etiketlerini temizle (TR |, NETFLIX |, VOD:, 4K -, vb.)
            name = _rxLeadingIptvTag.Replace(name, "").Trim();

            // 2. Parantez içindeki yılı yakala: (2024) veya [2024]
            var yearMatch = _rxTmdbYear.Match(name);
            if (yearMatch.Success)
            {
                if (int.TryParse(yearMatch.Groups[1].Success ? yearMatch.Groups[1].Value : yearMatch.Groups[2].Value, out int y))
                    year = y;
                name = name.Remove(yearMatch.Index, yearMatch.Length).Trim();
            }

            // 3. Nokta ve altçizgileri kelime aralarında boşluğa dönüştür (The.Matrix.1999 -> The Matrix 1999)
            name = _rxDotSeparator.Replace(name, " ");
            name = _rxUnderscoreSep.Replace(name, " ");

            // 4. Eğer yıl hâlâ bulunamadıysa metin içi 4 basamaklı yılı yakala (Örn: Dune 2021 1080p -> Yıl: 2021)
            if (!year.HasValue)
            {
                var standaloneMatch = _rxTmdbStandaloneYear.Match(name);
                if (standaloneMatch.Success && int.TryParse(standaloneMatch.Value, out int y2))
                {
                    year = y2;
                    name = name.Remove(standaloneMatch.Index, standaloneMatch.Length).Trim();
                }
            }

            // 5. Teknik etiketler, kodekler, çözünürlükler ve köşeli parantezler
            name = _rxTmdbTechTags.Replace(name, " ").Trim();
            name = _rxTmdbEpCode.Replace(name, " ").Trim();
            name = _rxTmdbEpCode2.Replace(name, " ").Trim();
            name = _rxTmdbSeasonWord.Replace(name, " ").Trim();
            name = _rxTmdbBrackets.Replace(name, " ").Trim();
            name = _rxTmdbSpaces.Replace(name, " ").Trim();
            name = name.Trim('-', '.', ',', ':', '/', '|', '~', ' ');

            return (name, year);
        }

        // ─────────────────────────────────────────────────────────────
        // Stopwords & Akıllı Aday Üretimi (GetTmdbNameCandidates)
        // ─────────────────────────────────────────────────────────────
        private static readonly HashSet<string> _tmdbStopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "ve","ile","bir","bu","şu","o","için","gibi","kadar","daha","en","çok","az",
            "adlı","isimli","filmi","dizisi","hikayesi","hikâyesi","sezonu","bölümü",
            "mini","the","a","an","of","and","or","in","on","at","to","de","da","ki",
            "mi","mı","mu","mü","her","tüm","son","yeni","efsanesi","masalı","masalları"
        };

        private static readonly Regex _rxDashSeparator = new(@"\s+[-–—/|~]\s*|\s*[-–—/|~]\s+", RegexOptions.Compiled);
        private static readonly Regex _rxColonSeparator = new(@"\s*:\s+", RegexOptions.Compiled);

        private static IEnumerable<string> GetTmdbNameCandidates(string cleanedName)
        {
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(cleanedName))
            {
                yielded.Add(cleanedName);
                yield return cleanedName;
            }

            // 1) Tire, eğik çizgi veya dik çizgi ile ayrılmış birleşik başlıklar
            string? splitRight = null;
            var dashMatch = _rxDashSeparator.Match(cleanedName);
            if (dashMatch.Success && dashMatch.Index > 0 && dashMatch.Index + dashMatch.Length < cleanedName.Length)
            {
                splitRight = cleanedName[(dashMatch.Index + dashMatch.Length)..].Trim();
                string splitLeft = cleanedName[..dashMatch.Index].Trim();

                if (splitRight.Length > 1 && yielded.Add(splitRight)) yield return splitRight;
                if (splitLeft.Length > 1 && yielded.Add(splitLeft)) yield return splitLeft;
            }
            else
            {
                var colonMatch = _rxColonSeparator.Match(cleanedName);
                if (colonMatch.Success && colonMatch.Index > 0 && colonMatch.Index + colonMatch.Length < cleanedName.Length)
                {
                    splitRight = cleanedName[(colonMatch.Index + colonMatch.Length)..].Trim();
                    string colonLeft = cleanedName[..colonMatch.Index].Trim();

                    if (splitRight.Length > 1 && yielded.Add(splitRight)) yield return splitRight;
                    if (colonLeft.Length > 1 && yielded.Add(colonLeft)) yield return colonLeft;
                }
            }

            // 2) İki nokta yerine boşluk içeren tam başlık (örn: "Dune: Part Two" -> "Dune Part Two")
            if (cleanedName.Contains(':'))
            {
                string noColon = cleanedName.Replace(":", " ").Trim();
                noColon = _rxTmdbSpaces.Replace(noColon, " ");
                if (noColon.Length > 1 && yielded.Add(noColon)) yield return noColon;
            }

            // 3) Stopword ve kök çıkarma analizi
            string wordsSource = splitRight ?? cleanedName;
            var rawWords = wordsSource.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            var keptWords = new List<string>();
            foreach (var w in rawWords)
            {
                int apoIdx = w.IndexOfAny(new[] { '\'', '’', '`' });
                string stem = (apoIdx > 1 ? w[..apoIdx] : w).Trim(':', ',', '.', '?', '!', '"', '-');

                if (stem.Length == 0) continue;
                if (_tmdbStopWords.Contains(stem)) continue;
                keptWords.Add(stem);
            }

            if (keptWords.Count >= 2 && keptWords.Count < rawWords.Length)
            {
                string combined = string.Join(" ", keptWords);
                if (yielded.Add(combined)) yield return combined;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: Başlık normalizasyonu + güven eşiği (confidence gate)
        //
        // KÖK SEBEP (yanlış poster/detay): FindTmdbSearchResult önceden
        // TMDb /search sonucundaki İLK öğeyi (results[0]) hiçbir doğrulama
        // yapmadan kabul ediyordu. TMDb'nin arama sıralaması popülerliğe
        // dayandığından, bulanık/kısmi bir sorguda (özellikle tek kelimelik
        // "son çare" adaylarında) alakasız ama popüler bir yapımın ilk sırada
        // dönmesi mümkündü — bu da bazı içeriklerde YANLIŞ poster/oyuncu/
        // özet gösterilmesine yol açıyordu (kullanıcının bildirdiği "yanlış"
        // sorunu). Bu, referans alınan başka bir açık kaynak IPTV
        // uygulamasının TMDb eşleştirme katmanında da (bkz. tmdb-matcher.ts
        // → pickConfidentMatch) aynı prensiple çözülmüş: sonuç, normalize
        // edilmiş başlığı sorguyla TAM eşleşmedikçe ve (yıl biliniyorsa) yayın
        // yılı ±1 tolerans içinde olmadıkça kabul edilmez.
        //
        // NormalizeTmdbTitle: küçük harfe çevirir, Türkçe karakterleri
        // (ı/İ/ş/ğ/ç/ö/ü) ve aksanları sadeleştirir, parantez/köşeli parantez
        // içeriğini, teknik/kalite etiketlerini, baştaki dil kodu/kelimesini,
        // sondaki yıl ve sezon ibarelerini (S05, Sezon 2, Season 3, Staffel 2)
        // temizler, noktalama işaretlerini kaldırır ve boşlukları sadeleştirir
        // — TMDb sonuç başlığı ile aranan ad arasında ADİL bir karşılaştırma
        // yapılabilmesi için.
        // ─────────────────────────────────────────────────────────────
        private static readonly Dictionary<char, char> _turkishNormalizeMap = new()
        {
            ['ı'] = 'i', ['İ'] = 'i', ['I'] = 'i',
            ['ş'] = 's', ['Ş'] = 's',
            ['ğ'] = 'g', ['Ğ'] = 'g',
            ['ç'] = 'c', ['Ç'] = 'c',
            ['ö'] = 'o', ['Ö'] = 'o',
            ['ü'] = 'u', ['Ü'] = 'u',
        };

        private static readonly Regex _rxNormBrackets      = new(@"[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);
        private static readonly Regex _rxNormLeadingLang   = new(@"^\s*[A-Za-z]{2,3}\s*[-|:]\s+", RegexOptions.Compiled);
        private static readonly Regex _rxNormLeadingWord   = new(@"^\s*(multi|english|german|french|arabic|turkish|russian|spanish|italian|deutsch)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // DÜZELTME (kritik bug — "1992", "1923" gibi salt yıldan ibaret
        // başlıklar hep "bulunamadı" veriyordu): Bu regex önceden
        // `\b(19|20)\d{2}\b\s*$` idi — yani başlığın TAMAMI zaten sadece bir
        // yıl sayısıysa (örn. "1992") bunu da bir "sondaki yıl etiketi" sanıp
        // sonucu BOŞ STRING'e indiriyordu; NormalizeTmdbTitle boş döndüğünde
        // PickConfidentMatch hiçbir zaman eşleşme bulamıyordu. Artık lookbehind
        // ile yıldan ÖNCE başka bir kelime/karakter olması şart koşuluyor —
        // "The Matrix 1999" → "The Matrix" (doğru temizlenir), ama "1992" tek
        // başına → hiç dokunulmaz (çünkü öncesinde silinecek başka bir şey yok).
        private static readonly Regex _rxNormTrailingYear  = new(@"(?<=\S)\s+(19|20)\d{2}\s*$", RegexOptions.Compiled);

        private static readonly Regex _rxNormTrailingSeason = new(@"\b(s(?:eason)?\.?\s*\d{1,2}|se[zs]on\w*\s*\d{1,2}|staffel\s*\d{1,2})\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // IPTV sağlayıcılarının başlık sonuna sıkça eklediği yayın kanalı
        // etiketleri (TMDb kayıtlarında ASLA yer almaz, örn. "Medeniyet
        // Kaşifleri TRT" → gerçek TMDb başlığı sadece "Medeniyet Kaşifleri").
        // Aynı yıl-etiketi mantığı: önce başka bir kelime yoksa (yani başlık
        // sadece "TRT" ise) dokunulmaz.
        private static readonly Regex _rxNormTrailingNetwork = new(@"(?<=\S)\s+trt(\s*\d+|\s*belgesel|\s*cocuk|\s*world|\s*spor)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _rxNormPunct         = new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);
        private static readonly Regex _rxNormSpaces        = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex _rxRomanNumerals     = new(@"\b(i{1,3}|iv|v|vi{1,3}|ix|x)\b(?=\s*$|\s+part|\s+bolum|\s+chapter|\s+kisim)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex _rxPartWords         = new(@"\b(part|kisim|bolum|chapter)\s+(one|two|three|four|five|six|seven|eight|nine|ten|bir|iki|uc|dort|bes|alti|yedi|sekiz|dokuz|on)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string ConvertPartWordsToDigits(string text)
        {
            return _rxPartWords.Replace(text, m =>
            {
                string p = m.Groups[1].Value.ToLowerInvariant();
                string w = m.Groups[2].Value.ToLowerInvariant();
                string digit = w switch
                {
                    "one" or "bir" => "1",
                    "two" or "iki" => "2",
                    "three" or "uc" => "3",
                    "four" or "dort" => "4",
                    "five" or "bes" => "5",
                    "six" or "alti" => "6",
                    "seven" or "yedi" => "7",
                    "eight" or "sekiz" => "8",
                    "nine" or "dokuz" => "9",
                    "ten" or "on" => "10",
                    _ => w
                };
                return $"{p} {digit}";
            });
        }

        private static string ConvertRomanNumeralsToDigits(string text)
        {
            return _rxRomanNumerals.Replace(text, m =>
            {
                string r = m.Value.ToLowerInvariant();
                return r switch
                {
                    "i" => "1",
                    "ii" => "2",
                    "iii" => "3",
                    "iv" => "4",
                    "v" => "5",
                    "vi" => "6",
                    "vii" => "7",
                    "viii" => "8",
                    "ix" => "9",
                    "x" => "10",
                    _ => r
                };
            });
        }

        private static string NormalizeTmdbTitle(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw;

            // DÜZELTME: "&" noktalama temizliğinde sessizce boşluğa çevrilip
            // kayboluyordu — "Kayko and Kokosh" ile "Kayko & Kokosh" farklı
            // kelime sayısına düşüp eşleşmiyordu. "&" anlamsal olarak "and"
            // demek olduğundan, punct-strip'ten ÖNCE kelime olarak açılır.
            s = s.Replace("&", " and ");
            s = Regex.Replace(s, @"\bve\b", "and", RegexOptions.IgnoreCase);

            s = _rxNormBrackets.Replace(s, " ");
            s = _rxTmdbTechTags.Replace(s, " ");
            s = _rxNormLeadingLang.Replace(s, " ");
            s = _rxNormLeadingWord.Replace(s, " ");
            s = _rxNormTrailingYear.Replace(s, "");
            s = _rxNormTrailingSeason.Replace(s, "");
            s = _rxNormTrailingNetwork.Replace(s, "");

            var mapped = new StringBuilder(s.Length);
            foreach (var ch in s)
                mapped.Append(_turkishNormalizeMap.TryGetValue(ch, out var m) ? m : ch);
            s = mapped.ToString();

            // Aksan/diyakritik temizliği (örn. "Amélie" → "Amelie")
            s = s.Normalize(NormalizationForm.FormD);
            var stripped = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    stripped.Append(ch);
            }
            s = stripped.ToString().Normalize(NormalizationForm.FormC);

            s = s.ToLowerInvariant();
            s = ConvertPartWordsToDigits(s);
            s = ConvertRomanNumeralsToDigits(s);
            s = _rxNormPunct.Replace(s, " ");
            s = _rxNormSpaces.Replace(s, " ").Trim();

            return s;
        }

        // ─────────────────────────────────────────────────────────────
        // TMDb Sonuç Uyumluluk Doğrulaması (Sanity Check)
        // ─────────────────────────────────────────────────────────────
        private static bool IsTmdbResultCompatible(
            JsonElement detail, string type, string rawName, string? knownOriginalName, int? knownYear)
        {
            string titleKey = type == "tv" ? "name" : "title";
            string origKey  = type == "tv" ? "original_name" : "original_title";
            string dateKey  = type == "tv" ? "first_air_date" : "release_date";

            string t1 = XtreamStr(detail, titleKey);
            string t2 = XtreamStr(detail, origKey);

            string normT1 = NormalizeTmdbTitle(t1);
            string normT2 = NormalizeTmdbTitle(t2);

            var namesToCheck = new List<string>();
            if (!string.IsNullOrWhiteSpace(knownOriginalName)) namesToCheck.Add(knownOriginalName!);
            if (!string.IsNullOrWhiteSpace(rawName)) namesToCheck.Add(rawName);

            foreach (var name in namesToCheck)
            {
                var (cleanName, _) = CleanNameForSearch(name);
                string normQuery = NormalizeTmdbTitle(cleanName);
                if (string.IsNullOrEmpty(normQuery)) continue;

                // 1. Birebir tam normalize eşleşme
                if (normT1 == normQuery || normT2 == normQuery) return true;

                // 2. Yüksek benzerlik (Levenshtein) - harf hatası veya küçük diyakritik farkı
                if (CalculateTitleSimilarity(normT1, normQuery) >= 0.85 || (!string.IsNullOrEmpty(normT2) && CalculateTitleSimilarity(normT2, normQuery) >= 0.85))
                    return true;

                // 3. Bölünmüş / iki noktalı / stopword adayları üzerinden tam eşleşme
                foreach (var cand in GetTmdbNameCandidates(cleanName))
                {
                    string normCand = NormalizeTmdbTitle(cand);
                    if (!string.IsNullOrEmpty(normCand))
                    {
                        if (normT1 == normCand || (!string.IsNullOrEmpty(normT2) && normT2 == normCand))
                            return true;
                    }
                }
            }

            // 4. Yıl kontrolü: Eğer bilinen bir yıl varsa ve TMDb çıkış yılıyla birebir örtüşüyorsa VE başlık da en azından makul benzerlik taşıyorsa
            int? tmdbYear = ParseYearFromProviderDate(XtreamStr(detail, dateKey));
            if (knownYear.HasValue && tmdbYear.HasValue && Math.Abs(knownYear.Value - tmdbYear.Value) <= 1)
            {
                foreach (var name in namesToCheck)
                {
                    var (cleanName, _) = CleanNameForSearch(name);
                    string normQuery = NormalizeTmdbTitle(cleanName);
                    if (!string.IsNullOrEmpty(normQuery))
                    {
                        if (CalculateTitleSimilarity(normT1, normQuery) >= 0.65 || (!string.IsNullOrEmpty(normT2) && CalculateTitleSimilarity(normT2, normQuery) >= 0.65))
                            return true;
                    }
                }
            }

            return false;
        }

        private static long GetJsonLong(JsonElement el, string key) =>
            el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

        private static double GetJsonDouble(JsonElement el, string key) =>
            el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

        private static JsonElement PickMostPopular(List<JsonElement> results)
        {
            var best = results[0];
            long bestVotes = GetJsonLong(best, "vote_count");
            double bestPop = GetJsonDouble(best, "popularity");
            for (int i = 1; i < results.Count; i++)
            {
                long v = GetJsonLong(results[i], "vote_count");
                double p = GetJsonDouble(results[i], "popularity");
                if (v > bestVotes || (v == bestVotes && p > bestPop))
                {
                    best = results[i]; bestVotes = v; bestPop = p;
                }
            }
            return best;
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: "Kararlı üstünlük" (decisive dominance) eşiği.
        //
        // KÖK SEBEP (sessiz/tespit edilemeyen yanlış poster-detay eşleşmeleri):
        // Yıl bilgisi yokken (Türkçe IPTV kataloğunda neredeyse hiç yok)
        // önceki mantık, aynı isimle birden fazla TMDb kaydı varsa hiç
        // sorgulamadan "en popüler" olanı seçiyordu. Bu, "Fallout" gibi
        // yapımlarda genelde doğru sonucu verse de (popülerlik farkı çok
        // büyük olduğundan), popülerlik farkı KÜÇÜK olan (yani gerçekten
        // belirsiz) durumlarda YANLIŞ içeriğin posterini/detayını sessizce
        // gösterebiliyordu — ve bu hatalar crash.log'a hiç düşmüyor (çünkü
        // "bulunamadı" değil, "yanlış bulundu" oluyor), yani kullanıcı bunu
        // ancak o içeriği zaten tanıyorsa fark edebiliyor.
        //
        // DÜZELTME (regresyon — daha önce doğru gösterilen "Adhura", "The
        // Confession", "The Test" gibi az bilinen/yabancı yapımların artık
        // hiç poster/detay göstermemesi): İlk sürümde asgari oy eşiği 50
        // olarak seçilmişti. Bu eşik Hollywood yapımları için makul olsa
        // da, TMDb'de birçok meşru yabancı dizi/film TEK HANELİ (hatta 0)
        // oy sayısıyla kayıtlıdır — bu içerikler rakipsiz (tek veya açık
        // ara önde) olsalar bile salt bu yüksek tabana takılıp
        // reddediliyordu. Artık taban çok daha düşük (MIN_CONFIDENT_VOTE_COUNT)
        // ve alternatif olarak TMDb'nin daha granüler "popularity" skoru da
        // (MIN_CONFIDENT_POPULARITY) kabul ediliyor — asıl güvence hâlâ
        // "kararlılık" (ikinci adaydan belirgin şekilde önde olma) şartında.
        // Oy sayıları EŞİTSE (çoğunlukla iki taraf da 0 oy aldığında olur)
        // "popularity" skoruna geçilir; o da eşitse/ayırt edici değilse
        // reddedilir.
        //
        // Tek bir birebir eşleşme varsa (exactTitleMatches.Count == 1) bu
        // eşik hiç uygulanmaz — zaten isim bazında belirsizlik yok.
        // ─────────────────────────────────────────────────────────────
        private const long MIN_CONFIDENT_VOTE_COUNT = 3;
        private const double MIN_CONFIDENT_POPULARITY = 1.0;
        private const double DOMINANCE_RATIO = 2.0;

        private static JsonElement? PickDecisivelyDominant(List<JsonElement> results)
        {
            if (results.Count == 1) return results[0];

            var ordered = results
                .Select(r => (Result: r, Votes: GetJsonLong(r, "vote_count"), Pop: GetJsonDouble(r, "popularity")))
                .OrderByDescending(x => x.Votes)
                .ThenByDescending(x => x.Pop)
                .ToList();

            var top = ordered[0];
            var runnerUp = ordered[1];

            // Top adayın en azından gerçekten "var olan"/izlenen bir yapım
            // olduğunu gösteren asgari taban — bunu sağlamıyorsa (neredeyse
            // hiç oy/ilgi yoksa) popülerlik sıralamasına güvenmek anlamsız,
            // reddedilir.
            bool topIsRealEntry = top.Votes >= MIN_CONFIDENT_VOTE_COUNT || top.Pop >= MIN_CONFIDENT_POPULARITY;
            if (!topIsRealEntry) return null;

            // Oy sayıları farklıysa oy bazlı kararlılık kontrol edilir;
            // eşitse (çoğunlukla iki taraf da az bilinen olup 0 oy aldığında
            // olur) TMDb'nin daha granüler "popularity" skoruna geçilir.
            bool decisive = top.Votes != runnerUp.Votes
                ? top.Votes >= Math.Max(1, runnerUp.Votes) * DOMINANCE_RATIO
                : (runnerUp.Pop <= 0 || top.Pop >= runnerUp.Pop * DOMINANCE_RATIO);

            return decisive ? top.Result : null;
        }

        // ─────────────────────────────────────────────────────────────
        // String Benzerlik & Levenshtein Mesafesi
        // ─────────────────────────────────────────────────────────────
        private static int ComputeLevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
            if (string.IsNullOrEmpty(t)) return s.Length;

            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }

        private static double CalculateTitleSimilarity(string s1, string s2)
        {
            if (string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase)) return 1.0;
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0.0;
            int maxLen = Math.Max(s1.Length, s2.Length);
            if (maxLen == 0) return 1.0;
            int dist = ComputeLevenshteinDistance(s1, s2);
            return 1.0 - ((double)dist / maxLen);
        }

        // ─────────────────────────────────────────────────────────────
        // Güvenilir Eşleşme Seçimi (PickConfidentMatch)
        // ─────────────────────────────────────────────────────────────
        private static JsonElement? PickConfidentMatch(JsonElement results, string queryTitle, int? queryYear, string type)
        {
            if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) return null;

            string wantedTitle = NormalizeTmdbTitle(queryTitle);
            if (string.IsNullOrEmpty(wantedTitle)) return null;

            string titleKey = type == "tv" ? "name" : "title";
            string origKey  = type == "tv" ? "original_name" : "original_title";
            string dateKey  = type == "tv" ? "first_air_date" : "release_date";

            var exactTitleMatches = new List<JsonElement>();
            var highSimilarityMatches = new List<JsonElement>();

            foreach (var r in results.EnumerateArray())
            {
                string t1 = XtreamStr(r, titleKey);
                string t2 = XtreamStr(r, origKey);
                string normT1 = NormalizeTmdbTitle(t1);
                string normT2 = !string.IsNullOrEmpty(t2) ? NormalizeTmdbTitle(t2) : "";

                if (normT1 == wantedTitle || (!string.IsNullOrEmpty(normT2) && normT2 == wantedTitle))
                {
                    exactTitleMatches.Add(r);
                }
                else
                {
                    double sim1 = CalculateTitleSimilarity(normT1, wantedTitle);
                    double sim2 = !string.IsNullOrEmpty(normT2) ? CalculateTitleSimilarity(normT2, wantedTitle) : 0.0;
                    double maxSim = Math.Max(sim1, sim2);

                    // Yüksek benzerlik toleransı (harf hatası / diyakritik kaçması)
                    if (maxSim >= 0.88 || (wantedTitle.Length >= 6 && maxSim >= 0.82))
                    {
                        highSimilarityMatches.Add(r);
                    }
                }
            }

            var candidateList = exactTitleMatches.Count > 0 ? exactTitleMatches : highSimilarityMatches;
            if (candidateList.Count == 0) return null;

            if (!queryYear.HasValue)
            {
                return PickDecisivelyDominant(candidateList);
            }

            var yearMatches = new List<JsonElement>();
            foreach (var r in candidateList)
            {
                string dateStr = XtreamStr(r, dateKey);
                if (dateStr.Length < 4 || !int.TryParse(dateStr.AsSpan(0, 4), out int y))
                {
                    if (exactTitleMatches.Contains(r)) yearMatches.Add(r);
                    continue;
                }

                if (Math.Abs(y - queryYear.Value) <= 1)
                {
                    yearMatches.Add(r);
                    continue;
                }
                if (type == "tv" && y <= queryYear.Value)
                {
                    yearMatches.Add(r);
                }
            }

            if (yearMatches.Count > 0) return PickMostPopular(yearMatches);
            return exactTitleMatches.Count > 0 ? PickDecisivelyDominant(exactTitleMatches) : null;
        }

        // ─────────────────────────────────────────────────────────────
        // TMDb API hız sınırlama + 429 farkındalığı
        // ─────────────────────────────────────────────────────────────
        private static readonly SemaphoreSlim _tmdbApiThrottle = new(4, 4);

        private static async Task<string?> TmdbApiGetAsync(string url, string debugContext)
        {
            await _tmdbApiThrottle.WaitAsync();
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        return await _tmdbHttpClient!.GetStringAsync(url);
                    }
                    catch (HttpRequestException hre) when (
                        hre.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt == 0)
                    {
                        await Task.Delay(1500);
                    }
                    catch (HttpRequestException hre)
                    {
                        if (hre.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                            LogError($"TmdbRateLimited({debugContext})", hre);
                        else
                            LogError($"TmdbHttpError({debugContext})", hre);
                        return null;
                    }
                    catch (Exception ex)
                    {
                        LogError($"TmdbHttpError({debugContext})", ex);
                        return null;
                    }
                }
                return null;
            }
            finally { _tmdbApiThrottle.Release(); }
        }

        /// <summary>
        /// Verilen isim adaylarının her birini (önce Türkçe, ardından İngilizce; önce yıl ile, sonra yılsız) dener.
        /// </summary>
        private async Task<JsonElement?> FindTmdbSearchResult(string type, string searchName, int? year)
        {
            EnsureTmdbHttpClient();

            var languages = new[] { "tr-TR", "en-US" };

            foreach (var candidate in GetTmdbNameCandidates(searchName))
            {
                if (string.IsNullOrWhiteSpace(candidate) || candidate.Length < 2) continue;

                var yearAttempts = year.HasValue ? new int?[] { year, null } : new int?[] { null };

                foreach (var lang in languages)
                {
                    foreach (var y in yearAttempts)
                    {
                        string yearParam = y.HasValue
                            ? (type == "tv" ? $"&first_air_date_year={y.Value}" : $"&year={y.Value}")
                            : "";
                        string url = $"{TMDB_BASE}/search/{type}?api_key={TMDB_API_KEY}&language={lang}&query={Uri.EscapeDataString(candidate)}{yearParam}";

                        try
                        {
                            string? json = await TmdbApiGetAsync(url, "search");
                            if (json == null) continue;
                            using var doc = JsonDocument.Parse(json);
                            var results = doc.RootElement.GetProperty("results");

                            var match = PickConfidentMatch(results, candidate, year, type);
                            if (match.HasValue) return match.Value.Clone();
                        }
                        catch { }
                    }
                }
            }

            return null;
        }

        // ─────────────────────────────────────────────────────────────
        // Tür (VOD / Dizi) Çapraz Arama
        // ─────────────────────────────────────────────────────────────
        private async Task<(string type, JsonElement result)?> FindTmdbSearchResultAnyType(
            string primaryType, string searchName, int? year)
        {
            var primary = await FindTmdbSearchResult(primaryType, searchName, year);
            if (primary.HasValue) return (primaryType, primary.Value);

            string otherType = primaryType == "tv" ? "movie" : "tv";
            var alt = await FindTmdbSearchResult(otherType, searchName, year);
            if (alt.HasValue) return (otherType, alt.Value);

            return null;
        }

        // ─────────────────────────────────────────────────────────────
        // Sağlayıcı verisiyle detay modalını TABAN olarak doldur
        // ─────────────────────────────────────────────────────────────
        private void ApplyProviderFallbackInfo(
            string genre, string director, string cast,
            string duration, string releaseDate, string rating, string plot)
        {
            if (!string.IsNullOrEmpty(genre))    VodInfoGenre.Text = genre;
            if (!string.IsNullOrEmpty(director)) { VodInfoDirector.Text = director; VodInfoDirRow.IsVisible = true; }
            if (!string.IsNullOrEmpty(cast))     { VodInfoCast.Text = cast; VodInfoCastRow.IsVisible = true; }
            if (!string.IsNullOrEmpty(duration) && duration != "0")
            { VodInfoDuration.Text = duration + " dk"; VodInfoDurRow.IsVisible = true; }
            if (!string.IsNullOrEmpty(releaseDate)) { VodInfoDate.Text = releaseDate; VodInfoDateRow.IsVisible = true; }
            if (!string.IsNullOrEmpty(rating) && rating != "0")
            {
                if (double.TryParse(rating, NumberStyles.Any, CultureInfo.InvariantCulture, out double r) && r > 0)
                { VodInfoAge.Text = $"⭐ {r:F1}/10"; VodInfoAgeRow.IsVisible = true; }
            }
            if (!string.IsNullOrEmpty(plot)) { VodInfoPlot.Text = plot; VodInfoPlotRow.IsVisible = true; }
        }

        // ─────────────────────────────────────────────────────────────
        // TMDb'den film/dizi bilgisi ve poster çek (Kalıcı Disk Önbellekli)
        // ─────────────────────────────────────────────────────────────
        private async Task FetchTmdbInfo(
            string rawName, string contentType, SeriesCard? seriesCard = null,
            int? knownTmdbId = null, string? knownOriginalName = null, int? knownYear = null)
        {
            try
            {
                string type = contentType == "Dizi" ? "tv" : "movie";

                string overrideLookupName = !string.IsNullOrWhiteSpace(knownOriginalName) ? knownOriginalName! : rawName;
                int? overrideId = TryGetTmdbOverrideId(overrideLookupName, type);
                if (overrideId.HasValue) knownTmdbId = overrideId;

                bool hasKnownId = knownTmdbId.HasValue && knownTmdbId.Value > 0;
                bool isOverridden = overrideId.HasValue && overrideId.Value == knownTmdbId;

                string searchName = "";
                int? year = null;
                string cacheKey;

                if (hasKnownId)
                {
                    cacheKey = $"id_{knownTmdbId!.Value}_{type}";
                }
                else
                {
                    string nameForSearch = !string.IsNullOrWhiteSpace(knownOriginalName)
                        ? knownOriginalName!
                        : rawName;

                    (searchName, year) = CleanNameForSearch(nameForSearch);
                    if (string.IsNullOrEmpty(searchName) || searchName.Length < 2) return;

                    year = knownYear ?? year;
                    cacheKey = $"{searchName}_{contentType}_{year?.ToString() ?? "-"}";
                }

                // 1. In-Memory Cache Kontrolü
                JsonElement? cached;
                lock (_posterCacheLock)
                {
                    if (_tmdbCache.TryGetValue(cacheKey, out cached))
                    {
                        if (cached.HasValue)
                        {
                            _ = ApplyTmdbData(cached.Value, contentType, seriesCard);
                            return;
                        }
                    }
                }

                // 2. Kalıcı Disk Önbelleği Kontrolü
                string metaDiskPath = GetTmdbMetaDiskPath(cacheKey);
                if (File.Exists(metaDiskPath))
                {
                    try
                    {
                        string diskJson = await File.ReadAllTextAsync(metaDiskPath);
                        if (!string.IsNullOrWhiteSpace(diskJson))
                        {
                            using var diskDoc = JsonDocument.Parse(diskJson);
                            var diskEl = diskDoc.RootElement.Clone();
                            SetTmdbCache(cacheKey, diskEl);
                            await ApplyTmdbData(diskEl, type == "tv" ? "Dizi" : "VOD", seriesCard);
                            return;
                        }
                    }
                    catch { }
                }

                int tmdbId = 0;
                string resolvedType = type;
                string? detailJson = null;

                if (hasKnownId)
                {
                    EnsureTmdbHttpClient();
                    string checkUrl = $"{TMDB_BASE}/{resolvedType}/{knownTmdbId!.Value}?api_key={TMDB_API_KEY}&language=tr-TR&append_to_response=credits";
                    detailJson = await TmdbApiGetAsync(checkUrl, "details-by-id");

                    if (detailJson == null)
                    {
                        string altType = resolvedType == "tv" ? "movie" : "tv";
                        string altUrl = $"{TMDB_BASE}/{altType}/{knownTmdbId!.Value}?api_key={TMDB_API_KEY}&language=tr-TR&append_to_response=credits";
                        detailJson = await TmdbApiGetAsync(altUrl, "details-by-id-alt");
                        if (detailJson != null) resolvedType = altType;
                    }

                    if (detailJson != null)
                    {
                        bool validId = isOverridden;
                        if (!validId)
                        {
                            try
                            {
                                using var checkDoc = JsonDocument.Parse(detailJson);
                                validId = IsTmdbResultCompatible(checkDoc.RootElement, resolvedType, rawName, knownOriginalName, knownYear);
                            }
                            catch { validId = false; }
                        }

                        if (validId)
                        {
                            tmdbId = knownTmdbId!.Value;
                        }
                        else
                        {
                            hasKnownId = false;
                            detailJson = null;
                        }
                    }
                    else
                    {
                        hasKnownId = false;
                    }
                }

                if (!hasKnownId)
                {
                    if (string.IsNullOrEmpty(searchName))
                    {
                        string nameForSearch = !string.IsNullOrWhiteSpace(knownOriginalName)
                            ? knownOriginalName!
                            : rawName;
                        (searchName, year) = CleanNameForSearch(nameForSearch);
                        if (string.IsNullOrEmpty(searchName) || searchName.Length < 2) return;
                        year = knownYear ?? year;
                        cacheKey = $"{searchName}_{contentType}_{year?.ToString() ?? "-"}";
                    }

                    var match = await FindTmdbSearchResultAnyType(type, searchName, year);
                    if (match == null)
                    {
                        SetTmdbCache(cacheKey, null);
                        return;
                    }

                    tmdbId       = match.Value.result.GetProperty("id").GetInt32();
                    resolvedType = match.Value.type;

                    EnsureTmdbHttpClient();
                    string detailUrl = $"{TMDB_BASE}/{resolvedType}/{tmdbId}?api_key={TMDB_API_KEY}&language=tr-TR&append_to_response=credits";
                    detailJson = await TmdbApiGetAsync(detailUrl, "details");

                    if (detailJson == null)
                    {
                        string altType = resolvedType == "tv" ? "movie" : "tv";
                        string altUrl = $"{TMDB_BASE}/{altType}/{tmdbId}?api_key={TMDB_API_KEY}&language=tr-TR&append_to_response=credits";
                        string? altJson = await TmdbApiGetAsync(altUrl, "details-alt-type");
                        if (altJson != null)
                        {
                            detailJson = altJson;
                            resolvedType = altType;
                        }
                    }
                }

                if (detailJson == null) return;

                var detailDoc = JsonDocument.Parse(detailJson);
                try
                {
                    var detail = detailDoc.RootElement;

                    string overviewValue = detail.TryGetProperty("overview", out var ovEl) && ovEl.ValueKind == JsonValueKind.String
                        ? (ovEl.GetString() ?? "") : "";
                    string originalLang = detail.TryGetProperty("original_language", out var olEl) && olEl.ValueKind == JsonValueKind.String
                        ? (olEl.GetString() ?? "") : "";

                    if (string.IsNullOrWhiteSpace(overviewValue) &&
                        !string.IsNullOrEmpty(originalLang) &&
                        !originalLang.Equals("tr", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string fallbackUrl = $"{TMDB_BASE}/{resolvedType}/{tmdbId}?api_key={TMDB_API_KEY}&language={originalLang}";
                            string? fallbackJson = await TmdbApiGetAsync(fallbackUrl, "details-lang-fallback");
                            if (fallbackJson != null)
                            {
                                using var fallbackDoc = JsonDocument.Parse(fallbackJson);
                                if (fallbackDoc.RootElement.TryGetProperty("overview", out var fovEl) &&
                                    fovEl.ValueKind == JsonValueKind.String)
                                {
                                    string fallbackOverview = fovEl.GetString() ?? "";
                                    if (!string.IsNullOrWhiteSpace(fallbackOverview))
                                    {
                                        var node = JsonNode.Parse(detailJson)!.AsObject();
                                        node["overview"] = fallbackOverview;
                                        var mergedDoc = JsonDocument.Parse(node.ToJsonString());
                                        detailDoc.Dispose();
                                        detailDoc = mergedDoc;
                                        detail = detailDoc.RootElement;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    // Hem in-memory hem de disk önbelleğine kaydet
                    SetTmdbCache(cacheKey, detail.Clone());

                    try
                    {
                        string saveJson = detail.GetRawText();
                        await File.WriteAllTextAsync(metaDiskPath, saveJson);
                    }
                    catch { }

                    await ApplyTmdbData(detail, resolvedType == "tv" ? "Dizi" : "VOD", seriesCard);
                }
                finally { detailDoc.Dispose(); }
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // TMDb Ülke ve Dil yardımcıları
        // ─────────────────────────────────────────────────────────────
        private static string FormatTmdbCountry(JsonElement detail)
        {
            var countries = new List<string>();
            if (detail.TryGetProperty("production_countries", out var pc) && pc.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in pc.EnumerateArray())
                {
                    if (c.TryGetProperty("iso_3166_1", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
                    {
                        string code = codeEl.GetString() ?? "";
                        string trName = MapCountryCodeToTurkish(code);
                        if (!string.IsNullOrEmpty(trName) && !countries.Contains(trName)) countries.Add(trName);
                    }
                    else if (c.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    {
                        string n = nameEl.GetString() ?? "";
                        if (!string.IsNullOrEmpty(n) && !countries.Contains(n)) countries.Add(n);
                    }
                    if (countries.Count >= 2) break;
                }
            }
            if (countries.Count == 0 && detail.TryGetProperty("origin_country", out var oc) && oc.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in oc.EnumerateArray())
                {
                    if (c.ValueKind == JsonValueKind.String)
                    {
                        string code = c.GetString() ?? "";
                        string trName = MapCountryCodeToTurkish(code);
                        if (!string.IsNullOrEmpty(trName) && !countries.Contains(trName)) countries.Add(trName);
                    }
                    if (countries.Count >= 2) break;
                }
            }
            return string.Join(", ", countries);
        }

        private static string MapCountryCodeToTurkish(string code) => code.ToUpperInvariant() switch
        {
            "TR" => "Türkiye",
            "US" => "ABD",
            "GB" or "UK" => "Birleşik Krallık",
            "DE" => "Almanya",
            "FR" => "Fransa",
            "IT" => "İtalya",
            "ES" => "İspanya",
            "KR" => "Güney Kore",
            "JP" => "Japonya",
            "CN" => "Çin",
            "IN" => "Hindistan",
            "CA" => "Kanada",
            "AU" => "Avustralya",
            "RU" => "Rusya",
            "SE" => "İsveç",
            "NO" => "Norveç",
            "DK" => "Danimarka",
            "BR" => "Brezilya",
            "MX" => "Meksika",
            "NL" => "Hollanda",
            "BE" => "Belçika",
            "PL" => "Polonya",
            "IE" => "İrlanda",
            "NZ" => "Yeni Zelanda",
            _ => code
        };

        // ─────────────────────────────────────────────────────────────
        // TMDb verisini UI'ya uygula
        // ─────────────────────────────────────────────────────────────
        private async Task ApplyTmdbData(JsonElement detail, string contentType, SeriesCard? seriesCard)
        {
            try
            {
                string SafeTmdb(JsonElement el, string key)
                {
                    if (!el.TryGetProperty(key, out var val)) return "";
                    return val.ValueKind switch
                    {
                        JsonValueKind.String => val.GetString() ?? "",
                        JsonValueKind.Number => val.GetRawText(),
                        _ => ""
                    };
                }

                string genres = "";
                if (detail.TryGetProperty("genres", out var genresArr) && genresArr.ValueKind == JsonValueKind.Array)
                    genres = string.Join(", ", genresArr.EnumerateArray()
                        .Where(g => g.TryGetProperty("name", out _))
                        .Select(g => g.GetProperty("name").GetString()));

                string cast = "";
                if (detail.TryGetProperty("credits", out var credits) &&
                    credits.TryGetProperty("cast", out var castArr) && castArr.ValueKind == JsonValueKind.Array)
                    cast = string.Join(", ", castArr.EnumerateArray()
                        .Take(6)
                        .Where(c => c.TryGetProperty("name", out _))
                        .Select(c => c.GetProperty("name").GetString()));

                string director = "";
                if (detail.TryGetProperty("credits", out var credits2) &&
                    credits2.TryGetProperty("crew", out var crewArr) && crewArr.ValueKind == JsonValueKind.Array)
                {
                    director = string.Join(", ", crewArr.EnumerateArray()
                        .Where(c => c.TryGetProperty("job", out var j) && j.GetString() == "Director")
                        .Take(2)
                        .Where(c => c.TryGetProperty("name", out _))
                        .Select(c => c.GetProperty("name").GetString()));

                    if (string.IsNullOrEmpty(director) &&
                        detail.TryGetProperty("created_by", out var creators) && creators.ValueKind == JsonValueKind.Array)
                        director = string.Join(", ", creators.EnumerateArray()
                            .Take(2)
                            .Where(c => c.TryGetProperty("name", out _))
                            .Select(c => c.GetProperty("name").GetString()));
                }

                string runtime = "";
                if (contentType == "Dizi")
                {
                    if (detail.TryGetProperty("number_of_seasons", out var ns) && ns.TryGetInt32(out int seasons) && seasons > 0)
                    {
                        if (detail.TryGetProperty("number_of_episodes", out var ne) && ne.TryGetInt32(out int episodes) && episodes > 0)
                            runtime = $"{seasons} Sezon • {episodes} Bölüm";
                        else
                            runtime = $"{seasons} Sezon";
                    }
                    else if (detail.TryGetProperty("episode_run_time", out var ert) &&
                        ert.ValueKind == JsonValueKind.Array && ert.GetArrayLength() > 0)
                    {
                        runtime = ert[0].GetRawText() + " dk";
                    }
                }
                else
                {
                    string rawRuntime = SafeTmdb(detail, "runtime");
                    if (int.TryParse(rawRuntime, out int mins) && mins > 0)
                    {
                        runtime = mins >= 60 ? $"{mins / 60}s {mins % 60}dk" : $"{mins} dk";
                    }
                    else if (!string.IsNullOrEmpty(rawRuntime) && rawRuntime != "0")
                    {
                        runtime = rawRuntime + " dk";
                    }
                }

                string titleKey   = contentType == "Dizi" ? "name"             : "title";
                string origKey    = contentType == "Dizi" ? "original_name"    : "original_title";
                string dateKey    = contentType == "Dizi" ? "first_air_date"   : "release_date";

                string tmdbTitle = SafeTmdb(detail, titleKey);
                string origName  = SafeTmdb(detail, origKey);
                string relDate   = SafeTmdb(detail, dateKey);
                string plot      = SafeTmdb(detail, "overview");
                string tagline   = SafeTmdb(detail, "tagline");
                string ratingStr = SafeTmdb(detail, "vote_average");
                string voteCountStr = SafeTmdb(detail, "vote_count");
                string poster    = SafeTmdb(detail, "poster_path");
                string backdrop  = SafeTmdb(detail, "backdrop_path");
                string country   = FormatTmdbCountry(detail);

                string formattedYear = "";
                if (!string.IsNullOrEmpty(relDate) && relDate.Length >= 4)
                {
                    formattedYear = relDate[..4];
                }

                string formattedPlot = plot;
                if (!string.IsNullOrWhiteSpace(tagline))
                {
                    formattedPlot = !string.IsNullOrWhiteSpace(plot) ? $"“{tagline}”\n\n{plot}" : $"“{tagline}”";
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!string.IsNullOrEmpty(genres))  VodInfoGenre.Text = genres;
                    if (!string.IsNullOrEmpty(origName) && !origName.Equals(tmdbTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        VodInfoOrigName.Text = origName;
                        VodInfoOrigRow.IsVisible = true;
                    }
                    if (!string.IsNullOrEmpty(director)) { VodInfoDirector.Text = director; VodInfoDirRow.IsVisible  = true; }
                    if (!string.IsNullOrEmpty(cast))     { VodInfoCast.Text     = cast;     VodInfoCastRow.IsVisible = true; }
                    if (!string.IsNullOrEmpty(runtime))  { VodInfoDuration.Text = runtime;  VodInfoDurRow.IsVisible  = true; }
                    if (!string.IsNullOrEmpty(formattedYear)) { VodInfoDate.Text = formattedYear; VodInfoDateRow.IsVisible = true; }
                    if (!string.IsNullOrEmpty(country))  { VodInfoCountry.Text  = country;  VodInfoCountryRow.IsVisible = true; }
                    if (!string.IsNullOrEmpty(ratingStr) && ratingStr != "0")
                    {
                        if (double.TryParse(ratingStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double rating) && rating > 0)
                        {
                            if (int.TryParse(voteCountStr, out int vCount) && vCount > 0)
                            {
                                string countText = vCount >= 1000 ? $"{(vCount / 1000.0):F1}k" : vCount.ToString();
                                VodInfoAge.Text = $"⭐ {rating:F1}/10 ({countText})";
                            }
                            else
                            {
                                VodInfoAge.Text = $"⭐ {rating:F1}/10";
                            }
                            VodInfoAgeRow.IsVisible = true;
                        }
                    }
                    if (!string.IsNullOrEmpty(formattedPlot)) { VodInfoPlot.Text = formattedPlot; VodInfoPlotRow.IsVisible = true; }
                });

                // Arka plan silüeti (Backdrop) indir ve uygula
                Bitmap? backdropBitmap = null;
                if (!string.IsNullOrEmpty(backdrop))
                {
                    try
                    {
                        bool memHit;
                        lock (_posterCacheLock)
                            memHit = _tmdbBackdropCache.TryGetValue(backdrop, out backdropBitmap);

                        if (!memHit || backdropBitmap == null)
                        {
                            string diskPath = GetBackdropDiskPath(backdrop);
                            if (File.Exists(diskPath))
                            {
                                try
                                {
                                    var bytes = await File.ReadAllBytesAsync(diskPath);
                                    using var ms = new MemoryStream(bytes);
                                    backdropBitmap = Bitmap.DecodeToWidth(ms, 1280);
                                }
                                catch { }
                            }

                            if (backdropBitmap == null)
                            {
                                string backdropUrl = TMDB_BACKDROP_IMG + backdrop;
                                EnsureTmdbHttpClient();
                                var bBytes = await _tmdbHttpClient!.GetByteArrayAsync(backdropUrl);
                                await File.WriteAllBytesAsync(diskPath, bBytes);
                                using var ms = new MemoryStream(bBytes);
                                backdropBitmap = Bitmap.DecodeToWidth(ms, 1280);
                            }

                            if (backdropBitmap != null)
                            {
                                SetBackdropCache(backdrop, backdropBitmap);
                            }
                        }

                        if (backdropBitmap != null)
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                VodInfoBackdropImage.Source = backdropBitmap;
                            });
                        }
                    }
                    catch { }
                }

                // Poster indir ve uygula
                if (!string.IsNullOrEmpty(poster))
                {
                    try
                    {
                        string posterUrl = TMDB_IMG + poster;
                        EnsureTmdbHttpClient();
                        var posterBytes = await _tmdbHttpClient!.GetByteArrayAsync(posterUrl);
                        using var ms = new MemoryStream(posterBytes);
                        var bitmap = Bitmap.DecodeToWidth(ms, 300);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            VodInfoPoster.Background = Avalonia.Media.Brushes.Transparent;
                            VodInfoPoster.Child = new Avalonia.Controls.Image
                                { Source = bitmap, Stretch = Avalonia.Media.Stretch.UniformToFill };

                            if (seriesCard != null)
                            {
                                seriesCard.LogoBitmap = bitmap;
                                SetPosterCache(seriesCard.ShowName, bitmap);
                            }

                            // Zemin silüeti henüz boşsa posteri silüet olarak ata
                            if (VodInfoBackdropImage.Source == null)
                            {
                                VodInfoBackdropImage.Source = bitmap;
                            }
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // Modal açılışında anında (0ms) poster silüeti göstermek için
        // hafıza ve disk önbelleklerinden en hızlı bitmap'i bulur.
        // ─────────────────────────────────────────────────────────────
        private Bitmap? GetBestAvailablePosterForChannel(Channel channel)
        {
            if (channel.LogoBitmap != null) return channel.LogoBitmap;

            lock (_posterCacheLock)
            {
                if (!string.IsNullOrEmpty(channel.Name) && _tmdbPosterCache.TryGetValue(channel.Name, out var b) && b != null)
                    return b;
                if (!string.IsNullOrEmpty(channel.ShowName) && _tmdbPosterCache.TryGetValue(channel.ShowName, out var b2) && b2 != null)
                    return b2;
            }

            if (!string.IsNullOrEmpty(channel.LogoUrl))
            {
                lock (_logoCache)
                {
                    if (_logoCache.TryGetValue(channel.LogoUrl, out var lb) && lb != null)
                        return lb;
                }

                string diskPath = GetLogoDiskPath(channel.LogoUrl);
                if (File.Exists(diskPath))
                {
                    try
                    {
                        using var fs = File.OpenRead(diskPath);
                        var bmp = Bitmap.DecodeToWidth(fs, 400);
                        SetLogoCache(channel.LogoUrl, bmp);
                        return bmp;
                    }
                    catch { }
                }
            }

            string posterDiskPath = GetPosterDiskPath(channel.Name);
            if (File.Exists(posterDiskPath))
            {
                try
                {
                    using var fs = File.OpenRead(posterDiskPath);
                    var bmp = Bitmap.DecodeToWidth(fs, 400);
                    SetPosterCache(channel.Name, bmp);
                    return bmp;
                }
                catch { }
            }

            return null;
        }

        private Bitmap? GetBestAvailablePosterForSeries(SeriesCard card)
        {
            if (card.LogoBitmap != null) return card.LogoBitmap;

            lock (_posterCacheLock)
            {
                if (!string.IsNullOrEmpty(card.ShowName) && _tmdbPosterCache.TryGetValue(card.ShowName, out var b) && b != null)
                    return b;
            }

            string posterDiskPath = GetPosterDiskPath(card.ShowName);
            if (File.Exists(posterDiskPath))
            {
                try
                {
                    using var fs = File.OpenRead(posterDiskPath);
                    var bmp = Bitmap.DecodeToWidth(fs, 400);
                    SetPosterCache(card.ShowName, bmp);
                    return bmp;
                }
                catch { }
            }

            return null;
        }

        private Bitmap? GetBestAvailableBackdropForChannel(Channel channel)
        {
            if (channel.TmdbId > 0)
            {
                string bkKey = "tmdb_bk_" + channel.TmdbId;
                lock (_posterCacheLock)
                {
                    if (_tmdbBackdropCache.TryGetValue(bkKey, out var b) && b != null)
                        return b;
                }
                string diskPath = GetBackdropDiskPath(bkKey);
                if (File.Exists(diskPath))
                {
                    try
                    {
                        using var fs = File.OpenRead(diskPath);
                        var bmp = Bitmap.DecodeToWidth(fs, 1280);
                        SetBackdropCache(bkKey, bmp);
                        return bmp;
                    }
                    catch { }
                }
            }

            lock (_posterCacheLock)
            {
                foreach (var kvp in _tmdbCache)
                {
                    if (kvp.Key.StartsWith(channel.Name, StringComparison.OrdinalIgnoreCase) && kvp.Value.HasValue)
                    {
                        if (kvp.Value.Value.TryGetProperty("backdrop_path", out var bpEl) && bpEl.ValueKind == JsonValueKind.String)
                        {
                            string bp = bpEl.GetString() ?? "";
                            if (!string.IsNullOrEmpty(bp))
                            {
                                if (_tmdbBackdropCache.TryGetValue(bp, out var bb) && bb != null)
                                    return bb;
                                string dp = GetBackdropDiskPath(bp);
                                if (File.Exists(dp))
                                {
                                    try
                                    {
                                        using var fs = File.OpenRead(dp);
                                        var bmp = Bitmap.DecodeToWidth(fs, 1280);
                                        SetBackdropCache(bp, bmp);
                                        return bmp;
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        private Bitmap? GetBestAvailableBackdropForSeries(SeriesCard card)
        {
            var (tmdbId, _, _) = GetSeriesCardTmdbInfo(card);
            if (tmdbId > 0)
            {
                string bkKey = "tmdb_bk_" + tmdbId;
                lock (_posterCacheLock)
                {
                    if (_tmdbBackdropCache.TryGetValue(bkKey, out var b) && b != null)
                        return b;
                }
                string diskPath = GetBackdropDiskPath(bkKey);
                if (File.Exists(diskPath))
                {
                    try
                    {
                        using var fs = File.OpenRead(diskPath);
                        var bmp = Bitmap.DecodeToWidth(fs, 1280);
                        SetBackdropCache(bkKey, bmp);
                        return bmp;
                    }
                    catch { }
                }
            }

            lock (_posterCacheLock)
            {
                foreach (var kvp in _tmdbCache)
                {
                    if (kvp.Key.StartsWith(card.ShowName, StringComparison.OrdinalIgnoreCase) && kvp.Value.HasValue)
                    {
                        if (kvp.Value.Value.TryGetProperty("backdrop_path", out var bpEl) && bpEl.ValueKind == JsonValueKind.String)
                        {
                            string bp = bpEl.GetString() ?? "";
                            if (!string.IsNullOrEmpty(bp))
                            {
                                if (_tmdbBackdropCache.TryGetValue(bp, out var bb) && bb != null)
                                    return bb;
                                string dp = GetBackdropDiskPath(bp);
                                if (File.Exists(dp))
                                {
                                    try
                                    {
                                        using var fs = File.OpenRead(dp);
                                        var bmp = Bitmap.DecodeToWidth(fs, 1280);
                                        SetBackdropCache(bp, bmp);
                                        return bmp;
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        // ─────────────────────────────────────────────────────────────
        // Poster yükleme – SeriesCard listesi
        // Daha önce tamamen seri (tek tek, aralarda sabit gecikmeyle)
        // çalışıyordu. Logo yüklemedeki gibi sınırlı eşzamanlılık
        // (SemaphoreSlim) kullanılarak büyük dizi kataloglarında poster
        // yükleme süresi belirgin şekilde kısaltılıyor.
        // ─────────────────────────────────────────────────────────────
        // ─────────────────────────────────────────────────────────────
        // Bir SeriesCard'ın bölümlerinden (varsa) sağlayıcının verdiği TMDb
        // ID'sini/orijinal adını okur (bkz. MainWindow.Sources.cs →
        // FetchXtreamSeriesChannels). Aynı dizinin tüm bölümlerine aynı
        // değer yazıldığından ilk bulunan yeterlidir.
        // ─────────────────────────────────────────────────────────────
        private static (int TmdbId, string OriginalName, string ReleaseDate) GetSeriesCardTmdbInfo(SeriesCard card)
        {
            foreach (var season in card.Seasons)
            {
                if (!card.EpisodesBySeason.TryGetValue(season, out var eps)) continue;
                var withId = eps.FirstOrDefault(e => e.TmdbId > 0);
                if (withId != null) return (withId.TmdbId, withId.OriginalName, withId.ProviderReleaseDate);
                var withDate = eps.FirstOrDefault(e => !string.IsNullOrEmpty(e.ProviderReleaseDate));
                if (withDate != null) return (0, "", withDate.ProviderReleaseDate);
            }
            return (0, "", "");
        }

        private async Task LoadTmdbPostersForCards(List<SeriesCard> cards)
        {
            var semaphore = new SemaphoreSlim(4, 4);
            try
            {
            var tasks = cards.Select(async card =>
            {
                bool memHit;
                Bitmap? memCached = null;
                lock (_posterCacheLock)
                    memHit = _tmdbPosterCache.TryGetValue(card.ShowName, out memCached);

                if (memHit)
                {
                    if (memCached != null && card.LogoBitmap != memCached)
                        await Dispatcher.UIThread.InvokeAsync(() => card.LogoBitmap = memCached);
                    return;
                }

                await semaphore.WaitAsync();
                try
                {
                    string diskPath = GetPosterDiskPath(card.ShowName);
                    if (File.Exists(diskPath))
                    {
                        try
                        {
                            var bytes = await Task.Run(() => File.ReadAllBytes(diskPath));
                            using var ms = new MemoryStream(bytes);
                            var bmp = Bitmap.DecodeToWidth(ms, 220);
                            SetPosterCache(card.ShowName, bmp);
                            await Dispatcher.UIThread.InvokeAsync(() => card.LogoBitmap = bmp);
                            return;
                        }
                        catch { /* disk cache bozuksa TMDb'den tekrar çekilecek */ }
                    }

                    var (knownTmdbId, knownOriginalName, knownReleaseDate) = GetSeriesCardTmdbInfo(card);
                    var posterUrl = await SearchTmdbPosterUrl(
                        card.ShowName, "tv",
                        knownTmdbId > 0 ? knownTmdbId : null,
                        !string.IsNullOrEmpty(knownOriginalName) ? knownOriginalName : null,
                        ParseYearFromProviderDate(knownReleaseDate));
                    if (!string.IsNullOrEmpty(posterUrl))
                    {
                        EnsureTmdbHttpClient();
                        var posterBytes = await _tmdbHttpClient!.GetByteArrayAsync(posterUrl);
                        await File.WriteAllBytesAsync(GetPosterDiskPath(card.ShowName), posterBytes);
                        using var ms = new MemoryStream(posterBytes);
                        var bitmap = Bitmap.DecodeToWidth(ms, 220);
                        SetPosterCache(card.ShowName, bitmap);
                        await Dispatcher.UIThread.InvokeAsync(() => card.LogoBitmap = bitmap);
                    }
                    else
                    {
                        SetPosterCache(card.ShowName, null);
                    }
                }
                catch { /* bu kart için poster alınamadı, devam */ }
                finally { semaphore.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);
            }
            finally { semaphore.Dispose(); }
        }

        // ─────────────────────────────────────────────────────────────
        // Poster yükleme – Channel listesi (VOD/Film)
        // Sınırlı eşzamanlılık (SemaphoreSlim) ile paralelleştirildi.
        // ─────────────────────────────────────────────────────────────
        private async Task LoadTmdbPostersForChannels(List<Channel> channels)
        {
            // DÜZELTME: Xtream kaynaklarında VOD içerikleri için, sağlayıcının
            // zaten verdiği TMDb ID'sini (varsa) kullanmak üzere aktif kaynak
            // burada bir kez tespit edilir (döngü içinde tekrar tekrar
            // _sources taranmaz). M3U/Link kaynaklarında veya Canlı/Dizi
            // içeriklerde bu yol hiç devreye girmez — mevcut davranış aynen
            // korunur.
            var xtreamSource = _sources.FirstOrDefault(s => s.IsActive && s.Type == "Xtream");

            var semaphore = new SemaphoreSlim(4, 4);
            try
            {
            var tasks = channels.Select(async ch =>
            {
                string searchKey = !string.IsNullOrEmpty(ch.ShowName) ? ch.ShowName : ch.Name;
                string type = ch.Type == "Dizi" ? "tv" : "movie";

                bool memHit;
                Bitmap? cached = null;
                lock (_posterCacheLock)
                    memHit = _tmdbPosterCache.TryGetValue(searchKey, out cached);

                if (memHit)
                {
                    if (cached != null && ch.LogoBitmap != cached)
                        await Dispatcher.UIThread.InvokeAsync(() => ch.LogoBitmap = cached);
                    return;
                }

                await semaphore.WaitAsync();
                try
                {
                    string diskPath = GetPosterDiskPath(searchKey);
                    if (File.Exists(diskPath))
                    {
                        try
                        {
                            var bytes = await File.ReadAllBytesAsync(diskPath);
                            using var ms = new MemoryStream(bytes);
                            var bmp = Bitmap.DecodeToWidth(ms, 220);
                            SetPosterCache(searchKey, bmp);
                            await Dispatcher.UIThread.InvokeAsync(() => ch.LogoBitmap = bmp);
                            return;
                        }
                        catch { /* disk cache bozuksa TMDb'den tekrar çekilecek */ }
                    }

                    // Sağlayıcının verdiği TMDb ID'si (varsa) — bkz.
                    // GetXtreamVodInfoAsync notu. Sadece VOD için ve daha önce
                    // hiç sorgulanmadıysa (Channel.TmdbId henüz 0) bir kerelik
                    // get_vod_info isteği atılır; sonuç Channel'a yazılır ki
                    // bu oturumda bir daha sorgulanmasın.
                    int? knownTmdbId = null;
                    string? knownOriginalName = null;
                    int? knownYear = null;
                    if (xtreamSource != null && ch.Type == "VOD")
                    {
                        if (ch.TmdbId > 0)
                        {
                            knownTmdbId = ch.TmdbId;
                        }
                        else
                        {
                            var info = await GetXtreamVodInfoAsync(xtreamSource, ch);
                            if (info.TmdbId > 0) { knownTmdbId = info.TmdbId; ch.TmdbId = info.TmdbId; }
                            if (!string.IsNullOrEmpty(info.OriginalName))
                            {
                                knownOriginalName = info.OriginalName;
                                ch.OriginalName = info.OriginalName;
                            }
                            // DÜZELTME: kısa/jenerik isimli içeriklerde (aynı
                            // isimle TMDb'de birden fazla kayıt olduğunda)
                            // PickConfidentMatch yıl bilgisi olmadan reddediyor
                            // — bkz. FetchTmdbInfo'daki aynı düzeltmenin notu.
                            knownYear = ParseYearFromProviderDate(info.ReleaseDate);
                        }
                    }
                    else if (ch.TmdbId > 0)
                    {
                        knownTmdbId = ch.TmdbId;
                    }

                    var posterUrl = await SearchTmdbPosterUrl(searchKey, type, knownTmdbId, knownOriginalName, knownYear);
                    if (!string.IsNullOrEmpty(posterUrl))
                    {
                        EnsureTmdbHttpClient();
                        var posterBytes = await _tmdbHttpClient!.GetByteArrayAsync(posterUrl);
                        await File.WriteAllBytesAsync(diskPath, posterBytes);
                        using var ms = new MemoryStream(posterBytes);
                        var bitmap = Bitmap.DecodeToWidth(ms, 220);
                        SetPosterCache(searchKey, bitmap);
                        await Dispatcher.UIThread.InvokeAsync(() => ch.LogoBitmap = bitmap);
                    }
                    else
                    {
                        SetPosterCache(searchKey, null);
                    }
                }
                catch { /* bu içerik için poster alınamadı, devam */ }
                finally { semaphore.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);
            }
            finally { semaphore.Dispose(); }
        }

        // ─────────────────────────────────────────────────────────────
        // TMDb poster URL arama (sadece search – detay çekmez)
        //
        // knownTmdbId doluysa arama tamamen atlanır, doğrudan /movie|tv/{id}
        // üzerinden poster_path okunur (sağlayıcının TMDb eşleşmesine
        // güvenilir). Yoksa knownOriginalName (Xtream o_name) varsa birincil
        // arama adayı olarak o kullanılır; o da yoksa mevcut isim temizleme +
        // çok-adaylı arama (dash/apostrof/kelime + movie⇄tv çapraz deneme)
        // aynen çalışır.
        // ─────────────────────────────────────────────────────────────
        private async Task<string> SearchTmdbPosterUrl(
            string name, string type, int? knownTmdbId = null, string? knownOriginalName = null, int? knownYear = null)
        {
            try
            {
                EnsureTmdbHttpClient();

                // YENİ: Genel eşleştirme tablosu — bkz. FetchTmdbInfo'daki
                // aynı düzeltmenin notu (MainWindow.TmdbOverrides.cs).
                string overrideLookupName = !string.IsNullOrWhiteSpace(knownOriginalName) ? knownOriginalName! : name;
                int? overrideId = TryGetTmdbOverrideId(overrideLookupName, type);
                if (overrideId.HasValue) knownTmdbId = overrideId;

                if (knownTmdbId.HasValue && knownTmdbId.Value > 0)
                {
                    string detailUrl = $"{TMDB_BASE}/{type}/{knownTmdbId.Value}?api_key={TMDB_API_KEY}&language=tr-TR";
                    string? detailJson = await TmdbApiGetAsync(detailUrl, "poster-by-id");

                    // DÜZELTME: bkz. FetchTmdbInfo'daki "tür yanlış varsayılmış
                    // olabilir" düzeltmesinin aynısı — bilinen ID doğru ama
                    // movie/tv türü yanlışsa TMDb 404 döner. Aynı ID ile diğer
                    // tür bir kez daha denenir; bu denenmeden isimle aramaya
                    // düşülürse (özellikle elle ID girilen az bilinen içerikler
                    // için) o arama da zaten başarısız olacağından poster hiç
                    // yüklenmez.
                    string altType = type == "tv" ? "movie" : "tv";
                    if (detailJson == null)
                        detailJson = await TmdbApiGetAsync($"{TMDB_BASE}/{altType}/{knownTmdbId.Value}?api_key={TMDB_API_KEY}&language=tr-TR", "poster-by-id-alt-type");

                    if (detailJson != null)
                    {
                        try
                        {
                            using var detailDoc = JsonDocument.Parse(detailJson);
                            bool isOverridden = overrideId.HasValue && overrideId.Value == knownTmdbId.Value;
                            if (isOverridden || IsTmdbResultCompatible(detailDoc.RootElement, type, name, knownOriginalName, knownYear))
                            {
                                if (detailDoc.RootElement.TryGetProperty("poster_path", out var dpp) && dpp.ValueKind == JsonValueKind.String)
                                {
                                    string dpath = dpp.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(dpath)) return TMDB_IMG + dpath;
                                }
                                return "";
                            }
                        }
                        catch { /* bilinen ID ile detay çekilemedi — isimle aramaya düş */ }
                    }
                    // detailJson null (istek başarısız oldu, ayrıca loglandı) —
                    // isimle aramaya düşülmeye devam edilir.
                }

                string nameForSearch = !string.IsNullOrWhiteSpace(knownOriginalName) ? knownOriginalName! : name;
                var (searchName, year) = CleanNameForSearch(nameForSearch);
                if (string.IsNullOrEmpty(searchName) || searchName.Length < 2) return "";

                // DÜZELTME: Sağlayıcının kendi verdiği çıkış tarihi (varsa)
                // başlık metninden çıkarılan yılın önüne geçer — bkz.
                // FetchTmdbInfo'daki aynı düzeltmenin notu.
                year = knownYear ?? year;

                // DÜZELTME: Artık FetchTmdbInfo ile aynı çok-adaylı arama
                // (FindTmdbSearchResultAnyType — dash/apostrof/kelime adayları
                // + movie⇄tv çapraz deneme) kullanılıyor.
                var result = await FindTmdbSearchResultAnyType(type, searchName, year);
                if (result.HasValue &&
                    result.Value.result.TryGetProperty("poster_path", out var pp) && pp.ValueKind == JsonValueKind.String)
                {
                    string path = pp.GetString() ?? "";
                    return !string.IsNullOrEmpty(path) ? TMDB_IMG + path : "";
                }

                // NOT: "Bulunamadı" burada da bir hata değil, beklenen bir
                // sonuçtur — bkz. FetchTmdbInfo'daki aynı notun açıklaması.
                // crash.log'a bilinçli olarak yazılmıyor.
            }
            catch { }
            return "";
        }
    }
}