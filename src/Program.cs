using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgedUpdater
{
    internal static class Program
    {
        private const int ExitOk = 0;
        private const int ExitError = 1;
        private const int ExitUpdatesAvailable = 2;

        private static int Main(string[] args)
        {
            try
            {
                return RunAsync(args).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Ui.EndProgress();
                Ui.Warn("Cancelled. Partial downloads are kept and will resume next time. A patch that was being copied is dropped.");
                return ExitError;
            }
            catch (UpdaterException ex) { Ui.EndProgress(); Ui.Error(ex.Message); return ExitError; }
            catch (CatalogException ex)
            {
                Ui.EndProgress();
                Ui.Error(ex.Message);
                Ui.Info("The updater reads the public downloads page; if the site was redesigned, the parser needs an update.");
                return ExitError;
            }
            catch (Exception ex) { Ui.EndProgress(); Ui.Error(ex.GetType().Name + ": " + ex.Message); return ExitError; }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            var cli = CommandLine.Parse(args);

            Ui.UseColor = !cli.NoColor;
            // JSON goes to stdout, so the human-readable chatter is silenced to keep it parseable.
            Ui.Quiet = cli.Flag("quiet") || cli.Flag("q") || cli.Flag("json");

            if (cli.Command == "help" || cli.Flag("help") || cli.Flag("h") || cli.Flag("?"))
            {
                PrintHelp();
                return ExitOk;
            }

            using (var cancellation = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cancellation.Cancel(); };

                string settingsPath = Workspace.SettingsPath();
                var settings = Store.Load<Settings>(settingsPath);
                Workspace.MigrateLegacyDataPath(settings, settingsPath);

                if (cli.Command == "games") return ManageGames(cli, settings, settingsPath);
                if (cli.Flag("all-games")) return await RunAllGamesAsync(cli, settings, cancellation.Token).ConfigureAwait(false);
                if (cli.Command == "path") return SetPath(cli, settings, settingsPath);

                var wow = ResolveInstall(cli, settings, out string gameName);
                if (!string.Equals(settings.WowPath, wow.Root, StringComparison.OrdinalIgnoreCase))
                {
                    settings.WowPath = wow.Root;
                    Store.Save(settingsPath, settings);
                }

                return await RunForGameAsync(cli, settings, wow, gameName, cancellation.Token).ConfigureAwait(false);
            }
        }

        /// <summary>Runs one command against one game folder.</summary>
        private static async Task<int> RunForGameAsync(CommandLine cli, Settings settings, WowInstall wow,
                                                       string gameName, CancellationToken ct)
        {
            using (var updater = new Updater(wow, settings))
            {
                if (cli.Command == "edition") return SetEdition(cli, updater, wow);
                if (cli.Command == "data") return SetData(cli, updater, wow);

                var edition = updater.ResolveEdition(cli.Option("edition"));

                if (gameName != null) Ui.Info("Game:    " + gameName);
                Ui.Info("Client:  " + wow.Root);
                Ui.Info("Data:    " + wow.DataDir + (wow.DataIsCustom ? "   (custom)" : string.Empty));
                Ui.Info("Edition: " + edition.Title);
                Ui.Info("Catalog: " + updater.CatalogUrl);
                Ui.Info(string.Empty);

                var catalog = await updater.FetchCatalogAsync(ct).ConfigureAwait(false);

                switch (cli.Command)
                {
                    case "list":
                        PrintCatalog(catalog);
                        return ExitOk;

                    case "status":
                        return await Status(cli, updater, catalog, ct).ConfigureAwait(false);

                    case "install":
                        return await Install(cli, updater, catalog, ct).ConfigureAwait(false);

                    case "update":
                        return await Update(cli, updater, catalog, ct).ConfigureAwait(false);

                    case "adopt":
                        return await Adopt(cli, updater, catalog, ct).ConfigureAwait(false);

                    case "copy":
                        return await CopyPatches(cli, updater, wow, catalog, settings, ct).ConfigureAwait(false);

                    case "remove":
                        return Remove(cli, updater, catalog);

                    case "verify":
                        return await Verify(cli, updater, catalog, ct).ConfigureAwait(false);

                    default:
                        Ui.Error("Unknown command: " + cli.Command);
                        PrintHelp();
                        return ExitError;
                }
            }
        }

        /// <summary>Commands that make sense across every registered game in one go.</summary>
        private static readonly HashSet<string> MultiGameCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "status", "update", "adopt", "verify", "list" };

        /// <summary>
        /// Runs the command once per registered game. A failure in one game is reported and
        /// the rest still run - one missing folder should not block updating the others.
        /// </summary>
        private static async Task<int> RunAllGamesAsync(CommandLine cli, Settings settings, CancellationToken ct)
        {
            if (!MultiGameCommands.Contains(cli.Command))
                throw new UpdaterException("--all-games works with status, update, adopt, verify and list. \""
                                           + cli.Command + "\" changes one game at a time - pick it with --game <name>.");
            if (cli.Option("wow") != null || cli.Option("game") != null || cli.Option("data") != null || cli.Option("edition") != null)
                throw new UpdaterException("--all-games cannot be combined with --wow, --game, --data or --edition; "
                                           + "those describe a single game.");
            if (cli.Flag("json"))
                throw new UpdaterException("--json reports on one game at a time. Use --game <name>.");
            if (settings.Games.Count == 0)
                throw new UpdaterException("No games are registered yet. Add them with:  ReforgedUpdater games add <name> \"<folder>\" --edition <edition>");

            var failed = new List<string>();
            var pending = new List<string>();
            var current = new List<string>();

            foreach (var game in settings.Games)
            {
                Ui.Info(string.Empty);
                Ui.Head("=============== " + game.Name + " ===============");

                try
                {
                    var wow = Workspace.ApplySavedData(WowInstall.Open(game.Path), explicitData: null, game.Name);
                    int code = await RunForGameAsync(cli, settings, wow, game.Name, ct).ConfigureAwait(false);

                    if (code == ExitError) failed.Add(game.Name);
                    else if (code == ExitUpdatesAvailable) pending.Add(game.Name);
                    else current.Add(game.Name);
                }
                catch (Exception ex) when (ex is UpdaterException || ex is CatalogException)
                {
                    Ui.EndProgress();
                    Ui.Error(game.Name + ": " + ex.Message);
                    failed.Add(game.Name);
                }
            }

            Ui.Info(string.Empty);
            Ui.Rule("All games");
            if (current.Count > 0) Ui.Good("  ok:                " + string.Join(", ", current));
            if (pending.Count > 0) Ui.Head("  updates available: " + string.Join(", ", pending));
            if (failed.Count > 0) Ui.Warn("failed: " + string.Join(", ", failed) + " (see messages above)");

            return failed.Count > 0 ? ExitError : pending.Count > 0 ? ExitUpdatesAvailable : ExitOk;
        }

        // ---------------------------------------------------------------- commands

        private static async Task<int> Status(CommandLine cli, Updater updater, List<CatalogEntry> catalog, CancellationToken ct)
        {
            var rows = await updater.GetStatusAsync(catalog, probeRemote: !cli.Flag("fast"), ct).ConfigureAwait(false);

            if (cli.Flag("json")) { Console.WriteLine(StatusJson(rows)); }
            else { PrintStatus(rows); }

            return rows.Any(r => r.NeedsDownload) ? ExitUpdatesAvailable : ExitOk;
        }

        private static async Task<int> Install(CommandLine cli, Updater updater, List<CatalogEntry> catalog, CancellationToken ct)
        {
            var rows = await updater.GetStatusAsync(catalog, probeRemote: false, ct).ConfigureAwait(false);

            List<ModuleStatus> chosen;
            if (cli.Flag("all"))
            {
                chosen = rows.Where(r => r.State == ModuleState.NotInstalled)
                             .Where(r => r.Entry.Variant == null || IsPrimaryVariant(r, rows))
                             .ToList();
            }
            else
            {
                if (cli.Values.Count == 0)
                {
                    Ui.Error("Name at least one module, or pass --all.  Example: ReforgedUpdater install A C G");
                    return ExitError;
                }
                chosen = Resolve(cli.Values, rows);
            }

            var already = chosen.Where(r => r.State == ModuleState.UpToDate).ToList();
            foreach (var row in already) Ui.Info(row.Entry.Display + " is already installed and current - skipping.");
            chosen = chosen.Except(already).ToList();

            foreach (var row in chosen.Where(r => r.State == ModuleState.Untracked))
                Ui.Warn(row.Entry.Display + ": " + row.Detail
                        + ". \"ReforgedUpdater adopt " + row.Entry.Id + "\" registers it without downloading.");

            if (chosen.Count == 0) { Ui.Good("Nothing to install."); return ExitOk; }

            return await Confirm(cli, updater, chosen, "Install", ct).ConfigureAwait(false);
        }

        private static async Task<int> Update(CommandLine cli, Updater updater, List<CatalogEntry> catalog, CancellationToken ct)
        {
            var rows = await updater.GetStatusAsync(catalog, probeRemote: !cli.Flag("fast"), ct).ConfigureAwait(false);

            var candidates = cli.Values.Count > 0 ? Resolve(cli.Values, rows) : rows.Where(r => r.Local != null).ToList();
            var chosen = candidates.Where(r => r.NeedsDownload).ToList();

            if (chosen.Count == 0)
            {
                PrintStatus(rows);
                Ui.Good("Everything installed is up to date.");

                var missing = rows.Where(r => r.State == ModuleState.NotInstalled).ToList();
                if (missing.Count > 0)
                    Ui.Info("Not installed: " + string.Join(", ", missing.Select(r => r.Entry.Id))
                            + "   (add with \"install <id>\")");
                return ExitOk;
            }

            return await Confirm(cli, updater, chosen, "Update", ct).ConfigureAwait(false);
        }

        private static async Task<int> Confirm(CommandLine cli, Updater updater, List<ModuleStatus> chosen, string verb, CancellationToken ct)
        {
            foreach (var row in chosen.Where(r => r.Remote == null))
                row.Remote = await ProbeSafe(updater, row, ct).ConfigureAwait(false);

            chosen = chosen.Where(r => r.Remote != null).ToList();
            if (chosen.Count == 0) { Ui.Error("None of those modules could be reached on the server."); return ExitError; }

            long total = chosen.Sum(r => Math.Max(0, r.Remote.Size));
            Ui.Rule(verb);
            foreach (var row in chosen)
                Ui.Info(string.Format(CultureInfo.InvariantCulture, "  {0,-16} {1,10}   {2}",
                    row.Entry.Display, Ui.Bytes(row.Remote.Size),
                    row.Detail.Length > 0 ? row.Detail : (row.Entry.Version != null ? "v" + row.Entry.Version : string.Empty)));
            Ui.Info("  " + chosen.Count + " file(s), " + Ui.Bytes(total) + " to download.");
            Ui.Info(string.Empty);

            if (cli.Flag("dry-run"))
            {
                await updater.ApplyAsync(chosen, dryRun: true, hash: false, ct).ConfigureAwait(false);
                return ExitOk;
            }

            if (!Ui.Confirm("Continue?", cli.Flag("yes") || cli.Flag("y")))
            {
                Ui.Info("Nothing was changed.");
                return ExitOk;
            }

            bool hash = !cli.Flag("no-hash");
            int done = await updater.ApplyAsync(chosen, dryRun: false, hash: hash, ct).ConfigureAwait(false);
            Ui.Info(string.Empty);
            Ui.Good(verb + " complete: " + done + " of " + chosen.Count + " file(s).");
            return done == chosen.Count ? ExitOk : ExitError;
        }

        private static async Task<RemoteInfo> ProbeSafe(Updater updater, ModuleStatus row, CancellationToken ct)
        {
            try
            {
                return await updater.ProbeAsync(row.Entry.Url, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Ui.Warn(row.Entry.Display + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Registers .mpq files that are already in Data but were downloaded by hand.</summary>
        private static async Task<int> Adopt(CommandLine cli, Updater updater, List<CatalogEntry> catalog, CancellationToken ct)
        {
            var rows = await updater.GetStatusAsync(catalog, probeRemote: true, ct).ConfigureAwait(false);

            var candidates = cli.Values.Count > 0
                ? Resolve(cli.Values, rows)
                : rows.Where(r => r.State == ModuleState.Untracked).ToList();

            if (candidates.Count == 0)
            {
                Ui.Info("No untracked .mpq files found in the Data folder.");
                Ui.Info("Everything present is already recorded; run \"status\" to see it.");
                return ExitOk;
            }

            Ui.Rule("Adopt");
            bool verify = cli.Flag("verify") || cli.Flag("hash");
            int adopted = await updater.AdoptAsync(candidates, verify, ct).ConfigureAwait(false);
            Ui.Info(string.Empty);

            if (adopted == 0)
            {
                Ui.Warn("Nothing was adopted.");
                return ExitError;
            }

            Ui.Good("Adopted " + adopted + " file(s). Run \"status\" to see what is current.");
            if (!verify)
                Ui.Warn("Only sizes were compared. A file of the right size but the wrong content would be"
                        + " recorded as current. Run \"adopt --verify\" to check the bytes against the server.");
            return ExitOk;
        }

        /// <summary>
        /// Copies the patches another registered game already has, instead of downloading them
        /// again. Only patches this game is missing are copied; nothing is overwritten.
        /// </summary>
        private static async Task<int> CopyPatches(CommandLine cli, Updater updater, WowInstall wow,
                                                   List<CatalogEntry> catalog, Settings settings, CancellationToken ct)
        {
            string fromName = cli.Option("from");
            if (string.IsNullOrWhiteSpace(fromName))
            {
                Ui.Error("Name the game to copy from.  Example: ReforgedUpdater copy --from warmane --game mine");
                Ui.Info(RegisteredNames(settings));
                return ExitError;
            }

            var game = settings.FindGame(fromName)
                       ?? throw new UpdaterException("No game named \"" + fromName + "\". " + RegisteredNames(settings));
            var sourceWow = Workspace.ApplySavedData(WowInstall.Open(game.Path), null, game.Name);

            using (var source = new Updater(sourceWow, settings))
            {
                List<string> only = null;
                if (cli.Values.Count > 0)
                {
                    var rows = catalog.Select(e => new ModuleStatus { Entry = e }).ToList();
                    only = Resolve(cli.Values, rows).Select(r => r.Entry.Id).ToList();
                }

                var plan = updater.PlanCopy(source, game.Name, catalog, only);

                foreach (string note in plan.Skipped) Ui.Info("  skipping " + note);
                if (plan.Items.Count == 0) { Ui.Good("Nothing to copy from " + game.Name + "."); return ExitOk; }

                Ui.Rule("Copy from " + game.Name);
                foreach (var item in plan.Items)
                    Ui.Info(string.Format(CultureInfo.InvariantCulture, "  {0,-16} {1,10}   {2}",
                        item.Entry.Display, Ui.Bytes(item.Size), item.Source.Version != null ? "v" + item.Source.Version : string.Empty));
                Ui.Info("  " + plan.Items.Count + " file(s), " + Ui.Bytes(plan.Bytes) + " to copy into " + wow.DataDir + ".");
                Ui.Info(string.Empty);

                if (cli.Flag("dry-run")) return ExitOk;

                if (!Ui.Confirm("Continue?", cli.Flag("yes") || cli.Flag("y")))
                {
                    Ui.Info("Nothing was changed.");
                    return ExitOk;
                }

                int done = await updater.CopyFromAsync(plan, cli.Flag("verify-copy"), ct).ConfigureAwait(false);
                Ui.Info(string.Empty);
                Ui.Good("Copy complete: " + done + " of " + plan.Items.Count + " file(s). "
                        + "Run \"status\" to see whether any are older than the site's.");
                return done == plan.Items.Count ? ExitOk : ExitError;
            }
        }

        private static int Remove(CommandLine cli, Updater updater, List<CatalogEntry> catalog)
        {
            if (cli.Values.Count == 0) { Ui.Error("Name at least one module to remove."); return ExitError; }

            var rows = catalog.Select(e => new ModuleStatus { Entry = e, Local = updater.State.Find(e.Id) }).ToList();
            var chosen = Resolve(cli.Values, rows);

            foreach (var row in chosen)
            {
                bool deleted = updater.Remove(row.Entry, deleteFile: !cli.Flag("keep-file"));
                Ui.Info(row.Entry.Display + (deleted ? ": deleted " + row.Entry.FileName : ": removed from the tracking list"));
            }
            return ExitOk;
        }

        private static async Task<int> Verify(CommandLine cli, Updater updater, List<CatalogEntry> catalog, CancellationToken ct)
        {
            var rows = await updater.GetStatusAsync(catalog, probeRemote: true, ct).ConfigureAwait(false);
            PrintStatus(rows);

            if (cli.Flag("deep"))
            {
                Ui.Rule("Content check");
                updater.VerifyDeep(rows, line => Ui.Info("  " + line));

                // The deep read may have proved a file stale that the metadata check passed.
                rows = await updater.GetStatusAsync(catalog, probeRemote: false, ct).ConfigureAwait(false);
            }

            return rows.Any(r => r.NeedsDownload) ? ExitUpdatesAvailable : ExitOk;
        }

        private static int SetPath(CommandLine cli, Settings settings, string settingsPath)
        {
            if (cli.Values.Count == 0)
            {
                Ui.Info("Configured client folder: " + (settings.WowPath ?? "<not set - add a game, or put this exe next to Wow.exe>"));
                Ui.Info("Settings file:            " + settingsPath);
                return ExitOk;
            }

            var wow = WowInstall.Open(cli.Values[0]);
            settings.WowPath = wow.Root;
            Store.Save(settingsPath, settings);

            // Pointing "path" at a non-standard Data folder: remember it for this game only.
            if (wow.DataIsCustom) Workspace.SaveGameDataPath(wow.Root, wow.DataDir);

            Ui.Good("Client folder set to " + wow.Root);
            Ui.Info("Patches will be written to " + Workspace.ApplySavedData(wow, null).DataDir);
            return ExitOk;
        }

        /// <summary>Shows or sets which patch library (WotLK, or vanilla per server) this client uses.</summary>
        private static int SetEdition(CommandLine cli, Updater updater, WowInstall wow)
        {
            string requested = cli.Values.Count > 0 ? cli.Values[0] : cli.Option("edition");
            if (!string.IsNullOrWhiteSpace(requested))
            {
                var chosen = updater.ResolveEdition(requested);
                Ui.Good("Edition for " + wow.Root + " set to " + chosen.Title + ".");
                Ui.Info("Catalog: " + updater.CatalogUrl);
                return ExitOk;
            }

            Edition remembered = Edition.Find(updater.State.Edition);
            int? major = wow.ClientMajorVersion();

            Ui.Info("Client:  " + wow.Root);
            Ui.Info("Wow.exe: " + (major.HasValue ? "version " + major.Value + ".x" : "version not readable"));
            Ui.Info("Edition: " + (remembered != null ? remembered.Title : "<not set>"));
            Ui.Info(string.Empty);
            Ui.Info("Available editions:");
            foreach (var e in Edition.All)
                Ui.Info(string.Format(CultureInfo.InvariantCulture, "  {0,-8} {1,-28} {2}", e.Name, e.Title, e.CatalogUrl));
            Ui.Info(string.Empty);
            Ui.Info("Set it with:  ReforgedUpdater edition turtle");
            return ExitOk;
        }

        /// <summary>Shows or sets the Data folder for one game. Stored in that game's own state.</summary>
        private static int SetData(CommandLine cli, Updater updater, WowInstall wow)
        {
            if (cli.Flag("default") || cli.Flag("reset"))
            {
                updater.State.DataPath = null;
                updater.Save();
                Ui.Good("Data folder for " + wow.Root + " reset to " + Path.Combine(wow.Root, "Data") + ".");
                return ExitOk;
            }

            if (cli.Values.Count == 0)
            {
                Ui.Info("Client:        " + wow.Root);
                Ui.Info("Data folder:   " + (updater.State.DataPath ?? "<default: the client's own Data folder>"));
                Ui.Info("Resolves to:   " + wow.DataDir);
                if (!string.IsNullOrEmpty(updater.State.DataPath) && !Directory.Exists(updater.State.DataPath))
                    Ui.Warn("The saved Data folder no longer exists. Reconnect the drive, or reset it with --default.");
                Ui.Info(string.Empty);
                Ui.Info("Set it with:     ReforgedUpdater data \"D:\\Games\\WoW\\Data\"");
                Ui.Info("Back to default: ReforgedUpdater data --default");
                Ui.Info("The setting belongs to this game only; add --game <name> to pick another.");
                return ExitOk;
            }

            var target = wow.WithData(cli.Values[0]);
            updater.State.DataPath = target.DataIsCustom ? target.DataDir : null;
            updater.Save();
            Ui.Good("Data folder for " + wow.Root + " set to " + target.DataDir + ".");
            return ExitOk;
        }

        // ---------------------------------------------------------------- games

        /// <summary>games | games add &lt;name&gt; &lt;folder&gt; | games remove &lt;name&gt;</summary>
        private static int ManageGames(CommandLine cli, Settings settings, string settingsPath)
        {
            string action = cli.Values.Count > 0 ? cli.Values[0].ToLowerInvariant() : "list";
            switch (action)
            {
                case "list":
                    ListGames(settings);
                    return ExitOk;

                case "add":
                    return AddGame(cli, settings, settingsPath);

                case "remove":
                case "forget":
                    return RemoveGame(cli, settings, settingsPath);

                default:
                    Ui.Error("Unknown games action \"" + cli.Values[0] + "\". Use: games, games add <name> <folder> --edition <edition>, games remove <name>");
                    return ExitError;
            }
        }

        private static void ListGames(Settings settings)
        {
            if (settings.Games.Count == 0)
            {
                Ui.Info("No games registered yet. Register each install once:");
                Ui.Info("  ReforgedUpdater games add warmane \"D:\\Games\\WotLK-Warmane\" --edition wotlk");
                Ui.Info("  ReforgedUpdater games add turtle \"D:\\Games\\TurtleWoW\" --edition turtle");
                return;
            }

            Ui.Rule("Games");
            Ui.Info(string.Format(CultureInfo.InvariantCulture, "    {0,-16} {1,-8} {2,-8} {3}", "NAME", "EDITION", "PATCHES", "FOLDER"));

            foreach (var game in settings.Games)
            {
                bool lastUsed = string.Equals(settings.WowPath, game.Path, StringComparison.OrdinalIgnoreCase);
                string marker = lastUsed ? "  * " : "    ";

                if (!Directory.Exists(game.Path))
                {
                    Ui.Warn(game.Name + ": folder is missing - " + game.Path);
                    continue;
                }

                var state = Store.Load<InstallState>(WowInstall.StatePathFor(game.Path));
                Ui.Info(string.Format(CultureInfo.InvariantCulture, "{0}{1,-16} {2,-8} {3,-8} {4}",
                    marker, game.Name, state.Edition ?? "not set", state.Files.Count, game.Path));
                if (!string.IsNullOrEmpty(state.DataPath))
                    Ui.Info("    " + new string(' ', 16) + " data: " + state.DataPath);
            }

            Ui.Info(string.Empty);
            Ui.Info("* = used when no game is named.  Pick one with --game <name>, or all with --all-games.");
        }

        private static int AddGame(CommandLine cli, Settings settings, string settingsPath)
        {
            // The edition is required here rather than detected: Wow.exe cannot tell a Turtle-based
            // client from a standard one, and registering a game is the one deliberate setup step
            // where asking costs nothing.
            string requestedEdition = cli.Option("edition");
            if (cli.Values.Count < 3 || string.IsNullOrWhiteSpace(requestedEdition))
            {
                Ui.Error("Usage: ReforgedUpdater games add <name> \"<game folder>\" --edition <edition> [--data <folder>]");
                Ui.Info(string.Empty);
                Ui.Info("--edition is required. Choose the patch set this game uses:");
                Ui.Info("  wotlk     WotLK 3.3.5a");
                Ui.Info("  kronos    Vanilla 1.12, standard client - Kronos and other non-Turtle vanilla servers");
                Ui.Info("  turtle    Vanilla 1.12, Turtle WoW client - Turtle and Turtle-based servers");
                return ExitError;
            }

            string name = cli.Values[1];
            if (!Workspace.IsUsableGameName(name))
                throw new UpdaterException("\"" + name + "\" is not a usable game name. Use up to 32 letters, digits, "
                                           + "dots, dashes or underscores, for example warmane or turtle.");

            var existing = settings.FindGame(name);
            if (existing != null)
                throw new UpdaterException("A game named \"" + existing.Name + "\" is already registered (" + existing.Path
                                           + "). Remove it first with:  ReforgedUpdater games remove " + existing.Name);

            // Validate everything before saving anything.
            Edition.Parse(requestedEdition);

            var wow = WowInstall.Open(cli.Values[2], cli.Option("data"));

            var samePath = settings.FindGameByPath(wow.Root);
            if (samePath != null)
                throw new UpdaterException("That folder is already registered as \"" + samePath.Name + "\".");

            settings.Games.Add(new GameEntry { Name = name, Path = wow.Root });
            Store.Save(settingsPath, settings);

            using (var updater = new Updater(wow, settings))
            {
                if (wow.DataIsCustom)
                {
                    updater.State.DataPath = wow.DataDir;
                    updater.Save();
                }

                // Explicit, so this records it in the game's state and warns if Wow.exe disagrees.
                var edition = updater.ResolveEdition(requestedEdition);

                Ui.Good("Registered \"" + name + "\" -> " + wow.Root);
                Ui.Info("  Edition: " + edition.Title);
                Ui.Info("  Data:    " + Workspace.ApplySavedData(wow, null).DataDir);
            }

            return ExitOk;
        }

        private static int RemoveGame(CommandLine cli, Settings settings, string settingsPath)
        {
            if (cli.Values.Count < 2) { Ui.Error("Usage: ReforgedUpdater games remove <name>"); return ExitError; }

            var game = settings.FindGame(cli.Values[1])
                       ?? throw new UpdaterException("No game named \"" + cli.Values[1] + "\". " + RegisteredNames(settings));

            settings.Games.Remove(game);
            Store.Save(settingsPath, settings);
            Ui.Good("Unregistered \"" + game.Name + "\". Nothing in " + game.Path + " was touched.");
            return ExitOk;
        }

        private static string RegisteredNames(Settings settings) =>
            settings.Games.Count == 0
                ? "No games are registered yet."
                : "Registered games: " + string.Join(", ", settings.Games.Select(g => g.Name)) + ".";

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Picks the game for this run: --game, then --wow, then the last one used, then the
        /// game this exe sits in. Nothing is searched for. The game's own saved Data folder is
        /// applied on top; the "data" command
        /// still runs when that folder is gone, since it is how the setting gets fixed.
        /// </summary>
        private static WowInstall ResolveInstall(CommandLine cli, Settings settings, out string gameName)
        {
            string explicitData = cli.Option("data");
            bool keepIfMissing = cli.Command == "data";
            string requestedGame = cli.Option("game");
            string explicitPath = cli.Option("wow") ?? cli.Option("path");
            WowInstall wow;

            if (!string.IsNullOrWhiteSpace(requestedGame))
            {
                if (explicitPath != null)
                    throw new UpdaterException("Use either --game or --wow, not both.");

                var game = settings.FindGame(requestedGame)
                           ?? throw new UpdaterException("No game named \"" + requestedGame + "\". " + RegisteredNames(settings));
                gameName = game.Name;
                return Workspace.ApplySavedData(WowInstall.Open(game.Path, explicitData), explicitData, gameName, keepIfMissing);
            }

            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                wow = WowInstall.Open(explicitPath, explicitData);
            }
            else
            {
                wow = WowInstall.LastUsed(settings.WowPath, explicitData) ?? WowInstall.BesideExe(explicitData);
                if (wow == null)
                {
                    // Data was given but the client folder is still unknown: that is enough to
                    // work with, since the .mpq files are all we touch.
                    if (!string.IsNullOrWhiteSpace(explicitData)) return Finish(WowInstall.ForData(explicitData), out gameName);

                    string why = string.IsNullOrWhiteSpace(settings.WowPath)
                        ? "No game chosen yet."
                        : "The game used last is no longer a game folder: " + settings.WowPath + ".";
                    throw new UpdaterException(why + " Add your game once with:  ReforgedUpdater games add <name> "
                                               + "\"D:\\Games\\MyServer\" --edition <edition>   (or put this exe next to Wow.exe)");
                }

                if (settings.Games.Count > 1)
                    Ui.Info("No game named, so using the last one (" + (settings.FindGameByPath(wow.Root)?.Name ?? wow.Root)
                            + "). Pick one with --game <name>, or run every game with --all-games.");
            }

            return Finish(wow, out gameName);

            WowInstall Finish(WowInstall resolved, out string name)
            {
                name = settings.FindGameByPath(resolved.Root)?.Name;
                return Workspace.ApplySavedData(resolved, explicitData, name, keepIfMissing);
            }
        }

        /// <summary>Maps user-typed ids ("A", "patch-a", "S-standalone") to catalog rows.</summary>
        private static List<ModuleStatus> Resolve(List<string> tokens, List<ModuleStatus> rows)
        {
            var result = new List<ModuleStatus>();
            foreach (string raw in tokens)
            {
                string token = raw.Trim();
                var match = rows.FirstOrDefault(r => string.Equals(r.Entry.Id, token, StringComparison.OrdinalIgnoreCase))
                         ?? rows.FirstOrDefault(r => string.Equals(r.Entry.PatchCode, token, StringComparison.OrdinalIgnoreCase))
                         ?? rows.FirstOrDefault(r => string.Equals(r.Entry.FileName, token, StringComparison.OrdinalIgnoreCase))
                         ?? rows.FirstOrDefault(r => string.Equals("patch-" + r.Entry.Id, token, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                    throw new UpdaterException("Unknown module \"" + raw + "\". Run \"list\" to see the available ids.");
                if (!result.Contains(match)) result.Add(match);
            }
            return result;
        }

        private static bool IsPrimaryVariant(ModuleStatus row, List<ModuleStatus> all)
        {
            // For a multi-variant slot (patch-S), --all takes only the first/standard one.
            var slot = all.Where(r => string.Equals(r.Entry.FileName, row.Entry.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
            return slot.Count <= 1 || ReferenceEquals(slot[0], row);
        }

        private static void PrintCatalog(List<CatalogEntry> catalog)
        {
            string group = null;
            foreach (var entry in catalog)
            {
                if (entry.Group != group) { group = entry.Group; Ui.Rule(group); }

                string badges = entry.Badges.Count > 0 ? "  [" + string.Join(", ", entry.Badges) + "]" : string.Empty;
                Ui.Head(string.Format(CultureInfo.InvariantCulture, "  {0,-14} {1,-8} {2}",
                    entry.Id, entry.Version != null ? "v" + entry.Version : "-", entry.Display + badges));
                if (!string.IsNullOrEmpty(entry.Name)) Ui.Info("      " + entry.Name);
                if (!string.IsNullOrEmpty(entry.VariantNote)) Ui.Info("      " + entry.VariantNote);
            }
            Ui.Info(string.Empty);
            Ui.Info("Install with:  ReforgedUpdater install A C G");
        }

        private static void PrintStatus(List<ModuleStatus> rows)
        {
            Ui.Rule("Status");
            Ui.Info(string.Format(CultureInfo.InvariantCulture, "  {0,-14} {1,-10} {2,-10} {3}",
                "MODULE", "INSTALLED", "SITE", "STATE"));

            foreach (var row in rows)
            {
                string state;
                switch (row.State)
                {
                    case ModuleState.NotInstalled: state = "not installed"; break;
                    case ModuleState.Untracked: state = "UNTRACKED"; break;
                    case ModuleState.UpToDate: state = "up to date"; break;
                    case ModuleState.UpdateAvailable: state = "UPDATE"; break;
                    case ModuleState.FileMissing: state = "MISSING"; break;
                    default: state = "CHANGED"; break;
                }

                string line = string.Format(CultureInfo.InvariantCulture, "  {0,-14} {1,-10} {2,-10} {3}",
                    row.Entry.Id,
                    row.Local?.Version != null ? "v" + row.Local.Version : (row.Local != null ? "yes" : "-"),
                    row.RemoteVersion,
                    state + (row.Detail.Length > 0 ? "  (" + row.Detail + ")" : string.Empty));

                if (row.NeedsDownload || row.State == ModuleState.Untracked) Ui.Head(line);
                else Ui.Info(line);
            }

            int pending = rows.Count(r => r.NeedsDownload);
            int tracked = rows.Count(r => r.Local != null);
            // Variants share one file, so count files rather than catalog rows.
            int untracked = rows.Where(r => r.State == ModuleState.Untracked)
                                .Select(r => r.Entry.FileName)
                                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Ui.Info(string.Empty);

            if (untracked > 0)
            {
                Ui.Head(untracked + " .mpq file(s) are already in Data but not tracked.");
                Ui.Info("Register them without downloading anything:  ReforgedUpdater adopt");
                Ui.Info(string.Empty);
            }

            if (pending > 0) Ui.Head(pending + " module(s) need downloading.  Run:  ReforgedUpdater update");
            else if (tracked == 0 && untracked == 0) Ui.Info("Nothing installed yet.  Run:  ReforgedUpdater install A C G   (or --all)");
            else if (tracked > 0) Ui.Good("All " + tracked + " installed module(s) are current.");
        }

        private static string StatusJson(List<ModuleStatus> rows)
        {
            var json = new StringBuilder();
            json.Append("{\"checkedAt\":").Append(Quote(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
            json.Append(",\"updatesAvailable\":").Append(rows.Count(r => r.NeedsDownload));
            json.Append(",\"modules\":[");

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (i > 0) json.Append(',');
                json.Append('{');
                json.Append("\"id\":").Append(Quote(row.Entry.Id));
                json.Append(",\"patch\":").Append(Quote(row.Entry.Display));
                json.Append(",\"name\":").Append(Quote(row.Entry.Name));
                json.Append(",\"file\":").Append(Quote(row.Entry.FileName));
                json.Append(",\"siteVersion\":").Append(Quote(row.Entry.Version));
                json.Append(",\"installedVersion\":").Append(Quote(row.Local?.Version));
                json.Append(",\"state\":").Append(Quote(row.State.ToString()));
                json.Append(",\"detail\":").Append(Quote(row.Detail));
                json.Append(",\"remoteSize\":").Append(row.Remote?.Size ?? -1);
                json.Append(",\"url\":").Append(Quote(row.Entry.Url));
                json.Append('}');
            }

            json.Append("]}");
            return json.ToString();
        }

        private static string Quote(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"
Project Reforged Updater - keeps the Project Reforged HD patches in sync
with https://projectreforged.github.io/ for WotLK 3.3.5 and vanilla 1.12.

USAGE
  ReforgedUpdater [command] [modules...] [options]

COMMANDS
  status              Compare the site against your Data folder   (default)
  list                Show every module the site offers
  install <id...>     Download and install modules   (--all for everything new)
  update [id...]      Re-download the modules that changed   (all tracked by default)
  adopt [id...]       Register .mpq files already in Data that you downloaded by
                      hand, without re-downloading them. --verify identifies them
                      by content instead of by size (reads every byte)
  copy --from <game> [id...]
                      Copy the patches another registered game has, instead of
                      downloading them again. Both games must use the same patch
                      set. Only patches this game is missing are copied
  remove <id...>      Delete a module's .mpq and stop tracking it
  verify [--deep]     Re-check installed files; --deep reads them and compares
                      the bytes against the published build
  games               List the game folders you have registered
  games add <name> <folder> --edition <edition>
                      Register a game folder under a short name.
                      --edition is required: wotlk, kronos or turtle
  games remove <name> Unregister a game; its files are not touched
  path [folder]       Show or set the World of Warcraft folder
  data [folder]       Show or set this game's Data folder for the .mpq files
                      (--default returns it to <client>\Data)
  edition [name]      Show or set which patch library this client uses
  help                This text

SEVERAL GAMES
  One exe serves any number of game folders. Each folder keeps its own
  edition, Data folder and patch records in <folder>\.reforged.
    ReforgedUpdater games add warmane ""D:\Games\WotLK-Warmane"" --edition wotlk
    ReforgedUpdater games add turtle ""D:\Games\TurtleWoW"" --edition turtle
    ReforgedUpdater update --all-games
  Without --game or --wow, commands use the game you used last.

EDITIONS
  wotlk               WotLK 3.3.5a
  kronos              Vanilla 1.12, standard client - Kronos and other
                      non-Turtle vanilla servers
  turtle              Vanilla 1.12, Turtle WoW client - Turtle and
                      Turtle-based servers
  Registered games always name their edition. For an unregistered folder a
  WotLK client is detected from Wow.exe; a vanilla one has to be told which,
  since Wow.exe looks the same for every vanilla server.

MODULE IDS
  The patch letter as shown on the site, e.g. A B C G I.
  A module's first variant keeps the letter; others get a suffix,
  e.g. S-standalone, T-ultra-base, L-less-thicc. See ""list"".

OPTIONS
  --game <name>       Use a registered game
  --from <game>       copy: the registered game to copy patches from
  --verify-copy       copy: read each copy back from disk and check it (slower)
  --all-games         Run status/update/adopt/verify/list for every registered game
  --wow <folder>      Use this client folder for one run
  --data <folder>     Use this Data folder for one run
  --edition <name>    Use this edition, and remember it for the client
  --all               install: every module not yet installed
  --yes, -y           Do not ask for confirmation
  --dry-run           Show what would be downloaded, change nothing
  --fast              Skip server checks; compare version labels only
  --no-hash           Skip the SHA-256 pass after each download
  --keep-file         remove: forget the module but leave the .mpq in place
  --json              status: machine-readable output
  --no-color          Plain output
  --quiet, -q         Errors and warnings only

EXIT CODES
  0 success   1 error   2 updates are available (status/verify/update)

EXAMPLES
  ReforgedUpdater                        check what changed
  ReforgedUpdater adopt --verify         keep patches you downloaded by hand
  ReforgedUpdater install A C G I        install the core visual modules
  ReforgedUpdater install S-standalone   the audio pack that needs no Patch-M
  ReforgedUpdater copy --from warmane --game mine    reuse another game's patches
  ReforgedUpdater edition turtle         a vanilla client that plays on Turtle
  ReforgedUpdater update --yes           update everything, unattended
  ReforgedUpdater verify --deep          detect a corrupted .mpq

Downloads resume where they left off, so a dropped connection is not a
restart. Close World of Warcraft before updating - the client keeps the
.mpq files open while it runs.
");
        }
    }

    /// <summary>Tiny argument parser: one command, positional module ids, and --flags.</summary>
    internal sealed class CommandLine
    {
        public string Command = "status";
        public readonly List<string> Values = new List<string>();
        private readonly Dictionary<string, string> _options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool NoColor => Flag("no-color");
        public bool Flag(string name) => _options.ContainsKey(name);
        public string Option(string name) => _options.TryGetValue(name, out string value) ? value : null;

        private static readonly HashSet<string> Commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "status", "list", "install", "update", "adopt", "copy", "remove", "verify", "path", "data", "edition", "games", "help" };

        private static readonly HashSet<string> TakesValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "wow", "path", "data", "edition", "game", "from" };

        public static CommandLine Parse(string[] args)
        {
            var cli = new CommandLine();
            bool commandSeen = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.StartsWith("--", StringComparison.Ordinal) || arg.StartsWith("-", StringComparison.Ordinal))
                {
                    string name = arg.TrimStart('-');
                    string value = null;

                    int equals = name.IndexOf('=');
                    if (equals >= 0) { value = name.Substring(equals + 1); name = name.Substring(0, equals); }
                    else if (TakesValue.Contains(name) && i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                        value = args[++i];

                    cli._options[name] = value ?? string.Empty;
                    continue;
                }

                if (!commandSeen && Commands.Contains(arg)) { cli.Command = arg.ToLowerInvariant(); commandSeen = true; }
                else cli.Values.Add(arg);
            }

            return cli;
        }
    }
}
