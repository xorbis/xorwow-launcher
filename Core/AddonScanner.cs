using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XorWoWLauncher.Core
{
    /// <summary>One loadable addon folder: Interface\AddOns\&lt;Folder&gt;\&lt;Folder&gt;.toc, as the 3.3.5a client sees it.</summary>
    public sealed class LocalAddon
    {
        public string Folder, Title, Version, Notes;
        public List<string> Deps = new List<string>();
    }

    public enum AddonOrigin { Realm, Launcher, Hand }

    /// <summary>An addon as the player thinks of it: a main folder and its companion folders (ElvUI + ElvUI_OptionsUI).</summary>
    public sealed class AddonGroup
    {
        public string Main, Title, Version, Notes;
        public List<string> Folders = new List<string>();
        public AddonOrigin Origin;
        public InstalledAddon Installed;   // Origin == Launcher
        public AddonInfo Match;            // Origin == Hand: the Warperia entry with the same folders, if any
    }

    /// <summary>
    /// Lists what is in Interface\AddOns: every folder with a .toc of its own name (Blizzard_* left out),
    /// read like the game's AddOns list does (## Title without colour codes, ## Version, ## Notes, the
    /// dependencies), then grouped into addons.
    /// </summary>
    public static class AddonScanner
    {
        public static List<LocalAddon> Scan(string addOnsDir)
        {
            var list = new List<LocalAddon>();
            if (!Directory.Exists(addOnsDir)) return list;
            foreach (var dir in Directory.EnumerateDirectories(addOnsDir))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xwnew")) continue;
                var toc = Path.Combine(dir, name + ".toc");
                if (!File.Exists(toc)) continue;   // not something the client loads
                var a = new LocalAddon { Folder = name, Title = name, Version = "", Notes = "" };
                try
                {
                    foreach (var line in File.ReadLines(toc, Encoding.UTF8).Take(80))
                    {
                        var m = Regex.Match(line, @"^##\s*([\w-]+)\s*:\s*(.*?)\s*$");
                        if (!m.Success) continue;
                        var key = m.Groups[1].Value.ToLowerInvariant();
                        var value = Clean(m.Groups[2].Value);
                        if (key == "title" && value.Length > 0) a.Title = value;
                        else if (key == "version") a.Version = value;
                        else if (key == "notes") a.Notes = value;
                        else if (key == "dependencies" || key == "requireddeps" || key.StartsWith("dep"))
                            a.Deps.AddRange(value.Split(',').Select(d => d.Trim()).Where(d => d.Length > 0));
                    }
                }
                catch (IOException) { }
                list.Add(a);
            }
            return list;
        }

        /// <summary>Colour codes (|cffRRGGBB ... |r) and texture tags out of a .toc title.</summary>
        static string Clean(string s) =>
            Regex.Replace(Regex.Replace(s, @"\|c[0-9a-fA-F]{8}|\|r|\|T[^|]*\|t", ""), @"\s+", " ").Trim();

        /// <summary>
        /// Groups the folders: the realm's addons and the launcher's installs keep their own folders;
        /// the rest are gathered by name family - ElvUI_OptionsUI with ElvUI, DBM-* with DBM-Core,
        /// Atlas_* with Atlas (the part before the first _ or -) - the family's main folder being the
        /// one named like the family, else the one the others depend on. Hand-installed groups are
        /// matched against the Warperia catalogue by folder names.
        /// </summary>
        public static List<AddonGroup> Group(List<LocalAddon> all, ICollection<string> realmFolders,
                                             IEnumerable<InstalledAddon> launcherAddons, IList<AddonInfo> catalogue)
        {
            var byName = all.ToDictionary(a => a.Folder, StringComparer.OrdinalIgnoreCase);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var groups = new List<AddonGroup>();

            var realm = all.Where(a => realmFolders.Contains(a.Folder, StringComparer.OrdinalIgnoreCase)).ToList();
            if (realm.Count > 0)
            {
                groups.Add(new AddonGroup { Origin = AddonOrigin.Realm, Main = realm[0].Folder, Title = "XorWoW addons", Folders = realm.Select(r => r.Folder).ToList() });
                foreach (var r in realm) taken.Add(r.Folder);
            }

            foreach (var inst in launcherAddons)
            {
                var present = inst.Folders.Where(f => byName.ContainsKey(f) && !taken.Contains(f)).ToList();
                var main = present.Select(f => byName[f]).FirstOrDefault();
                groups.Add(new AddonGroup
                {
                    Origin = AddonOrigin.Launcher, Installed = inst, Main = main?.Folder ?? inst.Folders.FirstOrDefault(),
                    Title = inst.Title, Version = inst.Version, Notes = main?.Notes ?? "", Folders = present,
                });
                foreach (var f in present) taken.Add(f);
            }

            foreach (var family in all.Where(a => !taken.Contains(a.Folder)).GroupBy(a => Stem(a.Folder), StringComparer.OrdinalIgnoreCase))
            {
                var members = family.ToList();
                var main = members.FirstOrDefault(m => m.Folder.Equals(family.Key, StringComparison.OrdinalIgnoreCase))
                        ?? members.OrderByDescending(m => members.Count(o => o.Deps.Contains(m.Folder, StringComparer.OrdinalIgnoreCase)))
                                  .ThenBy(m => m.Folder.Length).First();
                var g = new AddonGroup
                {
                    Origin = AddonOrigin.Hand, Main = main.Folder, Title = main.Title, Version = main.Version, Notes = main.Notes,
                    Folders = new[] { main.Folder }.Concat(members.Where(m => m != main).Select(m => m.Folder).OrderBy(f => f)).ToList(),
                };
                g.Match = BestMatch(g, catalogue);
                groups.Add(g);
            }
            return groups;
        }

        static string Stem(string folder)
        {
            var i = folder.IndexOfAny(new[] { '_', '-' });
            return i > 0 ? folder.Substring(0, i) : folder;
        }

        /// <summary>
        /// The Warperia addon that installs this group's main folder. Several can (forks for other
        /// servers: "ElvUI Epoch", "Auctionator-Fixed"), so: same version first, then same title, then
        /// the most folders in common. A candidate named differently is only kept when all its folders
        /// are installed; the UI always shows the Warperia name, so the player sees what would replace it.
        /// </summary>
        static AddonInfo BestMatch(AddonGroup g, IList<AddonInfo> catalogue)
        {
            if (catalogue == null) return null;
            return catalogue.Where(c => c.Folders.Contains(g.Main, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.FileUrl))
                            .Where(c => SameName(c.Title, g.Title) || c.Folders.All(f => g.Folders.Contains(f, StringComparer.OrdinalIgnoreCase)))
                            .OrderByDescending(c => !string.IsNullOrEmpty(g.Version) && Norm(c.Version) == Norm(g.Version))
                            .ThenByDescending(c => SameName(c.Title, g.Title))
                            .ThenByDescending(c => c.Folders.Count(f => g.Folders.Contains(f, StringComparer.OrdinalIgnoreCase)))
                            .ThenBy(c => c.Folders.Count)
                            .FirstOrDefault();
        }

        static string Norm(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9.]", "").TrimStart('v');
        static bool SameName(string a, string b) => Regex.Replace((a ?? "").ToLowerInvariant(), "[^a-z0-9]", "") == Regex.Replace((b ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

        /// <summary>Folders of other installed addons that require one of <paramref name="folders"/>.</summary>
        public static List<string> Dependents(List<LocalAddon> all, ICollection<string> folders) =>
            all.Where(a => !folders.Contains(a.Folder, StringComparer.OrdinalIgnoreCase)
                           && a.Deps.Any(d => folders.Contains(d, StringComparer.OrdinalIgnoreCase)))
               .Select(a => a.Title).Distinct().ToList();
    }
}
