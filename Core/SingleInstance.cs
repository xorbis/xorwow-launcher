using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace XorWoWLauncher.Core
{
    /// <summary>
    /// One launcher window at a time: every launcher (not the radio helper, which runs from the same
    /// exe) owns a named event "XorWoW-launcher-close-&lt;pid&gt;" and shuts itself down when it is set.
    /// A launcher that starts sets the event of every other one and waits for them to be gone, so the
    /// newest one wins - wherever its exe is (a copy in Downloads, the install folder, after an update).
    /// </summary>
    public static class SingleInstance
    {
        const string Prefix = "XorWoW-launcher-close-";
        static readonly TimeSpan GraceTime = TimeSpan.FromSeconds(5);
        static EventWaitHandle _close;

        /// <summary>Closes the other launchers, then starts listening for the next one.</summary>
        public static void Claim()
        {
            var me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == me || !EventWaitHandle.TryOpenExisting(Prefix + p.Id, out var other)) continue;
                    using (other) other.Set();
                    Log.Write($"closing the launcher already running (pid {p.Id})");
                    if (!p.WaitForExit((int)GraceTime.TotalMilliseconds))
                    {
                        Log.Write($"launcher pid {p.Id} did not close in {GraceTime.TotalSeconds:0} s - ending it");
                        p.Kill();
                        p.WaitForExit(2000);
                    }
                }
                catch (Exception e) { Log.Write($"could not close launcher pid {p.Id}: {e.Message}"); }
                finally { p.Dispose(); }
            }

            _close = new EventWaitHandle(false, EventResetMode.ManualReset, Prefix + me);
            ThreadPool.RegisterWaitForSingleObject(_close, (s, timedOut) =>
            {
                Log.Write("a newer launcher started - closing");
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
            }, null, Timeout.Infinite, true);
        }
    }
}
