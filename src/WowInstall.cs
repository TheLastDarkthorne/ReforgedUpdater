using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace ReforgedUpdater
{
    /// <summary>Locates the WoW 3.3.5 client and owns every path the updater writes to.</summary>
    internal sealed class WowInstall
    {
        private readonly string _dataOverride;

        public string Root { get; }

        /// <summary>Where the .mpq files go. Usually &lt;Root&gt;\Data, but it can be set explicitly.</summary>
        public string DataDir => _dataOverride ?? Path.Combine(Root, "Data");

        /// <summary>True when Data is somewhere other than the default location.</summary>
        public bool DataIsCustom => _dataOverride != null;

        public string WorkDir => Path.Combine(Root, ".reforged");
        public string StatePath => StatePathFor(Root);

        /// <summary>A client's state file, for callers that only have its folder.</summary>
        public static string StatePathFor(string root) => Path.Combine(root, ".reforged", "state.json");

        /// <summary>
        /// Partial downloads sit beside the Data folder rather than beside the client, so
        /// the final move stays on one volume even when Data lives on another drive.
        /// </summary>
        public string CacheDir
        {
            get
            {
                string beside = Path.GetDirectoryName(DataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return Path.Combine(string.IsNullOrEmpty(beside) ? Root : beside, ".reforged", "cache");
            }
        }

        private WowInstall(string root, string dataOverride)
        {
            Root = root;
            _dataOverride = dataOverride;
        }

        public string TargetPath(CatalogEntry entry) => Path.Combine(DataDir, entry.FileName);

        public void EnsureDirectories()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(CacheDir);
            HideWorkDir();
        }

        private void HideWorkDir()
        {
            foreach (string path in new[] { WorkDir, Path.GetDirectoryName(CacheDir) })
            {
                try
                {
                    var info = new DirectoryInfo(path);
                    if (info.Exists && (info.Attributes & FileAttributes.Hidden) == 0)
                        info.Attributes |= FileAttributes.Hidden;
                }
                catch { /* cosmetic only */ }
            }
        }

        public static bool LooksLikeWowFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                return File.Exists(Path.Combine(path, "Wow.exe"))
                    || File.Exists(Path.Combine(path, "WoW.exe"))
                    || Directory.Exists(Path.Combine(path, "Data"));
            }
            catch { return false; }
        }

        /// <summary>A folder that holds the client's archives rather than the client itself.</summary>
        public static bool LooksLikeDataFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                if (!Directory.Exists(path)) return false;
                string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return name.Equals("Data", StringComparison.OrdinalIgnoreCase)
                       || Directory.EnumerateFiles(path, "*.mpq").Any();
            }
            catch { return false; }
        }

        /// <summary>
        /// Opens an explicit client folder. Pointing at a Data folder by mistake is
        /// common enough to handle: the parent becomes the client folder.
        /// </summary>
        public static WowInstall Open(string path, string dataOverride = null)
        {
            string full = Path.GetFullPath(path);
            if (!Directory.Exists(full))
                throw new UpdaterException("No such folder: " + full);

            string data = NormalizeData(dataOverride);

            if (!LooksLikeWowFolder(full) && LooksLikeDataFolder(full))
            {
                string parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(parent))
                {
                    Ui.Info("That is a Data folder; using " + parent + " as the client folder.");
                    // Only treat it as custom when it is not the client's own Data folder.
                    bool isDefault = string.Equals(Path.Combine(parent, "Data"), full, StringComparison.OrdinalIgnoreCase);
                    return new WowInstall(parent, data ?? (isDefault ? null : full));
                }
            }

            if (!LooksLikeWowFolder(full))
                throw new UpdaterException("That folder has no Wow.exe and no Data folder: " + full);

            return new WowInstall(full, data);
        }

        /// <summary>
        /// Builds an install around an explicit Data folder when no client folder could be
        /// found. The .mpq files are all the updater touches, so that is enough to work with;
        /// state lives one level up from Data.
        /// </summary>
        public static WowInstall ForData(string dataPath)
        {
            string data = NormalizeData(dataPath);
            string parent = Path.GetDirectoryName(data.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return new WowInstall(string.IsNullOrEmpty(parent) ? data : parent, data);
        }

        /// <summary>The same client with a different Data folder; null or the default resets to &lt;Root&gt;\Data.</summary>
        public WowInstall WithData(string dataPath)
        {
            string data = NormalizeData(dataPath);
            bool isDefault = data != null && string.Equals(
                data.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.Combine(Root, "Data"), StringComparison.OrdinalIgnoreCase);
            return new WowInstall(Root, isDefault ? null : data);
        }

        /// <summary>Validates an explicit Data folder and returns it as an absolute path.</summary>
        public static string NormalizeData(string dataPath)
        {
            if (string.IsNullOrWhiteSpace(dataPath)) return null;

            string full = Path.GetFullPath(dataPath);
            if (!Directory.Exists(full))
                throw new UpdaterException("No such Data folder: " + full);
            return full;
        }

        /// <summary>
        /// Tries, in order: the saved setting, the folder the exe sits in (and its parents),
        /// the registry, then a handful of common install paths.
        /// </summary>
        public static WowInstall Detect(string configured, string dataOverride = null)
        {
            string data = NormalizeData(dataOverride);

            foreach (string candidate in Candidates(configured))
            {
                if (LooksLikeWowFolder(candidate))
                {
                    try { return new WowInstall(Path.GetFullPath(candidate), data); }
                    catch { /* keep looking */ }
                }
            }
            return null;
        }

        private static IEnumerable<string> Candidates(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured)) yield return configured;

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            for (var dir = new DirectoryInfo(exeDir); dir != null; dir = dir.Parent)
                yield return dir.FullName;

            foreach (string fromRegistry in RegistryPaths()) yield return fromRegistry;

            foreach (string drive in DriveInfo.GetDrives()
                         .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                         .Select(d => d.Name))
            {
                yield return Path.Combine(drive, "World of Warcraft");
                yield return Path.Combine(drive, "Games", "World of Warcraft");
                yield return Path.Combine(drive, "Program Files (x86)", "World of Warcraft");
            }
        }

        private static IEnumerable<string> RegistryPaths()
        {
            string[] keys =
            {
                @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft",
                @"SOFTWARE\Blizzard Entertainment\World of Warcraft"
            };

            foreach (string key in keys)
            {
                string value = null;
                try
                {
                    using (var handle = Registry.LocalMachine.OpenSubKey(key))
                        value = handle?.GetValue("InstallPath") as string;
                }
                catch { /* registry access is best effort */ }

                if (!string.IsNullOrWhiteSpace(value)) yield return value;
            }
        }

        /// <summary>
        /// The client's major version from Wow.exe's version resource: 3 for WotLK, 1 for
        /// vanilla. Null when there is no readable executable.
        /// </summary>
        public int? ClientMajorVersion()
        {
            foreach (string name in new[] { "Wow.exe", "WoW.exe", "WowClassic.exe" })
            {
                string exe = Path.Combine(Root, name);
                if (!File.Exists(exe)) continue;
                try
                {
                    var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                    if (info.FileMajorPart > 0) return info.FileMajorPart;
                }
                catch { /* unreadable resource: fall through */ }
            }
            return null;
        }

        /// <summary>True when the client (or anything else) is holding the file open for writing.</summary>
        public static bool IsLocked(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        public static bool GameIsRunning()
        {
            try
            {
                return System.Diagnostics.Process.GetProcesses()
                    .Any(p => p.ProcessName.Equals("Wow", StringComparison.OrdinalIgnoreCase)
                           || p.ProcessName.Equals("WoW", StringComparison.OrdinalIgnoreCase)
                           || p.ProcessName.StartsWith("Wow-64", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        /// <summary>Free space on the drive that holds Data - that is where the bytes land.</summary>
        public long FreeSpace()
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(DataDir))).AvailableFreeSpace; }
            catch { return long.MaxValue; }
        }
    }

    internal sealed class UpdaterException : Exception
    {
        public UpdaterException(string message) : base(message) { }
    }
}
