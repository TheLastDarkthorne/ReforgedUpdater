using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Shell;
using Microsoft.Win32;

namespace ReforgedUpdater.Gui
{
    /// <summary>
    /// The window drives the same engine as the command line: Updater, WowInstall and the
    /// shared settings file. It only decides what to ask and how to show the answers.
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly string _settingsPath;
        private Settings _settings;
        private WowInstall _wow;
        private Updater _updater;
        private List<CatalogEntry> _catalog;
        private List<ModuleStatus> _rows = new List<ModuleStatus>();
        private List<ModuleItem> _items = new List<ModuleItem>();
        private readonly HashSet<string> _picks = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // ticked module ids
        private CancellationTokenSource _cancel;
        private DateTime _lastCheck;
        private bool _syncing;      // set while code, not the user, moves a picker
        private bool _downloading;  // byte progress is on screen; engine chatter goes to the log only
        private int _notes;         // warnings during the current run
        private Outcome _outcome;   // what the finished run wants the bubble to say
        private bool _greeting = true; // Belora's hello stays up until the first check is done
        private readonly DateTime _openedAt = DateTime.Now;
        private int _sayCount;         // bumps on every Say, so a delayed message can tell it is stale

        /// <summary>How long the hello stays up, however quickly the first check finishes.</summary>
        private static readonly TimeSpan GreetingTime = TimeSpan.FromSeconds(3.5);

        public ObservableCollection<GameChoice> Games { get; } = new ObservableCollection<GameChoice>();
        public List<EditionChoice> Editions { get; } = Edition.All.Select(e => new EditionChoice(e)).ToList();
        public ObservableCollection<LogLine> Log { get; } = new ObservableCollection<LogLine>();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            // Fit small laptop screens: 1366x768 leaves about 720px above the taskbar.
            var work = SystemParameters.WorkArea;
            Width = Math.Min(Width, work.Width - 40);
            Height = Math.Min(Height, work.Height - 20);

            Ui.Sink = new WindowSink(Dispatcher, AddLog, ShowProgress, EndProgress);
            _settingsPath = Workspace.SettingsPath();

            // Settings are read before the window shows, so it opens in the right theme.
            _settings = Store.Load<Settings>(_settingsPath);
            ApplyTheme();
            SystemEvents.UserPreferenceChanged += OnWindowsSettingsChanged;

