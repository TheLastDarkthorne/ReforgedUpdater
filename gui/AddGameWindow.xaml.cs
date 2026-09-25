using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReforgedUpdater.Gui
{
    /// <summary>
    /// Registers a game folder, the same way "games add &lt;name&gt; &lt;folder&gt; --edition" does,
    /// so the command line sees it under the same name.
    /// </summary>
    public partial class AddGameWindow : Window
    {
        private readonly Settings _settings;
        private readonly string _settingsPath;
        private bool _nameEdited;
        private bool _settingName;

        /// <summary>The registered game's name, once added.</summary>
        public string AddedName { get; private set; }

        internal AddGameWindow(Settings settings, string settingsPath, string suggestedFolder)
        {
            InitializeComponent();
            _settings = settings;
            _settingsPath = settingsPath;
            if (!string.IsNullOrEmpty(suggestedFolder)) FolderBox.Text = suggestedFolder;
            Loaded += (s, e) => FolderBox.Focus();
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            string picked = FolderPicker.Pick(this, "Choose your World of Warcraft folder",
                Directory.Exists(FolderBox.Text) ? FolderBox.Text : null);
            if (picked != null) FolderBox.Text = picked;
        }

        private void OnNameEdited(object sender, TextChangedEventArgs e)
        {
            if (!_settingName) _nameEdited = NameBox.Text.Length > 0;
        }

        /// <summary>Suggests a nickname and a patch set from what is in the folder.</summary>
        private void OnFolderChanged(object sender, TextChangedEventArgs e)
        {
            string folder = FolderBox.Text.Trim().Trim('"');
            if (!Directory.Exists(folder)) { FolderHint.Text = "The folder with Wow.exe in it."; return; }

            if (!_nameEdited)
            {
                _settingName = true;
                NameBox.Text = SuggestName(folder);
                _settingName = false;
            }

            try
            {
                var wow = WowInstall.Open(folder);
                int? major = wow.ClientMajorVersion();
                if (major == Edition.Wotlk.ClientMajor)
                {
                    PickWotlk.IsChecked = true;
                    FolderHint.Text = "Found a 3.3.5 client. Looks like WotLK!";
                }
                else if (major == 1)
                {
                    if (PickWotlk.IsChecked == true) PickWotlk.IsChecked = false;
                    FolderHint.Text = "Found a 1.12 client. Every vanilla server has the same Wow.exe, so pick Kronos or Turtle below.";
                }
                else
                {
                    FolderHint.Text = "Found a Data folder" + (wow.Root != folder ? " (using " + wow.Root + ")" : string.Empty) + ".";
                }
            }
            catch (UpdaterException ex)
            {
                FolderHint.Text = ex.Message;
            }
        }

        private string SuggestName(string folder)
        {
            string leaf = Path.GetFileName(folder.TrimEnd('\\', '/'));
            if (leaf.Equals("Data", StringComparison.OrdinalIgnoreCase))
                leaf = Path.GetFileName(Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? leaf);

            string slug = Regex.Replace(leaf.ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-', '.', '_');
            if (slug.Length > 32) slug = slug.Substring(0, 32).TrimEnd('-');
            if (slug.Length == 0 || !Workspace.IsUsableGameName(slug)) slug = "wow";

            string candidate = slug;
            for (int n = 2; _settings.FindGame(candidate) != null; n++) candidate = slug + n;
            return candidate;
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            ErrorBox.Visibility = Visibility.Collapsed;
            try
            {
                AddedName = Register();
                DialogResult = true;
            }
            catch (UpdaterException ex)
            {
                ErrorText.Text = ex.Message;
                ErrorBox.Visibility = Visibility.Visible;
            }
        }

        private string Register()
        {
            string folder = FolderBox.Text.Trim().Trim('"');
            string name = NameBox.Text.Trim();
            var picked = new[] { PickWotlk, PickKronos, PickTurtle }.FirstOrDefault(r => r.IsChecked == true);

            if (folder.Length == 0) throw new UpdaterException("Choose the game folder first.");
            if (!Workspace.IsUsableGameName(name))
                throw new UpdaterException("Nicknames use up to 32 letters, digits, dots, dashes or underscores, like warmane or turtle.");
            if (picked == null) throw new UpdaterException("Pick which patch set this game uses.");

            var existing = _settings.FindGame(name);
            if (existing != null) throw new UpdaterException("You already have a game called \"" + existing.Name + "\".");

            var wow = WowInstall.Open(folder);
            var samePath = _settings.FindGameByPath(wow.Root);
            if (samePath != null) throw new UpdaterException("That folder is already added as \"" + samePath.Name + "\".");

            _settings.Games.Add(new GameEntry { Name = name, Path = wow.Root });
            Store.Save(_settingsPath, _settings);

            using (var updater = new Updater(wow, _settings))
            {
                if (wow.DataIsCustom)
                {
                    updater.State.DataPath = wow.DataDir;
                    updater.Save();
                }

                // Explicit, so it is remembered in the game's state, with a warning if Wow.exe disagrees.
                updater.ResolveEdition((string)picked.Tag);
            }

            return name;
        }

        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
    }
}
