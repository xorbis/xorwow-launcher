using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace XorWoWLauncher.Core
{
    /// <summary>
    /// XorWoW in Windows' installed apps (Settings > Apps, Control Panel > Programs and Features):
    /// a per-user entry under HKCU\...\Uninstall\XorWoW, written by the launcher itself at every
    /// start (no installer, no admin rights), whose Uninstall runs "XorWoW.exe --uninstall".
    /// </summary>
    public static class Uninstaller
    {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\XorWoW";
        public const string Flag = "--uninstall";

        /// <summary>Creates or refreshes the entry for the installation at <paramref name="root"/>.</summary>
        public static void Register(string root, long clientBytes)
        {
            try
            {
                var exe = SelfUpdate.ExePath;
                using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    k.SetValue("DisplayName", "XorWoW");
                    k.SetValue("DisplayIcon", exe + ",0");
                    k.SetValue("DisplayVersion", App.Version);
                    k.SetValue("Publisher", "XorWoW");
                    k.SetValue("InstallLocation", root);
                    k.SetValue("UninstallString", "\"" + exe + "\" " + Flag);
                    k.SetValue("URLInfoAbout", Settings.DefaultFiles);
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    if (k.GetValue("InstallDate") == null || !string.Equals(Convert.ToString(k.GetValue("InstallLocation")), root, StringComparison.OrdinalIgnoreCase))
                        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                    if (clientBytes > 0)
                        k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, (clientBytes + new FileInfo(exe).Length) / 1024), RegistryValueKind.DWord);
                }
            }
            catch (Exception e) { Log.Write("uninstall entry: " + e.Message); }
        }

        static void Unregister()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                    if (k == null || !string.Equals(Convert.ToString(k.GetValue("InstallLocation")), Root, StringComparison.OrdinalIgnoreCase)) return;
                Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
            }
            catch (Exception e) { Log.Write("uninstall entry removal: " + e.Message); }
        }

        static string Root => Path.GetDirectoryName(SelfUpdate.ExePath);

        /// <summary>Null when the uninstall can go ahead, else why not.</summary>
        public static string Blocker()
        {
            var client = Path.Combine(Root, "client");
            if (GameFolder.GameRunning(client)) return "World of Warcraft is running from this installation - close it first.";
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(SelfUpdate.ExePath)))
            {
                try
                {
                    if (p.Id != me.Id && string.Equals(p.MainModule.FileName, SelfUpdate.ExePath, StringComparison.OrdinalIgnoreCase))
                        return "The XorWoW launcher is open - close it first.";
                }
                catch { }
                finally { p.Dispose(); }
            }
            return null;
        }

        /// <summary>
        /// Removes the installation: client\ (but WTF\ and Screenshots\ when <paramref name="keepUserData"/>),
        /// the desktop shortcuts pointing at this XorWoW.exe, the launcher's data for it in %APPDATA%
        /// (all of it unless <paramref name="keepUserData"/>, which keeps settings and the saved login)
        /// and the Windows entry. XorWoW.exe and the emptied folders go after it exits (RemoveLauncherAfterExit).
        /// Never deletes anything outside its own folder and never follows a link out of it.
        /// </summary>
        public static void Run(bool keepUserData)
        {
            var root = Root;
            var client = Path.Combine(root, "client");
            Log.Write($"uninstalling {root} (keep settings: {keepUserData})");

            if (Directory.Exists(client))
            {
                foreach (var e in Directory.EnumerateFileSystemEntries(client).ToList())
                {
                    var name = Path.GetFileName(e);
                    if (keepUserData && (name.Equals("WTF", StringComparison.OrdinalIgnoreCase) || name.Equals("Screenshots", StringComparison.OrdinalIgnoreCase))) continue;
                    Delete(e);
                }
                TryRemoveEmptyDir(client);
            }
            foreach (var f in Directory.EnumerateFiles(root, "*.xwpart").Concat(Directory.EnumerateFiles(root, "XorWoW.exe.new")).ToList()) Delete(f);

            RemoveShortcuts();

            var state = AppPaths.StateFile(client);
            try { if (File.Exists(state)) File.Delete(state); } catch { }
            if (!keepUserData)
                foreach (var f in new[] { AppPaths.SettingsFile, AppPaths.SettingsFile + ".tmp" })
                    try { if (File.Exists(f)) File.Delete(f); } catch { }

            Unregister();
            Log.Write("uninstalled " + root);
        }

        /// <summary>
        /// The running exe cannot delete itself: a hidden cmd waits for this process to be gone
        /// (retrying the delete once a second, up to a minute), then removes the folder if nothing
        /// the player kept is left in it (rmdir without /s). Call it right before exiting.
        /// </summary>
        public static void RemoveLauncherAfterExit(bool keepUserData)
        {
            var exe = SelfUpdate.ExePath;
            var cmd = $"/c for /l %i in (1,1,60) do @if exist \"{exe}\" (ping 127.0.0.1 -n 2 >nul & del /f /q \"{exe}\" 2>nul)" +
                      $" & del /f /q \"{exe}.old\" 2>nul & rmdir \"{Root}\" 2>nul";
            if (!keepUserData) cmd += $" & rmdir /s /q \"{AppPaths.Dir}\" 2>nul";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetTempPath() });
        }

        /// <summary>Deletes a file or a folder tree; a link (junction, symlink) is removed, never followed.</summary>
        static void Delete(string path)
        {
            try
            {
                var attr = File.GetAttributes(path);
                if ((attr & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attr & FileAttributes.Directory) != 0) Directory.Delete(path, false); else File.Delete(path);
                    return;
                }
                if ((attr & FileAttributes.Directory) != 0)
                {
                    foreach (var e in Directory.EnumerateFileSystemEntries(path).ToList()) Delete(e);
                    Directory.Delete(path, false);
                }
                else
                {
                    if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch (Exception e) { Log.Write($"uninstall: could not delete {path}: {e.Message}"); }
        }

        static void TryRemoveEmptyDir(string dir)
        {
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }

        static void RemoveShortcuts()
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                var shell = Activator.CreateInstance(shellType);
                foreach (var name in new[] { "XorWoW.lnk", "XorWoW Launcher.lnk" })
                {
                    var path = Path.Combine(desktop, name);
                    if (!File.Exists(path)) continue;
                    var lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                    var target = Convert.ToString(lnk.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, lnk, null));
                    if (target.Equals(SelfUpdate.ExePath, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
                }
            }
            catch (Exception e) { Log.Write("uninstall shortcuts: " + e.Message); }
        }
    }
}
