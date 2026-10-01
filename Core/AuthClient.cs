using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XorWoWLauncher.Core
{
    public enum LoginStatus { Ok, BadCredentials, TokenRequired, Banned, Suspended, Locked, Unreachable, Error }

    public sealed class LoginResult
    {
        public LoginStatus Status;
        public string Message;
        public static LoginResult Of(LoginStatus s, string m) => new LoginResult { Status = s, Message = m };
    }

    /// <summary>
    /// Checks an account against the realm's authserver with the same SRP6 exchange the 3.3.5a
    /// client does (AUTH_LOGON_CHALLENGE + AUTH_LOGON_PROOF, see AuthSession.cpp in the core), so no
    /// login service of our own is needed and the password never leaves the PC. The connection is
    /// closed right after the proof: no realm list is asked for, nothing else happens server-side
    /// than for a client that logs in and quits at the realm list.
    /// </summary>
    public static class AuthClient
    {
        const ushort Build = 12340;   // 3.3.5a
        static readonly BigInteger K3 = 3;

        /// <summary>SHA1(USER:PASS), both upper-cased like AccountMgr does; all the launcher keeps of a saved password.</summary>
        public static byte[] CredentialHash(string user, string password)
        {
            using (var sha = SHA1.Create())
                return sha.ComputeHash(Encoding.UTF8.GetBytes(Upper(user) + ":" + Upper(password)));
        }

        public static string Upper(string s) => (s ?? "").Trim().ToUpperInvariant();

        public static async Task<LoginResult> LoginAsync(string host, int port, string user, byte[] credentialHash,
                                                         string token, CancellationToken ct)
        {
            user = Upper(user);
            if (user.Length == 0 || user.Length > 16) return LoginResult.Of(LoginStatus.BadCredentials, "Enter your account name.");
            try
            {
                using (var tcp = new TcpClient())
                {
                    var connect = tcp.ConnectAsync(host, port);
                    if (await Task.WhenAny(connect, Task.Delay(8000, ct)) != connect)
                        return LoginResult.Of(LoginStatus.Unreachable, "The realm does not answer. It may be down or restarting.");
                    await connect;
                    tcp.NoDelay = true;
                    var net = tcp.GetStream();
                    net.ReadTimeout = net.WriteTimeout = 10000;

                    // AUTH_LOGON_CHALLENGE
                    var acc = Encoding.ASCII.GetBytes(user);
                    var ch = new MemoryStream();
                    var w = new BinaryWriter(ch);
                    w.Write((byte)0x00);                    // cmd
                    w.Write((byte)0x08);                    // protocol
                    w.Write((ushort)(30 + acc.Length));     // bytes after this field
                    w.Write(Encoding.ASCII.GetBytes("WoW\0"));
                    w.Write((byte)3); w.Write((byte)3); w.Write((byte)5);
                    w.Write(Build);
                    w.Write(Encoding.ASCII.GetBytes("68x\0"));   // "x86" reversed
                    w.Write(Encoding.ASCII.GetBytes("niW\0"));   // "Win"
                    w.Write(Encoding.ASCII.GetBytes("SUne"));    // "enUS"
                    w.Write((uint)0);                       // timezone bias
                    w.Write((uint)0x0100007F);              // ip (unused by the server)
                    w.Write((byte)acc.Length);
                    w.Write(acc);
                    await net.WriteAsync(ch.ToArray(), 0, (int)ch.Length, ct);

                    var head = await ReadExact(net, 3, ct);
                    if (head[0] != 0x00) return LoginResult.Of(LoginStatus.Error, "Unexpected answer from the realm.");
                    if (head[2] != 0x00) return Fail(head[2]);

                    var B = await ReadExact(net, 32, ct);
                    var gLen = (await ReadExact(net, 1, ct))[0];
                    var g = await ReadExact(net, gLen, ct);
                    var nLen = (await ReadExact(net, 1, ct))[0];
                    var N = await ReadExact(net, nLen, ct);
                    var salt = await ReadExact(net, 32, ct);
                    await ReadExact(net, 16, ct);               // version challenge
                    var flags = (await ReadExact(net, 1, ct))[0];
                    if ((flags & 0x01) != 0) await ReadExact(net, 20, ct);
                    if ((flags & 0x02) != 0) await ReadExact(net, 12, ct);
                    if ((flags & 0x04) != 0) await ReadExact(net, 1, ct);
                    bool needsToken = (flags & 0x04) != 0;
                    if (needsToken && string.IsNullOrWhiteSpace(token))
                        return LoginResult.Of(LoginStatus.TokenRequired, "This account uses an authenticator: enter its code.");

                    // SRP6, numbers little-endian as on the wire
                    var n = Num(N); var gg = Num(g); var b = Num(B);
                    if (b % n == 0) return LoginResult.Of(LoginStatus.Error, "Bad answer from the realm.");
                    byte[] A, M1, K;
                    using (var sha = SHA1.Create())
                    {
                        var x = Num(sha.ComputeHash(Concat(salt, credentialHash)));
                        var aBytes = new byte[32];
                        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(aBytes);
                        var a = Num(aBytes);
                        A = Bytes(BigInteger.ModPow(gg, a, n), 32);
                        var u = Num(sha.ComputeHash(Concat(A, B)));
                        var baseS = ((b - K3 * BigInteger.ModPow(gg, x, n)) % n + n) % n;
                        var S = Bytes(BigInteger.ModPow(baseS, a + u * x, n), 32);
                        K = Interleave(S);
                        var hN = sha.ComputeHash(N); var hg = sha.ComputeHash(g);
                        var ng = hN.Select((v, i) => (byte)(v ^ hg[i])).ToArray();
                        M1 = sha.ComputeHash(Concat(ng, sha.ComputeHash(acc), salt, A, B, K));
                    }

                    // AUTH_LOGON_PROOF
                    var pr = new MemoryStream();
                    w = new BinaryWriter(pr);
                    w.Write((byte)0x01);
                    w.Write(A);
                    w.Write(M1);
                    w.Write(new byte[20]);                  // crc hash (StrictVersionCheck is off)
                    w.Write((byte)0);                       // number of keys
                    w.Write((byte)(needsToken ? 0x04 : 0x00));
                    if (needsToken)
                    {
                        var t = Encoding.ASCII.GetBytes(token.Trim());
                        w.Write((byte)t.Length); w.Write(t);
                    }
                    await net.WriteAsync(pr.ToArray(), 0, (int)pr.Length, ct);

                    var res = await ReadExact(net, 2, ct);
                    if (res[0] != 0x01) return LoginResult.Of(LoginStatus.Error, "Unexpected answer from the realm.");
                    if (res[1] != 0x00) return Fail(res[1]);
                    var M2 = await ReadExact(net, 20, ct);
                    using (var sha = SHA1.Create())
                        if (!sha.ComputeHash(Concat(A, M1, K)).SequenceEqual(M2))
                            return LoginResult.Of(LoginStatus.Error, "The realm's answer did not check out.");
                    return LoginResult.Of(LoginStatus.Ok, "Logged in.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (SocketException) { return LoginResult.Of(LoginStatus.Unreachable, "The realm does not answer. It may be down or restarting."); }
            catch (IOException) { return LoginResult.Of(LoginStatus.Unreachable, "The connection to the realm was cut."); }
        }

        static LoginResult Fail(byte code)
        {
            switch (code)
            {
                case 0x03: return LoginResult.Of(LoginStatus.Banned, "This account is banned.");
                case 0x0C: return LoginResult.Of(LoginStatus.Suspended, "This account is temporarily suspended.");
                case 0x04:
                case 0x05: return LoginResult.Of(LoginStatus.BadCredentials, "Wrong account name or password.");
                case 0x08: return LoginResult.Of(LoginStatus.Error, "The realm is busy, try again in a moment.");
                case 0x09: return LoginResult.Of(LoginStatus.Error, "The realm refused this client version.");
                case 0x10:
                case 0x19: return LoginResult.Of(LoginStatus.Locked, "This account is locked to another IP address or country.");
                default: return LoginResult.Of(LoginStatus.Error, "The realm refused the login (code " + code + ").");
            }
        }

        // AzerothCore's SRP6::SHA1Interleave, byte for byte
        static byte[] Interleave(byte[] S)
        {
            var b0 = new byte[16]; var b1 = new byte[16];
            for (int i = 0; i < 16; i++) { b0[i] = S[2 * i]; b1[i] = S[2 * i + 1]; }
            int p = 0;
            while (p < 32 && S[p] == 0) p++;
            if ((p & 1) != 0) p++;
            p /= 2;
            byte[] h0, h1;
            using (var sha = SHA1.Create())
            {
                h0 = sha.ComputeHash(b0, p, 16 - p);
                h1 = sha.ComputeHash(b1, p, 16 - p);
            }
            var k = new byte[40];
            for (int i = 0; i < 20; i++) { k[2 * i] = h0[i]; k[2 * i + 1] = h1[i]; }
            return k;
        }

        static BigInteger Num(byte[] le) => new BigInteger(le.Concat(new byte[] { 0 }).ToArray());

        static byte[] Bytes(BigInteger v, int len)
        {
            var raw = v.ToByteArray();
            var r = new byte[len];
            Array.Copy(raw, r, Math.Min(len, raw.Length));
            return r;
        }

        static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        static async Task<byte[]> ReadExact(NetworkStream s, int n, CancellationToken ct)
        {
            var buf = new byte[n];
            int got = 0;
            while (got < n)
            {
                var r = await s.ReadAsync(buf, got, n - got, ct);
                if (r == 0) throw new IOException("closed");
                got += r;
            }
            return buf;
        }
    }
}
