using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace XorWoWLauncher.Core
{
    public static class AppPaths
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XorWoW Launcher");
        public static string SettingsFile => Path.Combine(Dir, "settings.json");
        public static string LogFile => Path.Combine(Dir, "launcher.log");

        /// <summary>Per game folder: hash cache, the files the launcher put there, the addons it installed.</summary>
        public static string StateFile(string gameDir)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(gameDir).TrimEnd('\\').ToLowerInvariant()));
                return Path.Combine(Dir, "game-" + BitConverter.ToString(h, 0, 5).Replace("-", "").ToLowerInvariant() + ".json");
            }
        }
    }

    public sealed class Settings
    {
        /// <summary>Set at build time (XorWoWServer in XorWoWLauncher.csproj), 127.0.0.1 when the build names none.</summary>
        public static readonly string DefaultServer = BuildMetadata("DefaultServer") ?? "127.0.0.1";
        public const int AuthPort = 3724;
        public const string DefaultFiles = "https://dailywar.online/downloads/xorwow/";

        public string Server { get; set; } = DefaultServer;
        public string Files { get; set; } = DefaultFiles;
        public string Username { get; set; } = "";
        public bool Remember { get; set; }
        /// <summary>DPAPI-protected SHA1(USER:PASS) - the SRP6 login needs no more, the password itself is never stored.</summary>
        public string Credential { get; set; } = "";
        public string GameDir { get; set; } = "";
        /// <summary>DPAPI-protected password, kept with Remember me while GameAutoLogin is on: the game's login screen needs the password itself.</summary>
        public string Password { get; set; } = "";
        /// <summary>Type the verified password into the game's login screen at Play.</summary>
        public bool GameAutoLogin { get; set; } = true;
        public bool CloseOnPlay { get; set; }
        public bool ManageXorWoWAddons { get; set; } = true;
        public string AddonSort { get; set; } = "installs";

        public string FilesBase => string.IsNullOrWhiteSpace(Files) ? DefaultFiles : Files.Trim();
        public string AuthHost => string.IsNullOrWhiteSpace(Server) ? DefaultServer : Server.Trim();

        static string BuildMetadata(string key)
        {
            foreach (var a in typeof(Settings).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
            {
                var m = (System.Reflection.AssemblyMetadataAttribute)a;
                if (m.Key == key && !string.IsNullOrWhiteSpace(m.Value)) return m.Value.Trim();
            }
            return null;
        }

        static readonly byte[] Entropy =Encoding.UTF8.GetBytes("XorWoW Launcher credential v1");

        public byte[] LoadCredential()
        {
            if (string.IsNullOrEmpty(Credential)) return null;
            try { return ProtectedData.Unprotect(Convert.FromBase64String(Credential), Entropy, DataProtectionScope.CurrentUser); }
            catch { return null; }
        }

        public string LoadPassword()
        {
            if (string.IsNullOrEmpty(Password)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Password), Entropy, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }

        public void SavePassword(string password) =>
            Password = string.IsNullOrEmpty(password) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));

        public void SaveCredential(byte[] hash) =>
            Credential = hash == null ? "" : Convert.ToBase64String(ProtectedData.Protect(hash, Entropy, DataProtectionScope.CurrentUser));

        public static Settings Load()
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                    return Json.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new Settings();
            }
            catch (Exception e) { Log.Write("settings unreadable, starting fresh: " + e.Message); }
            return new Settings();
        }

        public void Save()
        {
            Directory.CreateDirectory(AppPaths.Dir);
            AtomicFile.WriteAllText(AppPaths.SettingsFile, Json.Serialize(this));
        }
    }

    public sealed class HashEntry
    {
        public long Size { get; set; }
        public long Ticks { get; set; }
        public string Sha { get; set; }
    }

    public sealed class InstalledAddon
    {
        public long Id { get; set; }
        public string Title { get; set; }
        public string Version { get; set; }
        public List<string> Folders { get; set; } = new List<string>();
        public string Icon { get; set; }
        public string Link { get; set; }
        public string InstalledAt { get; set; }
    }

    /// <summary>What the launcher knows about one game folder.</summary>
    public sealed class GameState
    {
        public Dictionary<string, HashEntry> Hashes { get; set; } = new Dictionary<string, HashEntry>();
        /// <summary>Client files (relative, as in the manifest) the launcher last installed; one that leaves the manifest is deleted.</summary>
        public List<string> Managed { get; set; } = new List<string>();
        public string XorWoWAddonsVersion { get; set; } = "";
        public List<string> XorWoWAddonFolders { get; set; } = new List<string>();
        public List<InstalledAddon> Addons { get; set; } = new List<InstalledAddon>();
        /// <summary>client\ was a WoW folder the player already had (moved in, not downloaded): uninstalling removes it too.</summary>
        public bool Adopted { get; set; }

        [System.Web.Script.Serialization.ScriptIgnore] public string File { get; private set; }

        public static GameState Load(string gameDir)
        {
            var f = AppPaths.StateFile(gameDir);
            GameState s = null;
            try { if (System.IO.File.Exists(f)) s = Json.Deserialize<GameState>(System.IO.File.ReadAllText(f)); }
            catch (Exception e) { Log.Write("state unreadable, rebuilding: " + e.Message); }
            s = s ?? new GameState();
            s.Hashes = s.Hashes ?? new Dictionary<string, HashEntry>();
            s.Managed = s.Managed ?? new List<string>();
            s.Addons = s.Addons ?? new List<InstalledAddon>();
            s.XorWoWAddonFolders = s.XorWoWAddonFolders ?? new List<string>();
            s.File = f;
            return s;
        }

        public void Save()
        {
            lock (this)
            {
                Directory.CreateDirectory(AppPaths.Dir);
                AtomicFile.WriteAllText(File, Json.Serialize(this));
            }
        }
    }

    public static class AtomicFile
    {
        public static void WriteAllText(string path, string text)
        {
            var tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (System.IO.File.Exists(path)) System.IO.File.Replace(tmp, path, null);
            else System.IO.File.Move(tmp, path);
        }
    }

    public static class Log
    {
        static readonly object Gate = new object();
        public static event Action<string> Line;

        public static void Write(string text)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text;
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.Dir);
                    var fi = new FileInfo(AppPaths.LogFile);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024) System.IO.File.Delete(fi.FullName);
                    System.IO.File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
                }
                catch { }
            }
            Line?.Invoke(text);
        }
    }
}
