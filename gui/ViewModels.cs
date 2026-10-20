using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ReforgedUpdater.Gui
{
    internal static class Palette
    {
        public static Brush Get(string key) => (Brush)Application.Current.FindResource(key);

        /// <summary>Colour pairs (strong, soft) the letter bubbles cycle through.</summary>
        private static readonly string[] Bubbles = { "Pink", "Lilac", "Sky", "Mint", "Peach", "Butter" };

        public static string BubbleFor(string letter) =>
            Bubbles[string.IsNullOrEmpty(letter) ? 0 : letter[0] % Bubbles.Length];
    }

    /// <summary>One tool card in the Tools window.</summary>
    public sealed class ToolItem
    {
        internal ToolStatus Status { get; }

        internal ToolItem(ToolStatus status) { Status = status; }

        private ToolDef Def => Status.Def;

        public string Title => Def.Name;
        public string Summary => Def.Summary;
        public string Warning => Def.Warning;
        public string RepoUrl => Def.RepoUrl;

        public bool IsInstalled => Status.Local != null;
        public bool IsPatcher => Def == ToolDef.Patcher;

        /// <summary>The patcher's Run button appears once it is installed, not before.</summary>
        public bool CanRunPatcher => IsPatcher && IsInstalled;
        public bool HasSettings => IsInstalled && Def.SettingsFile != null;
        public bool HasPreset => IsInstalled && Def.PresetUrl != null;
        public bool PresetChanged => Status.PresetChanged;
        public string PresetText => Status.PresetChanged ? "Settings changed" : "Recommended settings";

        public bool CanInstall => Status.NeedsInstall;

        public string InstallText
        {
            get
            {
                switch (Status.State)
                {
                    case ToolState.UpdateAvailable: return "Update";
                    case ToolState.Damaged: return "Repair";
                    default: return "Install";
                }
            }
        }

        public string VersionLine
        {
            get
            {
                string size = Status.DownloadSize > 0 ? " · " + Ui.Bytes(Status.DownloadSize) : string.Empty;
                if (Status.Local == null)
                    return Status.Remote != null ? "Latest " + Status.Remote.Version + size : "Could not check GitHub";
                if (Status.State == ToolState.Damaged) return "Installed " + Status.LocalVersion + " · " + Status.Detail;
                if (Status.State == ToolState.UpdateAvailable) return "You have " + Status.LocalVersion + " · latest " + Status.RemoteVersion + size;
                return "Installed " + Status.LocalVersion + (Status.State == ToolState.Unknown ? " · could not check GitHub" : string.Empty);
            }
        }

        public string NotesText => string.Join("\n", Status.Notes);
        public bool HasNotes => Status.Notes.Count > 0;

        public string StateText
        {
            get
            {
                switch (Status.State)
                {
                    case ToolState.UpToDate: return "Up to date";
                    case ToolState.UpdateAvailable: return "Update ready";
                    case ToolState.Damaged: return "Needs repair";
                    case ToolState.Unknown: return "Installed";
                    default: return "Not installed";
                }
            }
        }

        public string StateGlyph
        {
            get
            {
                switch (Status.State)
                {
                    case ToolState.UpToDate: return "";
                    case ToolState.UpdateAvailable: return "";
                    case ToolState.Damaged: return "";
                    case ToolState.Unknown: return "";
                    default: return "";
                }
            }
        }

        private string AccentKey
        {
            get
            {
                switch (Status.State)
                {
                    case ToolState.UpToDate: case ToolState.Unknown: return "Mint";
                    case ToolState.UpdateAvailable: return "Sky";
                    case ToolState.Damaged: return "Berry";
                    default: return "Lilac";
                }
            }
        }

        public Brush Accent => Palette.Get(AccentKey);
        public Brush AccentSoft => Palette.Get(AccentKey + "Soft");

        public string InstallName => InstallText + " " + Def.Name;
    }

    /// <summary>One module card.</summary>
    public sealed class ModuleItem : INotifyPropertyChanged
    {
        internal ModuleStatus Status { get; }
        private readonly string _accent;

        internal ModuleItem(ModuleStatus status, string wowDataDir)
        {
            Status = status;
            _accent = AccentKey(status.State);
            FileInData = File.Exists(Path.Combine(wowDataDir, status.Entry.FileName));
        }

        private CatalogEntry Entry => Status.Entry;

        public string Letter => Entry.Letter.Length > 2 ? Entry.Letter.Substring(0, 2) : Entry.Letter;
        public string Title => Entry.Display;
        public string Name => string.IsNullOrEmpty(Entry.Name) ? Entry.FileName : Entry.Name;
        public string Group => string.IsNullOrEmpty(Entry.Group) ? "Other" : Entry.Group;
        public string Description => Entry.Description ?? string.Empty;
        public bool FileInData { get; }

        public string Tooltip
        {
            get
            {
                var lines = new List<string> { Entry.Display + " - " + Name };
                if (!string.IsNullOrEmpty(Entry.Description)) lines.Add(Entry.Description);
                if (!string.IsNullOrEmpty(Entry.VariantNote)) lines.Add(Entry.VariantNote);
                if (Status.Detail.Length > 0) lines.Add("Status: " + Status.Detail);
                lines.Add("File: " + Entry.FileName + "    Id: " + Entry.Id);
                return string.Join("\n\n", lines);
            }
        }

        public string Badges =>
            string.Join("  ·  ", new[] { Entry.VariantNote }.Concat(Entry.Badges).Where(b => !string.IsNullOrEmpty(b)));

        public string VersionLine
        {
            get
            {
                string site = Entry.Version != null ? "v" + Entry.Version : "on the site";
                if (Status.State == ModuleState.Untracked) return Status.Detail;
                if (Status.Local == null) return "Latest " + site + SizeSuffix;
                string mine = Status.Local.Version != null ? "v" + Status.Local.Version : "an older build";
                return Status.NeedsDownload && Status.Detail.Length > 0
                    ? "You have " + mine + " · " + Status.Detail
                    : "You have " + mine + (Status.NeedsDownload ? " · latest " + site : string.Empty);
            }
        }

        private string SizeSuffix => Status.Remote != null && Status.Remote.Size > 0 ? " · " + Ui.Bytes(Status.Remote.Size) : string.Empty;

        public string StateText
        {
            get
            {
                switch (Status.State)
                {
                    case ModuleState.UpToDate: return "Up to date";
                    case ModuleState.UpdateAvailable: return "Update ready";
                    case ModuleState.NotInstalled: return "Not installed";
                    case ModuleState.Untracked: return "Found in Data";
                    case ModuleState.FileMissing: return "Missing";
                    default: return "Changed";
                }
            }
        }

        public string StateGlyph
        {
            get
            {
                switch (Status.State)
                {
                    case ModuleState.UpToDate: return "\uE73E";        // check
                    case ModuleState.UpdateAvailable: return "\uE896"; // download
                    case ModuleState.NotInstalled: return "\uE710";    // add
                    case ModuleState.Untracked: return "\uE721";       // search
                    default: return "\uE7BA";                          // warning
                }
            }
        }

        public string ActionText
        {
            get
            {
                switch (Status.State)
                {
                    case ModuleState.NotInstalled: return "Install";
                    case ModuleState.UpdateAvailable: return "Update";
                    case ModuleState.FileMissing: return "Download again";
                    case ModuleState.SizeMismatch: return "Repair";
                    case ModuleState.Untracked: return "Adopt";
                    default: return null;
                }
            }
        }

        public bool HasAction => ActionText != null;

        /// <summary>Cards that would download something can be ticked and installed together.</summary>
        public bool CanSelect => Status.State == ModuleState.NotInstalled || Status.NeedsDownload;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value || (value && !CanSelect)) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string SelectName => "Select " + Entry.Display;
        public string ActionName => ActionText + " " + Entry.Display;
        public string RemoveName => "Remove " + Entry.Display;
        public bool IsCurrent => Status.State == ModuleState.UpToDate;
        public bool CanRemove => Status.Local != null || (Status.State == ModuleState.Untracked && FileInData);

        public Brush Accent => Palette.Get(_accent);
        public Brush AccentSoft => Palette.Get(_accent + "Soft");
        public Brush BubbleBrush => Palette.Get(Palette.BubbleFor(Entry.Letter));
        public Brush BubbleSoft => Palette.Get(Palette.BubbleFor(Entry.Letter) + "Soft");

        private static string AccentKey(ModuleState state)
        {
            switch (state)
            {
                case ModuleState.UpToDate: return "Mint";
                case ModuleState.UpdateAvailable: return "Peach";
                case ModuleState.NotInstalled: return "Sky";
                case ModuleState.Untracked: return "Butter";
                default: return "Berry";
            }
        }
    }

    /// <summary>An entry in the game picker: a game the user added, or the one this app sits in.</summary>
    public sealed class GameChoice
    {
        public string Name { get; }
        public string Path { get; }
        public bool Registered { get; }

        public GameChoice(string name, string path, bool registered)
        {
            Name = name;
            Path = path;
            Registered = registered;
        }

        public string Hint => Registered ? Path : "this app's folder · " + Path;
    }

    /// <summary>
    /// Name and folder in the drop-down, only the name in the closed box: the selection box
    /// is templated by the ComboBox itself, each list entry by a ComboBoxItem.
    /// </summary>
    public sealed class GameTemplateSelector : DataTemplateSelector
    {
        public DataTemplate Full { get; set; }
        public DataTemplate Compact { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
            (container as FrameworkElement)?.TemplatedParent is ComboBoxItem ? Full : Compact;
    }

    public sealed class EditionChoice
    {
        internal Edition Edition { get; }
        internal EditionChoice(Edition edition) { Edition = edition; }

        public string Name => Edition.Name;
        public string Title => Edition.Title;
    }

    public sealed class LogLine
    {
        private readonly UiLevel _level;

        internal LogLine(UiLevel level, string text, string time = null)
        {
            _level = level;
            Time = time ?? DateTime.Now.ToString("HH:mm:ss");
            Text = text;
            switch (level)
            {
                case UiLevel.Good: Glyph = "\uE73E"; Brush = Palette.Get("Mint"); break;
                case UiLevel.Warn: Glyph = "\uE7BA"; Brush = Palette.Get("Peach"); break;
                case UiLevel.Error: Glyph = "\uE783"; Brush = Palette.Get("Berry"); break;
                case UiLevel.Head: Glyph = "\uE734"; Brush = Palette.Get("Lilac"); break;
                default: Glyph = "\uE946"; Brush = Palette.Get("Muted"); break;
            }
        }

        public string Time { get; }
        public string Text { get; }
        public string Glyph { get; }
        public Brush Brush { get; }

        /// <summary>The same line, coloured from the current palette.</summary>
        internal LogLine Repainted() => new LogLine(_level, Text, Time);
    }
}
