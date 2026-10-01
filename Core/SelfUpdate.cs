using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XorWoWLauncher.Core
{
    /// <summary>
    /// The launcher replaces itself when the file server carries another build: the new exe is
    /// downloaded next to it (".new", hash-checked like any file), the running one is renamed
    /// ".old" (Windows allows renaming a running exe), the new one takes its name and is started.
    /// The next start removes ".old".
    /// </summary>
    public static class SelfUpdate
    {
        public static string ExePath => Process.GetCurrentProcess().MainModule.FileName;

        public static void CleanUp()
        {
            for (int i = 0; i < 10; i++)
            {
                try { if (File.Exists(ExePath + ".old")) File.Delete(ExePath + ".old"); return; }
                catch { Thread.Sleep(300); }   // the previous process may still be exiting
            }
        }

        /// <summary>
        /// A higher version, or the same version built from changed sources (the publish rebuilds the
        /// launcher by itself without a version bump). Never a lower one.
        /// </summary>
        /// <summary>
        /// After a move into the install folder: deletes the copy the player started (in Downloads,
        /// usually) so XorWoW.exe is not on the disk twice. Only that exact file, only when it is
        /// byte-identical to this exe (the launcher it was copied from), never this one; it waits up
        /// to 30 s for the old process to exit and let go of the file.
        /// </summary>
        public static async Task RemoveOriginalAsync(string path)
        {
            try
            {
                var original = Path.GetFullPath(path);
                if (original.Equals(ExePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(original)) return;
                if (!original.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
                var mine = await Task.Run(() => ClientUpdater.HashFile(ExePath, null, CancellationToken.None));
                if (await Task.Run(() => ClientUpdater.HashFile(original, null, CancellationToken.None)) != mine)
                {
                    Log.Write("left " + original + " in place: not the same file as this launcher");
                    return;
                }
                for (int i = 0; i < 60; i++)
                {
                    try
                    {
                        File.SetAttributes(original, FileAttributes.Normal);
                        File.Delete(original);
                        Log.Write("removed the downloaded copy " + original);
                        return;
                    }
                    catch (IOException) { await Task.Delay(500); }
                    catch (UnauthorizedAccessException) { await Task.Delay(500); }
                }
                Log.Write("could not remove " + original + " (still in use)");
            }
            catch (Exception e) { Log.Write("removing the downloaded copy: " + e.Message); }
        }

        public static bool IsNewer(Manifest m)
        {
            if (m.LauncherFile == null || string.IsNullOrEmpty(m.LauncherVersion)) return false;
            if (!Version.TryParse(m.LauncherVersion, out var theirs) || !Version.TryParse(App.Version, out var ours)) return false;
            if (theirs != ours) return theirs > ours;
            try { return ClientUpdater.HashFile(ExePath, null, CancellationToken.None) != m.LauncherFile.Sha256; }
            catch { return false; }
        }

        /// <summary>Passed to the new build: "--updated-from &lt;previous version&gt;", so it can say it was updated.</summary>
        public const string UpdatedFromFlag = "--updated-from";

        /// <summary>
        /// True when the new build is in place and started - the caller shuts down.
        /// <paramref name="beforeRestart"/> runs once the new build is downloaded and checked, just before
        /// the swap: the moment to tell the player the launcher is about to restart by itself.
        /// </summary>
        public static async Task<bool> RunAsync(Net net, Manifest m, Action<long> onBytes, Func<Task> beforeRestart, CancellationToken ct)
        {
            var exe = ExePath;
            var tmp = exe + ".new";
            try
            {
                await net.DownloadAsync(m.LauncherFile.Path, tmp, m.LauncherFile.Size, m.LauncherFile.Sha256, onBytes, ct);
                if (beforeRestart != null) await beforeRestart();
                if (File.Exists(exe + ".old")) File.Delete(exe + ".old");
                File.Move(exe, exe + ".old");
                try { File.Move(tmp, exe); }
                catch { File.Move(exe + ".old", exe); throw; }
                Process.Start(new ProcessStartInfo(exe, $"{UpdatedFromFlag} {App.Version}") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
                Log.Write($"launcher updated {App.Version} -> {m.LauncherVersion}");
                return true;
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                Log.Write("launcher self-update failed: " + e.Message);
                Net.TryDelete(tmp);
                return false;
            }
        }
    }
}
