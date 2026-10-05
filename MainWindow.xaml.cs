using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using XorWoWLauncher.Core;

namespace XorWoWLauncher
{
    public partial class MainWindow : Window
    {
        readonly Settings _settings = App.Settings;
        Net _net;
        Warperia _warperia;
        Manifest _manifest;
        GameState _state;
        CancellationTokenSource _cts = new CancellationTokenSource();
        bool _busy;

        // login
        byte[] _savedCredential;
        bool _usingSaved, _suppressPassEvent;
        string _gamePassword;   // verified at login; typed into the game's login screen at Play
        string _account = "";

        // addons
        readonly ObservableCollection<AddonRow> _rows = new ObservableCollection<AddonRow>();
        int _page = 1;
        bool _browseLoaded;
        readonly DispatcherTimer _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        CancellationTokenSource _searchCts;

        // progress
        long _lastDone; DateTime _lastAt; double _speed;

        public MainWindow()
        {
            InitializeComponent();
            _net = new Net(_settings.FilesBase);
            _warperia = new Warperia(_net);
            AddonList.ItemsSource = _rows;
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); _ = LoadBrowseAsync(true); };
            TitleVersion.Text = "Launcher " + App.Version;
            LoginFooter.Text = "Realm " + _settings.AuthHost + "  ·  Launcher " + App.Version;
            Loaded += OnLoaded;
            StateChanged += (s, e) =>
            {
                MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
                Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            };
            Closing += (s, e) => { _cts.Cancel(); };
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            StartSnow();
            RememberBox.IsChecked = _settings.Remember;
            _savedCredential = _settings.Remember ? _settings.LoadCredential() : null;
            if (_savedCredential != null && !string.IsNullOrEmpty(_settings.Username))
            {
                UserBox.Text = _settings.Username;
                _suppressPassEvent = true;
                PassBox.Password = "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022";
                _suppressPassEvent = false;
                _usingSaved = true;
                LoginButton.Focus();
            }
            else
            {
                UserBox.Text = _settings.Username ?? "";
                if (UserBox.Text.Length > 0) PassBox.Focus(); else UserBox.Focus();
            }
            UserBox.TextChanged += (s, a) =>
            {
                if (_usingSaved && !AuthClient.Upper(UserBox.Text).Equals(AuthClient.Upper(_settings.Username)))
                {
                    _usingSaved = false;
                    _suppressPassEvent = true; PassBox.Password = ""; _suppressPassEvent = false;
                }
            };
            SortPopular.IsChecked = _settings.AddonSort != "modified";
            SortUpdated.IsChecked = _settings.AddonSort == "modified";
            CloseOnPlayBox.IsChecked = _settings.CloseOnPlay;
            GameLoginBox.IsChecked = _settings.GameAutoLogin;
            ManageAddonsBox.IsChecked = _settings.ManageXorWoWAddons;
            ServerBox.Text = _settings.AuthHost;
            AboutText.Text = "XorWoW Launcher " + App.Version;
            _settings.GameDir = ClientDir;
            if (App.UninstallMode) { _ = UninstallFlowAsync(); return; }
            if (!RootReady()) _ = EnsureGameFolderAsync();
            else Uninstaller.Register(OwnDir, 0);

