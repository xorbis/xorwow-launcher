using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace XorWoWLauncher.Core
{
    public sealed class StepProgress
    {
        public string Stage;     // what is happening, for the status line
        public string Detail;    // current file
        public long Done, Total; // bytes (0 total = indeterminate)
    }

    /// <summary>Passes on at most ten reports a second (hashing reports every megabyte).</summary>
    public sealed class Throttled : IProgress<StepProgress>
    {
        readonly IProgress<StepProgress> _inner;
        long _last;
        public Throttled(IProgress<StepProgress> inner) { _inner = inner; }
        public void Report(StepProgress p)
        {
            var now = Stopwatch.GetTimestamp();
            if (now - Interlocked.Read(ref _last) < Stopwatch.Frequency / 10 && p.Done < p.Total) return;
            Interlocked.Exchange(ref _last, now);
            _inner.Report(p);
        }
    }

    public sealed class PlanItem
    {
        public string Local;     // relative to the game folder, forward slashes
        public string Url;       // relative to the file server
        public long Size;
        public string Sha256;
    }

    public sealed class UpdatePlan
    {
        public List<PlanItem> Download = new List<PlanItem>();
        public List<string> Delete = new List<string>();      // relative files
        public List<string> DeleteDirs = new List<string>();  // relative folders (released addons dropped)
        public long Bytes => Download.Sum(d => d.Size);
        public bool Empty => Download.Count == 0 && Delete.Count == 0 && DeleteDirs.Count == 0;
    }

    public static class GameFolder
    {
        public static bool HasWow(string dir) => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "Wow.exe"));

        public enum Layout { Ready, Adoptable, Unusable }

        /// <summary>
        /// What an installation root (the folder XorWoW.exe sits in, the game in its client\) is:
        /// Ready - empty but for the launcher, or client\Wow.exe already there;
        /// Adoptable - a WoW folder itself (Wow.exe at the root): its files can move into client\;
        /// Unusable - anything else, with the reason.
        /// </summary>
        public static (Layout layout, string why) Classify(string root)
        {
            var why = CheckRoot(root);
            if (why != null) return (Layout.Unusable, why);
            var full = Path.GetFullPath(root).TrimEnd('\\');
            if (!Directory.Exists(full) || HasWow(Path.Combine(full, "client"))) return (Layout.Ready, null);
            if (!Directory.EnumerateFileSystemEntries(full).Any(e => !IsLauncherEntry(e))) return (Layout.Ready, null);
            if (HasWow(full)) return (Layout.Adoptable, full + " is a World of Warcraft folder.");
            return (Layout.Unusable, "That folder holds other files.");
        }

        /// <summary>The launcher itself and the client\ folder it installs the game into.</summary>
        static bool IsLauncherEntry(string path) =>
            IsOwn(path) || Path.GetFileName(path).Equals("client", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).StartsWith(".xorwow");

        /// <summary>Moves a WoW folder's files into its client\ subfolder (same drive: nothing is copied).</summary>
        public static void Adopt(string root)
        {
            var client = Path.Combine(root, "client");
            if (Directory.Exists(client) && Directory.EnumerateFileSystemEntries(client).Any())
                throw new IOException(client + " already exists and is not empty.");
            Directory.CreateDirectory(client);
            foreach (var e in Directory.EnumerateFileSystemEntries(root).ToList())
            {
                if (IsLauncherEntry(e)) continue;
                var dest = Path.Combine(client, Path.GetFileName(e));
                if (Directory.Exists(e)) Directory.Move(e, dest); else File.Move(e, dest);
            }
            Log.Write("moved the game files of " + root + " into client\\");
        }

        /// <summary>Null when <paramref name="dir"/> may be an installation root, else why not.</summary>
        static string CheckRoot(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return "Choose a folder.";
            string full;
            try { full = Path.GetFullPath(dir).TrimEnd('\\'); } catch { return "That is not a valid folder."; }
            if (full.Length <= 3) return "Pick a folder, not a whole drive.";
            var forbidden = new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            };
            if (forbidden.Any(f => !string.IsNullOrEmpty(f) && string.Equals(f.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase)))
                return "XorWoW needs a folder of its own (e.g. C:\\Games\\XorWoW), not " + full + ".";
            return null;
        }

        /// <summary>The launcher's file name at the root of the installation.</summary>
        public const string LauncherName = "XorWoW.exe";

        /// <summary>The launcher's own files at the root of the game folder (the running exe whatever its name, XorWoW.exe, their .old/.new).</summary>
        public static bool IsOwn(string path)
        {
            var own = Process.GetCurrentProcess().MainModule.FileName;
            var p = Path.GetFullPath(path);
            var canonical = Path.Combine(Path.GetDirectoryName(p), LauncherName);
            foreach (var exe in new[] { own, canonical })
                if (p.Equals(exe, StringComparison.OrdinalIgnoreCase) || p.Equals(exe + ".old", StringComparison.OrdinalIgnoreCase) || p.Equals(exe + ".new", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public static bool Writable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".xorwow-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>A "XorWoW" shortcut on the desktop to the launcher (WScript.Shell, late-bound).</summary>
        public static void DesktopShortcut(string exe)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                var shell = Activator.CreateInstance(shellType);
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var lnkPath = Path.Combine(desktop, "XorWoW.lnk");
                var lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                var t = lnk.GetType();
                // Never replace a "XorWoW" shortcut of the player's own that points elsewhere
                var existing = File.Exists(lnkPath) ? Convert.ToString(t.InvokeMember("TargetPath", BindingFlags.GetProperty, null, lnk, null)) : "";
                if (existing.Length > 0 && !existing.Equals(exe, StringComparison.OrdinalIgnoreCase))
                {
                    lnkPath = Path.Combine(desktop, "XorWoW Launcher.lnk");
                    lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                }
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { exe });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(exe) });
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { exe + ",0" });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "XorWoW" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            catch (Exception e) { Log.Write("desktop shortcut: " + e.Message); }
        }

        public static bool GameRunning(string dir)
        {
            foreach (var p in Process.GetProcessesByName("Wow"))
            {
                try
                {
                    if (string.Equals(Path.GetDirectoryName(p.MainModule.FileName).TrimEnd('\\'), Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { return true; }   // cannot tell: better safe
                finally { p.Dispose(); }
            }
            return false;
        }
    }

    /// <summary>Brings a game folder in line with the manifest: the client files, then the released XorWoW addons.</summary>
    public sealed class ClientUpdater
    {
        const string AddOns = "Interface/AddOns/";
        readonly Net _net;
        readonly string _dir;
        readonly GameState _state;

        public ClientUpdater(Net net, string gameDir, GameState state)
        {
            _net = net; _dir = Path.GetFullPath(gameDir); _state = state;
        }

        string Full(string rel) => Path.Combine(_dir, rel.Replace('/', '\\'));

        static string Key(string rel) => rel.Replace('\\', '/').ToLowerInvariant();

        // ---------- planning

        public Task<UpdatePlan> PlanClientAsync(Manifest m, bool verifyAll, IProgress<StepProgress> progress, CancellationToken ct) => Task.Run(() =>
        {
            var plan = new UpdatePlan();
            Compare(m.Client.Select(f => new PlanItem { Local = f.Path, Url = "client/" + f.Path, Size = f.Size, Sha256 = f.Sha256 }).ToList(),
                    plan, verifyAll, "Checking game files", progress, ct);

            var wanted = new HashSet<string>(m.Client.Select(f => Key(f.Path)));
            var delete = new HashSet<string>();

            // Files the launcher installed that the realm no longer ships, wherever they are
            foreach (var rel in _state.Managed)
                if (!wanted.Contains(Key(rel)) && File.Exists(Full(rel))) delete.Add(rel);

            // Files the realm explicitly retires
            foreach (var rel in m.Remove)
                if (!wanted.Contains(Key(rel)) && File.Exists(Full(rel))) delete.Add(rel);

            // Anything else outside the players' own folders (WTF, Interface, Data, Screenshots...),
            // only in a real WoW folder: never sweep a folder the launcher did not recognise.
            if (GameFolder.HasWow(_dir))
            {
                var keep = m.Keep.Select(k => k.Replace('\\', '/').ToLowerInvariant()).ToList();
                foreach (var rel in Walk(_dir, "", keep))
                    if (!wanted.Contains(Key(rel))) delete.Add(rel);
            }
            plan.Delete.AddRange(delete.OrderBy(d => d));
            return plan;
        }, ct);

        public Task<UpdatePlan> PlanAddonsAsync(Manifest m, IProgress<StepProgress> progress, CancellationToken ct) => Task.Run(() =>
        {
            var plan = new UpdatePlan();
            Compare(m.AddonFiles.Select(f => new PlanItem { Local = AddOns + f.Path, Url = m.AddonsBase + f.Path, Size = f.Size, Sha256 = f.Sha256 }).ToList(),
                    plan, false, "Checking the XorWoW addons", progress, ct);

            var wanted = new HashSet<string>(m.AddonFiles.Select(f => Key(AddOns + f.Path)));
            foreach (var folder in m.AddonFolders)
            {
                var dir = Full(AddOns + folder);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var rel = AddOns + folder + "/" + f.Substring(dir.Length + 1).Replace('\\', '/');
                    if (!wanted.Contains(Key(rel)) && !rel.EndsWith(".xwpart")) plan.Delete.Add(rel);
                }
            }
            // An addon dropped from the release goes away with it
            foreach (var old in _state.XorWoWAddonFolders)
                if (!m.AddonFolders.Any(f => f.Equals(old, StringComparison.OrdinalIgnoreCase)) && Directory.Exists(Full(AddOns + old)))
                    plan.DeleteDirs.Add(AddOns + old);
            return plan;
        }, ct);

        void Compare(List<PlanItem> items, UpdatePlan plan, bool verifyAll, string stage, IProgress<StepProgress> progress, CancellationToken ct)
        {
            long total = 0, done = 0;
            var toHash = new List<(PlanItem item, FileInfo fi)>();
            foreach (var it in items)
            {
                var fi = new FileInfo(Full(it.Local));
                if (!fi.Exists || fi.Length != it.Size) { plan.Download.Add(it); continue; }
                if (!verifyAll && _state.Hashes.TryGetValue(Key(it.Local), out var h) && h.Size == fi.Length && h.Ticks == fi.LastWriteTimeUtc.Ticks)
                {
                    if (h.Sha != it.Sha256) plan.Download.Add(it);
                    continue;
                }
                toHash.Add((it, fi));
                total += fi.Length;
            }
            foreach (var (it, fi) in toHash)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new StepProgress { Stage = stage, Detail = it.Local, Done = done, Total = total });
                var sha = HashFile(fi.FullName, n => { done += n; progress?.Report(new StepProgress { Stage = stage, Detail = it.Local, Done = done, Total = total }); }, ct);
                _state.Hashes[Key(it.Local)] = new HashEntry { Size = fi.Length, Ticks = fi.LastWriteTimeUtc.Ticks, Sha = sha };
                if (sha != it.Sha256) plan.Download.Add(it);
            }
            if (toHash.Count > 0) _state.Save();
        }

        static IEnumerable<string> Walk(string root, string rel, List<string> keep)
        {
            var dir = rel.Length == 0 ? root : Path.Combine(root, rel.Replace('/', '\\'));
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var r = rel + Path.GetFileName(f);
                if (Kept(r, keep) || GameFolder.IsOwn(f) || r.EndsWith(".xwpart", StringComparison.OrdinalIgnoreCase)) continue;
                yield return r;
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;   // never follow links out of the folder
                var r = rel + Path.GetFileName(d) + "/";
                if (Kept(r, keep)) continue;
                foreach (var x in Walk(root, r, keep)) yield return x;
            }
        }

        static bool Kept(string rel, List<string> keep)
        {
            var r = rel.ToLowerInvariant();
            return keep.Any(k => k.EndsWith("/") ? r.StartsWith(k) : r == k);
        }

        public static string HashFile(string path, Action<long> onBytes, CancellationToken ct)
        {
            using (var sha = new SHA256CryptoServiceProvider())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan))
            {
                var buf = new byte[1 << 20];
                int r;
                while ((r = f.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.TransformBlock(buf, 0, r, null, 0);
                    onBytes?.Invoke(r);
                }
                sha.TransformFinalBlock(buf, 0, 0);
                return Net.Hex(sha.Hash);
            }
        }

        // ---------- applying

        public async Task ApplyAsync(UpdatePlan plan, string stage, IProgress<StepProgress> progress, CancellationToken ct)
        {
            long total = plan.Bytes, done = 0;
            foreach (var rel in plan.Delete)
            {
                var f = Full(rel);
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                    _state.Hashes.Remove(Key(rel));
                    Log.Write("removed " + rel);
                }
                catch (Exception e) { Log.Write($"could not remove {rel}: {e.Message}"); }
            }
            foreach (var rel in plan.DeleteDirs)
            {
                try { Directory.Delete(Full(rel), true); Log.Write("removed folder " + rel); }
                catch (Exception e) { Log.Write($"could not remove {rel}: {e.Message}"); }
            }

            foreach (var it in plan.Download)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new StepProgress { Stage = stage, Detail = it.Local, Done = done, Total = total });
                await _net.DownloadAsync(it.Url, Full(it.Local), it.Size, it.Sha256, n =>
                {
                    done += n;
                    progress?.Report(new StepProgress { Stage = stage, Detail = it.Local, Done = done, Total = total });
                }, ct);
                var fi = new FileInfo(Full(it.Local));
                _state.Hashes[Key(it.Local)] = new HashEntry { Size = fi.Length, Ticks = fi.LastWriteTimeUtc.Ticks, Sha = it.Sha256 };
                _state.Save();
                Log.Write("updated " + it.Local);
            }
            _state.Save();
        }

        public void MarkClientApplied(Manifest m)
        {
            _state.Managed = m.Client.Select(f => f.Path).ToList();
            _state.Save();
        }

        public void MarkAddonsApplied(Manifest m)
        {
            _state.XorWoWAddonsVersion = m.AddonsVersion;
            _state.XorWoWAddonFolders = m.AddonFolders.ToList();
            _state.Save();
        }
    }
}
