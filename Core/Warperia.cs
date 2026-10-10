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
    /// The 3.3.5a addon catalogue of warperia.com: browse, search, sort by installs or last update,
    /// look up by id for update checks. Each addon carries its version, a zip and the folder names it
    /// installs. Warperia's REST API wants a key since 2026-10, which stays on the XorWoW server:
    /// scripts\update-warperia-catalogue.ps1 fetches the whole catalogue every few hours and puts it
    /// beside the manifest (warperia-catalogue.json), and the launcher reads that. The zips still come
    /// straight from warperia.com, which needs no key.
    /// </summary>
    public sealed class Warperia
    {
        const string CatalogueName = "warperia-catalogue.json";
        static readonly TimeSpan Fresh = TimeSpan.FromHours(1);
        readonly Net _net;
        List<AddonInfo> _catalogue;
        DateTime _fetched;

        public Warperia(Net net) { _net = net; }

        /// <summary>One page of 20 from the catalogue: a query as in <see cref="SearchCatalogue"/>, else all of it by installs or last update.</summary>
        public async Task<(List<AddonInfo> items, int pages)> SearchAsync(string query, string sort, int page, CancellationToken ct)
        {
            var all = await CatalogueAsync(ct) ?? throw new InvalidOperationException("the addon list could not be loaded");
            var list = !string.IsNullOrWhiteSpace(query) ? SearchCatalogue(all, query, sort)
                     : sort == "modified" ? all.OrderByDescending(a => DateTime.TryParse(a.Updated, out var d) ? d : DateTime.MinValue).ToList()
                     : all.OrderByDescending(a => a.Installs).ToList();
            return (list.Skip((page - 1) * 20).Take(20).ToList(), (list.Count + 19) / 20);
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
        /// The whole 3.3.5a catalogue (id, title, version, zip, folder names, browse details) from the
        /// realm's file server, at most once an hour; copied to %APPDATA% so a launcher that cannot reach
        /// the file server still recognises and updates addons from the last copy. Null when there is none.
        /// </summary>
        public async Task<List<AddonInfo>> CatalogueAsync(CancellationToken ct)
        {
            if (_catalogue != null && DateTime.UtcNow - _fetched < Fresh) return _catalogue;
            try
            {
                var root = Json.Parse(System.Text.Encoding.UTF8.GetString(await _net.GetBytesAsync(CatalogueName, ct)));
                if (Json.Long(root, "format") != 1) throw new InvalidDataException(CatalogueName + " has an unknown format");
                var seen = new HashSet<long>();
                // Only zips on warperia.com: the file is not signed like the manifest, so it cannot send the launcher elsewhere
                var items = Json.Arr(root, "items").Select(Parse).Where(a => a.Id > 0 && seen.Add(a.Id) && FromWarperia(a)).ToList();
                if (items.Count == 0) throw new InvalidDataException(CatalogueName + " lists no addon");
                _catalogue = items;
                _fetched = DateTime.UtcNow;
                try
                {
                    var cache = new CatalogueCache
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
                }
                catch (Exception e) { Log.Write("warperia catalogue copy: " + e.Message); }
                Log.Write($"warperia catalogue: {items.Count} addons");
                return items;
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                Log.Write("warperia catalogue: " + e.Message);
                if (_catalogue != null) return _catalogue;
                CatalogueCache cache = null;
                try { if (File.Exists(CatalogueFile)) cache = Json.Deserialize<CatalogueCache>(File.ReadAllText(CatalogueFile)); } catch { }
                return cache?.Format == 2 && cache.Items != null ? FromCache(cache) : null;
            }
        }

        static bool FromWarperia(AddonInfo a) =>
            string.IsNullOrEmpty(a.FileUrl) ||
            Uri.TryCreate(a.FileUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
            (u.Host.Equals("warperia.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".warperia.com", StringComparison.OrdinalIgnoreCase));

        static List<AddonInfo> FromCache(CatalogueCache c) =>
            c.Items.Select(i => new AddonInfo
            {
                Id = i.Id, Title = i.Title, Version = i.Version, FileUrl = i.FileUrl, Icon = i.Icon, Link = i.Link, Folders = i.Folders ?? new List<string>(),
                Summary = i.Summary, Author = i.Author, Updated = i.Updated, Categories = i.Categories, Installs = i.Installs,
            }).Where(FromWarperia).ToList();

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

        /// <summary>The latest catalogue entries of these addons; one that left Warperia is simply missing (what is installed stays).</summary>
        public async Task<List<AddonInfo>> GetAsync(IEnumerable<long> ids, CancellationToken ct)
        {
            var all = await CatalogueAsync(ct) ?? throw new InvalidOperationException("the addon list could not be loaded");
            var want = new HashSet<long>(ids);
            return all.Where(a => want.Contains(a.Id)).ToList();
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