            // Started by a self-update: say so (else it looks like a crash and a restart), and with a
            // remembered login carry straight on
            if (App.UpdatedFrom != null)
            {
                _updateNote = App.UpdatedFrom == App.Version ? "launcher updated to a new build of " + App.Version
                                                             : $"launcher updated from {App.UpdatedFrom} to {App.Version}";
                LoginNotice.Text = "The " + _updateNote + (_usingSaved ? " - logging you back in…" : ". Log in again to carry on.");
                LoginNotice.Visibility = Visibility.Visible;
                Log.Write(_updateNote);
                if (_usingSaved && RootReady()) Login_Click(this, new RoutedEventArgs());
            }
        }

        string _updateNote;   // shown once after a self-update restart

        DispatcherTimer _toastTimer;

        /// <summary>A notice at the top right of the main view that fades by itself after 10 s.</summary>
        void ShowToast(string text)
        {
            ToastText.Text = text;
            Toast.BeginAnimation(OpacityProperty, null);   // drop a previous fade, which would hold opacity at 0
            Toast.Opacity = 1;
            Toast.Visibility = Visibility.Visible;
            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(600));
                fade.Completed += (s2, e2) => Toast.Visibility = Visibility.Collapsed;
                Toast.BeginAnimation(OpacityProperty, fade);
            };
            _toastTimer.Start();
        }

        // ================================================================ window chrome

        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ================================================================ login

        void PassBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_suppressPassEvent) _usingSaved = false;
        }

        void LoginField_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Login_Click(sender, e);
        }

        async void Login_Click(object sender, RoutedEventArgs e)
        {
            if (!LoginButton.IsEnabled) return;
            var user = AuthClient.Upper(UserBox.Text);
            LoginMessage.Text = "";
            if (user.Length == 0) { LoginMessage.Text = "Enter your account name."; UserBox.Focus(); return; }
            if (!_usingSaved && PassBox.Password.Length == 0) { LoginMessage.Text = "Enter your password."; PassBox.Focus(); return; }

            var typedPassword = _usingSaved ? null : PassBox.Password;
            var credential = _usingSaved ? _savedCredential : AuthClient.CredentialHash(user, typedPassword);
            LoginButton.IsEnabled = false;
            LoginButton.Content = "LOGGING IN…";
            try
            {
                var res = await AuthClient.LoginAsync(_settings.AuthHost, Settings.AuthPort, user, credential, TokenBox.Text, _cts.Token);
                Log.Write($"login {user}: {res.Status}");
                switch (res.Status)
                {
                    case LoginStatus.Ok:
                        _settings.Remember = RememberBox.IsChecked == true;
                        _settings.Username = user;
                        _settings.SaveCredential(_settings.Remember ? credential : null);
                        _savedCredential = _settings.Remember ? credential : null;
                        // The verified password, for the game's login screen: in memory for this session;
                        // saved (encrypted) only with Remember me and game auto-login both on
                        _gamePassword = typedPassword ?? _settings.LoadPassword();
                        if (!_settings.Remember || !_settings.GameAutoLogin) _settings.SavePassword(null);
                        else if (typedPassword != null) _settings.SavePassword(typedPassword);
                        _settings.Save();
                        TokenBox.Text = "";
                        EnterMain(user);
                        break;
                    case LoginStatus.TokenRequired:
                        TokenPanel.Visibility = Visibility.Visible;
                        LoginMessage.Text = res.Message;
                        TokenBox.Focus();
                        break;
                    case LoginStatus.BadCredentials:
                        LoginMessage.Text = res.Message;
                        if (_usingSaved) { _usingSaved = false; _suppressPassEvent = true; PassBox.Password = ""; _suppressPassEvent = false; }
                        PassBox.Focus();
                        break;
                    default:
                        LoginMessage.Text = res.Message;
                        break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Write("login failed: " + ex);
                LoginMessage.Text = "Login failed: " + ex.Message;
            }
            finally
            {
                LoginButton.IsEnabled = true;
                LoginButton.Content = "LOG IN";
            }
        }

        void EnterMain(string user)
        {
            _account = user;
            AccountName.Text = user;
            WelcomeText.Text = "Welcome back, " + char.ToUpper(user[0]) + user.Substring(1).ToLowerInvariant();
            RealmAddress.Text = _settings.AuthHost + " · logged in " + DateTime.Now.ToString("HH:mm");
            LoginView.Visibility = Visibility.Collapsed;
            MainView.Visibility = Visibility.Visible;
            NavHome.IsChecked = true;
            _settings.GameDir = ClientDir;
            FolderText.Text = OwnDir;
            if (_updateNote != null) ShowToast(char.ToUpper(_updateNote[0]) + _updateNote.Substring(1));
            _ = RunUpdatesAsync(false);
        }

        void Logout_Click(object sender, RoutedEventArgs e)
        {
            _gamePassword = null;
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            MainView.Visibility = Visibility.Collapsed;
            LoginView.Visibility = Visibility.Visible;
            if (!_usingSaved) { _suppressPassEvent = true; PassBox.Password = ""; _suppressPassEvent = false; }
            LoginMessage.Text = "";
            PassBox.Focus();
        }

        // ================================================================ update pipeline

        async Task RunUpdatesAsync(bool verifyAll)
        {
            if (_busy) return;
            SetBusy(true);
            var ct = _cts.Token;
            try
            {
                Status("Contacting the XorWoW file server…");
                try { _manifest = await Manifest.FetchAsync(_net, ct); }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    Log.Write("manifest: " + ex.Message);
                    Status(ex is InvalidDataException ? ex.Message : "The update server does not answer - you can still play with the files you have.", Warn);
                    ClientText.Text = GameFolder.HasWow(_settings.GameDir) ? "Not checked" : "Not installed";
                    ClientDetail.Text = ex.Message;
                    return;
                }

                if (SelfUpdate.IsNewer(_manifest))
                {
                    Status("Updating the launcher to " + _manifest.LauncherVersion + "…");
                    var restarted = await SelfUpdate.RunAsync(_net, _manifest, null, async () =>
                    {
                        // Otherwise the window just vanishes and comes back: looks like a crash
                        _ = AskWithCheck("Updating XorWoW Launcher",
                            "A new version of the launcher was downloaded. It restarts by itself in a moment" +
                            (_savedCredential != null ? " and logs you back in." : "."), null);
                        await Task.Delay(2500);
                    }, ct);
                    if (restarted) { Application.Current.Shutdown(); return; }
                    DialogLayer.Visibility = Visibility.Collapsed;   // the update failed: carry on with this build
                }

                if (!await EnsureGameFolderAsync()) { Status("XorWoW needs a folder of its own - start the launcher again to choose one.", Warn); return; }
                var dir = _settings.GameDir;
                FolderText.Text = OwnDir;
                _state = GameState.Load(dir);

                if (GameFolder.GameRunning(dir))
                {
                    Status("World of Warcraft is running - close it and press \"Check for updates\".", Warn);
                    ShowClientSummary();
                    await LoadNotesAsync(ct);
                    return;
                }

                var up = new ClientUpdater(_net, dir, _state);
                var progress = new Throttled(new Progress<StepProgress>(OnProgress));

                var plan = await up.PlanClientAsync(_manifest, verifyAll, progress, ct);
                if (plan.Bytes > 1L << 30)
                {
                    var fresh = !GameFolder.HasWow(dir);
                    var choice = await Ask(fresh ? "Install World of Warcraft" : "Big update",
                        $"{plan.Download.Count} files to download, {Size(plan.Bytes)}, into\n{dir}\n\n" +
                        "A download that is interrupted picks up where it stopped next time.",
                        fresh ? "Download the game" : "Download", "Not now");
                    if (choice != 0) { Status("Update postponed.", Warn); ShowClientSummary(); return; }
                }
                ResetSpeed();
                if (!plan.Empty) await up.ApplyAsync(plan, "Updating the game", progress, ct);
                up.MarkClientApplied(_manifest);
                ShowClientSummary();

                var installedAddons = InstalledXorWoWVersion(dir);
                if (installedAddons != null && Version.TryParse(_manifest.AddonsVersion, out var published) && installedAddons > published)
                    Log.Write($"XorWoW addons left alone: {installedAddons} installed is newer than {published} published");
                else if (_settings.ManageXorWoWAddons && _manifest.AddonFiles.Count > 0)
                {
                    var ap = await up.PlanAddonsAsync(_manifest, progress, ct);
                    ResetSpeed();
                    if (!ap.Empty)
                    {
                        await up.ApplyAsync(ap, "Updating the XorWoW addons to " + _manifest.AddonsVersion, progress, ct);
                        Log.Write("XorWoW addons now " + _manifest.AddonsVersion);
                    }
                    up.MarkAddonsApplied(_manifest);
                }
                ShowClientSummary();

                var n = await UpdateWarperiaAddonsAsync(ct);
                await LoadNotesAsync(ct);
                Uninstaller.Register(OwnDir, _manifest.ClientBytes);   // size for Settings > Apps
                Status("Ready to play", Ok);
                DetailText.Text = $"Checked at {DateTime.Now:HH:mm}" + (n > 0 ? $" · {n} addon{(n > 1 ? "s" : "")} updated" : "") +
                                  (_updateNote != null ? " · " + _updateNote : "");
                _updateNote = null;
                LoginNotice.Visibility = Visibility.Collapsed;
                Bar.IsIndeterminate = false;
                Bar.Value = Bar.Maximum;
                SpeedText.Text = "";
            }
            catch (OperationCanceledException) { Status("Stopped - press \"Check for updates\" to carry on.", Warn); }
            catch (Exception ex)
            {
                Log.Write("update failed: " + ex);
                Status("Update failed: " + ex.Message, Danger);
            }
            finally
            {
                SetBusy(false);
                if (_rows.Count > 0) RefreshRows();
            }
        }

        void ShowClientSummary()
        {
            var dir = _settings.GameDir;
            if (!GameFolder.HasWow(dir)) { ClientText.Text = "Not installed"; ClientDetail.Text = dir; return; }
            if (_manifest != null && _state != null && _state.Managed.Count > 0)
            {
                ClientText.Text = "Up to date";
                ClientDetail.Text = $"{_manifest.Client.Count} files · {Size(_manifest.ClientBytes)} · {dir}";
            }
            else { ClientText.Text = "Installed"; ClientDetail.Text = dir; }
            ClientDetail.ToolTip = dir;
            if (_state != null && !string.IsNullOrEmpty(_state.XorWoWAddonsVersion))
            {
                XorAddonText.Text = "Version " + _state.XorWoWAddonsVersion;
                XorAddonDetail.Text = string.Join(", ", _state.XorWoWAddonFolders);
            }
            else if (!_settings.ManageXorWoWAddons) { XorAddonText.Text = "Not managed"; XorAddonDetail.Text = "Turned off in Settings"; }
        }

        // The installation root is the launcher's own folder: XorWoW.exe at its root, the game in
        // its client\ subfolder - the same layout as the file server (client\dist on LEGION).
        static string OwnDir => System.IO.Path.GetDirectoryName(SelfUpdate.ExePath);
        static string ClientDir => System.IO.Path.Combine(OwnDir, "client");

        static bool RootReady() => GameFolder.Classify(OwnDir).layout == GameFolder.Layout.Ready && GameFolder.Writable(OwnDir);

        async Task<bool> EnsureGameFolderAsync()
        {
            _settings.GameDir = ClientDir;
            if (RootReady()) return true;
            var (layout, why) = GameFolder.Classify(OwnDir);
            if (!GameFolder.Writable(OwnDir)) why = "Windows does not let the launcher write in " + OwnDir + ".";
            else if (layout == GameFolder.Layout.Adoptable && await AdoptAsync(OwnDir)) return true;
            await RelocateAsync(why);   // restarts from the new folder, or comes back when cancelled
            return false;
        }

        /// <summary>A WoW folder becomes an installation: its files move into client\ (instant on the same drive).</summary>
        async Task<bool> AdoptAsync(string root)
        {
            if (GameFolder.GameRunning(root)) { await Ask("World of Warcraft is running", "Close the game first, then try again.", "OK"); return false; }
            var text = root + " holds a World of Warcraft client. XorWoW keeps the game in a client folder next to XorWoW.exe, so its files move into\n" +
                       System.IO.Path.Combine(root, "client") + "\n\nNothing is copied or downloaded again: the launcher then only updates what differs from the realm's client.";
            if (await Ask("Use this World of Warcraft?", text, "Move the files", "Cancel") != 0) return false;
            try
            {
                GameFolder.Adopt(root);
                var st = GameState.Load(System.IO.Path.Combine(root, "client"));
                st.Adopted = true;
                st.Save();
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("adopt " + root + ": " + ex);
                await Ask("Could not move the game files", ex.Message, "OK");
                return false;
            }
        }

        /// <summary>
        /// The launcher sits where XorWoW cannot live (Downloads, the desktop, a folder with other
        /// files, Program Files...): it copies itself as XorWoW.exe into a folder the player picks,
        /// puts a shortcut on the desktop and restarts from there, where the game goes into client\.
        /// </summary>
        async Task RelocateAsync(string reason)
        {
            // The usual first run: XorWoW.exe straight from the browser's Downloads folder
            var suggested = System.IO.Path.Combine(System.IO.Path.GetPathRoot(Environment.SystemDirectory), "Games", "XorWoW");
            var offerSuggested = GameFolder.Classify(suggested).layout == GameFolder.Layout.Ready;
            var text = "XorWoW installs itself where XorWoW.exe is (the game goes into a client folder next to it, about 20 GB), " +
                       "and XorWoW.exe is in\n" + OwnDir + (string.IsNullOrEmpty(reason) ? "" : "\n" + reason) + "\n\n" +
                       (offerSuggested ? "Install it in " + suggested + ", or choose another folder" : "Choose a folder for it") +
                       " - an empty one, or a World of Warcraft 3.3.5a folder you already have, which is then updated instead of downloaded again. " +
                       "XorWoW.exe moves there, a XorWoW shortcut goes on the desktop, and it carries on from there.";
            while (true)
            {
                var buttons = offerSuggested ? new[] { "Install in " + suggested, "Choose folder…", "Cancel" } : new[] { "Choose folder…", "Cancel" };
                var choice = await Ask("Install XorWoW", text, buttons);
                if (choice == buttons.Length - 1) return;
                string target;
                if (offerSuggested && choice == 0) target = suggested;
                else using (var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "An empty folder for XorWoW, or your WoW 3.3.5a folder",
                    ShowNewFolderButton = true,
                })
                {
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) continue;
                    target = System.IO.Path.GetFullPath(dlg.SelectedPath);
                }
                {
                    var (layout, why) = GameFolder.Classify(target);
                    if (layout == GameFolder.Layout.Unusable) { text = why + "\n\nTry another folder."; continue; }
                    if (!GameFolder.Writable(target)) { text = "Windows does not let the launcher write there - pick a folder outside Program Files.\n\nTry another folder."; continue; }
                    if (layout == GameFolder.Layout.Adoptable && !await AdoptAsync(target)) continue;
                    var exe = System.IO.Path.Combine(target, GameFolder.LauncherName);
                    try
                    {
                        if (!string.Equals(exe, SelfUpdate.ExePath, StringComparison.OrdinalIgnoreCase)) File.Copy(SelfUpdate.ExePath, exe, true);
                        GameFolder.DesktopShortcut(exe);
                        Log.Write("launcher moved to " + target);
                        // The new copy deletes this one (in Downloads, typically) once this process is gone
                        var args = string.Equals(exe, SelfUpdate.ExePath, StringComparison.OrdinalIgnoreCase) ? "" : $"{App.MovedFromFlag} \"{SelfUpdate.ExePath}\"";
                        Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = target, UseShellExecute = false });
                        Application.Current.Shutdown();
                        return;
                    }
                    catch (Exception ex) { text = "Could not put the launcher there: " + ex.Message + "\n\nTry another folder."; }
                }
            }
        }

        async Task LoadNotesAsync(CancellationToken ct)
        {
            if (_manifest?.NotesFile == null) return;
            try
            {
                var bytes = await _net.GetBytesAsync(_manifest.NotesFile.Path, ct);
                using (var sha = SHA256.Create())
                    if (Net.Hex(sha.ComputeHash(bytes)) != _manifest.NotesFile.Sha256) throw new InvalidDataException("notes.json does not match the manifest");
                var (server, addons) = Notes.Parse(Encoding.UTF8.GetString(bytes));
                ServerNotes.ItemsSource = server;
                ServerNotesEmpty.Visibility = server.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                AddonNotes.ItemsSource = addons;
                var news = server.Concat(addons).OrderByDescending(x => x.Time).Take(5).ToList();
                NewsList.ItemsSource = news;
                NewsEmpty.Visibility = news.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { Log.Write("notes: " + ex.Message); }
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            CheckButton.IsEnabled = VerifyButton.IsEnabled = !busy;
            CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Bar.IsIndeterminate = busy;
            if (!busy) { Bar.IsIndeterminate = false; SpeedText.Text = ""; }
            PlayButton.IsEnabled = !busy && GameFolder.HasWow(_settings.GameDir);
        }

        void OnProgress(StepProgress p)
        {
            StatusText.Text = p.Stage;
            StatusText.Foreground = (Brush)FindResource("IceBright");
            DetailText.Text = p.Detail;
            Bar.IsIndeterminate = p.Total <= 0;
            if (p.Total > 0) Bar.Value = Bar.Maximum * p.Done / p.Total;

            var now = DateTime.UtcNow;
            var dt = (now - _lastAt).TotalSeconds;
            if (dt >= 0.25 && p.Done >= _lastDone)
            {
                var inst = (p.Done - _lastDone) / dt;
                _speed = _speed <= 0 ? inst : _speed * 0.8 + inst * 0.2;
                _lastDone = p.Done; _lastAt = now;
            }
            else if (p.Done < _lastDone) { _lastDone = p.Done; _lastAt = now; }
            if (p.Total > 0 && _speed > 1)
            {
                var left = TimeSpan.FromSeconds((p.Total - p.Done) / _speed);
                SpeedText.Text = $"{Size(p.Done)} of {Size(p.Total)}  ·  {Size((long)_speed)}/s  ·  {Eta(left)}";
            }
        }

        void ResetSpeed() { _lastDone = 0; _lastAt = DateTime.UtcNow; _speed = 0; }

        static string Eta(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min left" :
            t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min left" : $"{Math.Max(1, t.Seconds)} s left";

        public static string Size(long b)
        {
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.0") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0.0") + " MB";
            if (b >= 1L << 10) return (b / 1024.0).ToString("0") + " KB";
            return b + " B";
        }

        const int Ok = 1, Warn = 2, Danger = 3;
        void Status(string text, int tone = 0)
        {
            StatusText.Text = text;
            StatusText.Foreground = (Brush)FindResource(tone == Ok ? "Ok" : tone == Warn ? "Warn" : tone == Danger ? "Danger" : "IceBright");
            if (tone != 0) { DetailText.Text = ""; SpeedText.Text = ""; }
            if (tone != 0) Bar.IsIndeterminate = false;
        }

        void Check_Click(object sender, RoutedEventArgs e) => _ = RunUpdatesAsync(false);
        void Verify_Click(object sender, RoutedEventArgs e) => _ = RunUpdatesAsync(true);

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
        }

        void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(OwnDir)) Process.Start("explorer.exe", "\"" + OwnDir + "\"");
        }

        // ================================================================ play

        // Enter on the main view plays, unless a text field (addon search, server address) or a button took it
        void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled || e.Key != Key.Enter || e.IsRepeat) return;
            if (MainView.Visibility != Visibility.Visible || !PlayButton.IsEnabled) return;
            if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase || e.OriginalSource is PasswordBox) return;
            e.Handled = true;
            Play_Click(PlayButton, new RoutedEventArgs());
        }

        void Play_Click(object sender, RoutedEventArgs e)
        {
            var dir = _settings.GameDir;
            var exe = System.IO.Path.Combine(dir, "Wow.exe");
            if (!File.Exists(exe)) { Status("Wow.exe is missing - press \"Check for updates\".", Danger); return; }
            try { PrepareGameConfig(dir); } catch (Exception ex) { Log.Write("Config.wtf: " + ex.Message); }
            GuildBanners.Update(dir);   // the guild hall banners' textures (Data\Patch-Y.MPQ)
            Process wow;
            try
            {
                wow = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true });
                Log.Write("game started");
            }
            catch (Exception ex) { Status("Could not start the game: " + ex.Message, Danger); return; }
            Radio.StartBeside(wow);   // the addon's radio button, played by a helper that ends with the game
            WindowState = WindowState.Minimized;

            var autoLogin = _settings.GameAutoLogin && !string.IsNullOrEmpty(_gamePassword) && wow != null;
            if (autoLogin && !GameLogin.Supported(exe)) { autoLogin = false; Log.Write("game login: not a 3.3.5a (12340) Wow.exe - the password is left to the player"); }
            if (!autoLogin) { if (_settings.CloseOnPlay) Close(); return; }

            // The launcher stays alive (minimized) until the password is in, then closes if asked to
            var password = _gamePassword;
            Status("Logging you into the game…");
            _ = Task.Run(() => GameLogin.TypePasswordAsync(wow, password, CancellationToken.None)).ContinueWith(t =>
            {
                Status(t.Result ? "In the game - have fun!" : "Log in at the game's login screen.", t.Result ? Ok : 0);
                if (_settings.CloseOnPlay) Close();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// WTF\Config.wtf before each start: the account name (the game's login screen comes pre-filled,
        /// cursor in the password box), and - only when missing - the EULA/terms already accepted and the
        /// realm picked, so a fresh install goes from the intro straight to the login screen and on to
        /// the character list.
        /// </summary>
        /// <summary>
        /// The "## Version" of the installed XorWoW addon, which names the realm's addon set. Higher than
        /// the published one only on the realm's own machine, between scripts\update-addons.ps1 and the
        /// publish: the launcher must not put the older published copy back over it.
        /// </summary>
        static Version InstalledXorWoWVersion(string dir)
        {
            try
            {
                var toc = System.IO.Path.Combine(dir, "Interface", "AddOns", "XorWoW", "XorWoW.toc");
                if (!File.Exists(toc)) return null;
                foreach (var line in File.ReadLines(toc))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"^##\s*Version:\s*([\d.]+)");
                    if (m.Success) return Version.TryParse(m.Groups[1].Value, out var v) ? v : null;
                }
            }
            catch (Exception e) { Log.Write("reading the XorWoW addon's version: " + e.Message); }
            return null;
        }

        void PrepareGameConfig(string dir)
        {
            var wtf = System.IO.Path.Combine(dir, "WTF");
            Directory.CreateDirectory(wtf);
            var cfg = System.IO.Path.Combine(wtf, "Config.wtf");
            var lines = File.Exists(cfg) ? File.ReadAllLines(cfg).ToList() : new List<string>();
            var changed = SetCVar(lines, "accountName", _account.ToLowerInvariant(), true);
            changed |= SetCVar(lines, "readEULA", "1", false);
            changed |= SetCVar(lines, "readTOS", "1", false);
            changed |= SetCVar(lines, "realmName", "XorWoW", false);
            if (changed) File.WriteAllLines(cfg, lines);
        }

        static bool SetCVar(List<string> lines, string name, string value, bool overwrite)
        {
            var line = $"SET {name} \"{value}\"";
            var i = lines.FindIndex(l => l.StartsWith("SET " + name + " ", StringComparison.OrdinalIgnoreCase));
            if (i < 0) { lines.Add(line); return true; }
            if (!overwrite || lines[i] == line) return false;
            lines[i] = line;
            return true;
        }

        // ================================================================ navigation

        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (HomePage == null) return;
            HomePage.Visibility = NavHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            AddonsPage.Visibility = NavAddons.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            NotesPage.Visibility = NavNotes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (NavAddons.IsChecked == true)
            {
                if (TabInstalled.IsChecked == true) ShowInstalled();
                else if (!_browseLoaded) _ = LoadBrowseAsync(true);
            }
        }

        void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        // ================================================================ addons

        HashSet<string> Reserved()
        {
            var r = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "XorWoW", "HuntsUI", "Transmog", "Pawn" };
            foreach (var f in _manifest?.AddonFolders ?? new List<string>()) r.Add(f);
            foreach (var f in _state?.XorWoWAddonFolders ?? new List<string>()) r.Add(f);
            var addOns = System.IO.Path.Combine(_settings.GameDir ?? "", "Interface", "AddOns");
            if (Directory.Exists(addOns))
                foreach (var d in Directory.EnumerateDirectories(addOns, "Blizzard_*")) r.Add(System.IO.Path.GetFileName(d));
            return r;
        }

        void AddonTab_Checked(object sender, RoutedEventArgs e)
        {
            if (BrowseBar == null) return;
            var installed = TabInstalled.IsChecked == true;
            BrowseBar.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
            InstalledBar.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
            if (installed) ShowInstalled();
            else _ = LoadBrowseAsync(true);
        }

        void Sort_Checked(object sender, RoutedEventArgs e)
        {
            if (SortUpdated == null || !IsLoaded) return;
            _settings.AddonSort = SortUpdated.IsChecked == true ? "modified" : "installs";
            _settings.Save();
            _ = LoadBrowseAsync(true);
        }

        void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            _searchTimer.Stop();
            _ = LoadBrowseAsync(true);
        }

        void More_Click(object sender, RoutedEventArgs e) => _ = LoadBrowseAsync(false);

        async Task LoadBrowseAsync(bool reset)
        {
            if (TabInstalled.IsChecked == true) return;
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;
            if (reset) { _page = 1; _rows.Clear(); AddonScroll.ScrollToTop(); }
            MoreButton.Visibility = Visibility.Collapsed;
            AddonEmpty.Visibility = Visibility.Visible;
            AddonEmpty.Text = "Loading…";
            try
            {
                // A query searches the cached catalogue (by title first); browsing pages the API
                var query = SearchBox.Text.Trim();
                if (query.Length > 0 && _catalogue == null && !_catalogueTried) { _catalogueTried = true; _catalogue = await _warperia.CatalogueAsync(ct); }
                if (query.Length > 0 && _catalogue != null)
                {
                    if (ct.IsCancellationRequested) return;
                    _rows.Clear();
                    foreach (var a in Warperia.SearchCatalogue(_catalogue, query, _settings.AddonSort)) _rows.Add(RowFor(a));
                    RefreshRows();
                    _browseLoaded = true;
                    AddonEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    AddonEmpty.Text = "No addon matches \"" + query + "\".";
                    return;
                }
                var (items, pages) = await _warperia.SearchAsync(SearchBox.Text, _settings.AddonSort, _page, ct);
                if (ct.IsCancellationRequested) return;
                _browseLoaded = true;
                foreach (var a in items) _rows.Add(RowFor(a));
                RefreshRows();
                AddonEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                AddonEmpty.Text = "No addon matches \"" + SearchBox.Text.Trim() + "\".";
                MoreButton.Visibility = _page < pages ? Visibility.Visible : Visibility.Collapsed;
                _page++;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Write("warperia: " + ex.Message);
                AddonEmpty.Visibility = Visibility.Visible;
                AddonEmpty.Text = "Warperia does not answer right now (" + ex.Message + ").";
            }
        }

        static AddonRow RowFor(AddonInfo a)
        {
            var updated = DateTime.TryParse(a.Updated, out var d) ? d.ToString("d MMM yyyy") : "";
            return new AddonRow
            {
                Info = a,
                Title = a.Title,
                Author = string.IsNullOrEmpty(a.Author) ? "" : "by " + a.Author,
                Summary = a.Summary,
                IconUrl = a.Icon,
                Meta = string.Join("  ·  ", new[] {
                    string.IsNullOrEmpty(a.Version) ? null : "v" + a.Version,
                    a.Installs > 0 ? a.Installs.ToString("N0") + " installs" : null,
                    string.IsNullOrEmpty(updated) ? null : "updated " + updated,
                    string.IsNullOrEmpty(a.Categories) ? null : a.Categories }.Where(x => x != null)),
            };
        }

        void ShowInstalled()
        {
            _searchCts?.Cancel();
            _rows.Clear();
            MoreButton.Visibility = Visibility.Collapsed;
            var state = CurrentState();
            if (state == null)
            {
                AddonEmpty.Visibility = Visibility.Visible;
                AddonEmpty.Text = "Choose the game folder first (Play page).";
                InstalledSummary.Text = "";
                return;
            }
            if (state.XorWoWAddonFolders.Count > 0)
                _rows.Add(new AddonRow
                {
                    IsBundle = true,
                    Title = "XorWoW addons",
                    Author = "by XorWoW",
                    Summary = string.Join(", ", state.XorWoWAddonFolders) + " - released with the realm and updated with the game.",
                    Meta = "v" + state.XorWoWAddonsVersion,
                    IconUrl = "pack://application:,,,/Assets/logo.png",
                    HasAction = false,
                    Badge = "Managed by the realm",
                });
            foreach (var a in state.Addons.OrderBy(a => a.Title))
            {
                var row = new AddonRow
                {
                    Installed = a,
                    Title = a.Title,
                    Summary = string.Join(", ", a.Folders),
                    Meta = "v" + a.Version + "  ·  installed " + (DateTime.TryParse(a.InstalledAt, out var d) ? d.ToString("d MMM yyyy") : ""),
                    IconUrl = a.Icon,
                };
                _rows.Add(row);
            }

            // Everything else in Interface\AddOns: added by hand
            _localAddons = AddonScanner.Scan(System.IO.Path.Combine(_settings.GameDir, "Interface", "AddOns"));
            var groups = AddonScanner.Group(_localAddons, Reserved(), state.Addons, _catalogue);
            var hand = groups.Where(g => g.Origin == AddonOrigin.Hand).OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var g in hand)
            {
                _rows.Add(new AddonRow
                {
                    Group = g,
                    Title = g.Title,
                    Summary = string.IsNullOrEmpty(g.Notes) ? string.Join(", ", g.Folders) : g.Notes,
                    Meta = string.Join("  ·  ", new[] {
                        string.IsNullOrEmpty(g.Version) ? null : "v" + g.Version,
                        g.Folders.Count > 1 ? g.Folders.Count + " folders: " + string.Join(", ", g.Folders) : g.Folders[0],
                        g.Match == null ? null : "on Warperia as “" + g.Match.Title + "” v" + g.Match.Version }.Where(x => x != null)),
                    IconUrl = g.Match?.Icon,
                    HasAction = g.Match != null,
                    ActionText = "Keep updated",
                    CanRemove = true,
                    Badge = "Added by hand",
                });
            }
            RefreshRows();

            var realmCount = state.XorWoWAddonFolders.Count > 0 || groups.Any(g => g.Origin == AddonOrigin.Realm) ? 1 : 0;
            InstalledSummary.Text = $"{realmCount + state.Addons.Count + hand.Count} addons: " +
                                    (realmCount > 0 ? "the realm's, " : "") + $"{state.Addons.Count} from the launcher, {hand.Count} added by hand.";
            AddonEmpty.Visibility = Visibility.Collapsed;

            // The Warperia catalogue tells which hand-installed addons it could keep updated (once a day)
            if (_catalogue == null && !_catalogueTried)
            {
                _catalogueTried = true;
                _ = LoadCatalogueAsync();
            }
        }

        List<LocalAddon> _localAddons = new List<LocalAddon>();
        List<AddonInfo> _catalogue;
        bool _catalogueTried;

        async Task LoadCatalogueAsync()
        {
            try { _catalogue = await _warperia.CatalogueAsync(_cts.Token); }
            catch (OperationCanceledException) { }
            if (_catalogue != null && NavAddons.IsChecked == true && TabInstalled.IsChecked == true) ShowInstalled();
        }

        GameState CurrentState()
        {
            if (string.IsNullOrEmpty(_settings.GameDir) || !Directory.Exists(_settings.GameDir)) return null;
            if (_state == null) _state = GameState.Load(_settings.GameDir);
            return _state;
        }

        void RefreshRows()
        {
            var state = CurrentState();
            var reserved = Reserved();
            var addOns = System.IO.Path.Combine(_settings.GameDir ?? "", "Interface", "AddOns");
            foreach (var row in _rows)
            {
                if (row.IsBundle) continue;
                if (row.Group != null) { row.ActionEnabled = !_busy; continue; }
                var id = row.Info?.Id ?? row.Installed.Id;
                var installed = state?.Addons.FirstOrDefault(a => a.Id == id);
                row.ActionEnabled = !_busy && state != null;
                row.CanRemove = installed != null;
                row.Badge = "";
                if (row.Info != null && row.Info.Folders.Any(reserved.Contains))
                {
                    row.HasAction = false; row.Badge = "Comes with XorWoW";
                }
                else if (installed != null)
                {
                    var latest = row.Info?.Version;
                    if (!string.IsNullOrEmpty(latest) && latest != installed.Version && row.Info != null)
                    { row.HasAction = true; row.ActionText = "Update to " + latest; }
                    else { row.HasAction = false; row.Badge = "Installed · v" + installed.Version; }
                }
                else if (row.Info != null && row.Info.Folders.Count > 0 && row.Info.Folders.All(f => Directory.Exists(System.IO.Path.Combine(addOns, f))))
                {
                    row.HasAction = true; row.ActionText = "Reinstall"; row.Badge = "Already in AddOns";
                }
                else { row.HasAction = row.Info != null; row.ActionText = "Install"; }
            }
        }

        async void AddonAction_Click(object sender, RoutedEventArgs e)
        {
            var row = (AddonRow)((FrameworkElement)sender).Tag;
            if (row.Group != null) { await KeepUpdatedAsync(row); return; }
            if (row.Info == null) return;
            if (!await EnsureGameFolderAsync()) return;
            var state = CurrentState();
            row.ActionEnabled = false;
            row.ActionText = "Installing…";
            try
            {
                var folders = await _warperia.InstallAsync(row.Info, _settings.GameDir, Reserved(), _cts.Token);
                state.Addons.RemoveAll(a => a.Id == row.Info.Id);
                state.Addons.Add(new InstalledAddon
                {
                    Id = row.Info.Id, Title = row.Info.Title, Version = row.Info.Version, Folders = folders,
                    Icon = row.Info.Icon, Link = row.Info.Link, InstalledAt = DateTime.Now.ToString("s"),
                });
                state.Save();
                Status($"{row.Info.Title} {row.Info.Version} installed - enable it at the character screen (AddOns).", Ok);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Log.Write($"install {row.Info.Title}: {ex}");
                await Ask("Could not install " + row.Info.Title, ex.Message, "OK");
            }
            catch (OperationCanceledException) { }
            RefreshRows();
        }

        /// <summary>A hand-installed addon Warperia also has: reinstalled from there, then tracked and updated like the others.</summary>
        async Task KeepUpdatedAsync(AddonRow row)
        {
            var g = row.Group; var info = g.Match;
            var state = CurrentState();
            if (info == null || state == null) return;
            if (await Ask("Keep " + g.Title + " updated?",
                    $"The launcher installs Warperia's “{info.Title}” v{info.Version} in place of the copy you have ({string.Join(", ", info.Folders)}), " +
                    "then updates it with the game. Its saved settings in WTF stay.", "Reinstall and keep updated", "Cancel") != 0) return;
            row.ActionEnabled = false;
            row.ActionText = "Installing…";
            try
            {
                var folders = await _warperia.InstallAsync(info, _settings.GameDir, Reserved(), _cts.Token);
                state.Addons.RemoveAll(a => a.Id == info.Id);
                state.Addons.Add(new InstalledAddon
                {
                    Id = info.Id, Title = info.Title, Version = info.Version, Folders = folders,
                    Icon = info.Icon, Link = info.Link, InstalledAt = DateTime.Now.ToString("s"),
                });
                state.Save();
                Status($"{info.Title} {info.Version} is now kept up to date by the launcher.", Ok);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Write($"keep updated {g.Title}: {ex}");
                await Ask("Could not install " + info.Title, ex.Message, "OK");
            }
            ShowInstalled();
        }

        async Task RemoveHandAddonAsync(AddonGroup g)
        {
            var state = CurrentState();
            if (state == null) return;
            var needed = AddonScanner.Dependents(_localAddons, g.Folders);
            if (needed.Count > 0)
            {
                await Ask("Can't remove " + g.Title, "It is needed by " + string.Join(", ", needed) + ". Remove those first.", "OK");
                return;
            }
            if (await Ask("Remove " + g.Title + "?", "Its folders (" + string.Join(", ", g.Folders) + ") are deleted from Interface\\AddOns. Its saved settings in WTF stay.", "Remove", "Cancel") != 0) return;
            try
            {
                Warperia.Uninstall(new InstalledAddon { Id = -1, Title = g.Title, Folders = g.Folders }, _settings.GameDir, state.Addons, Reserved());
                Status(g.Title + " removed.", Ok);
            }
            catch (Exception ex) { await Ask("Could not remove " + g.Title, ex.Message, "OK"); }
            ShowInstalled();
        }

        async void AddonRemove_Click(object sender, RoutedEventArgs e)
        {
            var row = (AddonRow)((FrameworkElement)sender).Tag;
            if (row.Group != null) { await RemoveHandAddonAsync(row.Group); return; }
            var state = CurrentState();
            var id = row.Info?.Id ?? row.Installed?.Id ?? 0;
            var inst = state?.Addons.FirstOrDefault(a => a.Id == id);
            if (inst == null) return;
            if (await Ask("Remove " + inst.Title + "?", "Its folders (" + string.Join(", ", inst.Folders) + ") are deleted from Interface\\AddOns. Its saved settings in WTF stay.", "Remove", "Cancel") != 0) return;
            try
            {
                Warperia.Uninstall(inst, _settings.GameDir, state.Addons, Reserved());
                state.Addons.Remove(inst);
                state.Save();
                Status(inst.Title + " removed.", Ok);
            }
            catch (Exception ex) { await Ask("Could not remove " + inst.Title, ex.Message, "OK"); }
            if (TabInstalled.IsChecked == true) ShowInstalled(); else RefreshRows();
        }

        async void AddonUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true);
            AddonUpdateButton.IsEnabled = false;
            try
            {
                Status("Checking your addons for updates…");
                var n = await UpdateWarperiaAddonsAsync(_cts.Token);
                Status(n == 0 ? "Your addons are up to date." : $"{n} addon{(n > 1 ? "s" : "")} updated.", Ok);
            }
            catch (OperationCanceledException) { }
            finally
            {
                SetBusy(false);
                AddonUpdateButton.IsEnabled = true;
                if (TabInstalled.IsChecked == true) ShowInstalled();
            }
        }

        /// <summary>Reinstalls every launcher-installed addon whose Warperia version changed. Returns how many.</summary>
        async Task<int> UpdateWarperiaAddonsAsync(CancellationToken ct)
        {
            var state = CurrentState();
            if (state == null || state.Addons.Count == 0) return 0;
            Status("Checking your addons for updates…");
            List<AddonInfo> latest;
            try { latest = await _warperia.GetAsync(state.Addons.Select(a => a.Id), ct); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { Log.Write("addon update check: " + ex.Message); return 0; }
            int n = 0;
            var reserved = Reserved();
            foreach (var info in latest)
            {
                var inst = state.Addons.FirstOrDefault(a => a.Id == info.Id);
                if (inst == null || string.IsNullOrEmpty(info.Version) || info.Version == inst.Version) continue;
                try
                {
                    Status($"Updating {info.Title} to {info.Version}…");
                    var folders = await _warperia.InstallAsync(info, _settings.GameDir, reserved, ct);
                    inst.Version = info.Version; inst.Title = info.Title; inst.Folders = folders; inst.Icon = info.Icon;
                    inst.InstalledAt = DateTime.Now.ToString("s");
                    state.Save();
                    n++;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { Log.Write($"update {info.Title}: {ex.Message}"); }
            }
            return n;
        }

        // ================================================================ settings

        void SettingBox_Click(object sender, RoutedEventArgs e)
        {
            _settings.CloseOnPlay = CloseOnPlayBox.IsChecked == true;
            _settings.ManageXorWoWAddons = ManageAddonsBox.IsChecked == true;
            _settings.GameAutoLogin = GameLoginBox.IsChecked == true;
            if (!_settings.GameAutoLogin) _settings.SavePassword(null);   // no longer needed: not kept
            else if (_settings.Remember && !string.IsNullOrEmpty(_gamePassword)) _settings.SavePassword(_gamePassword);
            _settings.Save();
        }

        void SaveServer_Click(object sender, RoutedEventArgs e)
        {
            var s = ServerBox.Text.Trim();
            if (s.Length == 0 || Uri.CheckHostName(s) == UriHostNameType.Unknown) { ServerBox.Text = _settings.AuthHost; return; }
            _settings.Server = s;
            _settings.Save();
            _net.Dispose();
            _net = new Net(_settings.FilesBase);
            _warperia = new Warperia(_net);
            LoginFooter.Text = "Realm " + _settings.AuthHost + "  ·  Launcher " + App.Version;
            Status("Realm address saved.", Ok);
        }

        void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(AppPaths.LogFile)) Process.Start("notepad.exe", "\"" + AppPaths.LogFile + "\"");
        }

        // ================================================================ uninstall (Windows: Settings > Apps > XorWoW > Uninstall)

        async Task UninstallFlowAsync()
        {
            LoginView.Visibility = Visibility.Collapsed;
            var blocker = Uninstaller.Blocker();
            if (blocker != null) { await Ask("Uninstall XorWoW", blocker, "OK"); Close(); return; }

            long bytes = 0;
            try { if (Directory.Exists(ClientDir)) bytes = new DirectoryInfo(ClientDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
            var adopted = GameState.Load(ClientDir).Adopted;
            var text = "This removes XorWoW from\n" + OwnDir + "\n\n" +
                       "•  the game, in its client folder (" + Size(bytes) + ")" +
                       (adopted ? ", including the World of Warcraft files that were in this folder before XorWoW moved them into client" : "") + "\n" +
                       "•  the addons installed in it\n" +
                       "•  XorWoW.exe, its desktop shortcut and the launcher's data";
            DialogCheck.IsChecked = true;
            if (await AskWithCheck("Uninstall XorWoW", text, "Keep my game settings, screenshots and saved login", "Uninstall", "Cancel") != 0) { Close(); return; }
            var keep = DialogCheck.IsChecked == true;

            _ = AskWithCheck("Uninstalling XorWoW…", "Removing the files - this takes a moment.", null);
            try { await Task.Run(() => Uninstaller.Run(keep)); }
            catch (Exception ex)
            {
                Log.Write("uninstall failed: " + ex);
                await Ask("Uninstall incomplete", ex.Message + "\n\nDetails are in " + AppPaths.LogFile, "Close");
                Close();
                return;
            }
            await Ask("XorWoW is uninstalled",
                keep ? "Your game settings and screenshots are still in\n" + ClientDir + "\nDelete that folder whenever you like." : "Everything is gone. Thanks for playing!",
                "Close");
            Uninstaller.RemoveLauncherAfterExit(keep);
            Close();
        }

        // ================================================================ dialog

        Task<int> Ask(string title, string text, params string[] buttons) => AskWithCheck(title, text, null, buttons);

        /// <summary>The themed dialog; <paramref name="check"/> adds a checkbox (read DialogCheck.IsChecked after), no buttons shows a message that stays until the next dialog.</summary>
        Task<int> AskWithCheck(string title, string text, string check, params string[] buttons)
        {
            var tcs = new TaskCompletionSource<int>();
            DialogTitle.Text = title;
            DialogText.Text = text;
            DialogCheck.Visibility = check == null ? Visibility.Collapsed : Visibility.Visible;
            DialogCheck.Content = check;
            DialogButtons.Children.Clear();
            for (int i = 0; i < buttons.Length; i++)
            {
                var index = i;
                var b = new Button
                {
                    Content = buttons[i],
                    Style = (Style)FindResource(i == 0 ? "IceButton" : "GhostButton"),
                    Margin = new Thickness(10, 0, 0, 0),
                    MinWidth = 90,
                };
                b.Click += (s, e) => { DialogLayer.Visibility = Visibility.Collapsed; tcs.TrySetResult(index); };
                DialogButtons.Children.Add(b);
            }
            DialogLayer.Visibility = Visibility.Visible;
            if (DialogButtons.Children.Count > 0) (DialogButtons.Children[0] as Button)?.Focus();
            return tcs.Task;
        }

        // ================================================================ snow

        sealed class Flake { public Ellipse E; public double X, Y, Speed, Drift, Phase; }
        readonly List<Flake> _flakes = new List<Flake>();
        readonly Random _rng = new Random();
        TimeSpan _lastFrame;

        void StartSnow()
        {
            for (int i = 0; i < 70; i++)
            {
                var size = 1.2 + _rng.NextDouble() * 2.8;
                var f = new Flake
                {
                    E = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(Color.FromRgb(0xD6, 0xF4, 0xFF)), Opacity = 0.15 + _rng.NextDouble() * 0.55 },
                    X = _rng.NextDouble() * Math.Max(1, ActualWidth),
                    Y = _rng.NextDouble() * Math.Max(1, ActualHeight),
                    Speed = 8 + size * 7 + _rng.NextDouble() * 10,
                    Drift = 6 + _rng.NextDouble() * 14,
                    Phase = _rng.NextDouble() * Math.PI * 2,
                };
                if (size > 3.2) f.E.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 2 };
                Snow.Children.Add(f.E);
                _flakes.Add(f);
            }
            CompositionTarget.Rendering += OnFrame;
        }

        void OnFrame(object sender, EventArgs e)
        {
            var t = ((RenderingEventArgs)e).RenderingTime;
            var dt = _lastFrame == TimeSpan.Zero ? 0 : (t - _lastFrame).TotalSeconds;
            _lastFrame = t;
            if (WindowState == WindowState.Minimized || dt <= 0 || dt > 0.5) return;
            double w = Snow.ActualWidth, h = Snow.ActualHeight;
            var secs = t.TotalSeconds;
            foreach (var f in _flakes)
            {
                f.Y += f.Speed * dt;
                if (f.Y > h + 5) { f.Y = -5; f.X = _rng.NextDouble() * w; }
                Canvas.SetLeft(f.E, f.X + Math.Sin(secs * 0.6 + f.Phase) * f.Drift);
                Canvas.SetTop(f.E, f.Y);
            }
        }
    }
}
