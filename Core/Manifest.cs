using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XorWoWLauncher.Core
{
    public sealed class FileEntry
    {
        public string Path;     // relative, forward slashes, as served
        public long Size;
        public string Sha256;   // lowercase hex
    }

    /// <summary>
    /// manifest.json from the realm's file server (scripts\update-launcher-files.ps1 writes it): every
    /// client file and every file of the realm's addons with size and SHA-256, the launcher's
    /// own build, the release notes' hash. It is RSA-signed (manifest.sig) with a key that only
    /// LEGION holds and every download is checked against these hashes, so nothing that did not
    /// come from the realm is ever written into a game folder, even if the web host were tampered with.
    /// </summary>
    public sealed class Manifest
    {
        public DateTimeOffset Generated;
        public List<FileEntry> Client = new List<FileEntry>();
        public List<string> Keep = new List<string>();
        public List<string> Remove = new List<string>();
        public string AddonsVersion = "";
        public string AddonsBase = "client/Interface/AddOns/";   // where the addon files are served
        public List<string> AddonFolders = new List<string>();
        public List<FileEntry> AddonFiles = new List<FileEntry>();
        public string LauncherVersion = "";
        public FileEntry LauncherFile;
        public FileEntry NotesFile;

        public long ClientBytes => Client.Sum(f => f.Size);

        public static Manifest Parse(string json)
        {
            var root = Json.Parse(json);
            if (Json.Long(root, "format") != 1) throw new InvalidDataException("This launcher is too old for the realm's file server - download the new one from " + App.Settings.FilesBase);
            var m = new Manifest();
            DateTimeOffset.TryParse(Json.Str(root, "generated"), out m.Generated);
            var client = Json.Obj(root, "client");
            m.Client = Files(Json.Arr(client, "files"));
            m.Keep = Json.Arr(client, "keep").Select(o => Convert.ToString(o)).ToList();
            m.Remove = Json.Arr(client, "remove").Select(o => Convert.ToString(o)).ToList();
            var addons = Json.Obj(root, "addons");
            m.AddonsVersion = Json.Str(addons, "version");
            var addonsBase = Json.Str(addons, "base");
            if (addonsBase.Length > 0 && !addonsBase.Contains("..")) m.AddonsBase = addonsBase.TrimEnd('/') + "/";
            m.AddonFolders = Json.Arr(addons, "folders").Select(o => Convert.ToString(o)).ToList();
            m.AddonFiles = Files(Json.Arr(addons, "files"));
            var launcher = Json.Obj(root, "launcher");
            if (launcher != null)
            {
                m.LauncherVersion = Json.Str(launcher, "version");
                m.LauncherFile = File(launcher);
            }
            var notes = Json.Obj(root, "notes");
            if (notes != null) m.NotesFile = File(notes);
            return m;
        }

        static List<FileEntry> Files(List<object> list) => list.Select(File).Where(f => f != null).ToList();

        static FileEntry File(object o)
        {
            var p = Json.Str(o, "path");
            if (string.IsNullOrEmpty(p) || p.Contains("..") || System.IO.Path.IsPathRooted(p.Replace('/', '\\'))) return null;
            return new FileEntry { Path = p, Size = Json.Long(o, "size"), Sha256 = Json.Str(o, "sha256").ToLowerInvariant() };
        }

        static RSACryptoServiceProvider _key;
        static RSACryptoServiceProvider Key
        {
            get
            {
                if (_key != null) return _key;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("signing-key.public.xml"))
                using (var r = new StreamReader(s))
                {
                    var rsa = new RSACryptoServiceProvider();
                    rsa.FromXmlString(r.ReadToEnd());
                    return _key = rsa;
                }
            }
        }

        public static async Task<Manifest> FetchAsync(Net net, CancellationToken ct)
        {
            var body = await net.GetBytesAsync("manifest.json", ct);
            var sig = Encoding.ASCII.GetString(await net.GetBytesAsync("manifest.sig", ct)).Trim();
            bool ok;
            try { ok = Key.VerifyData(body, "SHA256", Convert.FromBase64String(sig)); }
            catch (FormatException) { ok = false; }
            if (!ok) throw new InvalidDataException("The file server's manifest is not signed by XorWoW - refusing to update from it.");
            return Parse(Encoding.UTF8.GetString(body));
        }
    }

    /// <summary>HTTP to the realm's file server, and to Warperia.</summary>
    public sealed class Net : IDisposable
    {
        public readonly HttpClient Http;
        public string Base { get; }

        public Net(string baseUrl)
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            System.Net.ServicePointManager.DefaultConnectionLimit = 8;
            Base = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
            Http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate })
            { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("XorWoWLauncher/" + App.Version);
        }

        public static string UrlPath(string rel) => string.Join("/", rel.Split('/').Select(Uri.EscapeDataString));

        public Uri Url(string rel) => new Uri(Base + UrlPath(rel));

        public async Task<byte[]> GetBytesAsync(string relOrAbs, CancellationToken ct, int timeoutSeconds = 30)
        {
            var uri = relOrAbs.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(relOrAbs) : Url(relOrAbs);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using (var req = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                    using (var res = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token))
                    {
                        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{uri.Host}: {(int)res.StatusCode} {res.ReasonPhrase} for {uri.AbsolutePath}");
                        return await res.Content.ReadAsByteArrayAsync();
                    }
                }
            }
        }

        /// <summary>
        /// Downloads <paramref name="rel"/> into <paramref name="dest"/>, resuming a previous partial
        /// download (dest + ".xwpart") with an HTTP range, and only moves it in place when size and
        /// SHA-256 match the manifest. <paramref name="onBytes"/> gets each chunk's length.
        /// </summary>
        public async Task DownloadAsync(string rel, string dest, long size, string sha256, Action<long> onBytes, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            var part = dest + ".xwpart";
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadOnce(rel, dest, part, size, sha256, onBytes, ct);
                    return;
                }
                catch (Exception e) when (!(e is OperationCanceledException) && attempt < 4)
                {
                    Log.Write($"download {rel} failed (attempt {attempt}): {e.Message}");
                    if (e is InvalidDataException) { TryDelete(part); }
                    await Task.Delay(1500 * attempt, ct);
                }
            }
        }

        async Task DownloadOnce(string rel, string dest, string part, long size, string sha256, Action<long> onBytes, CancellationToken ct)
        {
            using (var hasher = new SHA256CryptoServiceProvider())
            {
                long have = 0;
                var buf = new byte[1 << 20];
                if (File.Exists(part))
                {
                    have = new FileInfo(part).Length;
                    if (have > size) { File.Delete(part); have = 0; }
                    else
                    {
                        using (var f = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                        {
                            int r;
                            while ((r = await f.ReadAsync(buf, 0, buf.Length, ct)) > 0) hasher.TransformBlock(buf, 0, r, null, 0);
                        }
                        onBytes?.Invoke(have);
                    }
                }

                if (have < size)
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, Url(rel)))
                    {
                        if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                        using (var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
                        {
                            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode} {res.ReasonPhrase} for {rel}");
                            if (have > 0 && res.StatusCode != System.Net.HttpStatusCode.PartialContent)
                            {
                                // Server ignored the range: start over
                                onBytes?.Invoke(-have);
                                have = 0;
                                hasher.Initialize();
                                File.Delete(part);
                            }
                            using (var src = await res.Content.ReadAsStreamAsync())
                            using (var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                            {
                                int r;
                                while (true)
                                {
                                    var read = src.ReadAsync(buf, 0, buf.Length, ct);
                                    // a stalled connection is retried instead of hanging forever
                                    if (await Task.WhenAny(read, Task.Delay(60000, ct)) != read) throw new IOException("no data for 60 s");
                                    r = await read;
                                    if (r <= 0) break;
                                    hasher.TransformBlock(buf, 0, r, null, 0);
                                    await dst.WriteAsync(buf, 0, r, ct);
                                    have += r;
                                    onBytes?.Invoke(r);
                                    if (have > size) break;
                                }
                            }
                        }
                    }
                }

                hasher.TransformFinalBlock(new byte[0], 0, 0);
                var got = Hex(hasher.Hash);
                if (have != size || got != sha256)
                {
                    onBytes?.Invoke(-have);
                    throw new InvalidDataException($"{rel}: got {have} bytes / {got.Substring(0, 12)}, expected {size} / {sha256.Substring(0, Math.Min(12, sha256.Length))}");
                }
                ReplaceFile(part, dest);
            }
        }

        public static void ReplaceFile(string src, string dest)
        {
            if (File.Exists(dest))
            {
                File.SetAttributes(dest, FileAttributes.Normal);
                File.Delete(dest);
            }
            File.Move(src, dest);
        }

        public static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        public static string Hex(byte[] b) => BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();

        public void Dispose() => Http.Dispose();
    }
}
