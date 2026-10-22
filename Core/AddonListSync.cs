using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XorWoWLauncher.Core
{
    /// <summary>One addon of the account's synced list.</summary>
    public sealed class SyncedAddon
    {
        /// <summary>The main folder, lower-cased: the same addon whether the launcher or the player installed it.</summary>
        public string Key { get; set; }
        /// <summary>Warperia id, 0 when Warperia does not have it (then it cannot be installed elsewhere).</summary>
        public long Id { get; set; }
        public string Title { get; set; }
        public List<string> Folders { get; set; } = new List<string>();
        /// <summary>False once removed on one PC: the others remove it too.</summary>
        public bool Installed { get; set; }
        /// <summary>When Installed last changed (UTC, sortable): on merge the later one wins, per addon.</summary>
        public string Changed { get; set; } = "";
    }

    /// <summary>
    /// The account's installed addons (user, 2026-10-21): WTF\Account\&lt;ACCOUNT&gt;\launcher-addons.json,
    /// synced with the rest of the folder by WtfSync, and the missing ones installed from Warperia.
    /// Each PC records what it installed or removed since its last sync, per addon with a timestamp,
    /// and two lists merge addon by addon (the later change wins), so installing on one PC and
    /// another addon on a second PC between syncs keeps both. The realm's own addons are not listed
    /// (the manifest keeps them). A removal on one PC removes the addon from the others too
    /// (user, 2026-10-22); one Warperia cannot give back is zipped into addon-backups first.
    /// </summary>
    public sealed class AddonListSync
    {
        public const string FileName = "launcher-addons.json";

        sealed class ListFile
        {
            public int Format { get; set; } = 1;
            public List<SyncedAddon> Addons { get; set; } = new List<SyncedAddon>();
        }

        readonly string _file;

        public AddonListSync(string accountDir) { _file = Path.Combine(accountDir, FileName); }

        /// <summary>The addons in Interface\AddOns, by key: the launcher's installs and the hand-installed ones (matched to Warperia when the catalogue knows them).</summary>
        public static Dictionary<string, SyncedAddon> LocalAddons(string gameDir, GameState state, ICollection<string> reserved, IList<AddonInfo> catalogue)
        {
            var all = AddonScanner.Scan(Path.Combine(gameDir, "Interface", "AddOns"));
            var r = new Dictionary<string, SyncedAddon>();
            foreach (var g in AddonScanner.Group(all, reserved, state.Addons, catalogue))
            {
                if (g.Origin == AddonOrigin.Realm || g.Folders.Count == 0) continue;
                var launcher = g.Origin == AddonOrigin.Launcher;
                var folders = launcher ? g.Installed.Folders : g.Folders;
                var key = (launcher ? folders.FirstOrDefault() : g.Main)?.ToLowerInvariant();
                if (string.IsNullOrEmpty(key) || r.ContainsKey(key)) continue;
                r[key] = new SyncedAddon { Key = key, Id = launcher ? g.Installed.Id : g.Match?.Id ?? 0, Title = g.Title, Folders = folders.ToList(), Installed = true };
            }
            return r;
        }

        /// <summary>
        /// Writes this PC's installs and removals since its last sync (<paramref name="previous"/>, the
        /// keys installed then; null on its first sync: everything here counts as installed now).
        /// </summary>
        public bool RecordLocalChanges(Dictionary<string, SyncedAddon> local, ICollection<string> previous)
        {
            var list = Read();
            var map = list.ToDictionary(a => a.Key);
            var prev = new HashSet<string>(previous ?? new string[0]);
            var now = Now();
            foreach (var l in local.Values)
            {
                if (map.TryGetValue(l.Key, out var e))
                {
                    if (e.Id == 0 && l.Id > 0) e.Id = l.Id;   // matched on Warperia since: now installable elsewhere
                    // removed on another PC while this one had it: not put back on the list, removed here after the sync
                    if (e.Installed || prev.Contains(l.Key)) continue;
                }
                map[l.Key] = new SyncedAddon { Key = l.Key, Id = l.Id > 0 ? l.Id : e?.Id ?? 0, Title = l.Title, Folders = l.Folders, Installed = true, Changed = now };
            }
            if (previous != null)
                foreach (var k in prev.Where(k => !local.ContainsKey(k)))
                    if (map.TryGetValue(k, out var e) && e.Installed) { e.Installed = false; e.Changed = now; }
            return Write(map.Values);
        }

        /// <summary>Merges another PC's list (the realm's copy) into this one; true when this one changed.</summary>
        public bool MergeFrom(byte[] otherJson)
        {
            List<SyncedAddon> other;
            try { other = Json.Deserialize<ListFile>(Encoding.UTF8.GetString(otherJson))?.Addons ?? new List<SyncedAddon>(); }
            catch (Exception e) { Log.Write("addon list from the realm unreadable: " + e.Message); return false; }
            var map = Read().ToDictionary(a => a.Key);
            foreach (var o in other.Where(Valid))
            {
                if (!map.TryGetValue(o.Key, out var mine) || string.CompareOrdinal(o.Changed, mine.Changed) > 0)
                {
                    if (mine != null && o.Id == 0) o.Id = mine.Id;
                    map[o.Key] = o;
                }
                else if (mine.Id == 0 && o.Id > 0) mine.Id = o.Id;
            }
            return Write(map.Values);
        }

        /// <summary>Removed on another PC and still here: this PC's entries (its own folders), to remove too.</summary>
        public List<SyncedAddon> Removed(Dictionary<string, SyncedAddon> local)
        {
            var list = Read();
            // a folder another listed addon still uses stays
            var kept = new HashSet<string>(list.Where(a => a.Installed).SelectMany(a => a.Folders), StringComparer.OrdinalIgnoreCase);
            return list.Where(a => !a.Installed && local.ContainsKey(a.Key))
                       .Select(a => local[a.Key])
                       .Select(l => new SyncedAddon { Key = l.Key, Id = l.Id, Title = l.Title, Folders = l.Folders.Where(f => !kept.Contains(f)).ToList() })
                       .Where(l => l.Folders.Count > 0).ToList();
        }

        /// <summary>Listed as installed, but none of its folders is here.</summary>
        public List<SyncedAddon> Missing(Dictionary<string, SyncedAddon> local, string gameDir)
        {
            var addOns = Path.Combine(gameDir, "Interface", "AddOns");
            return Read().Where(a => a.Installed && !local.ContainsKey(a.Key)
                                     && !a.Folders.Any(f => Directory.Exists(Path.Combine(addOns, f)))).ToList();
        }

        // ------------------------------------------------------------------ the file

        static bool Valid(SyncedAddon a) =>
            a != null && !string.IsNullOrEmpty(a.Key) && a.Folders != null && a.Folders.Count > 0
            && a.Folders.All(f => !string.IsNullOrEmpty(f) && f.Trim('.').Length > 0 && f.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);

        static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        List<SyncedAddon> Read()
        {
            try
            {
                if (File.Exists(_file))
                    return (Json.Deserialize<ListFile>(File.ReadAllText(_file))?.Addons ?? new List<SyncedAddon>()).Where(Valid).ToList();
            }
            catch (Exception e) { Log.Write(FileName + " unreadable, starting a new one: " + e.Message); }
            return new List<SyncedAddon>();
        }

        /// <summary>Only when the content changed (sorted, so an untouched list keeps the folder's hash).</summary>
        bool Write(IEnumerable<SyncedAddon> addons)
        {
            var text = Json.Serialize(new ListFile { Addons = addons.OrderBy(a => a.Key, StringComparer.Ordinal).ToList() });
            try { if (File.Exists(_file) && File.ReadAllText(_file) == text) return false; } catch { }
            Directory.CreateDirectory(Path.GetDirectoryName(_file));
            AtomicFile.WriteAllText(_file, text);
            return true;
        }
    }
}
