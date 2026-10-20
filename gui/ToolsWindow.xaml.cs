using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace ReforgedUpdater.Gui
{
    /// <summary>
    /// The extra downloads beside the patches: the Wow.exe patcher, DXVK, the fog and the fonts.
    /// Installing and updating happen here with their own progress; the patcher is only ever
    /// opened by a click on its button, and it is the user who drives it.
    /// </summary>
    public partial class ToolsWindow : Window
    {
        private static readonly Regex StepRx = new Regex(@"^\[(\d+)/(\d+)\]\s*(.+)$");

        private readonly ToolKit _kit;
        private readonly IReadOnlyList<ToolDef> _tools;
        private CancellationTokenSource _cancel;
        private IUiSink _previousSink;
        private bool _busy;
        private bool _patcherOpened;
        private readonly List<string> _errors = new List<string>();

        internal ToolsWindow(Updater updater, string gameName)
        {
            InitializeComponent();
            _kit = updater.Tools;
            _tools = ToolKit.For(updater.Edition);
            IntoText.Text = "For " + gameName + ". Downloaded from GitHub; you run the patcher yourself.";

            Loaded += async (s, e) =>
            {
                // The engine reports to whichever sink is set. While this window is up it is this one,
                // so the main window's activity log does not fill with this window's progress.
                _previousSink = Ui.Sink;
                Ui.Sink = new WindowSink(Dispatcher, OnEngineMessage, OnEngineProgress, OnEngineEnd);
                await RefreshAsync(false);
            };
            Closed += (s, e) => Ui.Sink = _previousSink;

            // Coming back from the patcher: look at Wow.exe again.
            Activated += async (s, e) =>
            {
                if (!_patcherOpened || _busy) return;
                _patcherOpened = false;
                await RefreshAsync(false);
            };
        }

        // ================================================================ state

        private void UpdateEnabled()
        {
            ToolList.IsEnabled = !_busy;
            RefreshButton.IsEnabled = !_busy;
            CloseButton.IsEnabled = !_busy;
        }

        private void ShowError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (!_errors.Contains(message)) _errors.Add(message);
            while (_errors.Count > 3) _errors.RemoveAt(0);
            ErrorText.Text = string.Join("\n", _errors);
            ErrorBox.Visibility = Visibility.Visible;
            Buddy.Mood = MascotMood.Oops;
        }

        private void ClearError()
        {
            _errors.Clear();
            ErrorBox.Visibility = Visibility.Collapsed;
        }

        private async Task RefreshAsync(bool forgetReleases)
        {
            if (_busy) return;
            if (forgetReleases) ToolKit.ForgetCachedReleases();

            _busy = true;
            UpdateEnabled();
            LoadingText.Visibility = Visibility.Visible;
            try
            {
                var rows = await Task.Run(() => _kit.GetStatusAsync(_tools, true, CancellationToken.None));
                ToolList.ItemsSource = rows.Select(r => new ToolItem(r)).ToList();

                // A GitHub problem (rate limit, no connection) is one message, not one per tool.
                foreach (string why in rows.Where(r => r.Error != null).Select(r => r.Error).Distinct())
                    ShowError(why);
            }
            catch (UpdaterException ex) { ShowError(ex.Message); }
            finally
            {
                _busy = false;
                UpdateEnabled();
                LoadingText.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>Runs one job with the progress strip showing, then looks at everything again.</summary>
        private async Task WorkAsync(string title, Func<CancellationToken, Task> work)
        {
            if (_busy) return;

            ClearError();
            StatusText.Text = string.Empty;
            _busy = true;
            UpdateEnabled();
            ProgressBox.Visibility = Visibility.Visible;
            ProgressTitle.Text = title;
            ProgressDetail.Text = string.Empty;
            Bar.IsIndeterminate = true;
            StopButton.IsEnabled = true;
            Buddy.Mood = MascotMood.Busy;
            _cancel = new CancellationTokenSource();

            try { await work(_cancel.Token); }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Stopped. Nothing half-installed is left behind.";
                Buddy.Mood = MascotMood.Idle;
            }
            catch (UpdaterException ex) { ShowError(ex.Message); }
            catch (Exception ex) { ShowError(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                _cancel.Dispose();
                _cancel = null;
                _busy = false;
                ProgressBox.Visibility = Visibility.Collapsed;
                UpdateEnabled();
            }

            await RefreshAsync(false);
        }

        // ================================================================ from the engine

        private void OnEngineMessage(UiLevel level, string text)
        {
            switch (level)
            {
                case UiLevel.Error:
                    ShowError(text);
                    break;
                case UiLevel.Warn:
                    StatusText.Text = text;
                    break;
                case UiLevel.Good:
                    StatusText.Text = text.Trim();
                    ProgressDetail.Text = text.Trim();
                    break;
                default:
                    if (_busy) ProgressDetail.Text = text;
                    break;
            }
        }

        private void OnEngineProgress(string label, long done, long total, double bytesPerSecond)
        {
            if (!_busy) return;
            var step = StepRx.Match(label.Trim());
            string name = step.Success ? step.Groups[3].Value.Trim() : label.Trim();
            ProgressTitle.Text = "Downloading " + name
                                 + (step.Success && step.Groups[2].Value != "1" ? "  ·  " + step.Groups[1].Value + " of " + step.Groups[2].Value : string.Empty);

            double fraction = total > 0 ? Math.Min(1, (double)done / total) : 0;
            Bar.IsIndeterminate = total <= 0;
            Bar.Value = fraction * 100;
            ProgressDetail.Text = string.Format(CultureInfo.CurrentCulture, "{0:0}%  ·  {1} of {2}", fraction * 100, Ui.Bytes(done), Ui.Bytes(total));
        }

        private void OnEngineEnd()
        {
            if (!_busy) return;
            Bar.IsIndeterminate = true;
            ProgressDetail.Text = "Checking the download...";
        }

        // ================================================================ buttons

        private static ToolItem ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ToolItem;

        private async void OnInstall(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;
            var row = item.Status;

            if (WowInstall.GameIsRunning() && !CuteDialog.Show(this, "Is World of Warcraft open?",
                    "The game locks its DLLs while it runs, so I can't replace them. Close it first, then press Try anyway.",
                    "Try anyway", "Cancel", MascotMood.Oops))
                return;

            if (row.Local == null && !CuteDialog.Show(this, "Install " + row.Def.Name + "?",
                    row.Def.Summary + "\n\n" + row.Def.Warning + "\n\nIt comes from " + row.Def.RepoUrl + ".",
                    "Install", "Not now", MascotMood.Idle))
                return;

            await WorkAsync("Getting ready...", async ct =>
            {
                int done = await Task.Run(() => _kit.InstallAsync(new List<ToolStatus> { row }, ct), ct);
                if (done == 0) return;

                Buddy.Mood = MascotMood.Happy;
                StatusText.Text = row.Def == ToolDef.Patcher
                    ? "Installed. Next, close World of Warcraft and click Run patcher. Press R in its menu for the Reforged preset."
                    : "Installed " + row.Def.Name + ".";
            });
        }

        private void OnRunPatcher(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            ClearError();

            string exe;
            try { exe = _kit.CheckCanRunPatcher(); }
            catch (UpdaterException ex) { ShowError(ex.Message); return; }

            if (!CuteDialog.Show(this, "Open the patcher?",
                    "It opens in its own window on this game's Wow.exe:\n" + exe
                    + "\n\nYou choose what it changes in its menu; press R for the Reforged preset. Some of its patches can get you "
                    + "banned on some servers, and some load DLLs that servers block. Close World of Warcraft first. "
                    + "I won't select or confirm anything for you.",
                    "Open the patcher", "Not now", MascotMood.Idle))
                return;

            try
            {
                _kit.StartPatcher(false)?.Dispose();
                _patcherOpened = true;
                StatusText.Text = "The patcher is open in its own window. When you're done there, come back here and I'll look at Wow.exe again.";
            }
            catch (UpdaterException ex) { ShowError(ex.Message); }
            catch (Win32Exception ex) { ShowError("Could not start the patcher: " + ex.Message); }
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;
            Open("explorer.exe", "\"" + _kit.ToolDir(item.Status.Def) + "\"");
        }

        private void OnOpenSettings(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;
            string path = _kit.SettingsPath(item.Status.Def);
            if (path == null || !System.IO.File.Exists(path))
            {
                ShowError("The settings file isn't there yet. " + item.Title + " creates it the first time the game runs with it.");
                return;
            }
            Open(path, null);
        }

        private void OnRepository(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item != null) Open(item.RepoUrl, null);
        }

        private async void OnPreset(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;
            var def = item.Status.Def;

            await WorkAsync("Getting the recommended settings...", async ct =>
            {
                var plan = await Task.Run(() => _kit.PlanPresetAsync(def, ct), ct);

                if (plan.Identical)
                {
                    _kit.AcknowledgePreset(def, plan);
                    StatusText.Text = "Your " + def.SettingsFile + " already matches the recommended settings.";
                    Buddy.Mood = MascotMood.Happy;
                    return;
                }

                string what = !plan.FileExists
                    ? def.SettingsFile + " isn't in the game folder yet, so I'd create it."
                    : plan.Differences.Count + (plan.Differences.Count == 1 ? " of your settings differs" : " of your settings differ") + ":\n"
                      + string.Join("\n", plan.Differences.Take(10)) + (plan.Differences.Count > 10 ? "\n...and " + (plan.Differences.Count - 10) + " more" : string.Empty);

                bool use = CuteDialog.Show(this, "Use the recommended settings?",
                    what + "\n\nA copy of your current file is kept next to it as .bak.", "Use recommended", "Keep mine", MascotMood.Idle);

                if (use)
                {
                    string backup = await Task.Run(() => _kit.ApplyPreset(def, plan), ct);
                    StatusText.Text = "Applied the recommended settings." + (backup != null ? " Your old file is " + System.IO.Path.GetFileName(backup) + "." : string.Empty);
                    Buddy.Mood = MascotMood.Happy;
                }
                else
                {
                    _kit.AcknowledgePreset(def, plan);
                    StatusText.Text = "Kept yours. I won't mention it again until the recommended settings change.";
                }
            });
        }

        private async void OnRemove(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;
            var def = item.Status.Def;

            if (!CuteDialog.Show(this, "Remove " + def.Name + "?",
                    "I'll delete the files I put in the game folder. Settings files and anything you changed stay.",
                    "Remove", "Keep it", MascotMood.Idle))
                return;

            await WorkAsync("Removing " + def.Name + "...", async ct =>
            {
                var left = await Task.Run(() => _kit.Remove(def), ct);
                StatusText.Text = "Removed " + def.Name + "." + (left.Count > 0 ? " Left in place: " + string.Join("; ", left) + "." : string.Empty);
            });
        }

        private async void OnRefresh(object sender, RoutedEventArgs e)
        {
            ClearError();
            StatusText.Text = string.Empty;
            await RefreshAsync(true);
        }

        private void OnStop(object sender, RoutedEventArgs e)
        {
            StopButton.IsEnabled = false;
            ProgressTitle.Text = "Stopping...";
            _cancel?.Cancel();
        }

        private void OnDone(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(CancelEventArgs e)
        {
            // A job is writing to the game folder; let it finish or be stopped first.
            if (_busy)
            {
                e.Cancel = true;
                StatusText.Text = "Press Stop first, or wait for this to finish.";
                return;
            }
            base.OnClosing(e);
        }

        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Open(string file, string arguments)
        {
            try { Process.Start(new ProcessStartInfo(file, arguments ?? string.Empty) { UseShellExecute = true }); }
            catch (Exception ex) { ShowError("Could not open " + file + ": " + ex.Message); }
        }
    }
}
