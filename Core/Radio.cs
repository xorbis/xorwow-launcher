using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Media.Core;
using Windows.Media.Playback;
using Forms = System.Windows.Forms;

namespace XorWoWLauncher.Core
{
    /// <summary>
    /// The in-game radio (the XorWoW addon's Radio.lua): Everlook Broadcasting Co., Turtle WoW's
    /// community station. The 3.3.5 client cannot play a network stream and an addon cannot reach
    /// another program, so the launcher plays it, in a small helper started beside the game at Play
    /// ("XorWoW.exe --radio &lt;game pid&gt;", no window) that ends with the game.
    ///
    /// The addon keeps what it wants in a global, XorWoWRadioState = 2000 x keep playing in the
    /// background (0/1) + 1000 x playing (0/1) + volume (0-100, a fraction too: the radio's own
    /// volume already scaled by the game's master volume), which the helper reads from the
    /// game's memory twice a second (GameLua) - without the background flag the radio is muted while
    /// another window is in front or the game is minimized: nothing on
    /// screen, any window mode, minimized or covered. It acts on a state that differs from the last
    /// one it acted on, so its tray icon can still stop or start the radio without the game
    /// overriding it at the next read. No such global (the character screen, an older addon): the
    /// radio keeps doing what it did.
    ///
    /// Sound: Windows.Media.Playback (WinRT, in every Windows 10/11; WPF's MediaPlayer needs the
    /// legacy Windows Media Player and failed to open the stream here). Volume follows the game's.
    /// </summary>
    public sealed class Radio
    {
        public const string Flag = "--radio";
        const string StreamUrl = "https://radio.turtle-music.org/stream";
        const string StatusUrl = "https://radio.turtle-music.org/status-json.xsl";
        const string PageUrl = "https://turtlecraft.gg/radio";
        const string Station = "Everlook Broadcasting Co.";
        static readonly TimeSpan ReadEvery = TimeSpan.FromMilliseconds(500);
        static readonly TimeSpan TitleEvery = TimeSpan.FromSeconds(20);
        static readonly TimeSpan StalledAfter = TimeSpan.FromSeconds(20);   // buffering this long: reconnect

        /// <summary>Starts the helper for a game the launcher just started; one per game process.</summary>
        public static void StartBeside(Process wow)
        {
            if (wow == null) return;
            try { Process.Start(new ProcessStartInfo(SelfUpdate.ExePath, $"{Flag} {wow.Id}") { UseShellExecute = false }); }
            catch (Exception e) { Log.Write("radio: could not start the helper: " + e.Message); }
        }

        /// <summary>The helper's whole life: false when there is nothing to do (no such game, or a helper already on it).</summary>
        public static bool Run(int pid)
        {
            Process wow;
            try { wow = Process.GetProcessById(pid); }
            catch { return false; }
            var mutex = new Mutex(true, "XorWoW-radio-" + pid, out var first);
            if (!first) { mutex.Dispose(); return false; }
            new Radio(wow, mutex).Begin();
            return true;
        }

        readonly Process _wow;
        readonly Mutex _mutex;
        readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
        readonly DispatcherTimer _timer;
        readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        MediaPlayer _player;
        MediaSource _source;
        Forms.NotifyIcon _tray;
        Forms.ToolStripMenuItem _titleItem, _playItem;

        bool _playing;
        double _volume = 50;   // percent, already scaled by the game's master volume - fractions too
        string _title;
        (bool on, double volume)? _acted;
        DateTime _bufferingSince = DateTime.MaxValue, _nextTitle = DateTime.MinValue;
        readonly GameLua _lua;
        bool _stateSeen, _keepInBackground;

        Radio(Process wow, Mutex mutex)
        {
            _wow = wow;
            _mutex = mutex;
            _lua = new GameLua(wow.Id);
            _timer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = ReadEvery };
            _timer.Tick += (s, e) => Tick();
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("XorWoWLauncher/" + App.Version);
        }

        void Begin()
        {
            Log.Write($"radio: watching the game (pid {_wow.Id})" + (_lua.Open ? "" : " - cannot read its memory, only the tray icon works"));
            _timer.Start();
        }

        void End()
        {
            _timer.Stop();
            Stop();
            Disconnect();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _http.Dispose();
            _lua.Dispose();
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
            Log.Write("radio: the game closed, helper done");
            System.Windows.Application.Current.Shutdown();
        }

