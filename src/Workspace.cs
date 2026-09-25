using System;
using System.IO;
using System.Text.RegularExpressions;

namespace ReforgedUpdater
{
    /// <summary>
    /// Settings and per-game plumbing shared by the command line and the window, so both
    /// read the same reforged-updater.json and apply a game's saved Data folder the same way.
    /// </summary>
    internal static class Workspace
    {
        private static readonly Regex GameNameRx = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$");

        /// <summary>Short, typeable, and not "all", which reads like --all-games.</summary>
        public static bool IsUsableGameName(string name) =>
            name != null && GameNameRx.IsMatch(name) && !name.Equals("all", StringComparison.OrdinalIgnoreCase);

        public static string SettingsPath()
        {
            string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "reforged-updater.json");
            try
            {
                string probe = beside + ".probe";
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return beside;
            }
            catch
            {
                // Program Files and similar are read-only for normal users.
                string roaming = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ProjectReforgedUpdater");
                Directory.CreateDirectory(roaming);
                return Path.Combine(roaming, "reforged-updater.json");
            }
        }

        /// <summary>
        /// Applies the game's own saved Data folder, unless this run already chose one
        /// (--data, or a path that pointed straight at a Data folder).
        /// </summary>
        public static WowInstall ApplySavedData(WowInstall wow, string explicitData)
        {
            if (!string.IsNullOrWhiteSpace(explicitData) || wow.DataIsCustom) return wow;

            var state = Store.Load<InstallState>(wow.StatePath);
            if (string.IsNullOrWhiteSpace(state.DataPath)) return wow;

            try { return wow.WithData(state.DataPath); }
            catch (UpdaterException)
            {
                throw new UpdaterException("This game's saved Data folder no longer exists: " + state.DataPath
                                           + ". Reconnect the drive, or reset it with:  ReforgedUpdater data --default --wow \""
                                           + wow.Root + "\"");
            }
        }

        public static void SaveGameDataPath(string root, string dataPath)
        {
            string statePath = WowInstall.StatePathFor(root);
            var state = Store.Load<InstallState>(statePath);
            state.DataPath = dataPath;
            Store.Save(statePath, state);
        }

        /// <summary>
        /// Older versions kept one Data folder next to the exe and applied it to every game.
        /// It belongs to the game it was set for (the last one used), so it moves there.
        /// </summary>
        public static void MigrateLegacyDataPath(Settings settings, string settingsPath)
        {
            if (string.IsNullOrWhiteSpace(settings.DataPath)) return;

            string legacy = settings.DataPath;
            string root = settings.WowPath;

            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                var state = Store.Load<InstallState>(WowInstall.StatePathFor(root));
                if (string.IsNullOrWhiteSpace(state.DataPath)) SaveGameDataPath(root, legacy);
                Ui.Info("Note: the saved Data folder (" + legacy + ") now belongs to " + root
                        + " only; other games use their own Data folders.");
            }
            else
            {
                Ui.Warn("The saved Data folder " + legacy + " used to apply to every game and has been cleared. "
                        + "Set it again for the right game with:  ReforgedUpdater data \"<folder>\" --game <name>");
            }

            settings.DataPath = null;
            Store.Save(settingsPath, settings);
        }
    }
}
