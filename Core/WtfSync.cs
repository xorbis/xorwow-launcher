using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XorWoWLauncher.Core
{
    public enum WtfSyncOutcome { UpToDate, Uploaded, Downloaded, NewerOnRealm, Busy, Failed }

    public sealed class WtfSyncResult
    {
        public WtfSyncOutcome Outcome;
        public string Message;
        public static WtfSyncResult Of(WtfSyncOutcome o, string m) => new WtfSyncResult { Outcome = o, Message = m };
    }

    /// <summary>What this PC last agreed on with the realm, per game folder and account.</summary>
    public sealed class WtfSyncState
    {
        public long Rev { get; set; }
        public string Hash { get; set; } = "";
        /// <summary>The same without the addon list: tells a change of the settings from a change of the list.</summary>
        public string RealHash { get; set; } = "";
        public string SyncedAt { get; set; } = "";
        /// <summary>Keys of the addons installed here at the last sync (AddonListSync); null before the first.</summary>
        public List<string> Addons { get; set; }
    }

    /// <summary>
    /// Keeps WTF\Account\&lt;ACCOUNT&gt;\ (addon SavedVariables, per-character folders, AddOns.txt,
    /// layout-local.txt...) the same on every PC through the realm's xorwow-wtf-sync service
    /// (docker\wtf-sync\server.py, port 3725). Rules (user, 2026-10-21):
    ///   - upload only when the folder changed since this PC's last sync;
    ///   - download only when the realm holds a newer copy than this PC last saw, and never while the
    ///     game runs (it writes the folder back when it closes);
    ///   - both changed (played on two PCs between syncs): the most recently written copy wins.
    /// The realm keeps the last 10 uploads, and a download first zips the folder it replaces into
    /// %APPDATA%\XorWoW Launcher\wtf-backups (last 5 per account), so nothing is ever lost for good.
    /// The *-cache files are left out: the game already syncs macros/bindings/settings with the realm.
    /// Login is the authserver's SRP6 run by the service itself, then every request is HMAC-signed
    /// with the session key - the password never travels.
    /// </summary>
    public sealed class WtfSync : IDisposable
    {
        public const int Port = 3725;
        const int KeepBackups = 5;

        readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        readonly string _base, _user, _gameDir, _folder;
        readonly byte[] _credential;
        readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        string _sid;
        byte[] _key;
        long _clockOffset;   // server clock - ours, from the login challenge

        public WtfSync(string host, string user, byte[] credential, string gameDir)
        {
            _base = "http://" + host + ":" + Port;
            _user = AuthClient.Upper(user);
            _credential = credential;
            _gameDir = Path.GetFullPath(gameDir);
            _folder = Path.Combine(_gameDir, "WTF", "Account", _user);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("XorWoWLauncher/" + App.Version);
        }

        public string Account => _user;
        public string Folder => _folder;

        /// <summary>
        /// The addon list (AddonListSync.FileName) is merged rather than replaced: called with the other
        /// copy's bytes (the realm's before an upload over a newer copy, this PC's after a download),
        /// it merges them into the local file and says whether that changed. It does not count as a
        /// change of the settings for "the copy written last wins", so a new PC never overwrites them.
        /// </summary>
        public Func<byte[], bool> MergeAddonList { get; set; }

        public void Dispose() => _http.Dispose();

        // ------------------------------------------------------------------ the sync itself

        /// <summary>One sync in whichever direction is due. <paramref name="gameRunning"/>: upload only.</summary>
        public async Task<WtfSyncResult> SyncAsync(bool gameRunning, CancellationToken ct)
        {
            if (!await _gate.WaitAsync(0)) return WtfSyncResult.Of(WtfSyncOutcome.Busy, "a sync is already running");
            try
            {
                var local = await Task.Run(() => Scan(), ct);
                var remote = await MetaAsync(ct);
                var st = LoadState();

                if (remote.Rev == 0)
                {
                    if (local.Files.Count == 0) return WtfSyncResult.Of(WtfSyncOutcome.UpToDate, "nothing to sync yet");
                    return await UploadAsync(local, 0, gameRunning, ct);
                }
                if (local.Hash == remote.Hash)
                {
                    SaveState(remote.Rev, remote.Hash, local.RealHash);
                    return WtfSyncResult.Of(WtfSyncOutcome.UpToDate, "up to date");
                }

                var remoteChanged = st == null || remote.Rev != st.Rev || remote.Hash != st.Hash;
                // A folder without settings (new install, WTF deleted) never goes up over the realm's copy
                var localChanged = (st == null || local.Hash != st.Hash) && local.RealCount > 0;
                // Only the addon list changed here: it merges into the realm's copy, never replaces it
                var settingsChanged = local.RealCount > 0 && (st == null || string.IsNullOrEmpty(st.RealHash) || local.RealHash != st.RealHash);
                // Both changed: the copy written last wins (a fresh PC with an empty folder never uploads)
                var download = !localChanged || (remoteChanged && (!settingsChanged || remote.ContentTime >= local.ContentTime));
                Log.Write($"wtf sync {_user}: local {(localChanged ? "changed" : "unchanged")} (written {Stamp(local.ContentTime)}), " +
                          $"realm rev {remote.Rev} {(remoteChanged ? "new" : "known")} (written {Stamp(remote.ContentTime)}) -> {(download ? "download" : "upload")}");
                if (!download)
                {
                    // Over a copy this PC has not seen: its addon list joins this one's first
                    if (remoteChanged && MergeAddonList != null)
                    {
                        var theirs = await FetchAddonListAsync(ct);
                        if (theirs != null && MergeAddonList(theirs)) local = await Task.Run(() => Scan(), ct);
                    }
                    return await UploadAsync(local, remote.Rev, gameRunning, ct);
                }
                if (gameRunning) return WtfSyncResult.Of(WtfSyncOutcome.NewerOnRealm, "a newer copy is on the realm - fetched at the next Play");
                var mine = MergeAddonList == null ? null : ReadIfExists(Path.Combine(_folder, AddonListSync.FileName));
                var got = await DownloadAsync(remote, ct);
                // This PC's own addon changes go back into the downloaded list, and up to the realm
                if (mine != null && MergeAddonList(mine))
                {
                    var up = await UploadAsync(await Task.Run(() => Scan(), ct), remote.Rev, false, ct);
                    Log.Write($"wtf sync {_user}: this PC's addon changes merged into the realm's copy: {up.Message}");
                }
                return got;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                Log.Write($"wtf sync {_user}: {e.Message}");
                return WtfSyncResult.Of(WtfSyncOutcome.Failed, Friendly(e));
            }
            finally { _gate.Release(); }
        }

        async Task<WtfSyncResult> UploadAsync(Snapshot local, long baseRev, bool gameRunning, CancellationToken ct)
        {
            // The game writes SavedVariables at logout / reload: let it finish before reading them
            if (gameRunning && local.Newest > DateTime.UtcNow.AddSeconds(-10))
                return WtfSyncResult.Of(WtfSyncOutcome.Busy, "the game is writing its settings - next time");
            var (zip, hash) = await Task.Run(() => Pack(local), ct);
            using (var res = await SendAsync(HttpMethod.Put, "/v1/wtf", zip, ct,
                       ("X-Base-Rev", baseRev.ToString()), ("X-Content-Time", local.ContentTime.ToString())))
            {
                var meta = Meta.Parse(await res.Content.ReadAsStringAsync());
                if (res.StatusCode == HttpStatusCode.Conflict)
                    return WtfSyncResult.Of(WtfSyncOutcome.NewerOnRealm, "another PC saved a newer copy - fetched at the next Play");
                Check(res, meta);
                SaveState(meta.Rev, meta.Hash, local.RealHash);
                Log.Write($"wtf sync {_user}: uploaded {local.Files.Count} files ({zip.Length / 1024} KB) as rev {meta.Rev}" +
                          (meta.Hash == hash ? "" : " (hash differs from ours: a file changed while packing)"));
                return WtfSyncResult.Of(WtfSyncOutcome.Uploaded, "saved to the realm");
            }
        }

        async Task<byte[]> FetchAddonListAsync(CancellationToken ct)
        {
            using (var res = await SendAsync(HttpMethod.Get, "/v1/wtf/data", null, ct))
            {
                if (!res.IsSuccessStatusCode) return null;
                using (var zip = new ZipArchive(new MemoryStream(await res.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read))
                {
                    var e = zip.Entries.FirstOrDefault(x => x.FullName.Equals(AddonListSync.FileName, StringComparison.OrdinalIgnoreCase));
                    if (e == null) return null;
                    using (var s = e.Open()) { var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }
                }
            }
        }

        static byte[] ReadIfExists(string path)
        {
            try { return File.Exists(path) ? ReadShared(path) : null; } catch (IOException) { return null; }
        }

        async Task<WtfSyncResult> DownloadAsync(Meta remote, CancellationToken ct)
        {
            byte[] zip;
            using (var res = await SendAsync(HttpMethod.Get, "/v1/wtf/data", null, ct))
            {
                if (!res.IsSuccessStatusCode) Check(res, Meta.Parse(await res.Content.ReadAsStringAsync()));
                zip = await res.Content.ReadAsByteArrayAsync();
            }
            var now = await Task.Run(() => { Apply(zip); return Scan(); }, ct);
            SaveState(remote.Rev, remote.Hash, now.RealHash);
            Log.Write($"wtf sync {_user}: downloaded rev {remote.Rev} ({zip.Length / 1024} KB)");
            return WtfSyncResult.Of(WtfSyncOutcome.Downloaded, "updated from the realm (saved on another PC)");
        }

        // ------------------------------------------------------------------ the folder

        sealed class Snapshot
        {
            public List<string> Files = new List<string>();   // relative, '/'-separated
            public string Hash, RealHash;
            public int RealCount;                               // files other than the addon list
            public long ContentTime;                            // newest write of those, unix seconds
            public DateTime Newest;
        }

        static bool Excluded(string name)
        {
            var n = name.ToLowerInvariant();
            return n == "cache.md5" || n.EndsWith("-cache.wtf") || n.EndsWith("-cache.txt")
                || n.EndsWith(".bak") || n.EndsWith(".old") || n.EndsWith(".tmp") || n.EndsWith(".xwsync");
        }

        IEnumerable<string> LocalFiles(string root)
        {
            if (!Directory.Exists(root)) yield break;
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                if (!Excluded(Path.GetFileName(f)))
                    yield return f.Substring(root.Length).TrimStart('\\').Replace('\\', '/');
        }

        Snapshot Scan()
        {
            var s = new Snapshot { Newest = DateTime.MinValue };
            var entries = new List<(string, string)>();
            foreach (var rel in LocalFiles(_folder))
            {
                var full = Path.Combine(_folder, rel.Replace('/', '\\'));
                if (!rel.Equals(AddonListSync.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    var t = File.GetLastWriteTimeUtc(full);
                    if (t > s.Newest) s.Newest = t;
                    s.RealCount++;
                }
                s.Files.Add(rel);
                entries.Add((rel.ToLowerInvariant(), Sha256Hex(ReadShared(full))));
            }
            s.Hash = ContentHash(entries);
            s.RealHash = ContentHash(entries.Where(e => e.Item1 != AddonListSync.FileName.ToLowerInvariant()).ToList());
            s.ContentTime = s.RealCount == 0 ? 0 : new DateTimeOffset(s.Newest, TimeSpan.Zero).ToUnixTimeSeconds();
            return s;
        }

        /// <summary>The same digest the service computes over the zip: sorted lower-cased paths with each file's SHA-256.</summary>
        static string ContentHash(List<(string path, string sha)> entries)
        {
            var sb = new StringBuilder();
            foreach (var e in entries.OrderBy(e => e.path, StringComparer.Ordinal)) sb.Append(e.path).Append('\n').Append(e.sha).Append('\n');
            return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        (byte[], string) Pack(Snapshot s)
        {
            var entries = new List<(string, string)>();
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (var rel in s.Files)
                {
                    byte[] data;
                    var full = Path.Combine(_folder, rel.Replace('/', '\\'));
                    try { data = ReadShared(full); }
                    catch (FileNotFoundException) { continue; }
                    var e = zip.CreateEntry(rel, CompressionLevel.Optimal);
                    // Written time kept (ExtractToFile restores it): a downloaded copy is not "newer" than its source
                    e.LastWriteTime = File.GetLastWriteTime(full);
                    using (var w = e.Open()) w.Write(data, 0, data.Length);
                    entries.Add((rel.ToLowerInvariant(), Sha256Hex(data)));
                }
            return (ms.ToArray(), ContentHash(entries));
        }

        /// <summary>Makes the folder the zip's copy: unpacked aside first, the old folder zipped into wtf-backups.</summary>
        void Apply(byte[] zipBytes)
        {
            var tmp = _folder + ".xwsync";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            var root = Path.GetFullPath(tmp) + "\\";
            var incoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
                    foreach (var e in zip.Entries)
                    {
                        if (e.FullName.EndsWith("/") || Excluded(e.Name)) continue;
                        var dest = Path.GetFullPath(Path.Combine(tmp, e.FullName.Replace('/', '\\')));
                        if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("bad path in the realm's copy: " + e.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                        incoming.Add(dest.Substring(root.Length).Replace('\\', '/'));
                    }

                Backup();
                foreach (var rel in LocalFiles(_folder).ToList())
                    if (!incoming.Contains(rel)) File.Delete(Path.Combine(_folder, rel.Replace('/', '\\')));
                foreach (var rel in incoming)
                {
                    var dest = Path.Combine(_folder, rel.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(Path.Combine(tmp, rel.Replace('/', '\\')), dest, true);
                }
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        void Backup()
        {
            if (!Directory.Exists(_folder) || !Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Any()) return;
            var dir = Path.Combine(AppPaths.Dir, "wtf-backups");
            Directory.CreateDirectory(dir);
            ZipFile.CreateFromDirectory(_folder, Path.Combine(dir, _user + "-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".zip"));
            foreach (var old in new DirectoryInfo(dir).GetFiles(_user + "-*.zip").OrderByDescending(f => f.Name).Skip(KeepBackups))
                try { old.Delete(); } catch { }
        }

        static byte[] ReadShared(string path)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var ms = new MemoryStream();
                f.CopyTo(ms);
                return ms.ToArray();
            }
        }

        // ------------------------------------------------------------------ this PC's state

        static string StateFile => Path.Combine(AppPaths.Dir, "wtf-sync.json");
        string StateKey => _gameDir.TrimEnd('\\').ToLowerInvariant() + "|" + _user;
        static readonly object StateGate = new object();

        static Dictionary<string, WtfSyncState> ReadStates()
        {
            try { if (File.Exists(StateFile)) return Json.Deserialize<Dictionary<string, WtfSyncState>>(File.ReadAllText(StateFile)) ?? new Dictionary<string, WtfSyncState>(); }
            catch (Exception e) { Log.Write("wtf-sync.json unreadable, starting fresh: " + e.Message); }
            return new Dictionary<string, WtfSyncState>();
        }

        public WtfSyncState LoadState()
        {
            lock (StateGate) return ReadStates().TryGetValue(StateKey, out var s) ? s : null;
        }

        void SaveState(long rev, string hash, string realHash)
        {
            lock (StateGate)
            {
                var all = ReadStates();
                all.TryGetValue(StateKey, out var old);
                all[StateKey] = new WtfSyncState { Rev = rev, Hash = hash, RealHash = realHash, SyncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Addons = old?.Addons };
                Directory.CreateDirectory(AppPaths.Dir);
                AtomicFile.WriteAllText(StateFile, Json.Serialize(all));
            }
        }

        /// <summary>The addons installed here once a sync went through (AddonListSync's "previous").</summary>
        public void SaveAddonKeys(IEnumerable<string> keys)
        {
            lock (StateGate)
            {
                var all = ReadStates();
                if (!all.TryGetValue(StateKey, out var st)) return;   // no sync yet: the next one counts them all as new
                st.Addons = keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                AtomicFile.WriteAllText(StateFile, Json.Serialize(all));
            }
        }

        // ------------------------------------------------------------------ the service

        sealed class Meta
        {
            public long Rev, ContentTime;
            public string Hash = "", Error = "";

            public static Meta Parse(string json)
            {
                object o;
                try { o = Json.Parse(json); } catch { return new Meta { Error = "unexpected answer from the realm" }; }
                return new Meta { Rev = Json.Long(o, "rev"), ContentTime = Json.Long(o, "contentTime"), Hash = Json.Str(o, "hash"), Error = Json.Str(o, "error") };
            }
        }

        sealed class SyncException : Exception { public SyncException(string m) : base(m) { } }

        static void Check(HttpResponseMessage res, Meta meta)
        {
            if (res.IsSuccessStatusCode) return;
            throw new SyncException($"{(int)res.StatusCode} {(meta.Error.Length > 0 ? meta.Error : res.ReasonPhrase)}");
        }

        async Task<Meta> MetaAsync(CancellationToken ct)
        {
            using (var res = await SendAsync(HttpMethod.Get, "/v1/wtf", null, ct))
            {
                var meta = Meta.Parse(await res.Content.ReadAsStringAsync());
                Check(res, meta);
                return meta;
            }
        }

        async Task LoginAsync(CancellationToken ct)
        {
            _sid = null;
            var acc = Encoding.ASCII.GetBytes(_user);
            var ch = await PostJsonAsync("/v1/auth/challenge", new Dictionary<string, object> { ["user"] = _user }, ct);
            var sid = Json.Str(ch, "sid");
            if (!AuthClient.Srp6Proof(acc, _credential, Hex(Json.Str(ch, "B")), Hex(Json.Str(ch, "g")), Hex(Json.Str(ch, "N")),
                                      Hex(Json.Str(ch, "salt")), out var A, out var M1, out var K))
                throw new SyncException("bad answer from the realm");
            var serverTime = Json.Long(ch, "time");
            if (serverTime > 0) _clockOffset = serverTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var pr = await PostJsonAsync("/v1/auth/proof", new Dictionary<string, object> { ["sid"] = sid, ["A"] = Hex(A), ["M1"] = Hex(M1) }, ct);
            if (!Hex(AuthClient.ServerProof(A, M1, K)).Equals(Json.Str(pr, "M2"), StringComparison.OrdinalIgnoreCase))
                throw new SyncException("the realm's answer did not check out");
            _sid = sid;
            _key = K;
        }

        async Task<object> PostJsonAsync(string path, Dictionary<string, object> body, CancellationToken ct)
        {
            using (var content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json"))
            using (var res = await _http.PostAsync(_base + path, content, ct))
            {
                var o = Json.Parse(await res.Content.ReadAsStringAsync());
                if (!res.IsSuccessStatusCode) throw new SyncException($"{(int)res.StatusCode} {Json.Str(o, "error")}");
                return o;
            }
        }

        /// <summary>A signed request; logs in first when needed, and once more when the service forgot the session (restarted).</summary>
        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[] body, CancellationToken ct, params (string, string)[] headers)
        {
            for (var attempt = 0; ; attempt++)
            {
                if (_sid == null) await LoginAsync(ct);
                var req = new HttpRequestMessage(method, _base + path);
                if (body != null) req.Content = new ByteArrayContent(body);
                var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _clockOffset;
                string sig;
                using (var h = new HMACSHA256(_key))
                    sig = Hex(h.ComputeHash(Encoding.UTF8.GetBytes($"{method.Method}\n{path}\n{t}\n{Sha256Hex(body ?? new byte[0])}")));
                req.Headers.Add("X-Session", _sid);
                req.Headers.Add("X-Time", t.ToString());
                req.Headers.Add("X-Signature", sig);
                foreach (var (k, v) in headers) req.Headers.Add(k, v);
                var res = await _http.SendAsync(req, ct);
                req.Dispose();
                if (res.StatusCode != HttpStatusCode.Unauthorized || attempt > 0) return res;
                res.Dispose();
                _sid = null;
            }
        }

        // ------------------------------------------------------------------ helpers

        static string Friendly(Exception e)
        {
            if (e is HttpRequestException || e is TaskCanceledException) return "the realm's sync service does not answer";
            if (e is SyncException && e.Message.StartsWith("403")) return "the realm refused the login";
            return e.Message;
        }

        static string Stamp(long unix) => unix == 0 ? "never" : DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data));
        }

        static string Hex(byte[] b) => BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();

        static byte[] Hex(string s)
        {
            var r = new byte[s.Length / 2];
            for (var i = 0; i < r.Length; i++) r[i] = Convert.ToByte(s.Substring(2 * i, 2), 16);
            return r;
        }
    }
}
