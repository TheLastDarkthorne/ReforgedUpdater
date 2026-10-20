using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgedUpdater
{
    /// <summary>The "tools" command: the extra downloads (patcher, DXVK, fog, fonts) that sit beside the .mpq patches.</summary>
    internal static class ToolsCommand
    {
        private const int ExitOk = 0;
        private const int ExitError = 1;
        private const int ExitUpdatesAvailable = 2;

        public static async Task<int> RunAsync(CommandLine cli, Updater updater, WowInstall wow, CancellationToken ct)
        {
            var tools = ToolKit.For(updater.Edition);
            if (tools.Count == 0)
            {
                Ui.Info(updater.Edition.Title + " has no extra tools that this updater manages yet. "
                        + "The downloads page lists some; install those by hand.");
                return ExitOk;
            }

            var kit = updater.Tools;
            string action = cli.Values.Count > 0 ? cli.Values[0].ToLowerInvariant() : "status";
            var ids = cli.Values.Skip(1).ToList();

            switch (action)
            {
                case "status":
                case "list":
                    return await Status(cli, kit, tools, ct).ConfigureAwait(false);
                case "install":
                case "add":
                    return await Install(cli, kit, tools, ids, update: false, ct).ConfigureAwait(false);
                case "update":
                    return await Install(cli, kit, tools, ids, update: true, ct).ConfigureAwait(false);
                case "remove":
                case "uninstall":
                    return Remove(cli, kit, tools, ids);
                case "open":
                    return Open(kit, tools, ids);
                case "run":
                    return Run(cli, kit, tools, ids);
                case "preset":
                    return await Preset(cli, kit, tools, ids, ct).ConfigureAwait(false);
                default:
                    Ui.Error("Unknown tools action: " + action);
                    Ui.Info("Use one of: status, install, update, remove, open, run, preset.   Example:  ReforgedUpdater tools install patcher dxvk fog lexara");
                    return ExitError;
            }
        }

        // ---------------------------------------------------------------- status

        private static async Task<int> Status(CommandLine cli, ToolKit kit, IReadOnlyList<ToolDef> tools, CancellationToken ct)
        {
            var rows = await kit.GetStatusAsync(tools, checkRemote: !cli.Flag("fast"), ct).ConfigureAwait(false);
            Print(rows);

            if (rows.Any(r => r.Local == null))
                Ui.Info("Install with:  ReforgedUpdater tools install " + string.Join(" ", rows.Where(r => r.Local == null).Select(r => r.Def.Id)));
            if (rows.Any(r => r.Local != null && r.NeedsInstall))
                Ui.Info("Update with:   ReforgedUpdater tools update");

            return rows.Any(r => r.Local != null && r.NeedsInstall) ? ExitUpdatesAvailable : ExitOk;
        }

        private static void Print(List<ToolStatus> rows)
        {
            Ui.Rule("Tools");
            Ui.Info(string.Format(CultureInfo.InvariantCulture, "  {0,-9} {1,-14} {2,-14} {3}", "TOOL", "INSTALLED", "LATEST", "STATE"));
            foreach (var row in rows)
            {
                string line = string.Format(CultureInfo.InvariantCulture, "  {0,-9} {1,-14} {2,-14} {3}",
                    row.Def.Id, Trim(row.LocalVersion, 14), Trim(row.RemoteVersion, 14), row.Detail);
                if (row.State == ToolState.UpToDate) Ui.Good(line);
                else if (row.State == ToolState.UpdateAvailable || row.State == ToolState.Damaged) Ui.Head(line);
                else Ui.Info(line);
                Ui.Info("      " + row.Def.Name);
                foreach (string note in row.Notes) Ui.Info("      * " + note);
            }
            Ui.Info(string.Empty);

            // The same GitHub problem hits every tool; say it once.
            foreach (string error in rows.Where(r => r.Error != null).Select(r => r.Error).Distinct())
                Ui.Warn(error);
        }

        private static string Trim(string value, int width) => value.Length <= width ? value : value.Substring(0, width - 1) + "~";

        /// <summary>
        /// A few lines for "status": only the tools already installed are looked up, so a plain status
        /// never spends GitHub's hourly allowance on tools the user has not asked for. Returns true when one has an update.
        /// </summary>
        public static async Task<bool> SummaryAsync(Updater updater, bool checkRemote, CancellationToken ct)
        {
            var tools = ToolKit.For(updater.Edition);
            if (tools.Count == 0) return false;

            var installed = tools.Where(t => updater.Tools.IsInstalled(t)).ToList();
            if (installed.Count == 0)
            {
                Ui.Info("Extra tools (Wow.exe patcher, DXVK, fog, fonts) are not installed. See them with:  ReforgedUpdater tools");
                return false;
            }

            List<ToolStatus> rows;
            try { rows = await updater.Tools.GetStatusAsync(installed, checkRemote, ct).ConfigureAwait(false); }
            catch (UpdaterException ex) { Ui.Warn(ex.Message); return false; }

            Print(rows);
            bool pending = rows.Any(r => r.NeedsInstall);
            if (pending) Ui.Info("Update them with:  ReforgedUpdater tools update");
            return pending;
        }

        /// <summary>Called by "update": brings the tools already installed up to date, after the patches.</summary>
        public static async Task<int> UpdateInstalledAsync(CommandLine cli, Updater updater, CancellationToken ct)
        {
            var tools = ToolKit.For(updater.Edition);
            var installed = tools.Where(t => updater.Tools.IsInstalled(t)).ToList();
            if (installed.Count == 0 || cli.Flag("fast")) return ExitOk;

            Ui.Info(string.Empty);
            try
            {
                var rows = await updater.Tools.GetStatusAsync(installed, checkRemote: true, ct).ConfigureAwait(false);
                foreach (string error in rows.Where(r => r.Error != null).Select(r => r.Error).Distinct())
                    Ui.Warn(error);

                // A tool that could not be looked up can't be updated now; asking first would only lead to a failure.
                var chosen = rows.Where(r => r.Remote != null && r.NeedsInstall).ToList();
                if (chosen.Count == 0)
                {
                    if (rows.Any(r => r.Remote == null)) return ExitError;
                    Ui.Good("The extra tools are up to date too.");
                    foreach (var row in rows.Where(r => r.Notes.Count > 0))
                        foreach (string note in row.Notes.Where(n => !n.StartsWith("Wow.exe has", StringComparison.Ordinal)))
                            Ui.Info("  " + row.Def.Name + ": " + note);
                    return ExitOk;
                }
                return await InstallRows(cli, updater.Tools, chosen, "Update tools", ct).ConfigureAwait(false);
            }
            catch (UpdaterException ex)
            {
                Ui.Warn(ex.Message);
                return ExitError;
            }
        }

        // ---------------------------------------------------------------- install and update

        private static List<ToolDef> Resolve(IReadOnlyList<ToolDef> tools, List<string> ids)
        {
            var result = new List<ToolDef>();
            foreach (string id in ids)
            {
                var def = ToolKit.Find(tools, id)
                          ?? throw new UpdaterException("Unknown tool \"" + id + "\". The tools are: " + string.Join(", ", tools.Select(t => t.Id)) + ".");
                if (!result.Contains(def)) result.Add(def);
            }
            return result;
        }

        private static async Task<int> Install(CommandLine cli, ToolKit kit, IReadOnlyList<ToolDef> tools, List<string> ids,
                                               bool update, CancellationToken ct)
        {
            List<ToolDef> picked;
            if (ids.Count > 0) picked = Resolve(tools, ids);
            else if (update) picked = tools.Where(kit.IsInstalled).ToList();
            else if (cli.Flag("all")) picked = tools.Where(t => !kit.IsInstalled(t)).ToList();
            else
            {
                Ui.Error("Name at least one tool, or pass --all.  Example: ReforgedUpdater tools install patcher dxvk fog lexara");
                Ui.Info("Tools: " + string.Join(", ", tools.Select(t => t.Id)));
                return ExitError;
            }

            if (picked.Count == 0) { Ui.Info("No tools are installed yet.  Install with:  ReforgedUpdater tools install --all"); return ExitOk; }

            var rows = await kit.GetStatusAsync(picked, checkRemote: true, ct).ConfigureAwait(false);
            if (rows.Any(r => r.Remote == null))
            {
                foreach (string error in rows.Where(r => r.Remote == null).Select(r => r.Error ?? "could not be checked").Distinct())
                    Ui.Error(error);
                return ExitError;
            }

            var chosen = new List<ToolStatus>();
            foreach (var row in rows)
            {
                if (update && row.Local == null) { Ui.Warn(row.Def.Name + " is not installed. Use \"tools install " + row.Def.Id + "\"."); continue; }
                if (!row.NeedsInstall) { Ui.Info(row.Def.Name + " is already installed and current - skipping."); continue; }
                chosen.Add(row);
            }

            if (chosen.Count == 0) { Ui.Good("Nothing to " + (update ? "update" : "install") + "."); return ExitOk; }
            return await InstallRows(cli, kit, chosen, update ? "Update tools" : "Install tools", ct).ConfigureAwait(false);
        }

        /// <summary>Lists what will happen, asks, and installs. Shared with "update".</summary>
        private static async Task<int> InstallRows(CommandLine cli, ToolKit kit, List<ToolStatus> chosen, string verb, CancellationToken ct)
        {
            Ui.Rule(verb);
            long total = 0;
            foreach (var row in chosen)
            {
                total += row.DownloadSize;
                Ui.Info(string.Format(CultureInfo.InvariantCulture, "  {0,-9} {1,-14} {2,10}   {3}",
                    row.Def.Id, row.RemoteVersion, Ui.Bytes(row.DownloadSize),
                    row.Local == null ? "new" : row.LocalVersion == row.RemoteVersion ? row.Detail : "from " + row.LocalVersion));
                Ui.Info("      " + row.Def.Name);
                Ui.Info("      " + row.Def.Summary);
                Ui.Info("      ! " + row.Def.Warning);
                if (row.Def.Folder != null) Ui.Info("      goes in: " + row.Def.Folder + "\\ inside the game folder");
            }
            Ui.Info("  " + chosen.Count + " tool(s), " + Ui.Bytes(total) + " to download from GitHub.");
            Ui.Info("  These are other people's tools. The updater checks each download against the checksum GitHub lists,");
            Ui.Info("  never replaces a file it did not place, and never runs any of them.");
            Ui.Info(string.Empty);

            if (cli.Flag("dry-run")) return ExitOk;

            if (!Ui.Confirm("Continue?", cli.Flag("yes") || cli.Flag("y")))
            {
                Ui.Info("Nothing was changed.");
                return ExitOk;
            }

            int done = await kit.InstallAsync(chosen, ct).ConfigureAwait(false);
            Ui.Info(string.Empty);
            Ui.Good(verb + " complete: " + done + " of " + chosen.Count + " tool(s).");

            if (chosen.Any(r => r.Def == ToolDef.Patcher) && kit.IsInstalled(ToolDef.Patcher))
            {
                Ui.Info(string.Empty);
                Ui.Head("Next step, yours to do:");
                Ui.Info("  Close World of Warcraft, then run the patcher and press R for the Reforged preset:");
                Ui.Info("      ReforgedUpdater tools run patcher");
            }

            return done == chosen.Count ? ExitOk : ExitError;
        }

        // ---------------------------------------------------------------- remove, open, run

        private static int Remove(CommandLine cli, ToolKit kit, IReadOnlyList<ToolDef> tools, List<string> ids)
        {
            if (ids.Count == 0) { Ui.Error("Name the tool to remove.  Example: ReforgedUpdater tools remove dxvk"); return ExitError; }

            int failed = 0;
            foreach (var def in Resolve(tools, ids))
            {
                if (!kit.IsInstalled(def)) { Ui.Info(def.Name + " is not installed by this updater."); continue; }
                if (!Ui.Confirm("Remove " + def.Name + " from this game?", cli.Flag("yes") || cli.Flag("y"))) { Ui.Info("Kept " + def.Name + "."); continue; }

                try
                {
                    var left = kit.Remove(def);
                    Ui.Good("Removed " + def.Name + ".");
                    foreach (string item in left) Ui.Info("  left in place: " + item);
                }
                catch (UpdaterException ex) { Ui.Error(ex.Message); failed++; }
            }
            return failed == 0 ? ExitOk : ExitError;
        }

        private static int Open(ToolKit kit, IReadOnlyList<ToolDef> tools, List<string> ids)
        {
            var def = ids.Count == 0 ? ToolDef.Patcher : Resolve(tools, ids)[0];
            string folder = kit.ToolDir(def);
            if (!Directory.Exists(folder)) throw new UpdaterException(def.Name + " is not installed in this game.");
            Process.Start("explorer.exe", "\"" + folder + "\"");
            return ExitOk;
        }

        private static int Run(CommandLine cli, ToolKit kit, IReadOnlyList<ToolDef> tools, List<string> ids)
        {
            if (ids.Count > 0 && Resolve(tools, ids)[0] != ToolDef.Patcher)
                throw new UpdaterException("Only the patcher is something you run. The other tools load by themselves when the game starts.");

            string exe = kit.CheckCanRunPatcher();

            Ui.Head("This opens the patcher on " + exe);
            Ui.Info("  It edits Wow.exe. You choose what it changes in its own menu; press R for the Reforged preset.");
            Ui.Info("  Some of its patches can get you banned on some servers, and some load DLLs that servers block.");
            Ui.Info("  Close World of Warcraft first. The updater does not select or confirm anything for you.");
            if (!Ui.Confirm("Open the patcher?", cli.Flag("yes") || cli.Flag("y")))
            {
                Ui.Info("Nothing was started.");
                return ExitOk;
            }

            using (var process = kit.StartPatcher(inThisConsole: true))
            {
                process.WaitForExit();
                Ui.Info(string.Empty);

                var marks = kit.ReadExeMarks();
                if (marks != null)
                    Ui.Info(marks.Patched ? "Wow.exe has been patched." : "Wow.exe has not been patched.");
                if (marks != null && kit.IsInstalled(ToolDef.Lexara) && !marks.LoadsLexara)
                    Ui.Warn("Lexara is installed, but Wow.exe does not load it. Run the patcher again with patch No. 30 selected.");

                return process.ExitCode == 0 ? ExitOk : ExitError;
            }
        }

        // ---------------------------------------------------------------- recommended settings

        private static async Task<int> Preset(CommandLine cli, ToolKit kit, IReadOnlyList<ToolDef> tools, List<string> ids, CancellationToken ct)
        {
            var def = ids.Count == 0 ? ToolDef.Fog : Resolve(tools, ids)[0];
            var plan = await kit.PlanPresetAsync(def, ct).ConfigureAwait(false);

            if (plan.Identical)
            {
                kit.AcknowledgePreset(def, plan);
                Ui.Good("Your " + def.SettingsFile + " already matches the Project Reforged recommended settings.");
                return ExitOk;
            }

            Ui.Rule("Recommended settings for " + def.Name);
            if (!plan.FileExists) Ui.Info("  " + def.SettingsFile + " is not in the game folder; it will be created.");
            else
            {
                Ui.Info("  " + plan.Differences.Count + " setting(s) in " + plan.FilePath + " differ from the recommended ones:");
                foreach (string line in plan.Differences.Take(30)) Ui.Info("      " + line);
                if (plan.Differences.Count > 30) Ui.Info("      ... and " + (plan.Differences.Count - 30) + " more");
            }
            Ui.Info(string.Empty);

            if (cli.Flag("dry-run")) return ExitOk;

            if (!Ui.Confirm("Replace it with the recommended settings? (a copy of yours is kept next to it as .bak)", cli.Flag("yes") || cli.Flag("y")))
            {
                kit.AcknowledgePreset(def, plan);
                Ui.Info("Kept yours. You won't be reminded until the recommended settings change again.");
                return ExitOk;
            }

            string backup = kit.ApplyPreset(def, plan);
            Ui.Good("Applied the recommended settings." + (backup != null ? " Your old file is " + backup : string.Empty));
            return ExitOk;
        }
    }
}