            SourceInitialized += (s, e) => NativeMethods.StyleFrame(this, Themes.IsDark);
            StateChanged += (s, e) => FitMaximized();
            Loaded += async (s, e) => await StartAsync();
            Closing += OnClosing;
            // SystemEvents holds its handlers statically; let go of the window when it closes.
            Closed += (s, e) => SystemEvents.UserPreferenceChanged -= OnWindowsSettingsChanged;
        }

        // ================================================================ bindable state

        public event PropertyChangedEventHandler PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            Raise(name);
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void RaiseCommands()
        {
            foreach (string name in new[] { nameof(IsIdle), nameof(CanCheck), nameof(CanPrimary), nameof(PrimaryText),
                                            nameof(HasUntracked), nameof(AdoptText), nameof(ShowModules),
                                            nameof(HasPicks) })
                Raise(name);
        }

        // Belora greets in Thalassian, the high elves' language.
        private string _bubble = "Bal'a dash, malanore!";
        public string Bubble { get => _bubble; set => Set(ref _bubble, value); }

        private string _bubbleSub = "That's \"Greetings, traveler!\" Let me look for your games.";
        public string BubbleSub { get => _bubbleSub; set => Set(ref _bubbleSub, value); }

        private MascotMood _mood = MascotMood.Happy;
        public MascotMood Mood { get => _mood; set => Set(ref _mood, value); }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set { Set(ref _isBusy, value); RaiseCommands(); }
        }

        public bool IsIdle => !_isBusy;

        private bool _canCancel;
        public bool CanCancel { get => _canCancel; set => Set(ref _canCancel, value); }

        private string _progressTitle;
        public string ProgressTitle { get => _progressTitle; set => Set(ref _progressTitle, value); }

        private string _progressDetail;
        public string ProgressDetail { get => _progressDetail; set => Set(ref _progressDetail, value); }

        private double _progressValue;
        public double ProgressValue { get => _progressValue; set => Set(ref _progressValue, value); }

        private bool _progressIndeterminate = true;
        public bool ProgressIndeterminate { get => _progressIndeterminate; set => Set(ref _progressIndeterminate, value); }

        private double _cardWidth = 340;
        public double CardWidth { get => _cardWidth; set => Set(ref _cardWidth, value); }

        private bool _hasGame;
        public bool HasGame { get => _hasGame; set { Set(ref _hasGame, value); RaiseCommands(); } }

        private bool _showEmpty;
        public bool ShowEmpty { get => _showEmpty; set => Set(ref _showEmpty, value); }

        private bool _showNeedsEdition;
        public bool ShowNeedsEdition { get => _showNeedsEdition; set { Set(ref _showNeedsEdition, value); RaiseCommands(); } }

        public bool ShowModules => HasGame && !ShowNeedsEdition && _rows.Count > 0;

        private bool _showLog;
        public bool ShowLog { get => _showLog; set => Set(ref _showLog, value); }

        private string _dataFolder;
        public string DataFolder { get => _dataFolder; set => Set(ref _dataFolder, value); }

        private GameChoice _currentGame;

        private int UpdateCount => _rows.Count(r => r.Local != null && r.NeedsDownload);
        private int UntrackedCount => UntrackedRows().Select(r => r.Entry.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        public bool CanCheck => IsIdle && HasGame && !ShowNeedsEdition;
        private bool FreshGame => _rows.Count > 0 && _rows.All(r => r.Local == null) && !HasUntracked;

        /// <summary>
        /// The big button: get the ticked patches; otherwise update what changed, or on a fresh
        /// game, install everything.
        /// </summary>
        public bool CanPrimary => IsIdle && !ShowNeedsEdition && (HasPicks || UpdateCount > 0 || FreshGame);

        public string PrimaryText =>
            HasPicks ? PicksVerb + " " + _picks.Count + " selected"
            : UpdateCount > 1 ? "Update " + UpdateCount + " patches"
            : UpdateCount == 1 ? "Update 1 patch"
            : FreshGame ? "Install all patches"
            : "All caught up";
        public bool HasUntracked => UntrackedCount > 0;
        public string AdoptText => UntrackedCount == 1 ? "Adopt 1 file I found" : "Adopt " + UntrackedCount + " files I found";

        private List<ModuleStatus> UntrackedRows() => _rows.Where(r => r.State == ModuleState.Untracked).ToList();

        public bool HasPicks => _picks.Count > 0;

        /// <summary>The ticked patches, in the order the site lists them.</summary>
        private List<ModuleStatus> PickedRows() => _rows.Where(r => _picks.Contains(r.Entry.Id)).ToList();

        private string PicksVerb
        {
            get
            {
                var picked = PickedRows();
                return picked.All(r => r.Local == null) ? "Install" : picked.All(r => r.Local != null) ? "Update" : "Get";
            }
        }

        // ================================================================ start-up and games

        private async Task StartAsync()
        {
            Workspace.MigrateLegacyDataPath(_settings, _settingsPath);
            await ReloadGamesAsync(null);
        }

        /// <summary>
        /// The games the user added, plus the one this app was copied into, opening the one
        /// used last. Nothing is searched for: the window only looks after folders the user chose.
        /// </summary>
        private async Task ReloadGamesAsync(string selectPath)
        {
            var beside = WowInstall.BesideExe();

            _syncing = true;
            Games.Clear();
            foreach (var game in _settings.Games) Games.Add(new GameChoice(game.Name, game.Path, registered: true));
            if (beside != null && _settings.FindGameByPath(beside.Root) == null)
                Games.Insert(0, new GameChoice(Path.GetFileName(beside.Root.TrimEnd('\\')), beside.Root, registered: false));
            _syncing = false;

            if (Games.Count == 0)
            {
                CloseGame();
                ShowEmpty = true;
                string hint = UnaddedLastGame() != null
                    ? "Last time you used " + UnaddedLastGame() + ". Press Choose game folder and I'll fill it in."
                    : "Show me the folder your World of Warcraft is in, or copy me next to Wow.exe, and I'll look after its patches.";
                if (_greeting) Say("Bal'a dash, malanore!", "Greetings, traveler! " + hint, MascotMood.Happy);
                else Say("Which game should I look after?", hint, MascotMood.Idle);
                _greeting = false;
                return;
            }

            ShowEmpty = false;
            var choice = Games.FirstOrDefault(g => SamePath(g.Path, selectPath ?? _settings.WowPath)) ?? Games[0];
            _syncing = true;
            GamePicker.SelectedItem = choice;
            _syncing = false;
            await OpenGameAsync(choice, refetchSite: false);
        }

        private async Task OpenGameAsync(GameChoice game, bool refetchSite)
        {
            // A saved Data folder that is gone stops the game from opening, and with it the menu
            // that resets the folder, so the reset is offered here instead.
            string missing = !IsBusy && Directory.Exists(game.Path) ? Workspace.MissingSavedData(game.Path) : null;
            if (missing != null && CuteDialog.Show(this, "Where did the Data folder go?",
                    game.Name + "'s patches are set to go to " + missing + ", but that folder isn't there anymore. "
                    + "If it's on a drive that isn't connected, plug it in and press Not now. "
                    + "Or I can use the game's own Data folder instead.",
                    "Use the game's own Data folder", "Not now", MascotMood.Oops))
            {
                Workspace.SaveGameDataPath(game.Path, null);
                AddLog(UiLevel.Good, "Data folder for " + game.Name + " is back to " + Path.Combine(game.Path, "Data") + ".");
            }

            await RunAsync("Looking at " + game.Name + "...", async ct =>
            {
                CloseGame();
                _currentGame = game;

                _wow = Workspace.ApplySavedData(WowInstall.Open(game.Path), null, game.Registered ? game.Name : null);
                _updater = new Updater(_wow, _settings);
                HasGame = true;
                DataFolder = _wow.DataDir + (_wow.DataIsCustom ? "  (moved)" : string.Empty);

                // The last game used is the command line's default too.
                if (!SamePath(_settings.WowPath, _wow.Root))
                {
                    _settings.WowPath = _wow.Root;
                    Store.Save(_settingsPath, _settings);
                }

                // Every vanilla client has the same Wow.exe, so a new vanilla game has to be told its server.
                if (Edition.Find(_updater.State.Edition) == null && _wow.ClientMajorVersion() == 1)
                {
                    ShowNeedsEdition = true;
                    SelectEdition(null);
                    _outcome = new Outcome("Which server do you play on?",
                        game.Name + " is a vanilla 1.12 client. Pick Kronos or Turtle below.", MascotMood.Idle);
                    return;
                }

                SelectEdition(_updater.ResolveEdition(null));
                await RefreshAsync(refetchSite, ct);
            });
        }

        private void CloseGame()
        {
            _updater?.Dispose();
            _updater = null;
            _wow = null;
            _catalog = null;
            _rows = new List<ModuleStatus>();
            _items = new List<ModuleItem>();
            _picks.Clear();
            ModuleList.ItemsSource = null;
            HasGame = false;
            ShowNeedsEdition = false;
            RaiseCommands();
        }

        private void SelectEdition(Edition edition)
        {
            _syncing = true;
            EditionPicker.SelectedItem = Editions.FirstOrDefault(e => e.Edition == edition);
            _syncing = false;
        }

        /// <summary>Reads the downloads page and asks the server about everything on disk.</summary>
        private async Task RefreshAsync(bool refetchSite, CancellationToken ct)
        {
            ProgressTitle = "Reading the downloads page...";
            if (refetchSite) Updater.ForgetCachedPages();
            _catalog = await _updater.FetchCatalogAsync(ct);

            ProgressTitle = "Asking the server about your patches...";
            _rows = await _updater.GetStatusAsync(_catalog, probeRemote: true, ct);
            _lastCheck = DateTime.Now;
            ShowRows();
        }

        private void ShowRows()
        {
            var items = _rows.Select(r => new ModuleItem(r, _wow.DataDir)).ToList();

            // Ticks survive a re-check, except on patches that no longer need downloading.
            _picks.RemoveWhere(id => !items.Any(i => i.CanSelect && string.Equals(i.Status.Entry.Id, id, StringComparison.OrdinalIgnoreCase)));
            foreach (var item in items)
            {
                item.IsSelected = _picks.Contains(item.Status.Entry.Id);
                item.PropertyChanged += OnPickChanged;
            }
            _items = items;

            var view = new ListCollectionView(items);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModuleItem.Group)));
            ModuleList.ItemsSource = view;
            RaiseCommands();
        }

        /// <summary>Belora's summary of where things stand.</summary>
        private void Summarize()
        {
            if (_updater == null || _currentGame == null) return;

            string when = _lastCheck == default ? string.Empty : " · checked " + _lastCheck.ToString("t", CultureInfo.CurrentCulture);
            string sub = _currentGame.Name + " · " + (_updater.Edition?.Title ?? "?") + when;
            if (_notes > 0) sub += " · " + _notes + (_notes == 1 ? " note" : " notes") + " in Activity";

            int updates = UpdateCount, untracked = UntrackedCount, installed = _rows.Count(r => r.Local != null);

            if (updates > 0)
                Say(updates == 1 ? "An update is ready for you!" : updates + " updates are ready for you!", sub, MascotMood.Excited);
            else if (untracked > 0)
                Say("I found " + (untracked == 1 ? "a patch" : untracked + " patches") + " you already have",
                    "Adopt them and I'll keep them up to date. " + sub, MascotMood.Excited);
            else if (installed == 0)
                Say("Nothing installed yet", "Tick the patches you want, or install them all with the button up top. " + sub,
                    MascotMood.Idle);
            else
                Say("Everything's up to date!", sub, MascotMood.Happy);
        }

        private void Say(string headline, string sub, MascotMood mood)
        {
            _sayCount++;
            Bubble = headline;
            BubbleSub = sub;
            Mood = mood;
        }

        // ================================================================ running work

        /// <summary>How the bubble should read after a run, when the plain summary is not enough.</summary>
        private sealed class Outcome
        {
            public readonly string Headline, Sub;
            public readonly MascotMood? Mood;

            public Outcome(string headline, string sub = null, MascotMood? mood = null)
            {
                Headline = headline;
                Sub = sub;
                Mood = mood;
            }
        }

        /// <summary>
        /// Runs one piece of work at a time with the progress card up, and turns the engine's
        /// exceptions into something Belora can say.
        /// </summary>
        private async Task RunAsync(string title, Func<CancellationToken, Task> work)
        {
            if (IsBusy) return;

            IsBusy = true;
            CanCancel = true;
            _cancel = new CancellationTokenSource();
            _outcome = null;
            _notes = 0;
            _downloading = false;
            // The progress card says what is happening, so the greeting can stay a moment longer.
            if (!_greeting) Say("On it!", title.TrimEnd('.'), MascotMood.Busy);
            ProgressTitle = title;
            ProgressDetail = string.Empty;
            ProgressIndeterminate = true;
            Taskbar.ProgressState = TaskbarItemProgressState.Indeterminate;

            try
            {
                await work(_cancel.Token);

                // A quick first check would swap the hello out before anyone reads it. The bubble
                // waits instead; the window is usable in the meantime.
                var outcome = _outcome;
                var wait = _greeting ? GreetingTime - (DateTime.Now - _openedAt) : TimeSpan.Zero;
                if (wait > TimeSpan.Zero) _ = SayOutcomeLaterAsync(outcome, wait);
                else SayOutcome(outcome);
            }
            catch (OperationCanceledException)
            {
                AddLog(UiLevel.Warn, "Stopped. Partial downloads are kept and pick up where they left off.");
                Summarize();
                Say("Okay, I stopped!", "Anything half-downloaded is kept, so next time picks up where this left off.", MascotMood.Idle);
            }
            catch (UpdaterException ex) { Oops(ex.Message); }
            catch (CatalogException ex) { Oops(ex.Message + " The downloads page may have changed; the updater needs a fix for it."); }
            catch (Exception ex) { Oops(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                _cancel.Dispose();
                _cancel = null;
                _downloading = false;
                _greeting = false;
                IsBusy = false;
                Taskbar.ProgressState = TaskbarItemProgressState.None;
            }
        }

        /// <summary>The summary of where things stand, or what the run asked to say instead.</summary>
        private void SayOutcome(Outcome outcome)
        {
            Summarize();
            if (outcome != null) Say(outcome.Headline, outcome.Sub ?? BubbleSub, outcome.Mood ?? Mood);
        }

        private async Task SayOutcomeLaterAsync(Outcome outcome, TimeSpan wait)
        {
            int said = _sayCount;
            await Task.Delay(wait);
            // If Belora said something else meanwhile (the user started another job), that wins.
            if (said == _sayCount && !IsBusy) SayOutcome(outcome);
        }

        private void Oops(string message)
        {
            AddLog(UiLevel.Error, message);
            Say("Oh no, something went wrong", message, MascotMood.Oops);
            RaiseCommands();
        }

        private async Task DownloadAsync(List<ModuleStatus> rows, string verb)
        {
            if (rows.Count == 0 || _updater == null) return;

            await RunAsync("Getting ready...", async ct =>
            {
                // One unreachable patch should not hold back the rest of a batch.
                var skipped = new List<ModuleStatus>();
                foreach (var row in rows.Where(r => r.Remote == null))
                {
                    try { row.Remote = await _updater.ProbeAsync(row.Entry.Url, ct); }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        Ui.Warn(row.Entry.Display + ": could not reach the server (" + ex.Message + ")");
                        skipped.Add(row);
                    }
                }
                if (skipped.Count == rows.Count)
                    throw new UpdaterException(rows.Count == 1
                        ? "I couldn't reach the server for " + rows[0].Entry.Display + "."
                        : "I couldn't reach the server for any of those patches.");
                rows = rows.Except(skipped).ToList();

                long total = rows.Sum(r => Math.Max(0, r.Remote.Size));
                string what = rows.Count == 1 ? rows[0].Entry.Display : rows.Count + " patches";
                string left = skipped.Count == 0 ? string.Empty
                    : "\n\nI couldn't reach the server for " + string.Join(", ", skipped.Select(r => r.Entry.Display))
                      + ", so I'll leave " + (skipped.Count == 1 ? "it" : "them") + " out this time.";

                bool go = CuteDialog.Show(this, verb + " " + what + "?",
                    "That's " + Ui.Bytes(total) + " to download into " + _wow.DataDir + ".\n\n"
                    + "If the connection drops, I'll pick up where I left off." + left,
                    verb, "Not now", MascotMood.Excited);
                if (!go) return;

                if (WowInstall.GameIsRunning() && !CuteDialog.Show(this, "Is World of Warcraft open?",
                        "The game locks its patch files while it runs, so I can't swap them. Close it first, then press Try anyway.",
                        "Try anyway", "Cancel", MascotMood.Oops))
                    return;

                ProgressTitle = "Starting the download...";
                int done = await Task.Run(() => _updater.ApplyAsync(rows, dryRun: false, hash: true, ct), ct);

                ProgressTitle = "Checking everything once more...";
                ProgressIndeterminate = true;
                await RefreshAsync(false, ct);

                _outcome = new Outcome(done == 1 ? "Yay! " + rows[0].Entry.Display + " is installed" : "Yay! " + done + " patches installed",
                                       mood: MascotMood.Happy);
            });
        }

        private async Task AdoptAsync(List<ModuleStatus> rows)
        {
            if (rows.Count == 0 || _updater == null) return;

            await RunAsync("Reading your patch files...", async ct =>
            {
                ProgressDetail = "I compare each file with the server, about a minute per 3 GB.";
                int adopted = await Task.Run(() => _updater.AdoptAsync(rows, verify: true, ct), ct);
                var ids = new HashSet<string>(rows.Select(r => r.Entry.Id), StringComparer.OrdinalIgnoreCase);
                await RefreshAsync(false, ct);

                // An adopted file that is not the published build now shows as an update.
                bool stale = _rows.Any(r => ids.Contains(r.Entry.Id) && r.NeedsDownload);
                if (adopted == 0)
                {
                    _outcome = new Outcome("Hmm, I couldn't adopt that", "The Activity log says why.", MascotMood.Oops);
                    ShowLog = true;
                }
                else
                {
                    _outcome = new Outcome(adopted == 1 ? "Adopted!" : "Adopted " + adopted + " patches!",
                        stale ? "Not everything matches the latest build, so there's an update ready."
                              : "They match the published builds, so there's nothing to download.",
                        stale ? MascotMood.Excited : MascotMood.Happy);
                }
            });
        }

        // ================================================================ progress from the engine

        private static readonly Regex StepRx = new Regex(@"^\[(\d+)/(\d+)\]\s*(.+)$");

        private void ShowProgress(string label, long done, long total, double bytesPerSecond)
        {
            if (!IsBusy) return;
            _downloading = true;

            var step = StepRx.Match(label.Trim());
            string name = step.Success ? step.Groups[3].Value.Trim() : label.Trim();
            ProgressTitle = "Downloading " + name
                            + (step.Success && step.Groups[2].Value != "1" ? "  ·  " + step.Groups[1].Value + " of " + step.Groups[2].Value : string.Empty);

            double fraction = total > 0 ? Math.Min(1, (double)done / total) : 0;
            string eta = total > 0 && bytesPerSecond > 1
                ? Ui.Duration(TimeSpan.FromSeconds((total - done) / bytesPerSecond)) + " left"
                : "working out the time...";

            ProgressIndeterminate = total <= 0;
            ProgressValue = fraction * 100;
            ProgressDetail = string.Format(CultureInfo.CurrentCulture, "{0:0}%  ·  {1} of {2}  ·  {3}/s  ·  {4}",
                fraction * 100, Ui.Bytes(done), Ui.Bytes(total), Ui.Bytes((long)bytesPerSecond), eta);

            Taskbar.ProgressState = total > 0 ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.Indeterminate;
            Taskbar.ProgressValue = fraction;
        }

        private void EndProgress()
        {
            if (!IsBusy || !_downloading) return;
            _downloading = false;
            ProgressIndeterminate = true;
            ProgressDetail = "Double-checking the download...";
            Taskbar.ProgressState = TaskbarItemProgressState.Indeterminate;
        }

        private void AddLog(UiLevel level, string text)
        {
            Log.Add(new LogLine(level, text));
            while (Log.Count > 500) Log.RemoveAt(0);
            LogList.ScrollIntoView(Log[Log.Count - 1]);

            if (level == UiLevel.Warn || level == UiLevel.Error) _notes++;

            // Between downloads, the engine's latest word is the most useful progress detail.
            if (IsBusy && !_downloading && level != UiLevel.Error) ProgressDetail = text;
        }

        // ================================================================ header and strip

        private async void OnGameChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || !(GamePicker.SelectedItem is GameChoice game)) return;
            await OpenGameAsync(game, refetchSite: false);
        }

        private async void OnEditionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _updater == null || !(EditionPicker.SelectedItem is EditionChoice choice)) return;
            await SwitchEditionAsync(choice.Name);
        }

        private async void OnPickEdition(object sender, RoutedEventArgs e) =>
            await SwitchEditionAsync((string)((FrameworkElement)sender).Tag);

        private async Task SwitchEditionAsync(string name)
        {
            await RunAsync("Switching patch sets...", async ct =>
            {
                var edition = _updater.ResolveEdition(name);
                SelectEdition(edition);
                ShowNeedsEdition = false;
                await RefreshAsync(false, ct);
            });
        }

        private async void OnCheck(object sender, RoutedEventArgs e)
        {
            if (_currentGame == null) return;
            await RunAsync("Checking for updates...", ct => RefreshAsync(refetchSite: true, ct));
        }

        private async void OnPrimary(object sender, RoutedEventArgs e)
        {
            if (HasPicks) await DownloadAsync(PickedRows(), PicksVerb);
            else if (UpdateCount > 0) await DownloadAsync(_rows.Where(r => r.Local != null && r.NeedsDownload).ToList(), "Update");
            else await InstallAllAsync();
        }

        private async void OnInstallAll(object sender, RoutedEventArgs e) => await InstallAllAsync();

        private async Task InstallAllAsync()
        {
            // A slot with several variants (patch-S) takes only its first, main one.
            var fresh = _rows.Where(r => r.State == ModuleState.NotInstalled)
                             .Where(r => ReferenceEquals(r, _rows.First(o => string.Equals(o.Entry.FileName, r.Entry.FileName,
                                                                                           StringComparison.OrdinalIgnoreCase))))
                             .ToList();
            await DownloadAsync(fresh, "Install");
        }

        private async void OnAdoptAll(object sender, RoutedEventArgs e) => await AdoptAsync(UntrackedRows());

        private async void OnAddGame(object sender, RoutedEventArgs e)
        {
            string suggestion = _currentGame != null && !_currentGame.Registered ? _currentGame.Path : UnaddedLastGame();
            var dialog = new AddGameWindow(_settings, _settingsPath, suggestion) { Owner = this };
            if (dialog.ShowDialog() != true) return;

            AddLog(UiLevel.Good, "Added " + dialog.AddedName + ".");
            await ReloadGamesAsync(_settings.FindGame(dialog.AddedName)?.Path);
        }

        private void OnToggleLog(object sender, RoutedEventArgs e) => ShowLog = !ShowLog;

        private void OnMore(object sender, RoutedEventArgs e)
        {
            bool ready = IsIdle && _updater != null && !ShowNeedsEdition;
            InstallAllItem.IsEnabled = ready && _rows.Any(r => r.State == ModuleState.NotInstalled);
            VerifyItem.IsEnabled = ready && _rows.Any(r => r.Local != null);
            OpenGameItem.IsEnabled = _wow != null;
            ChangeDataItem.IsEnabled = IsIdle && _updater != null;
            ResetDataItem.IsEnabled = IsIdle && _updater != null && _wow.DataIsCustom;
            ForgetItem.IsEnabled = IsIdle && _currentGame != null && _currentGame.Registered;
            FollowThemeItem.IsEnabled = _settings.WindowTheme != null;

            // Hang the menu below the button, right edges lined up, so it stays inside the window.
            MoreMenu.PlacementTarget = MoreButton;
            MoreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Left;
            MoreMenu.HorizontalOffset = MoreButton.ActualWidth + 10;
            MoreMenu.VerticalOffset = MoreButton.ActualHeight - 6;
            MoreMenu.IsOpen = true;
        }

        // ================================================================ cards

        private static ModuleItem ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ModuleItem;

        private async void OnModuleAction(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;

            if (item.Status.State == ModuleState.Untracked) await AdoptAsync(new List<ModuleStatus> { item.Status });
            else await DownloadAsync(new List<ModuleStatus> { item.Status }, item.Status.Local == null ? "Install" : "Update");
        }

        /// <summary>A click anywhere on a card, other than on its buttons, ticks or unticks it.</summary>
        private void OnCardClicked(object sender, MouseButtonEventArgs e)
        {
            var item = ItemOf(sender);
            if (e.Handled || item == null || !item.CanSelect || !IsIdle) return;
            item.IsSelected = !item.IsSelected;
        }

        private void OnPickChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ModuleItem.IsSelected) || !(sender is ModuleItem item)) return;

            var entry = item.Status.Entry;
            if (!item.IsSelected)
            {
                _picks.Remove(entry.Id);
                RaiseCommands();
                return;
            }

            _picks.Add(entry.Id);

            // Variants share one file (patch-S standard and standalone), so only one of them can be ticked.
            var sibling = _items.FirstOrDefault(i => i != item && i.IsSelected
                                                     && string.Equals(i.Status.Entry.FileName, entry.FileName, StringComparison.OrdinalIgnoreCase));
            if (sibling != null)
            {
                sibling.IsSelected = false;
                Say(entry.Display + " and " + sibling.Title + " share one file",
                    "Only one of them fits in " + entry.FileName + ", so I unticked " + sibling.Title + ".", MascotMood.Idle);
            }
            RaiseCommands();
        }

        private void OnClearPicks(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items.Where(i => i.IsSelected)) item.IsSelected = false;
        }

        private async void OnRemoveModule(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null || _updater == null) return;

            var entry = item.Status.Entry;
            bool ok = CuteDialog.Show(this, "Remove " + entry.Display + "?",
                item.FileInData
                    ? "This deletes " + entry.FileName + " from your Data folder. You can install it again any time."
                    : entry.FileName + " is already gone, so I'll just stop keeping track of it.",
                "Remove", "Keep it", MascotMood.Oops);
            if (!ok) return;

            await RunAsync("Removing " + entry.Display + "...", async ct =>
            {
                await Task.Run(() => _updater.Remove(entry, deleteFile: true), ct);
                AddLog(UiLevel.Info, "Removed " + entry.Display + ".");
                await RefreshAsync(false, ct);
                _outcome = new Outcome(entry.Display + " is gone", mood: MascotMood.Idle);
            });
        }

        // ================================================================ menu

        private async void OnVerifyDeep(object sender, RoutedEventArgs e)
        {
            await RunAsync("Reading every installed patch...", async ct =>
            {
                if (_catalog == null) await RefreshAsync(false, ct);

                ProgressTitle = "Reading every installed patch...";
                var rows = await _updater.GetStatusAsync(_catalog, probeRemote: true, ct);
                await Task.Run(() => _updater.VerifyDeep(rows, line => Ui.Info(line)), ct);
                await RefreshAsync(false, ct);

                int stale = UpdateCount;
                _outcome = stale == 0
                    ? new Outcome("Every file checks out!", mood: MascotMood.Happy)
                    : new Outcome(stale == 1 ? "1 file needs a fresh copy" : stale + " files need a fresh copy",
                                  "Press Update and I'll fix them.", MascotMood.Excited);
                ShowLog = true;
            });
        }

        private void OnOpenData(object sender, RoutedEventArgs e) => OpenFolder(_wow?.DataDir);
        private void OnOpenGame(object sender, RoutedEventArgs e) => OpenFolder(_wow?.Root);

        private async void OnChangeData(object sender, RoutedEventArgs e)
        {
            if (_updater == null) return;
            string picked = FolderPicker.Pick(this, "Where should " + _currentGame.Name + "'s patches go?", _wow.DataDir);
            if (picked == null) return;

            try
            {
                var target = _wow.WithData(picked);
                _updater.State.DataPath = target.DataIsCustom ? target.DataDir : null;
                _updater.Save();
                AddLog(UiLevel.Good, "Data folder for " + _currentGame.Name + " set to " + target.DataDir + ".");
            }
            catch (UpdaterException ex)
            {
                CuteDialog.Show(this, "That folder won't work", ex.Message, "OK", null, MascotMood.Oops);
                return;
            }

            await OpenGameAsync(_currentGame, refetchSite: false);
        }

        private async void OnResetData(object sender, RoutedEventArgs e)
        {
            if (_updater == null) return;
            _updater.State.DataPath = null;
            _updater.Save();
            AddLog(UiLevel.Good, "Data folder for " + _currentGame.Name + " is back to " + Path.Combine(_wow.Root, "Data") + ".");
            await OpenGameAsync(_currentGame, refetchSite: false);
        }

        private async void OnForgetGame(object sender, RoutedEventArgs e)
        {
            var game = _currentGame;
            if (game == null || !game.Registered) return;

            bool ok = CuteDialog.Show(this, "Forget " + game.Name + "?",
                "I'll stop looking after it. Nothing in " + game.Path + " is touched, and you can add it again later.",
                "Forget it", "Keep it", MascotMood.Oops);
            if (!ok) return;

            var entry = _settings.FindGame(game.Name);
            if (entry != null) _settings.Games.Remove(entry);
            if (SamePath(_settings.WowPath, game.Path)) _settings.WowPath = null;
            Store.Save(_settingsPath, _settings);
            AddLog(UiLevel.Info, "Forgot " + game.Name + ".");

            await ReloadGamesAsync(null);
        }

        private void OnOpenSite(object sender, RoutedEventArgs e) =>
            Launch(_updater?.CatalogUrl ?? Catalog.DefaultUrl);

        /// <summary>The command-line updater lives beside this exe and shares its settings.</summary>
        private void OnOpenCli(object sender, RoutedEventArgs e)
        {
            string folder = AppDomain.CurrentDomain.BaseDirectory;
            string cli = Path.Combine(folder, "ReforgedUpdater.exe");
            if (!File.Exists(cli))
            {
                CuteDialog.Show(this, "The command-line updater isn't here",
                    "Put ReforgedUpdater.exe next to this app and I'll open it for you. Build it with:  dotnet build -c Release",
                    "OK", null, MascotMood.Oops);
                return;
            }

            string game = _currentGame != null && _currentGame.Registered ? " --game " + _currentGame.Name : string.Empty;
            Launch("cmd.exe", "/k \"title Reforged Updater & \"" + cli + "\" status" + game + " & echo. & echo Type ReforgedUpdater help for every command.\"",
                   folder);
        }

        // ================================================================ theme

        /// <summary>Picks the palette from the saved choice, or from Windows when there is none.</summary>
        private void ApplyTheme()
        {
            bool dark = Themes.WantsDark(_settings.WindowTheme);
            if (dark != Themes.IsDark) Themes.Apply(dark);
            NativeMethods.StyleFrame(this, dark);

            ThemeButton.Content = dark ? "\uE706" : "\uE708"; // sun, moon
            string other = dark ? "Light mode" : "Dark mode";
            ThemeButton.ToolTip = other;
            System.Windows.Automation.AutomationProperties.SetName(ThemeButton, other);

            // Cards and log lines keep the brushes they were made with; remake them in the new colours.
            if (_wow != null) ShowRows();
            var lines = Log.ToList();
            Log.Clear();
            foreach (var line in lines) Log.Add(line.Repainted());
        }

        private void OnToggleTheme(object sender, RoutedEventArgs e)
        {
            _settings.WindowTheme = Themes.IsDark ? Themes.Light : Themes.Dark;
            Store.Save(_settingsPath, _settings);
            ApplyTheme();
        }

        private void OnFollowWindowsTheme(object sender, RoutedEventArgs e)
        {
            _settings.WindowTheme = null;
            Store.Save(_settingsPath, _settings);
            ApplyTheme();
        }

        private void OnWindowsSettingsChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // Windows reports a light/dark switch as a General preference change.
            if (e.Category == UserPreferenceCategory.General && _settings.WindowTheme == null)
                Dispatcher.BeginInvoke(new Action(ApplyTheme));
        }

        // ================================================================ window chrome

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnMaximize(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        /// <summary>A chromeless window overhangs the screen when maximized; pull the content back in.</summary>
        private void FitMaximized()
        {
            bool maximized = WindowState == WindowState.Maximized;
            var border = SystemParameters.WindowResizeBorderThickness;
            Frame.Margin = maximized
                ? new Thickness(border.Left + 4, border.Top + 4, border.Right + 4, border.Bottom + 4)
                : new Thickness(0);
            MaxButton.Content = maximized ? "" : "";
            MaxButton.ToolTip = maximized ? "Restore" : "Maximize";
        }

        private void OnScrollerSized(object sender, SizeChangedEventArgs e)
        {
            // Cards share the row evenly: as many ~330px columns as fit.
            double available = e.NewSize.Width - Scroller.Padding.Left - Scroller.Padding.Right - 10;
            int columns = Math.Max(1, (int)(available / 330));
            CardWidth = Math.Floor(available / columns);
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            CanCancel = false;
            ProgressTitle = "Stopping...";
            _cancel?.Cancel();
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!IsBusy || !_downloading) return;

            bool stop = CuteDialog.Show(this, "Stop downloading?",
                "What's downloaded so far is kept, and next time picks up where this left off.",
                "Stop and close", "Keep going", MascotMood.Oops);
            if (!stop) { e.Cancel = true; return; }
            _cancel?.Cancel();
        }

        // ================================================================ helpers

        /// <summary>
        /// A game folder the command line used last (with --wow or path) that was never added,
        /// so the Add dialog can offer it instead of the user browsing for it again.
        /// </summary>
        private string UnaddedLastGame()
        {
            string last = _settings.WowPath;
            return WowInstall.LooksLikeWowFolder(last) && _settings.FindGameByPath(last) == null ? last : null;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private void OpenFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            Launch("explorer.exe", "\"" + folder + "\"");
        }

        private void Launch(string file, string arguments = null, string workingDirectory = null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(file, arguments ?? string.Empty)
                {
                    UseShellExecute = true,
                    WorkingDirectory = workingDirectory ?? string.Empty
                });
            }
            catch (Exception ex)
            {
                AddLog(UiLevel.Error, "Could not open " + file + ": " + ex.Message);
            }
        }
    }
}
