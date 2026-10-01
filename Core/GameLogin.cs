using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XorWoWLauncher.Core
{
    /// <summary>
    /// Logs the game in with the password the launcher already verified: the 3.3.5a client takes no
    /// password on its command line or in Config.wtf, so it is typed into the game's window
    /// (WM_CHAR messages posted to it - no focus needed, the player can keep using the PC).
    ///
    /// It is typed only once the client really shows its login screen: build 12340 keeps the name of
    /// the current glue screen at a fixed address ("movie" for the intro, "login", "charselect"...),
    /// read with ReadProcessMemory (the player's own process, no rights needed). Typed once, never
    /// retried (a wrong guess must not pile up failed logins); the outcome is only logged.
    /// </summary>
    public static class GameLogin
    {
        const long GlueScreenAddress = 0x00B6A9E0;   // Wow.exe 3.3.5a (12340), not relocated
        static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(3);   // intro movie + a slow first start
        static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);      // the login screen fading in

        /// <summary>Only the client whose memory layout is known: Wow.exe 3.3.5.12340.</summary>
        public static bool Supported(string wowExe)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(wowExe);
                return v.FileMajorPart == 3 && v.FileMinorPart == 3 && v.FileBuildPart == 5 && v.FilePrivatePart == 12340;
            }
            catch { return false; }
        }

        public static async Task<bool> TypePasswordAsync(Process wow, string password, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(password)) return false;
            var h = OpenProcess(ProcessVmRead | ProcessQueryLimitedInformation, false, wow.Id);
            if (h == IntPtr.Zero) { Log.Write("game login: cannot read the game's state (" + Marshal.GetLastWin32Error() + ")"); return false; }
            try
            {
                var clock = Stopwatch.StartNew();
                TimeSpan? loginSince = null;
                string screen = "";
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (wow.HasExited) { Log.Write("game login: the game closed before its login screen"); return false; }
                    if (clock.Elapsed > GiveUpAfter) { Log.Write($"game login: no login screen after {GiveUpAfter.TotalMinutes:0} min (last screen '{screen}')"); return false; }
                    screen = ReadScreen(h);
                    if (screen == "charselect" || screen == "realmwizard") { Log.Write("game login: already past the login screen"); return true; }
                    loginSince = screen == "login" ? loginSince ?? clock.Elapsed : (TimeSpan?)null;
                    wow.Refresh();
                    if (loginSince.HasValue && clock.Elapsed - loginSince.Value >= Settle && wow.MainWindowHandle != IntPtr.Zero) break;
                    await Task.Delay(200, ct);
                }

                var window = wow.MainWindowHandle;
                foreach (var c in password)
                {
                    PostMessage(window, WmChar, (IntPtr)c, (IntPtr)1);
                    await Task.Delay(20, ct);
                }
                PostMessage(window, WmKeyDown, (IntPtr)VkReturn, (IntPtr)0x001C0001);
                PostMessage(window, WmKeyUp, (IntPtr)VkReturn, unchecked((IntPtr)(int)0xC01C0001));
                Log.Write("game login: password typed at the login screen");

                // Confirmation: the client leaves "login" once the realm accepted it
                var typedAt = clock.Elapsed;
                while (clock.Elapsed - typedAt < TimeSpan.FromSeconds(30) && !wow.HasExited)
                {
                    await Task.Delay(250, ct);
                    screen = ReadScreen(h);
                    if (screen != "login" && screen.Length > 0) { Log.Write($"game login: in ({screen})"); return true; }
                }
                Log.Write("game login: still at the login screen 30 s after typing (realm down, or a dialog in the way) - left to the player");
                return false;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception e) { Log.Write("game login: " + e.Message); return false; }
            finally { CloseHandle(h); }
        }

        static string ReadScreen(IntPtr process)
        {
            var buf = new byte[24];
            if (!ReadProcessMemory(process, new IntPtr(GlueScreenAddress), buf, buf.Length, out _)) return "";
            var len = Array.IndexOf(buf, (byte)0);
            if (len < 0) len = buf.Length;
            for (int i = 0; i < len; i++) if (buf[i] < 0x20 || buf[i] > 0x7E) return "";   // not a screen name: not the known layout
            return Encoding.ASCII.GetString(buf, 0, len);
        }

        const int ProcessVmRead = 0x0010, ProcessQueryLimitedInformation = 0x1000;
        const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmChar = 0x0102;
        const int VkReturn = 0x0D;

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
