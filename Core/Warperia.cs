using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XorWoWLauncher.Core
{
    public sealed class AddonInfo
    {
        public long Id;
        public string Title, Summary, Version, FileUrl, Link, Icon, Author, Updated, Categories;
        public long Installs;
        public List<string> Folders = new List<string>();
    }

    /// <summary>
    /// The 3.3.5a addon catalogue of warperia.com through its public WordPress REST API
    /// (/wp-json/wp/v2/wotlk-addons): search, sort by installs or last update, look up by id for
    /// update checks. Each addon carries its version, a zip and the folder names it installs.
    /// </summary>
    public sealed class Warperia
    {
        const string Api = "https://warperia.com/wp-json/wp/v2/wotlk-addons";
        const string Fields = "id,title,link,featured_image,addon_categories,author_name,custom_fields.summary,custom_fields.version," +
                              "custom_fields.file,custom_fields.folder_list,custom_fields.installs,custom_fields.post_modified,custom_fields.title_toc";
        readonly Net _net;

        public Warperia(Net net) { _net = net; }

        public async Task<(List<AddonInfo> items, int pages)> SearchAsync(string query, string sort, int page, CancellationToken ct)
        {
            var url = $"{Api}?per_page=20&page={page}&_fields={Fields}&orderby={(sort == "modified" ? "modified" : "installs")}";
            if (!string.IsNullOrWhiteSpace(query)) url += "&search=" + Uri.EscapeDataString(query.Trim());
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                using (var res = await _net.Http.GetAsync(url, cts.Token))
                {
                    if ((int)res.StatusCode == 400) return (new List<AddonInfo>(), 0);   // page past the end
                    res.EnsureSuccessStatusCode();
                    int.TryParse(res.Headers.TryGetValues("X-WP-TotalPages", out var v) ? v.FirstOrDefault() : "1", out var pages);
                    var body = await res.Content.ReadAsStringAsync();
                    return (Json.List(Json.Parse(body)).Select(Parse).ToList(), pages);
                }
            }
        }

        sealed class CatalogueEntry
        {
            public long Id { get; set; }
            public string Title { get; set; }
            public string Version { get; set; }
            public string FileUrl { get; set; }
            public string Icon { get; set; }
            public string Link { get; set; }
            public List<string> Folders { get; set; }
            public string Summary { get; set; }
            public string Author { get; set; }
            public string Updated { get; set; }
            public string Categories { get; set; }
            public long Installs { get; set; }
        }

        sealed class CatalogueCache
        {
            public int Format { get; set; }   // 2: with the browse details
            public DateTime Fetched { get; set; }
            public List<CatalogueEntry> Items { get; set; }
        }

        static string CatalogueFile => Path.Combine(AppPaths.Dir, "warperia-catalogue.json");

        /// <summary>
        /// The whole 3.3.5a catalogue (id, title, version, zip, folder names) - what the API cannot search
        /// by, a folder name - to recognise addons installed by hand. Fetched at most once a day (about
        /// nine requests of 100), cached in %APPDATA%; a stale copy, or null, when Warperia does not answer.
        /// </summary>
        public async Task<List<AddonInfo>> CatalogueAsync(CancellationToken ct)
        {
            CatalogueCache cache = null;
            try { if (File.Exists(CatalogueFile)) cache = Json.Deserialize<CatalogueCache>(File.ReadAllText(CatalogueFile)); } catch { }
            if (cache?.Format != 2) cache = null;
            if (cache?.Items != null && DateTime.UtcNow - cache.Fetched < TimeSpan.FromDays(1)) return FromCache(cache);
            try
            {
                var items = new List<AddonInfo>();
                for (int page = 1, pages = 1; page <= pages && page <= 30; page++)
                {
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        cts.CancelAfter(TimeSpan.FromSeconds(30));
                        using (var res = await _net.Http.GetAsync($"{Api}?per_page=100&page={page}&_fields={Fields}&orderby=installs", cts.Token))
                        {
                            res.EnsureSuccessStatusCode();
                            int.TryParse(res.Headers.TryGetValues("X-WP-TotalPages", out var v) ? v.FirstOrDefault() : "1", out pages);
                            items.AddRange(Json.List(Json.Parse(await res.Content.ReadAsStringAsync())).Select(Parse));
                        }
                    }
                }
                cache = new CatalogueCache
                {
                    Format = 2,
                    Fetched = DateTime.UtcNow,
                    Items = items.Select(a => new CatalogueEntry
                    {
                        Id = a.Id, Title = a.Title, Version = a.Version, FileUrl = a.FileUrl, Icon = a.Icon, Link = a.Link, Folders = a.Folders,
                        Summary = a.Summary, Author = a.Author, Updated = a.Updated, Categories = a.Categories, Installs = a.Installs,
                    }).ToList(),
                };
                Directory.CreateDirectory(AppPaths.Dir);
                AtomicFile.WriteAllText(CatalogueFile, Json.Serialize(cache));
                Log.Write($"warperia catalogue: {items.Count} addons");
                return items;
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                Log.Write("warperia catalogue: " + e.Message);
                return cache?.Items != null ? FromCache(cache) : null;
            }
        }

        static List<AddonInfo> FromCache(CatalogueCache c) =>
            c.Items.Select(i => new AddonInfo
            {
                Id = i.Id, Title = i.Title, Version = i.Version, FileUrl = i.FileUrl, Icon = i.Icon, Link = i.Link, Folders = i.Folders ?? new List<string>(),
                Summary = i.Summary, Author = i.Author, Updated = i.Updated, Categories = i.Categories, Installs = i.Installs,
            }).ToList();

        /// <summary>
        /// Search the cached catalogue - Warperia's own search ranks words found anywhere in the
        /// descriptions, so "elvui" does not even bring up ElvUI. Every word of the query must be in the
        /// title or the summary; title hits come first, then by installs or last update.
        /// </summary>
        public static List<AddonInfo> SearchCatalogue(List<AddonInfo> catalogue, string query, string sort)
        {
            var words = query.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool In(string text) => words.All(w => (text ?? "").ToLowerInvariant().Contains(w));
            var hits = catalogue.Where(a => In(a.Title) || In(a.Title + " " + a.Summary + " " + string.Join(" ", a.Folders)));
            var ordered = hits.OrderByDescending(a => In(a.Title));
            ordered = sort == "modified"
                ? ordered.ThenByDescending(a => DateTime.TryParse(a.Updated, out var d) ? d : DateTime.MinValue)
                : ordered.ThenByDescending(a => a.Installs);
            return ordered.Take(100).ToList();
        }

        public async Task<List<AddonInfo>> GetAsync(IEnumerable<long> ids, CancellationToken ct)
        {
            // The collection ignores include=, so one request per addon (few, and only after an update check)
            var result = new List<AddonInfo>();
            foreach (var id in ids.Distinct())
            {
                try
                {
                    var body = System.Text.Encoding.UTF8.GetString(await _net.GetBytesAsync($"{Api}/{id}?_fields={Fields}", ct));
                    var a = Parse(Json.Parse(body));
                    if (a.Id == id) result.Add(a);
                }
                catch (HttpRequestException e) { Log.Write($"warperia addon {id}: {e.Message}"); }   // removed from Warperia: keep what is installed
            }
            return result;
        }

        static AddonInfo Parse(object o)
        {
            var cf = Json.Obj(o, "custom_fields");
            var a = new AddonInfo
            {
                Id = Json.Long(o, "id"),
                Title = Clean(Json.Str(o, "title")),
                Link = Json.Str(o, "link"),
                Icon = Json.Str(o, "featured_image"),
                Author = Clean(Json.Str(o, "author_name")),
                Summary = Clean(Json.Str(cf, "summary")),
                Version = Clean(Json.Str(cf, "version")),
                FileUrl = Json.Str(cf, "file"),
                Installs = Json.Long(cf, "installs"),
                Updated = Json.Str(cf, "post_modified"),
                Categories = string.Join(", ", Json.Arr(o, "addon_categories").Select(c => Clean(Convert.ToString(c)))),
            };
            foreach (var f in Json.Arr(cf, "folder_list"))
            {
                var name = f is string s ? s : Convert.ToString(Json.List(f).FirstOrDefault());
                name = (name ?? "").Trim().Trim('/', '\\');
                if (name.Length > 0 && name.Trim('.').Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !a.Folders.Contains(name, StringComparer.OrdinalIgnoreCase))
                    a.Folders.Add(name);
            }
            if (string.IsNullOrEmpty(a.Summary)) a.Summary = Clean(Json.Str(cf, "title_toc"));
            return a;
        }

        static string Clean(string s) => WebUtility.HtmlDecode(Regex.Replace(s ?? "", "<[^>]+>", "")).Trim();

        /// <summary>
        /// Downloads the addon's zip and puts its folders into Interface\AddOns, replacing older
        /// copies of those folders. Returns the folder names installed.
        /// </summary>
        public async Task<List<string>> InstallAsync(AddonInfo a, string gameDir, ICollection<string> reserved, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(a.FileUrl)) throw new InvalidOperationException(a.Title + " has no download on Warperia.");
            var clash = a.Folders.FirstOrDefault(f => reserved.Contains(f, StringComparer.OrdinalIgnoreCase));
            if (clash != null) throw new InvalidOperationException($"{a.Title} would replace {clash}, which comes with XorWoW.");

            var zipBytes = await _net.GetBytesAsync(a.FileUrl, ct, 300);
            var addOns = Path.Combine(gameDir, "Interface", "AddOns");
            Directory.CreateDirectory(addOns);
            using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            {
                var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                var map = FindFolders(entries.Select(e => e.FullName.Replace('\\', '/')).ToList(), a.Folders);
                if (map.Count == 0) throw new InvalidDataException(a.Title + ": no addon folder found in its zip.");
                var clash2 = map.Keys.FirstOrDefault(f => reserved.Contains(f, StringComparer.OrdinalIgnoreCase));
                if (clash2 != null) throw new InvalidOperationException($"{a.Title} would replace {clash2}, which comes with XorWoW.");

                foreach (var kv in map)
                {
                    var target = Path.GetFullPath(Path.Combine(addOns, kv.Key));
                    if (!string.Equals(Path.GetDirectoryName(target), Path.GetFullPath(addOns).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)) continue;
                    var staging = target + ".xwnew";
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);
                    foreach (var e in entries.Where(e => e.FullName.Replace('\\', '/').StartsWith(kv.Value, StringComparison.OrdinalIgnoreCase)))
                    {
                        var rest = e.FullName.Replace('\\', '/').Substring(kv.Value.Length);
                        var dest = Path.GetFullPath(Path.Combine(staging, rest.Replace('/', '\\')));
                        if (!dest.StartsWith(staging + "\\", StringComparison.OrdinalIgnoreCase)) continue;   // zip-slip
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                    }
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    Directory.Move(staging, target);
                }
                Log.Write($"installed {a.Title} {a.Version} ({string.Join(", ", map.Keys)})");
                return map.Keys.ToList();
            }
        }

        /// <summary>
        /// folder name -> zip prefix ("Questie-335/" or "wrapper/Questie-335/"): the shallowest
        /// directory of each listed name; without a list, every directory holding a .toc of its own name.
        /// </summary>
        static Dictionary<string, string> FindFolders(List<string> paths, List<string> folders)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var names = folders.ToList();
            if (names.Count == 0)
            {
                foreach (var p in paths.Where(p => p.EndsWith(".toc", StringComparison.OrdinalIgnoreCase)))
                {
                    var seg = p.Split('/');
                    if (seg.Length >= 2 && Path.GetFileNameWithoutExtension(seg[seg.Length - 1]).Equals(seg[seg.Length - 2], StringComparison.OrdinalIgnoreCase))
                        names.Add(seg[seg.Length - 2]);
                }
            }
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string best = null;
                foreach (var p in paths)
                {
                    var seg = p.Split('/');
                    for (int i = 0; i < seg.Length - 1; i++)
                        if (seg[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                        {
                            var prefix = string.Join("/", seg.Take(i + 1)) + "/";
                            if (best == null || prefix.Length < best.Length) best = prefix;
                            break;
                        }
                }
                // a nested library folder (Addon/Libs/X) does not count when X is also a top-level folder elsewhere
                if (best != null && !map.Values.Any(v => best.StartsWith(v, StringComparison.OrdinalIgnoreCase)))
                    map[name] = best;
            }
            return map;
        }

        public static void Uninstall(InstalledAddon a, string gameDir, IEnumerable<InstalledAddon> others, ICollection<string> reserved)
        {
            var shared = new HashSet<string>(others.Where(o => o.Id != a.Id).SelectMany(o => o.Folders), StringComparer.OrdinalIgnoreCase);
            foreach (var f in a.Folders)
            {
                if (f.Trim('.').Length == 0 || f.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                if (shared.Contains(f) || reserved.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
                var dir = Path.Combine(gameDir, "Interface", "AddOns", f);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            Log.Write($"removed addon {a.Title}");
        }
    }
}
