using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgedUpdater
{
    /// <summary>One thing a tool puts in the game folder: a zip entry or a loose release file, and where it goes.</summary>
    internal sealed class ToolRule
    {
        public string From;       // zip entry (forward slashes)
        public string To;         // path under the tool's folder
        public bool KeepYours;    // a settings file: written only when it is not there yet
    }

    /// <summary>One file in a GitHub release that the tool needs.</summary>
    internal sealed class ToolAsset
    {
        public string[] Names;                // release file names to accept; the first one present wins
        public Regex NameRx;                  // or a pattern, for names that carry a version
        public bool IsZip;
        public bool WholeZip;                 // place every entry (without the zip's single top folder)
        public List<ToolRule> Rules = new List<ToolRule>();

        public static ToolAsset Loose(string to, params string[] names)
        {
            var asset = new ToolAsset { Names = names };
            asset.Rules.Add(new ToolRule { To = to });
            return asset;
        }

        public static ToolAsset Zip(string nameRx, params ToolRule[] rules) =>
            new ToolAsset { NameRx = new Regex(nameRx, RegexOptions.IgnoreCase), IsZip = true, Rules = rules.ToList() };

        public static ToolAsset ZipAll(string nameRx) =>
            new ToolAsset { NameRx = new Regex(nameRx, RegexOptions.IgnoreCase), IsZip = true, WholeZip = true };

        public string Describe() => Names != null ? string.Join(" or ", Names) : NameRx.ToString();
    }

    /// <summary>
    /// A tool the downloads page lists besides the .mpq patches. Where its files go is written
    /// down here rather than read from the tool's README, because READMEs drift from what the
    /// release really ships; a release that no longer has these files stops the install loudly.
    /// </summary>
    internal sealed class ToolDef
    {
        public string Id;
        public string[] Aliases = new string[0];
        public string Name;
        public string Summary;
        public string Warning;
        public string Repo;                    // "owner/name" on GitHub
        public string Folder;                  // under the game folder; null = the game folder itself
        public List<ToolAsset> Assets = new List<ToolAsset>();
        public string PresetUrl;               // the project's recommended settings for the tool's settings file
        public string SettingsFile;            // under Folder
        public string Launcher;                // under Folder; something the user runs themselves

        public string RepoUrl => "https://github.com/" + Repo;

        public bool Is(string token) =>
            string.Equals(Id, token, StringComparison.OrdinalIgnoreCase)
            || Aliases.Any(a => string.Equals(a, token, StringComparison.OrdinalIgnoreCase));

        public static readonly ToolDef Patcher = new ToolDef
        {
            Id = "patcher",
            Aliases = new[] { "stony", "st0ny", "exe-patcher" },
            Name = "St0ny's AIO WoW EXE Patcher",
            Summary = "Patches Wow.exe in one pass: fixes, longer view distance, and the loaders the other tools rely on. You run it yourself.",
            Warning = "It edits Wow.exe. Its Reforged preset (press R in its menu) uses patches its author rates safe, "
                      + "and some of them load DLLs that some servers block.",
            Repo = "Raz0r1337/St0nys-AIO-WoW-EXE-Patcher",
            Folder = "Reforged Patcher",
            Launcher = "patcher-win.bat",
            Assets = { ToolAsset.ZipAll(@"^St0nys-AIO-WoW-EXE-Patcher-.*\.zip$") }
        };

        public static readonly ToolDef Dxvk = new ToolDef
        {
            Id = "dxvk",
            Name = "DXVK (Reforged build)",
            Summary = "A faster Direct3D 9 layer, recommended with the fog. Adds d3d9.dll next to Wow.exe.",
            Warning = "It replaces Direct3D with its own DLL, which some servers may block.",
            Repo = "Raz0r1337/dxvk",
            Assets = { ToolAsset.Zip(@"^DXVK-WoW-x86\.zip$", new ToolRule { From = "d3d9.dll", To = "d3d9.dll" }) }
        };

        public static readonly ToolDef Fog = new ToolDef
        {
            Id = "fog",
            Aliases = new[] { "vfog", "volumetric-fog", "coavolfog" },
            Name = "Volumetric Fog & Light Shafts",
            Summary = "Adds volumetric fog and light shafts. Settings live in CoAVolFog.ini; press Ctrl+F7 in the game to change them.",
            Warning = "It loads as version.dll next to Wow.exe, which some servers may block.",
            Repo = "Raz0r1337/coa-vfog",
            SettingsFile = "CoAVolFog.ini",
            PresetUrl = "https://pub-0f05631d243e4046993fc02ca7be9542.r2.dev/Wrath%20of%20the%20Lich%20King/mods/presets/CoAVolFog.ini",
            Assets =
            {
                ToolAsset.Zip(@"^CoAVolFog-Win32\.zip$",
                    new ToolRule { From = "version.dll", To = "version.dll" },
                    new ToolRule { From = "CoAVolFog.dll", To = "CoAVolFog.dll" },
                    new ToolRule { From = "fogdata.bin", To = "fogdata.bin" },
                    new ToolRule { From = "waterdata.bin", To = "waterdata.bin" },
                    new ToolRule { From = "CoAVolFog.ini", To = "CoAVolFog.ini", KeepYours = true })
            }
        };

        // The patcher's Lexara loader (patch No. 30) loads "Lexara.dll", so that is the name it goes in as.
        // Lexara's own README says dinput8.dll, which loads it a second time next to the loader.
        public static readonly ToolDef Lexara = new ToolDef
        {
            Id = "lexara",
            Aliases = new[] { "fonts" },
            Name = "Lexara HD fonts",
            Summary = "Sharp HD fonts for the whole interface. The patcher's Lexara loader (patch No. 30, on in the Reforged preset) loads it.",
            Warning = "It is a DLL that some servers may block.",
            Repo = "Stormhand-dev/Lexara---HD-Font-Renderer-for-WoW-3.3.5",
            Assets =
            {
                ToolAsset.Loose("Lexara.dll", "lexara.dll", "dinput8.dll"),
                ToolAsset.Loose("skia.dll", "skia.dll")
            }
        };

        public static readonly IReadOnlyList<ToolDef> Wotlk = new[] { Patcher, Dxvk, Fog, Lexara };
    }

    // ---------------------------------------------------------------- GitHub's release JSON

    [DataContract]
    internal sealed class GhAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "browser_download_url")] public string Url { get; set; }
        [DataMember(Name = "digest")] public string Digest { get; set; }

        /// <summary>GitHub's SHA-256 for the file, or null when it lists none.</summary>
        public string Sha256 =>
            Digest != null && Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? Digest.Substring(7).ToLowerInvariant()
                : null;
    }

    [DataContract]
    internal sealed class GhRelease
    {
        [DataMember(Name = "tag_name")] public string Tag { get; set; }
        [DataMember(Name = "published_at")] public string Published { get; set; }
        [DataMember(Name = "assets")] public List<GhAsset> Assets { get; set; }

        public string Version => Tag == null ? null : Regex.Replace(Tag, "^[vV](?=[0-9])", string.Empty);
    }

    internal enum ToolState { NotInstalled, UpToDate, UpdateAvailable, Damaged, Unknown }

    internal sealed class ToolStatus
    {
        public ToolDef Def;
        public InstalledTool Local;
        public GhRelease Remote;
        public ToolState State;
        public string Detail = string.Empty;
        public string Error;                       // why GitHub could not be checked, or why the release can't be used
        public List<string> Notes = new List<string>();
        public bool PresetChanged;

        public bool NeedsInstall => State == ToolState.NotInstalled || State == ToolState.UpdateAvailable || State == ToolState.Damaged;
        public string LocalVersion => Local?.Version ?? "-";
        public string RemoteVersion => Remote?.Version ?? "-";

        public long DownloadSize
        {
            get
            {
                try { return Remote == null ? 0 : ToolKit.PickAssets(Def, Remote).Sum(p => p.Value.Size); }
                catch (UpdaterException) { return 0; }
            }
        }
    }

    /// <summary>What Wow.exe itself says about the tools, read from the file without running it.</summary>
    internal sealed class ExeMarks
    {
        public string Path;
        public bool Patched;        // carries the patcher's watermark text
        public bool LoadsLexara;    // has the patcher's "Lexara.dll" loader
    }

    internal sealed class PresetPlan
    {
        public byte[] Bytes;
        public string Tag;
        public string FilePath;
        public bool FileExists;
        public bool Identical;
        public List<string> Differences = new List<string>();
    }

    /// <summary>
    /// Downloads, places and updates the extra tools the downloads page lists besides the
    /// patches. It never runs one: the patcher is started by the user, from a button or
    /// "tools run patcher". It also never replaces a file it did not place itself.
    /// </summary>
    internal sealed class ToolKit
    {
        private static readonly Dictionary<string, GhRelease> ReleaseCache =
            new Dictionary<string, GhRelease>(StringComparer.OrdinalIgnoreCase);

        public static void ForgetCachedReleases() { lock (ReleaseCache) ReleaseCache.Clear(); }

        private const string Watermark = "Patched with St0nys AIO WoW.exe Patcher";
        private const string LexaraLoader = "Lexara.dll";

        private readonly WowInstall _wow;
        private readonly InstallState _state;
        private readonly Action _save;
        private readonly Downloader _http;
        private int _step;

        public ToolKit(WowInstall wow, InstallState state, Action save, Downloader http)
        {
            _wow = wow;
            _state = state;
            _save = save;
            _http = http;
        }

        /// <summary>The tools for a patch set. Only WotLK has any so far; the vanilla pages list different ones.</summary>
        public static IReadOnlyList<ToolDef> For(Edition edition) =>
            edition == Edition.Wotlk ? ToolDef.Wotlk : (IReadOnlyList<ToolDef>)new ToolDef[0];

        public static ToolDef Find(IReadOnlyList<ToolDef> tools, string token) =>
            tools.FirstOrDefault(t => t.Is(token ?? string.Empty));

        public string Root => _wow.Root;
        public string ToolDir(ToolDef def) => def.Folder == null ? _wow.Root : Path.Combine(_wow.Root, def.Folder);
        public string PatcherLauncher => Path.Combine(ToolDir(ToolDef.Patcher), ToolDef.Patcher.Launcher);
        public string SettingsPath(ToolDef def) => def.SettingsFile == null ? null : Path.Combine(ToolDir(def), def.SettingsFile);
        public bool IsInstalled(ToolDef def) => _state.FindTool(def.Id) != null;

        // ---------------------------------------------------------------- what GitHub has

        public async Task<GhRelease> LatestAsync(ToolDef def, CancellationToken ct)
        {
            lock (ReleaseCache)
                if (ReleaseCache.TryGetValue(def.Repo, out var cached)) return cached;

            GhRelease release;
            try
            {
                string json = await _http.GetStringAsync("https://api.github.com/repos/" + def.Repo + "/releases/latest", ct)
                                         .ConfigureAwait(false);
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    release = (GhRelease)new DataContractJsonSerializer(typeof(GhRelease)).ReadObject(stream);
            }
            catch (HttpRequestException ex)
            {
                // The same words for every tool, so a window shows one message rather than one per tool.
                if (ex.Message.Contains("403") || ex.Message.Contains("429"))
                    throw new UpdaterException("GitHub is limiting requests from your network (about 60 an hour without an account). Try again in a while.");

                throw new UpdaterException("Could not check " + def.Name + " on GitHub. "
                                           + (ex.Message.Contains("404") ? "GitHub has no release for it." : ex.Message));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new UpdaterException("GitHub did not answer in time while checking " + def.Name + ".");
            }
            catch (SerializationException)
            {
                throw new UpdaterException("GitHub's answer for " + def.Name + " was not what I expected.");
            }

            if (release?.Assets == null || string.IsNullOrEmpty(release.Tag))
                throw new UpdaterException(def.Name + " has no published release on GitHub.");

            lock (ReleaseCache) ReleaseCache[def.Repo] = release;
            return release;
        }

        /// <summary>Which release file each of the tool's assets resolves to; throws when the release no longer has one.</summary>
        public static List<KeyValuePair<ToolAsset, GhAsset>> PickAssets(ToolDef def, GhRelease release)
        {
            var picked = new List<KeyValuePair<ToolAsset, GhAsset>>();
            foreach (var asset in def.Assets)
            {
                GhAsset found = null;
                if (asset.Names != null)
                    foreach (string name in asset.Names)
                    {
                        found = release.Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (found != null) break;
                    }
                if (found == null && asset.NameRx != null)
                    found = release.Assets.FirstOrDefault(a => asset.NameRx.IsMatch(a.Name));

                if (found == null)
                    throw new UpdaterException(def.Name + " " + release.Tag + " no longer has the file I expected (" + asset.Describe()
                                               + "), so I won't guess. The updater needs a fix for this tool; for now, "
                                               + "install it by hand from " + def.RepoUrl + "/releases");
                picked.Add(new KeyValuePair<ToolAsset, GhAsset>(asset, found));
            }
            return picked;
        }

        // ---------------------------------------------------------------- status

        public async Task<List<ToolStatus>> GetStatusAsync(IReadOnlyList<ToolDef> tools, bool checkRemote, CancellationToken ct)
        {
            var rows = new List<ToolStatus>();
            foreach (var def in tools)
            {
                var row = new ToolStatus { Def = def, Local = _state.FindTool(def.Id) };

                if (checkRemote)
                {
                    try { row.Remote = await LatestAsync(def, ct).ConfigureAwait(false); }
                    catch (UpdaterException ex) { row.Error = ex.Message; }
                }

                Classify(row);
                rows.Add(row);
            }

            // The notes need Wow.exe once, not once per tool.
            var marks = ReadExeMarks();
            foreach (var row in rows)
                await AddNotesAsync(row, marks, checkRemote, ct).ConfigureAwait(false);
            return rows;
        }

        private void Classify(ToolStatus row)
        {
            if (row.Local == null)
            {
                row.State = ToolState.NotInstalled;
                if (row.Detail.Length == 0) row.Detail = "not installed";
                return;
            }

            var gone = row.Local.Files.Where(f => !f.Config && !File.Exists(Absolute(f.Path))).ToList();
            var changed = row.Local.Files.Where(f => !f.Config && File.Exists(Absolute(f.Path))
                                                     && new FileInfo(Absolute(f.Path)).Length != f.Size).ToList();
            if (gone.Count > 0 || changed.Count > 0)
            {
                row.State = ToolState.Damaged;
                row.Detail = gone.Count > 0
                    ? gone.Count + " file(s) are missing, such as " + Path.GetFileName(gone[0].Path)
                    : Path.GetFileName(changed[0].Path) + " is not the file that was installed";
                return;
            }

            if (row.Remote == null)
            {
                row.State = ToolState.Unknown;
                if (row.Detail.Length == 0) row.Detail = "not checked";
                return;
            }

            bool newerTag = !string.Equals(row.Local.Version, row.Remote.Version, StringComparison.OrdinalIgnoreCase);
            bool newerFiles = false;
            try
            {
                foreach (var pair in PickAssets(row.Def, row.Remote))
                {
                    string recorded = row.Local.Assets.FirstOrDefault(a => a.StartsWith(pair.Value.Name + "=", StringComparison.OrdinalIgnoreCase));
                    if (recorded != null && pair.Value.Sha256 != null
                        && !recorded.Substring(pair.Value.Name.Length + 1).Equals(pair.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                        newerFiles = true;
                }
            }
            catch (UpdaterException ex) { row.Error = ex.Message; }

            if (newerTag || newerFiles)
            {
                row.State = ToolState.UpdateAvailable;
                row.Detail = newerTag ? "update available" : "re-published under the same version";
            }
            else
            {
                row.State = ToolState.UpToDate;
                row.Detail = "up to date";
            }
        }

        private async Task AddNotesAsync(ToolStatus row, ExeMarks marks, bool checkRemote, CancellationToken ct)
        {
            if (row.Local == null) return;

            if (row.Def == ToolDef.Lexara && marks != null)
            {
                if (!marks.LoadsLexara)
                    row.Notes.Add("Your Wow.exe does not load Lexara yet. Run the patcher and keep patch No. 30 (Load Lexara.dll) selected.");
                else if (File.Exists(Path.Combine(_wow.Root, "dinput8.dll")))
                    row.Notes.Add("Your Wow.exe loads Lexara.dll, and a dinput8.dll is also in the game folder. If that is Lexara from a "
                                  + "manual install, delete it, or Lexara loads twice.");
            }

            if (row.Def == ToolDef.Patcher && marks != null)
                row.Notes.Add(marks.Patched
                    ? "Wow.exe has been patched by it."
                    : "Wow.exe has not been patched yet. Run the patcher and press R for the Reforged preset.");

            if (row.Def.PresetUrl != null && checkRemote && row.Local.PresetTag != null)
            {
                try
                {
                    var info = await _http.ProbeAsync(row.Def.PresetUrl, ct).ConfigureAwait(false);
                    string tag = info.ETag ?? info.LastModified;
                    if (tag != null && !tag.Equals(row.Local.PresetTag, StringComparison.Ordinal))
                    {
                        row.PresetChanged = true;
                        row.Notes.Add("The project's recommended settings changed. Your " + row.Def.SettingsFile
                                      + " is untouched; \"tools preset " + row.Def.Id + "\" shows the differences.");
                    }
                }
                catch (Exception ex) when (ex is UpdaterException || ex is HttpRequestException) { /* the note is optional */ }
            }
        }

        // ---------------------------------------------------------------- reading Wow.exe

        /// <summary>The client's Wow.exe, whatever the case of its name; null when there is none.</summary>
        public string ExePath()
        {
            foreach (string name in new[] { "Wow.exe", "WoW.exe", "wow.exe" })
            {
                string path = Path.Combine(_wow.Root, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>Looks for the patcher's marks inside Wow.exe. Read-only; null when the file cannot be read.</summary>
        public ExeMarks ReadExeMarks()
        {
            string path = ExePath();
            if (path == null) return null;

            try
            {
                byte[] data;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length > 64L * 1024 * 1024) return null;   // not a client
                    data = new byte[stream.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = stream.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }

                return new ExeMarks
                {
                    Path = path,
                    Patched = IndexOf(data, Encoding.ASCII.GetBytes(Watermark)) >= 0,
                    LoadsLexara = IndexOf(data, Encoding.ASCII.GetBytes(LexaraLoader + "\0")) >= 0
                };
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static int IndexOf(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
            byte first = needle[0];
            int last = haystack.Length - needle.Length;
            for (int i = 0; i <= last; i++)
            {
                if (haystack[i] != first) continue;
                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        // ---------------------------------------------------------------- paths

        private string Absolute(string relative) => Path.Combine(_wow.Root, relative.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Joins a name from inside a download onto a folder, refusing anything that would land outside it.</summary>
        internal static string Inside(string folder, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative))
                throw new UpdaterException("A download listed a file with no name.");

            string baseFull = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full;
            try { full = Path.GetFullPath(Path.Combine(baseFull, relative.Replace('/', Path.DirectorySeparatorChar))); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new UpdaterException("A download listed a file name that is not valid here: " + relative);
            }

            if (!full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
                throw new UpdaterException("A download wanted to write outside the game folder (" + relative + "), so I stopped.");
            return full;
        }

        private string Relative(string full) =>
            full.Substring(Path.GetFullPath(_wow.Root).TrimEnd('\\', '/').Length + 1).Replace('\\', '/');

        // ---------------------------------------------------------------- installing

        /// <summary>One file about to be placed.</summary>
        private sealed class Planned
        {
            public string Rel;          // relative to the game folder
            public string Dest;
            public bool Config;
            public bool Skip;           // already there and left alone
            public long Size;
            public string Sha256;
            public string Staged;
            public Func<Stream> Open;   // where the bytes come from
        }

        /// <summary>Installs or updates the given tools. Returns how many finished; a failure in one does not stop the others.</summary>
        public async Task<int> InstallAsync(List<ToolStatus> rows, CancellationToken ct)
        {
            if (rows.Count == 0) return 0;
            _wow.EnsureDirectories();

            if (WowInstall.GameIsRunning())
                Ui.Warn("World of Warcraft looks like it is running. Close it first, or the DLLs cannot be replaced.");

            int total = 0;
            foreach (var row in rows)
            {
                if (row.Remote == null) row.Remote = await LatestAsync(row.Def, ct).ConfigureAwait(false);
                total += PickAssets(row.Def, row.Remote).Count;
            }

            _step = 0;
            int ok = 0;
            foreach (var row in rows)
            {
                try
                {
                    await InstallOneAsync(row, total, ct).ConfigureAwait(false);
                    ok++;
                }
                catch (UpdaterException ex)
                {
                    Ui.EndProgress();
                    Ui.Error(row.Def.Name + ": " + ex.Message);
                }
            }
            return ok;
        }

        private async Task InstallOneAsync(ToolStatus row, int total, CancellationToken ct)
        {
            var def = row.Def;
            var release = row.Remote;
            var prior = _state.FindTool(def.Id);
            string stageDir = Path.Combine(_wow.CacheDir, "tools", def.Id);
            Directory.CreateDirectory(stageDir);

            // 1. Download every file the release needs and check it against GitHub's checksum.
            var downloaded = new List<KeyValuePair<ToolAsset, string>>();
            var assetRecords = new List<string>();
            var openZips = new List<ZipArchive>();
            var staged = new List<Planned>();

            try
            {
                foreach (var pair in PickAssets(def, release))
                {
                    string part = Path.Combine(stageDir, pair.Value.Name + ".part");
                    string label = string.Format(CultureInfo.InvariantCulture, "[{0}/{1}] {2}: {3}", ++_step, total, def.Name, pair.Value.Name);
                    var remote = new RemoteInfo { Size = pair.Value.Size, SupportsRange = false };

                    downloaded.Add(new KeyValuePair<ToolAsset, string>(pair.Key, part));
                    string digest = await _http.FetchAsync(label, pair.Value.Url, remote, part, hash: true, ct).ConfigureAwait(false);

                    string expected = pair.Value.Sha256;
                    if (expected == null)
                        Ui.Warn(pair.Value.Name + ": GitHub lists no checksum for this file, so it could not be checked.");
                    else if (!expected.Equals(digest, StringComparison.OrdinalIgnoreCase))
                        throw new UpdaterException(pair.Value.Name + " does not match the checksum GitHub lists for it, so I did not install it. "
                                                   + "Try again; if it keeps happening, the download is being altered on the way.");

                    assetRecords.Add(pair.Value.Name + "=" + digest);
                }

                // 2. Work out every file and make sure nothing that is not ours would be replaced.
                var plan = new List<Planned>();
                string presetTag = prior?.PresetTag;

                foreach (var pair in downloaded)
                    PlanAsset(def, pair.Key, pair.Value, plan, openZips);

                if (plan.GroupBy(p => p.Dest, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                    throw new UpdaterException("The release lists the same file twice; I won't guess which one is right.");

                // A settings file that is not there yet starts from the project's recommended settings.
                foreach (var item in plan.Where(p => p.Config && !File.Exists(p.Dest)))
                {
                    if (def.PresetUrl == null) continue;
                    try
                    {
                        var preset = await _http.GetBytesAsync(def.PresetUrl, ct).ConfigureAwait(false);
                        item.Open = () => new MemoryStream(preset.Bytes, false);
                        item.Size = preset.Bytes.Length;
                        presetTag = preset.Tag;
                        Ui.Info("  " + Path.GetFileName(item.Dest) + " starts from the Project Reforged recommended settings.");
                    }
                    catch (HttpRequestException ex)
                    {
                        Ui.Warn("Could not get the recommended settings (" + ex.Message + "); " + Path.GetFileName(item.Dest)
                                + " starts from the tool's own defaults.");
                    }
                }

                var conflicts = new List<string>();
                foreach (var item in plan)
                {
                    bool exists = File.Exists(item.Dest);
                    bool ours = prior != null && prior.Files.Any(f => f.Path.Equals(item.Rel, StringComparison.OrdinalIgnoreCase));

                    if (item.Config && exists) { item.Skip = true; continue; }
                    if (exists && !ours)
                    {
                        // The same bytes as the new file: an earlier manual install of this very tool. Take it over.
                        if (!Downloader.Sha256(item.Dest).Equals(HashOf(item), StringComparison.OrdinalIgnoreCase))
                            conflicts.Add(item.Rel);
                    }
                }

                if (conflicts.Count > 0)
                    throw new UpdaterException("These files are already in the game folder and did not come from this updater, "
                                               + "so I won't replace them:  " + string.Join(", ", conflicts)
                                               + ".  If they are an older copy of the same tool, move or delete them and run this again.");

                foreach (var item in plan.Where(p => !p.Skip && WowInstall.IsLocked(p.Dest)))
                    throw new UpdaterException(Path.GetFileName(item.Dest) + " is in use. Close World of Warcraft and try again.");

                long needed = plan.Where(p => !p.Skip).Sum(p => p.Size) + 64L * 1024 * 1024;
                long free = FreeSpace();
                if (free < needed)
                    throw new UpdaterException("Not enough free space in the game folder's drive: " + Ui.Bytes(needed) + " needed, "
                                               + Ui.Bytes(free) + " available.");

                // 3. Write each file beside its destination first, so a failure leaves the game folder as it was.
                foreach (var item in plan.Where(p => !p.Skip))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Dest));
                    item.Staged = item.Dest + ".reforged-new";
                    staged.Add(item);
                    item.Sha256 = StageFile(item);
                }

                // 4. Swap them in, putting the old ones back if any swap fails.
                Commit(staged);

                // 5. Files the previous version had that this one dropped (never settings files, never changed ones).
                if (prior != null)
                    foreach (var old in prior.Files.Where(f => !f.Config && !plan.Any(p => p.Rel.Equals(f.Path, StringComparison.OrdinalIgnoreCase))).ToList())
                        RemoveRecorded(old);

                // 6. Remember it.
                var record = new InstalledTool
                {
                    Id = def.Id,
                    Version = release.Version,
                    InstalledAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Assets = assetRecords,
                    PresetTag = presetTag
                };
                foreach (var item in plan)
                {
                    if (item.Skip)
                    {
                        // A settings file that was already there stays recorded only if we had put it there.
                        var before = prior?.Files.FirstOrDefault(f => f.Path.Equals(item.Rel, StringComparison.OrdinalIgnoreCase));
                        if (before != null) record.Files.Add(before);
                        continue;
                    }
                    record.Files.Add(new ToolFile { Path = item.Rel, Sha256 = item.Sha256, Size = item.Size, Config = item.Config });
                }

                _state.Tools.RemoveAll(t => t.Id.Equals(def.Id, StringComparison.OrdinalIgnoreCase));
                _state.Tools.Add(record);
                _save();

                foreach (var item in plan.Where(p => p.Skip))
                    Ui.Info("  kept your " + Path.GetFileName(item.Dest));
                Ui.Good("  installed " + def.Name + " " + release.Version);
            }
            finally
            {
                foreach (var zip in openZips) zip.Dispose();
                foreach (var item in staged) TryDelete(item.Staged);
                foreach (var pair in downloaded) { TryDelete(pair.Value); TryDelete(pair.Value + ".meta"); }
                try { if (!Directory.EnumerateFileSystemEntries(stageDir).Any()) Directory.Delete(stageDir); } catch { /* leftover only */ }
                try { string parent = Path.GetDirectoryName(stageDir); if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent); } catch { /* leftover only */ }
            }
        }

        private void PlanAsset(ToolDef def, ToolAsset asset, string downloadedPath, List<Planned> plan, List<ZipArchive> openZips)
        {
            string toolDir = ToolDir(def);

            if (!asset.IsZip)
            {
                string dest = Inside(toolDir, asset.Rules[0].To);
                plan.Add(new Planned
                {
                    Rel = Relative(dest),
                    Dest = dest,
                    Size = new FileInfo(downloadedPath).Length,
                    Open = () => new FileStream(downloadedPath, FileMode.Open, FileAccess.Read, FileShare.Read)
                });
                return;
            }

            ZipArchive zip;
            try { zip = new ZipArchive(new FileStream(downloadedPath, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read); }
            catch (InvalidDataException) { throw new UpdaterException(Path.GetFileName(downloadedPath).Replace(".part", string.Empty) + " is not a valid zip file."); }
            openZips.Add(zip);
            var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();

            if (asset.WholeZip)
            {
                // Release zips usually hold one folder named after the tool; its contents go straight into the tool's folder.
                string top = null;
                var names = files.Select(e => e.FullName.Replace('\\', '/')).ToList();
                var tops = names.Select(n => n.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (tops.Count == 1 && names.All(n => n.Contains("/"))) top = tops[0];

                foreach (var entry in files)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (top != null) name = name.Substring(top.Length + 1);
                    AddEntry(plan, toolDir, entry, name, false);
                }
                return;
            }

            foreach (var rule in asset.Rules)
            {
                var entry = files.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(rule.From, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                    throw new UpdaterException(def.Name + "'s download no longer contains " + rule.From
                                               + ", so I won't guess. The updater needs a fix for this tool; for now, install it by hand from "
                                               + def.RepoUrl + "/releases");
                AddEntry(plan, toolDir, entry, rule.To, rule.KeepYours);
            }
        }

        private void AddEntry(List<Planned> plan, string toolDir, ZipArchiveEntry entry, string name, bool config)
        {
            string dest = Inside(toolDir, name);
            plan.Add(new Planned { Rel = Relative(dest), Dest = dest, Config = config, Size = entry.Length, Open = () => entry.Open() });
        }

        private static string HashOf(Planned item)
        {
            using (var stream = item.Open())
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string StageFile(Planned item)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var source = item.Open())
            using (var output = new FileStream(item.Staged, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buffer = new byte[1 << 20];
                long written = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    written += read;
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                output.Flush(true);
                item.Size = written;
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>Moves the staged files into place, keeping each old file until all of them are in; on any failure, puts them all back.</summary>
        private static void Commit(List<Planned> staged)
        {
            var done = new List<Tuple<Planned, string>>();   // item that is now in place, and its old file (or null)
            Planned current = null;
            string currentBackup = null;

            try
            {
                foreach (var item in staged)
                {
                    current = item;
                    currentBackup = null;
                    if (File.Exists(item.Dest))
                    {
                        currentBackup = item.Dest + ".reforged-old";
                        TryDelete(currentBackup);
                        File.Move(item.Dest, currentBackup);
                    }
                    File.Move(item.Staged, item.Dest);
                    done.Add(Tuple.Create(item, currentBackup));
                    current = null;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    if (current != null && currentBackup != null && !File.Exists(current.Dest) && File.Exists(currentBackup))
                        File.Move(currentBackup, current.Dest);
                }
                catch { /* best effort */ }

                for (int i = done.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        File.Delete(done[i].Item1.Dest);
                        if (done[i].Item2 != null) File.Move(done[i].Item2, done[i].Item1.Dest);
                    }
                    catch { /* best effort */ }
                }
                throw new UpdaterException("Could not put the files in place (" + ex.Message + "). Nothing was changed.");
            }

            foreach (var entry in done)
                if (entry.Item2 != null) TryDelete(entry.Item2);
        }

        private long FreeSpace()
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_wow.Root))).AvailableFreeSpace; }
            catch { return long.MaxValue; }
        }

        private static void TryDelete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch { /* leftover only */ }
        }

        // ---------------------------------------------------------------- removing

        /// <summary>
        /// Deletes the files the tool placed, except settings files and files that changed since.
        /// Returns what was left behind and why.
        /// </summary>
        public List<string> Remove(ToolDef def)
        {
            var record = _state.FindTool(def.Id) ?? throw new UpdaterException(def.Name + " was not installed by this updater.");
            var left = new List<string>();

            foreach (var file in record.Files)
            {
                if (file.Config) { left.Add(Path.GetFileName(file.Path) + " (your settings)"); continue; }
                string why = RemoveRecorded(file);
                if (why != null) left.Add(why);
            }

            // Folders that held only the tool's files go too; anything else in them stays.
            if (def.Folder != null)
            {
                string dir = ToolDir(def);
                PruneEmptyDirectories(dir);
                if (Directory.Exists(dir))
                    left.Add(def.Folder + " (it still holds files that are not part of the download, such as the patcher's saved choices and Wow.exe backups)");
            }

            _state.Tools.RemoveAll(t => t.Id.Equals(def.Id, StringComparison.OrdinalIgnoreCase));
            _save();
            return left;
        }

        /// <summary>Deletes one recorded file if it is still the one that was placed. Returns why it stayed, or null.</summary>
        private string RemoveRecorded(ToolFile file)
        {
            string path = Absolute(file.Path);
            if (!File.Exists(path)) return null;

            if (new FileInfo(path).Length != file.Size || !Downloader.Sha256(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(path) + " (it changed since it was installed)";
            if (WowInstall.IsLocked(path))
                throw new UpdaterException(Path.GetFileName(path) + " is in use. Close World of Warcraft and try again.");

            File.Delete(path);
            return null;
        }

        private static void PruneEmptyDirectories(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (string sub in Directory.GetDirectories(dir)) PruneEmptyDirectories(sub);
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { /* leave it */ }
        }

        // ---------------------------------------------------------------- recommended settings

        /// <summary>Fetches the project's settings for a tool and compares them with the file in the game folder.</summary>
        public async Task<PresetPlan> PlanPresetAsync(ToolDef def, CancellationToken ct)
        {
            if (def.PresetUrl == null) throw new UpdaterException(def.Name + " has no recommended settings.");
            if (!IsInstalled(def)) throw new UpdaterException(def.Name + " is not installed. Install it first:  tools install " + def.Id);

            Downloader.Fetched fetched;
            try { fetched = await _http.GetBytesAsync(def.PresetUrl, ct).ConfigureAwait(false); }
            catch (HttpRequestException ex) { throw new UpdaterException("Could not get the recommended settings: " + ex.Message); }

            var plan = new PresetPlan { Bytes = fetched.Bytes, Tag = fetched.Tag, FilePath = SettingsPath(def) };
            plan.FileExists = File.Exists(plan.FilePath);
            if (!plan.FileExists) return plan;

            byte[] mine = File.ReadAllBytes(plan.FilePath);
            plan.Identical = mine.SequenceEqual(fetched.Bytes);
            if (!plan.Identical) plan.Differences = Differences(Encoding.UTF8.GetString(mine), Encoding.UTF8.GetString(fetched.Bytes));
            return plan;
        }

        /// <summary>The settings that differ, as "Name: yours -> recommended". Lines starting with ; or # are comments.</summary>
        internal static List<string> Differences(string mine, string recommended)
        {
            var a = ReadSettings(mine);
            var b = ReadSettings(recommended);
            var result = new List<string>();
            foreach (var pair in b)
            {
                a.TryGetValue(pair.Key, out string yours);
                if (!string.Equals(yours, pair.Value, StringComparison.Ordinal))
                    result.Add(pair.Key + ": " + (yours ?? "(not set)") + " -> " + pair.Value);
            }
            return result;
        }

        private static Dictionary<string, string> ReadSettings(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#' || trimmed[0] == '[') continue;
                int eq = trimmed.IndexOf('=');
                if (eq > 0) map[trimmed.Substring(0, eq).Trim()] = trimmed.Substring(eq + 1).Trim();
            }
            return map;
        }

        /// <summary>Replaces the settings file with the recommended one, keeping the old one as a .bak copy. Returns the backup path, or null.</summary>
        public string ApplyPreset(ToolDef def, PresetPlan plan)
        {
            var record = _state.FindTool(def.Id) ?? throw new UpdaterException(def.Name + " is not installed.");
            string path = plan.FilePath;
            string backup = null;

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path))
            {
                backup = path + ".bak";
                if (File.Exists(backup)) backup = path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".bak";
                File.Copy(path, backup, false);
            }

            string temp = path + ".reforged-new";
            File.WriteAllBytes(temp, plan.Bytes);
            try
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                if (!File.Exists(path) && backup != null) File.Copy(backup, path);
                throw new UpdaterException("Could not replace " + def.SettingsFile + " (" + ex.Message + "). Your file is as it was.");
            }

            string rel = Relative(path);
            record.Files.RemoveAll(f => f.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));
            record.Files.Add(new ToolFile { Path = rel, Sha256 = Downloader.Sha256(path), Size = plan.Bytes.Length, Config = true });
            record.PresetTag = plan.Tag;
            _save();
            return backup;
        }

        /// <summary>Records that the user has seen the current recommended settings and chose to keep theirs.</summary>
        public void AcknowledgePreset(ToolDef def, PresetPlan plan)
        {
            var record = _state.FindTool(def.Id);
            if (record == null) return;
            record.PresetTag = plan.Tag;
            _save();
        }

        // ---------------------------------------------------------------- running the patcher

        /// <summary>Checks the patcher can be started now and returns this game's Wow.exe. The caller has already asked the user.</summary>
        public string CheckCanRunPatcher()
        {
            if (!IsInstalled(ToolDef.Patcher) || !File.Exists(PatcherLauncher))
                throw new UpdaterException("The patcher is not installed in this game. Install it first:  tools install patcher");
            string exe = ExePath() ?? throw new UpdaterException("There is no Wow.exe in " + _wow.Root + ".");
            if (WowInstall.GameIsRunning())
                throw new UpdaterException("World of Warcraft is running. Close it first; the patcher cannot change Wow.exe while it runs.");
            return exe;
        }

        /// <summary>
        /// Opens the patcher on this game's Wow.exe. The user drives its menu; nothing is selected
        /// or confirmed for them. With <paramref name="inThisConsole"/> the patcher uses the current console.
        /// </summary>
        public Process StartPatcher(bool inThisConsole)
        {
            string exe = CheckCanRunPatcher();
            var start = new ProcessStartInfo(PatcherLauncher, "-Path \"" + exe + "\"")
            {
                WorkingDirectory = ToolDir(ToolDef.Patcher),
                UseShellExecute = !inThisConsole
            };
            return Process.Start(start);
        }
    }
}