        void Tick()
        {
            bool exited;
            try { exited = _wow.HasExited; } catch { exited = true; }
            if (exited) { End(); return; }

            if (_lua.Number("XorWoWRadioState") is double state && state >= 0 && state <= 3100)
            {
                if (!_stateSeen) { _stateSeen = true; Log.Write("radio: reading the addon's state"); }
                _keepInBackground = state >= 2000;
                var rest = state - (_keepInBackground ? 2000 : 0);
                var read = (on: rest >= 1000, volume: Math.Round(rest - (rest >= 1000 ? 1000 : 0), 2));
                if (_acted != read)
                {
                    if (_acted != null) Log.Write($"radio: the game asks for {(read.on ? "play" : "stop")} at {read.volume:0.##}% (state {state})");
                    _acted = read;
                    SetVolume(read.volume);
                    if (read.on != _playing) { if (read.on) Play(); else Stop(); }
                }
            }

            if (_playing && _player != null)
            {
                // quiet while another window is in front (or the game is minimized), unless the addon
                // says to keep playing: a mute, not a pause, so it comes back live and at once
                var mute = !_keepInBackground && !GameInFront();
                if (_player.IsMuted != mute) _player.IsMuted = mute;

                if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Buffering || _player.PlaybackSession.PlaybackState == MediaPlaybackState.Opening)
                {
                    if (_bufferingSince == DateTime.MaxValue) _bufferingSince = DateTime.UtcNow;
                    else if (DateTime.UtcNow - _bufferingSince > StalledAfter) { Log.Write("radio: stalled, reconnecting"); Connect(); }
                }
                else _bufferingSince = DateTime.MaxValue;

                if (DateTime.UtcNow >= _nextTitle) { _nextTitle = DateTime.UtcNow + TitleEvery; _ = FetchTitleAsync(); }
            }
        }

        // ================================================================ playing

        void Play()
        {
            _playing = true;
            Connect();
            _nextTitle = DateTime.MinValue;
            Log.Write($"radio: playing (volume {_volume:0.##}%)");
            UpdateTray();
        }

        /// <summary>
        /// A new player on a new connection, every time: swapping only the source left the old
        /// connections open (three at once, seen 2026-09-30) and the player stuck buffering for good.
        /// </summary>
        void Connect()
        {
            Disconnect();
            _bufferingSince = DateTime.MaxValue;
            var player = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Media, Volume = _volume / 100.0 };
            player.CommandManager.IsEnabled = false;   // no media overlay / media keys: the game owns play and stop
            player.MediaFailed += (s, e) => _ui.BeginInvoke(new Action(() => { if (s == _player) OnFailed(e.Error + " " + e.ErrorMessage); }));
            _source = MediaSource.CreateFromUri(new Uri(StreamUrl));
            player.Source = _source;
            _player = player;
            player.Play();
        }

        void Disconnect()
        {
            if (_player == null) return;
            try { _player.Pause(); _player.Source = null; } catch { }
            try { _source?.Dispose(); } catch { }
            try { _player.Dispose(); } catch { }
            _player = null;
            _source = null;
        }

        void Stop()
        {
            if (!_playing) return;
            _playing = false;
            _title = null;
            Disconnect();   // a live stream: drop the connection, the next play starts live again
            Log.Write("radio: stopped");
            UpdateTray();
        }

        bool GameInFront()
        {
            var window = GetForegroundWindow();
            return window != IntPtr.Zero && GetWindowThreadProcessId(window, out var pid) != 0 && pid == _wow.Id;
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);

        void SetVolume(double volume)
        {
            _volume = Math.Max(0, Math.Min(100, volume));
            if (_player != null) _player.Volume = _volume / 100.0;
            UpdateTray();
        }

        void OnFailed(string error)
        {
            if (!_playing) return;
            Log.Write("radio: stream failed (" + error.Trim() + "), retrying in 5 s");
            var retry = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromSeconds(5) };
            retry.Tick += (s, e) => { retry.Stop(); if (_playing) Connect(); };
            retry.Start();
        }

        async Task FetchTitleAsync()
        {
            try
            {
                var status = Json.Obj(Json.Parse(await _http.GetStringAsync(StatusUrl)), "icestats");
                // one mount: "source" is an object; several: an array of them
                var source = Json.Get(status, "source");
                var first = source as IDictionary<string, object> ?? Json.List(source).Find(o => o is IDictionary<string, object>) as IDictionary<string, object>;
                var title = first == null ? null : Json.Str(first, "title");
                if (!string.IsNullOrWhiteSpace(title) && title != _title && _playing)
                {
                    _title = title.Trim();
                    UpdateTray();
                }
            }
            catch (Exception e) { Log.Write("radio: song title: " + e.Message); }
        }

        // ================================================================ tray icon

        /// <summary>Shown from the first play on, until the game closes: the title, play / stop, the volume.</summary>
        void UpdateTray()
        {
            if (_tray == null)
            {
                if (!_playing) return;
                var menu = new Forms.ContextMenuStrip();
                _titleItem = new Forms.ToolStripMenuItem(Station) { Enabled = false };
                _playItem = new Forms.ToolStripMenuItem("Stop", null, (s, e) => { if (_playing) Stop(); else Play(); });
                var volume = new Forms.ToolStripMenuItem("Volume");
                foreach (var v in new[] { 100, 75, 50, 25, 10 })
                {
                    var level = v;
                    volume.DropDownItems.Add(new Forms.ToolStripMenuItem(v + "%", null, (s, e) => SetVolume(level)));
                }
                volume.DropDownOpening += (s, e) =>
                {
                    foreach (Forms.ToolStripMenuItem item in volume.DropDownItems) item.Checked = Math.Abs(_volume - double.Parse(item.Text.TrimEnd('%'))) < 0.5;
                };
                menu.Items.Add(_titleItem);
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add(_playItem);
                menu.Items.Add(volume);
                menu.Items.Add(new Forms.ToolStripMenuItem("Open the station's page", null, (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo(PageUrl) { UseShellExecute = true }); } catch { }
                }));
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add(new Forms.ToolStripMenuItem("Stop and hide this icon", null, (s, e) => { Stop(); _tray.Visible = false; }));
                _tray = new Forms.NotifyIcon { Icon = AppIcon(), ContextMenuStrip = menu };
                _tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) { if (_playing) Stop(); else Play(); } };
            }
            if (_playing) _tray.Visible = true;
            var state = _playing ? (_title ?? Station) : "Stopped";
            _titleItem.Text = _playing && _title != null ? _title : Station;
            _playItem.Text = _playing ? "Stop" : "Play";
            _tray.Text = Clip($"XorWoW Radio ({_volume:0.#}%)\n{state}", 63);   // NotifyIcon refuses more than 63 characters
        }

        static Icon AppIcon()
        {
            try { return Icon.ExtractAssociatedIcon(SelfUpdate.ExePath); }
            catch { return SystemIcons.Application; }
        }

        static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
