using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace ReforgedUpdater
{
    /// <summary>One downloadable .mpq advertised on the downloads page.</summary>
    internal sealed class CatalogEntry
    {
        public string Id;           // "A", "S", "S-standalone" - what the user types
        public string PatchCode;    // "Patch-A"
        public string Letter;       // "A" - the client-visible patch slot
        public string Name;         // "Player Characters & NPCs"
        public string Description;
        public string Version;      // "1.3.1", or null when the page shows none
        public string Url;
        public string FileName;     // "patch-A.mpq"
        public string Variant;      // "Standard" / "Standalone", or null
        public string VariantNote;  // "Requires Patch-M"
        public string Group;        // "Core Modules"
        public List<string> Badges = new List<string>();

        public string Display => PatchCode + (Variant == null ? string.Empty : " (" + Variant + ")");
    }

    /// <summary>
    /// Scrapes the Project Reforged downloads page. The page is a hand-written static
    /// document with stable class names, so the markup is read with anchored patterns
    /// rather than a full HTML parser. Parse throws when nothing matches, so a site
    /// redesign fails loudly instead of silently reporting "no updates".
    /// </summary>
    internal static class Catalog
    {
        public const string DefaultUrl = "https://projectreforged.github.io/wotlk/downloads/";

        private const RegexOptions Opts = RegexOptions.Singleline | RegexOptions.IgnoreCase;

        private static readonly Regex GroupRx = new Regex("class=\"group-label\">(.*?)</", Opts);
        private static readonly Regex CardRx = new Regex("<div class=\"dl-card\"", Opts);
        private static readonly Regex PatchRx = new Regex("class=\"dl-patch\">(.*?)</span>", Opts);
        private static readonly Regex NameRx = new Regex("class=\"dl-name\">(.*?)</span>", Opts);
        private static readonly Regex DescRx = new Regex("class=\"dl-desc\">(.*?)</div>", Opts);
        private static readonly Regex VersionRx = new Regex("class=\"dl-version\">.*?v([0-9][0-9A-Za-z.-]*)", Opts);
        private static readonly Regex BadgeRx = new Regex("class=\"badge[^\"]*\">(.*?)</span>", Opts);
        private static readonly Regex VariantRx = new Regex("class=\"dl-variant-name\">(.*?)</div>\\s*(?:<div class=\"dl-variant-sub\">(.*?)</div>)?", Opts);
        private static readonly Regex LinkRx = new Regex("href=\"(?<url>https?://[^\"]+?\\.mpq)\"", Opts);
        private static readonly Regex TagRx = new Regex("<[^>]+>", Opts);
        private static readonly Regex SpaceRx = new Regex("\\s+", Opts);
        private static readonly Regex LetterRx = new Regex("patch[-_ ]?([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
        private static readonly Regex SlugRx = new Regex("[^a-z0-9]+");
        private static readonly Regex FillerRx = new Regex(@"\b(version|variant|edition)\b");

        // Astral-plane pictographs (surrogate pairs), the misc-symbol and dingbat blocks,
        // and the joiners/selectors that glue emoji together.
        private static readonly Regex EmojiRx = new Regex(@"[\uD800-\uDFFF☀-➿️‍]");

        public static List<CatalogEntry> Parse(string html)
        {
            if (string.IsNullOrEmpty(html))
                throw new CatalogException("The downloads page returned an empty response.");

            var groups = GroupRx.Matches(html).Cast<Match>()
                .Select(m => new { m.Index, Label = Text(m.Groups[1].Value) })
                .ToList();

            var cardStarts = CardRx.Matches(html).Cast<Match>().Select(m => m.Index).ToList();
            if (cardStarts.Count == 0)
                throw new CatalogException("No download cards found - the page layout has changed.");

            var entries = new List<CatalogEntry>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < cardStarts.Count; i++)
            {
                int start = cardStarts[i];
                int end = i + 1 < cardStarts.Count ? cardStarts[i + 1] : html.Length;
                string card = html.Substring(start, end - start);

                var links = LinkRx.Matches(card).Cast<Match>().ToList();
                if (links.Count == 0) continue; // dependency/mod cards link to GitHub, not to an .mpq

                string patchCode = First(PatchRx, card);
                string name = First(NameRx, card);
                string description = First(DescRx, card);
                string version = First(VersionRx, card, decode: false);
                string group = groups.Where(g => g.Index < start).Select(g => g.Label).LastOrDefault() ?? "Other";
                var badges = BadgeRx.Matches(card).Cast<Match>()
                    .Select(m => Text(m.Groups[1].Value))
                    .Where(b => b.Length > 0)
                    .ToList();

                var variants = VariantRx.Matches(card).Cast<Match>()
                    .Select(m => new { m.Index, Name = Text(m.Groups[1].Value), Note = Text(m.Groups[2].Value) })
                    .ToList();

                foreach (var link in links)
                {
                    string url = WebUtility.HtmlDecode(link.Groups["url"].Value);
                    var variant = variants.LastOrDefault(v => v.Index < link.Index);
                    bool multiVariant = variants.Count > 1;

                    var entry = new CatalogEntry
                    {
                        PatchCode = string.IsNullOrEmpty(patchCode) ? FileNameOf(url) : patchCode,
                        Name = name,
                        Description = description,
                        Version = string.IsNullOrEmpty(version) ? null : version,
                        Url = url,
                        FileName = FileNameOf(url),
                        Variant = multiVariant && variant != null ? variant.Name : null,
                        VariantNote = multiVariant && variant != null ? variant.Note : null,
                        Group = group,
                        Badges = badges
                    };

                    entry.Letter = LetterOf(entry.PatchCode, entry.FileName);
                    entry.Id = MakeId(entry, usedIds);
                    entries.Add(entry);
                }
            }

            if (entries.Count == 0)
                throw new CatalogException("The page listed no .mpq downloads - the layout has probably changed.");

            return entries;
        }

        /// <summary>"Patch-A" -> "A". Falls back to the file name, then to the raw string.</summary>
        private static string LetterOf(string patchCode, string fileName)
        {
            var m = LetterRx.Match(patchCode ?? string.Empty);
            if (!m.Success) m = LetterRx.Match(fileName ?? string.Empty);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : (patchCode ?? fileName ?? "?");
        }

        /// <summary>
        /// Short, typeable id. The first variant on a card - the one the site lists as the
        /// main download - keeps the bare letter, so "install S" or "install T" does the
        /// obvious thing; later variants get a suffix ("S-standalone", "T-ultra-base").
        /// </summary>
        private static string MakeId(CatalogEntry entry, HashSet<string> used)
        {
            string id = used.Contains(entry.Letter)
                ? entry.Letter + "-" + Slug(entry.Variant ?? "alt")
                : entry.Letter;

            string candidate = id;
            for (int n = 2; used.Contains(candidate); n++) candidate = id + n.ToString();
            used.Add(candidate);
            return candidate;
        }

        private static string Slug(string value)
        {
            // "Less Thicc Version" -> "less-thicc": filler words only make ids longer to type.
            string text = FillerRx.Replace(value.ToLowerInvariant(), " ");
            string slug = SlugRx.Replace(text, "-").Trim('-');
            return slug.Length == 0 ? "alt" : slug;
        }

        private static string FileNameOf(string url)
        {
            string path = url;
            int query = path.IndexOfAny(new[] { '?', '#' });
            if (query >= 0) path = path.Substring(0, query);
            int slash = path.LastIndexOf('/');
            return Uri.UnescapeDataString(slash >= 0 ? path.Substring(slash + 1) : path);
        }

        private static string First(Regex rx, string input, bool decode = true)
        {
            var m = rx.Match(input);
            if (!m.Success) return string.Empty;
            return decode ? Text(m.Groups[1].Value) : m.Groups[1].Value.Trim();
        }

        /// <summary>Strips inline tags, decodes entities, and collapses whitespace.</summary>
        private static string Text(string html)
        {
            if (string.IsNullOrEmpty(html)) return string.Empty;
            string text = TagRx.Replace(html, " ");
            text = WebUtility.HtmlDecode(text);
            // Badges carry emoji ("⚔️ Kronos"); a Windows console prints them as "??".
            text = EmojiRx.Replace(text, string.Empty);
            return SpaceRx.Replace(text, " ").Trim();
        }
    }

    internal sealed class CatalogException : Exception
    {
        public CatalogException(string message) : base(message) { }
    }
}
