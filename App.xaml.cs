using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using XorWoWLauncher.Core;

namespace XorWoWLauncher
{
    public partial class App : Application
    {
        public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        public static Settings Settings { get; private set; }
        /// <summary>Started by Windows' "Uninstall" (XorWoW.exe --uninstall).</summary>
        public static bool UninstallMode { get; private set; }
        /// <summary>Passed by a launcher that just copied itself into the install folder: "--moved-from &lt;its path&gt;".</summary>
        public const string MovedFromFlag = "--moved-from";
        /// <summary>Set when this run was started by a self-update: the version it replaced.</summary>
        public static string UpdatedFrom { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // The radio helper started beside the game at Play: no window, no update, no settings
            var radio = Array.FindIndex(e.Args, a => a.Equals(Radio.Flag, StringComparison.OrdinalIgnoreCase));
            if (radio >= 0)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DispatcherUnhandledException += (s, a) => { Log.Write("radio: " + a.Exception); a.Handled = true; };
                base.OnStartup(e);
                if (radio + 1 >= e.Args.Length || !int.TryParse(e.Args[radio + 1], out var pid) || !Radio.Run(pid)) Shutdown();
                return;
            }

            // Before the settings: the launcher being closed may still write them on its way out
            SingleInstance.Claim();
            Settings = Core.Settings.Load();
            UninstallMode = e.Args.Any(a => a.Equals(Uninstaller.Flag, StringComparison.OrdinalIgnoreCase));
            var moved = Array.FindIndex(e.Args, a => a.Equals(MovedFromFlag, StringComparison.OrdinalIgnoreCase));
            if (moved >= 0 && moved + 1 < e.Args.Length) _ = SelfUpdate.RemoveOriginalAsync(e.Args[moved + 1]);
            var updated = Array.FindIndex(e.Args, a => a.Equals(SelfUpdate.UpdatedFromFlag, StringComparison.OrdinalIgnoreCase));
            if (updated >= 0 && updated + 1 < e.Args.Length) UpdatedFrom = e.Args[updated + 1];
            DispatcherUnhandledException += OnCrash;
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Log.Write("fatal: " + a.ExceptionObject);
            SelfUpdate.CleanUp();
            Log.Write($"XorWoW Launcher {Version} started");
            base.OnStartup(e);
            try { new MainWindow().Show(); }
            catch (Exception ex)
            {
                Log.Write("startup failed: " + ex);
                MessageBox.Show("The launcher could not start: " + (ex.InnerException ?? ex).Message + "\n\nDetails are in " + AppPaths.LogFile, "XorWoW Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Write("unhandled: " + e.Exception);
            MessageBox.Show(e.Exception.Message + "\n\nDetails are in " + AppPaths.LogFile, "XorWoW Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
