using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using XorWoWLauncher.Core;

namespace XorWoWLauncher
{
    /// <summary>One line of the addon lists: a Warperia addon, one installed from it, or the XorWoW bundle.</summary>
    public sealed class AddonRow : INotifyPropertyChanged
    {
        public AddonInfo Info;            // from Warperia (browse, or the latest known for an installed one)
        public InstalledAddon Installed;  // installed by the launcher
        public bool IsBundle;             // the XorWoW addons, managed with the client
        public AddonGroup Group;          // an addon added by hand (found in Interface\AddOns)

        public string Title { get; set; }
        public string Author { get; set; }
        public string Summary { get; set; }
        public string Meta { get; set; }
        public string IconUrl { get; set; }

        BitmapImage _icon;
        public BitmapImage IconImage
        {
            get
            {
                if (_icon != null || string.IsNullOrEmpty(IconUrl)) return _icon;
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.UriSource = new Uri(IconUrl);
                    b.DecodePixelWidth = 88;
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.EndInit();
                    _icon = b;
                }
                catch { }
                return _icon;
            }
        }

        string _actionText = "Install"; bool _actionEnabled = true, _hasAction = true, _canRemove; string _badge = "";
        public string ActionText { get => _actionText; set => Set(ref _actionText, value); }
        public bool ActionEnabled { get => _actionEnabled; set => Set(ref _actionEnabled, value); }
        public bool HasAction { get => _hasAction; set => Set(ref _hasAction, value); }
        public bool CanRemove { get => _canRemove; set => Set(ref _canRemove, value); }
        public string Badge { get => _badge; set => Set(ref _badge, value); }

        public event PropertyChangedEventHandler PropertyChanged;
        void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
