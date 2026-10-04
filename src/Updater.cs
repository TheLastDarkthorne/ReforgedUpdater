using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgedUpdater
{
    internal enum ModuleState
    {
        NotInstalled,

        /// <summary>The .mpq is in Data but the updater never installed it, so nothing is known about it.</summary>
        Untracked,

        UpToDate,
        UpdateAvailable,
        FileMissing,
        SizeMismatch
    }

    /// <summary>One module as it stands right now: what the site offers, what is on disk.</summary>
    internal sealed class ModuleStatus
    {
        public CatalogEntry Entry;
        public InstalledFile Local;
        public RemoteInfo Remote;
        public ModuleState State;
        public string Detail = string.Empty;

        public bool NeedsDownload => State == ModuleState.UpdateAvailable
                                  || State == ModuleState.FileMissing
                                  || State == ModuleState.SizeMismatch;

        public string LocalVersion => Local?.Version ?? (Local != null ? "installed" : "-");
        public string RemoteVersion => Entry.Version == null ? "-" : "v" + Entry.Version;
    }

    /// <summary>One patch that would be copied from another game.</summary>
    internal sealed class CopyEntry
    {
        public CatalogEntry Entry;
        public InstalledFile Source;
        public string SourcePath;
        public long Size;
    }

    /// <summary>What copying patches from another game would do: the patches, and why others are left out.</summary>
    internal sealed class CopyPlan
    {
        public string SourceName;
        public List<CopyEntry> Items = new List<CopyEntry>();
        public List<string> Skipped = new List<string>();

        public long Bytes => Items.Sum(i => i.Size);
    }

    /// <summary>The update engine: compares the site against the client folder and applies changes.</summary>
    internal sealed class Updater : IDisposable
    {
        private readonly Downloader _http = new Downloader();
        private readonly WowInstall _wow;
        private readonly Settings _settings;
        private readonly InstallState _state;

        public Updater(WowInstall wow, Settings settings)
        {
            _wow = wow;
            _settings = settings;
            _state = Store.Load<InstallState>(wow.StatePath);
        }

        public void Dispose() => _http.Dispose();

        public Task<RemoteInfo> ProbeAsync(string url, CancellationToken ct) => _http.ProbeAsync(url, ct);

        /// <summary>The edition this run works with, set by <see cref="ResolveEdition"/>.</summary>
        public Edition Edition { get; private set; }

        public string CatalogUrl => _settings.CatalogOverride ?? Edition?.CatalogUrl ?? Catalog.DefaultUrl;

        /// <summary>
        /// Decides which patch library this client uses: an explicit choice wins and is
        /// remembered for the client; otherwise the remembered one; otherwise Wow.exe's
        /// version. A vanilla client cannot say which server it is for, so that case asks.
        /// </summary>
        public Edition ResolveEdition(string requested)
        {
            Edition remembered = Edition.Find(_state.Edition);

            if (!string.IsNullOrWhiteSpace(requested))
            {
                var chosen = Edition.Parse(requested);
                if (remembered != null && remembered != chosen)
                    Ui.Warn("Switching this client from " + remembered.Title + " to " + chosen.Title
                            + ". Modules installed for " + remembered.Name + " that differ will show as updates.");

                WarnOnClientMismatch(chosen);
                Remember(chosen);
                return Edition = chosen;
            }

            if (remembered != null) return Edition = remembered;

            int? major = _wow.ClientMajorVersion();
            if (major == Edition.Wotlk.ClientMajor)
            {
                Remember(Edition.Wotlk);
                return Edition = Edition.Wotlk;
            }

            if (major == 1)
                throw new UpdaterException(
                    "This is a vanilla 1.12 client, and vanilla has a separate patch set per server. Tell the updater which:\n"
                    + "    ReforgedUpdater edition kronos     (standard 1.12 client - Kronos and similar servers)\n"
                    + "    ReforgedUpdater edition turtle     (Turtle WoW's custom client)");

            // No readable Wow.exe: keep the original behaviour, but do not remember a guess.
            return Edition = Edition.Wotlk;
        }

        private void Remember(Edition edition)
        {
            if (string.Equals(_state.Edition, edition.Name, StringComparison.OrdinalIgnoreCase)) return;
            _state.Edition = edition.Name;
            Save();
        }

        private void WarnOnClientMismatch(Edition edition)
        {
            int? major = _wow.ClientMajorVersion();
            if (major.HasValue && major.Value != edition.ClientMajor)
                Ui.Warn("Wow.exe in this folder is version " + major.Value + ".x, but " + edition.Title
                        + " patches are for a " + edition.ClientMajor + ".x client. Check the folder before installing.");
        }

        // One run can visit several games that share a downloads page; fetch each page once.
        private static readonly Dictionary<string, string> PageCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Drops the fetched pages, so a long-lived window can see the site change.</summary>
        public static void ForgetCachedPages() => PageCache.Clear();

        public async Task<List<CatalogEntry>> FetchCatalogAsync(CancellationToken ct)
        {
            string url = CatalogUrl;
            if (!PageCache.TryGetValue(url, out string html))
            {
                try
                {
                    html = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    throw new UpdaterException("Could not reach " + url + ": " + ex.Message);
                }
                PageCache[url] = html;
            }
            return Catalog.Parse(html);
        }

        /// <summary>
        /// Builds the status table. When <paramref name="probeRemote"/> is set, every
        /// installed module is HEADed so a silent re-upload (same version label, new
        /// bytes) is still caught.
        /// </summary>
        public async Task<List<ModuleStatus>> GetStatusAsync(List<CatalogEntry> catalog, bool probeRemote, CancellationToken ct)
        {
            var rows = catalog.Select(entry => new ModuleStatus
            {
                Entry = entry,
                Local = _state.Find(entry.Id)
            }).ToList();

            if (probeRemote)
            {
                // Probe anything already on disk, tracked or not: an untracked .mpq still
                // needs a remote size to say whether it is the current build.
                var present = rows.Where(r => r.Local != null || File.Exists(_wow.TargetPath(r.Entry))).ToList();
                if (present.Count > 0)
                    await ProbeAllAsync(present, ct).ConfigureAwait(false);
            }

            foreach (var row in rows) Classify(row);

            _state.LastCheck = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            Save();
            return rows;
        }

        private async Task ProbeAllAsync(List<ModuleStatus> rows, CancellationToken ct)
        {
            using (var gate = new SemaphoreSlim(4))
            {
                var probes = rows.Select(async row =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try { row.Remote = await _http.ProbeAsync(row.Entry.Url, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        Ui.Warn(row.Entry.Display + ": could not check the server (" + ex.Message + ")");
                    }
                    finally { gate.Release(); }
                });

                await Task.WhenAll(probes).ConfigureAwait(false);
            }
        }

        private void Classify(ModuleStatus row)
        {
            string path = _wow.TargetPath(row.Entry);

            if (row.Local == null)
            {
                // The slot may belong to a sibling variant that is already tracked
                // (patch-S standard vs standalone), in which case this row is simply absent.
                bool slotTaken = _state.Files.Any(f =>
                    string.Equals(f.FileName, row.Entry.FileName, StringComparison.OrdinalIgnoreCase));

                // A hand-downloaded .mpq: present, but with no record of which build it is.
                if (File.Exists(path) && !slotTaken)
                {
                    long size = new FileInfo(path).Length;
                    row.State = ModuleState.Untracked;
                    row.Detail = row.Remote != null && row.Remote.Size > 0
                        ? Ui.Bytes(size) + " in Data, " + (size == row.Remote.Size
                            ? "matches the current build" : "differs from the current build")
                        : Ui.Bytes(size) + " in Data, not tracked";
                }
                else
                {
                    row.State = ModuleState.NotInstalled;
                }
                return;
            }

            string target = path;
            if (!File.Exists(target))
            {
                row.State = ModuleState.FileMissing;
                row.Detail = "the .mpq is gone from Data";
                return;
            }

            long onDisk = new FileInfo(target).Length;
            if (row.Local.Size > 0 && onDisk != row.Local.Size)
            {
                row.State = ModuleState.SizeMismatch;
                // Exact byte counts: a one-byte difference is invisible at MB rounding.
                row.Detail = "on disk " + onDisk.ToString("N0", CultureInfo.InvariantCulture)
                             + " bytes, expected " + row.Local.Size.ToString("N0", CultureInfo.InvariantCulture);
                return;
            }

            // Known at adoption time to be something other than the published build. A same
            // length build would slip past every comparison below, so this is checked first.
            if (row.Local.ContentDiffers)
            {
                row.State = ModuleState.UpdateAvailable;
                row.Detail = "not the published build";
                return;
            }

            // Same slot, different source file: a server-specific build after an edition
            // switch (Kronos patch-A vs Turtle patch-A), or a variant the site moved.
            if (!string.IsNullOrEmpty(row.Local.Url) &&
                !string.Equals(row.Local.Url, row.Entry.Url, StringComparison.OrdinalIgnoreCase))
            {
                row.State = ModuleState.UpdateAvailable;
                row.Detail = "installed from a different source file";
                return;
            }

            if (row.Entry.Version != null && row.Local.Version != null &&
                !row.Entry.Version.Equals(row.Local.Version, StringComparison.OrdinalIgnoreCase))
            {
                row.State = ModuleState.UpdateAvailable;
                row.Detail = "v" + row.Local.Version + " -> v" + row.Entry.Version;
                return;
            }

            if (row.Remote != null && !string.IsNullOrEmpty(row.Remote.ETag) && !string.IsNullOrEmpty(row.Local.ETag)
                && !row.Remote.ETag.Equals(row.Local.ETag, StringComparison.Ordinal))
            {
                row.State = ModuleState.UpdateAvailable;
                row.Detail = "the file changed on the server";
                return;
            }

            if (row.Remote != null && row.Remote.Size > 0 && row.Local.Size > 0 && row.Remote.Size != row.Local.Size)
            {
                row.State = ModuleState.UpdateAvailable;
                row.Detail = "size changed: " + Ui.Bytes(row.Local.Size) + " -> " + Ui.Bytes(row.Remote.Size);
                return;
            }

            row.State = ModuleState.UpToDate;
        }

        /// <summary>Downloads and installs the given modules. Returns the number that succeeded.</summary>
        public async Task<int> ApplyAsync(List<ModuleStatus> rows, bool dryRun, bool hash, CancellationToken ct)
        {
            if (rows.Count == 0) return 0;

            _wow.EnsureDirectories();

            foreach (var row in rows.Where(r => r.Remote == null))
                row.Remote = await _http.ProbeAsync(row.Entry.Url, ct).ConfigureAwait(false);

            long needed = rows.Sum(r => Math.Max(0, r.Remote.Size)) + (256L * 1024 * 1024);
            long free = _wow.FreeSpace();
            if (free < needed)
                throw new UpdaterException("Not enough free space: " + Ui.Bytes(needed) + " needed, "
                                           + Ui.Bytes(free) + " available on the client's drive.");

            if (dryRun)
            {
                foreach (var row in rows)
                    Ui.Info("  would download " + row.Entry.Display + "  " + Ui.Bytes(row.Remote.Size));
                return 0;
            }

            if (WowInstall.GameIsRunning())
                Ui.Warn("World of Warcraft looks like it is running. Close it before installing, or the swap will fail.");

            int installed = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                string label = string.Format(CultureInfo.InvariantCulture, "[{0}/{1}] {2}",
                    i + 1, rows.Count, row.Entry.Display.PadRight(20));

                string partPath = Path.Combine(_wow.CacheDir, row.Entry.FileName + ".part");
                string digest = await _http.FetchAsync(label, row.Entry.Url, row.Remote, partPath, hash, ct)
                                           .ConfigureAwait(false);

                Install(row, partPath, digest);
                installed++;

                Ui.Good("  installed " + row.Entry.Display
                        + (row.Entry.Version != null ? " v" + row.Entry.Version : string.Empty)
                        + "  (" + Ui.Bytes(row.Remote.Size) + ")");
            }

            return installed;
        }

        /// <summary>Swaps the finished .part into Data, keeping the old file until the move succeeds.</summary>
        private void Install(ModuleStatus row, string partPath, string digest)
        {
            Swap(row.Entry.FileName, partPath);
            ForgetSiblings(row.Entry.Id, row.Entry.FileName);

            _state.Record(new InstalledFile
            {
                Id = row.Entry.Id,
                FileName = row.Entry.FileName,
                Url = row.Entry.Url,
                Version = row.Entry.Version,
                ETag = row.Remote.ETag,
                LastModified = row.Remote.LastModified,
                Size = new FileInfo(_wow.TargetPath(row.Entry)).Length,
                Sha256 = digest,
                InstalledAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            });
            Save();
        }

        /// <summary>Moves a finished file into Data, putting the previous file back if the move fails.</summary>
        private void Swap(string fileName, string partPath)
        {
            string target = Path.Combine(_wow.DataDir, fileName);
            string backup = target + ".old";

            if (WowInstall.IsLocked(target))
                throw new UpdaterException("Cannot replace " + fileName
                                           + " because it is in use. Close World of Warcraft and run the update again.");

            try
            {
                if (File.Exists(backup)) File.Delete(backup);
                if (File.Exists(target)) File.Move(target, backup);

                File.Move(partPath, target);

                if (File.Exists(backup)) File.Delete(backup);
                try { File.Delete(partPath + ".meta"); } catch { /* leftover only */ }
            }
            catch (Exception ex)
            {
                // Put the previous file back so the client is never left without one.
                if (!File.Exists(target) && File.Exists(backup)) File.Move(backup, target);
                throw new UpdaterException("Could not install " + fileName + ": " + ex.Message);
            }
        }

        /// <summary>Installing one variant of a slot replaces any other variant of the same slot.</summary>
        private void ForgetSiblings(string id, string fileName)
        {
            foreach (var sibling in _state.Files
                         .Where(f => !string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(f.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                _state.Forget(sibling.Id);
                Ui.Info("  (replaced " + sibling.Id + ", which used the same " + fileName + " slot)");
            }
        }

        // ------------------------------------------------------------ copying between games

        /// <summary>
        /// Works out which of another game's installed patches this game is missing. Patches
        /// only copy between games that use the same patch set, because each set has its own
        /// builds of a few modules; and only ones this game does not already have, so nothing
        /// here is overwritten.
        /// </summary>
        /// <param name="only">Module ids to copy, or null for every patch the other game has.</param>
        public CopyPlan PlanCopy(Updater source, string sourceName, List<CatalogEntry> catalog, List<string> only)
        {
            Edition mine = Edition.Find(_state.Edition);
            Edition theirs = Edition.Find(source._state.Edition);

            if (string.Equals(Path.GetFullPath(source._wow.DataDir).TrimEnd('\\', '/'),
                              Path.GetFullPath(_wow.DataDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new UpdaterException(sourceName + " uses the same Data folder as this game, so there is nothing to copy.");
            if (mine == null)
                throw new UpdaterException("This game has no patch set chosen yet, so I can't tell whether "
                                           + sourceName + "'s patches fit it.");
            if (theirs == null)
                throw new UpdaterException(sourceName + " has no patch set chosen yet. Choose one for it first, "
                                           + "for example:  ReforgedUpdater edition " + mine.Name + " --game " + sourceName);
            if (mine != theirs)
                throw new UpdaterException(sourceName + " uses " + theirs.Title + " and this game uses " + mine.Title
                                           + ". Each patch set has its own builds, so patches only copy between games that use the same one.");

            var wanted = only == null || only.Count == 0
                ? null
                : new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);
            var plan = new CopyPlan { SourceName = sourceName };

            foreach (var record in source._state.Files)
            {
                if (wanted != null && !wanted.Contains(record.Id)) continue;

                var entry = catalog.FirstOrDefault(e => string.Equals(e.Id, record.Id, StringComparison.OrdinalIgnoreCase));
                if (entry == null) { plan.Skipped.Add(record.Id + ": no longer on the downloads page"); continue; }

                string from = source._wow.TargetPath(entry);
                if (!File.Exists(from)) { plan.Skipped.Add(entry.Display + ": the file is missing from " + sourceName); continue; }

                long size = new FileInfo(from).Length;
                if (record.Size > 0 && size != record.Size)
                {
                    plan.Skipped.Add(entry.Display + ": the file in " + sourceName + " has changed since it was installed");
                    continue;
                }

                if (File.Exists(_wow.TargetPath(entry)))
                {
                    plan.Skipped.Add(entry.Display + ": this game already has " + entry.FileName);
                    continue;
                }

                plan.Items.Add(new CopyEntry { Entry = entry, Source = record, SourcePath = from, Size = size });
            }

            if (wanted != null)
                foreach (string id in wanted.Where(id => source._state.Find(id) == null))
                    plan.Skipped.Add(id + ": not installed in " + sourceName);

            // Hand-downloaded files carry no record of which build they are, so they are not copied.
            if (wanted == null)
            {
                int untracked = catalog
                    .Where(e => source._state.Find(e.Id) == null
                             && !source._state.Files.Any(f => string.Equals(f.FileName, e.FileName, StringComparison.OrdinalIgnoreCase))
                             && File.Exists(source._wow.TargetPath(e)))
                    .Select(e => e.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (untracked > 0)
                    plan.Skipped.Add(untracked + " .mpq file(s) in " + sourceName + " are not tracked, so I can't tell which build they are. "
                                     + "Adopt them there first to copy them");
            }

            return plan;
        }

        /// <summary>
        /// Copies the planned patches into this game's Data folder. Each one goes to a temporary
        /// file first and is hashed on the way; it only replaces anything once it is complete,
        /// and is dropped if it does not match what the other game recorded when it installed it.
        /// With <paramref name="verifyCopy"/> each finished copy is also read back from this game's
        /// drive and checked, which catches a bad write at the cost of reading every file again.
        /// Returns the number copied.
        /// </summary>
        public async Task<int> CopyFromAsync(CopyPlan plan, bool verifyCopy, CancellationToken ct)
        {
            if (plan.Items.Count == 0) return 0;

            _wow.EnsureDirectories();

            long needed = plan.Bytes + (256L * 1024 * 1024);
            long free = _wow.FreeSpace();
            if (free < needed)
                throw new UpdaterException("Not enough free space: " + Ui.Bytes(needed) + " needed, "
                                           + Ui.Bytes(free) + " available on this game's drive.");

            if (WowInstall.GameIsRunning())
                Ui.Warn("World of Warcraft looks like it is running. Close it before copying, or the swap will fail.");

            int copied = 0;
            for (int i = 0; i < plan.Items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var item = plan.Items[i];
                string label = string.Format(CultureInfo.InvariantCulture, "[{0}/{1}] {2}",
                    i + 1, plan.Items.Count, item.Entry.Display.PadRight(20));
                string partPath = Path.Combine(_wow.CacheDir, item.Entry.FileName + ".copy");

                string digest;
                try
                {
                    digest = await Task.Run(() => CopyFile(item.SourcePath, partPath, item.Size, label, verifyCopy, ct), ct).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    Ui.EndProgress();
                    Ui.Warn(item.Entry.Display + ": could not be copied (" + ex.Message + ")");
                    continue;
                }
                Ui.EndProgress();

                if (!string.IsNullOrEmpty(item.Source.Sha256) && !digest.Equals(item.Source.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(partPath); } catch { /* leftover only */ }
                    Ui.Warn(item.Entry.Display + ": the file in " + plan.SourceName + " doesn't match what was recorded when it was installed, "
                            + "so I didn't copy it. Check it there with \"verify --deep\".");
                    continue;
                }

                if (verifyCopy)
                {
                    Ui.Info("  checking the copy of " + item.Entry.Display + " (" + Ui.Bytes(item.Size) + ")...");
                    bool intact = await Task.Run(() => ReadBackMatches(partPath, digest), ct).ConfigureAwait(false);
                    if (!intact)
                    {
                        try { File.Delete(partPath); } catch { /* leftover only */ }
                        Ui.Warn(item.Entry.Display + ": the copy didn't read back the same as the original, so I didn't use it. "
                                + "The drive may have a problem; try copying it again.");
                        continue;
                    }
                }

                try { Swap(item.Entry.FileName, partPath); }
                catch (UpdaterException)
                {
                    try { File.Delete(partPath); } catch { /* leftover only */ }
                    throw;
                }
                ForgetSiblings(item.Entry.Id, item.Entry.FileName);

                // The record travels with the file, so a copy of an older build still shows as an update.
                _state.Record(new InstalledFile
                {
                    Id = item.Source.Id,
                    FileName = item.Source.FileName,
                    Url = item.Source.Url,
                    Version = item.Source.Version,
                    ETag = item.Source.ETag,
                    LastModified = item.Source.LastModified,
                    Size = new FileInfo(_wow.TargetPath(item.Entry)).Length,
                    Sha256 = digest,
                    InstalledAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ContentDiffers = item.Source.ContentDiffers
                });
                Save();
                copied++;

                Ui.Good("  copied " + item.Entry.Display
                        + (item.Source.Version != null ? " v" + item.Source.Version : string.Empty)
                        + "  (" + Ui.Bytes(item.Size) + ")");
            }

            return copied;
        }

        /// <summary>Reads a finished copy back from disk and checks it against the checksum of the original.</summary>
        private static bool ReadBackMatches(string path, string expected)
        {
            try { return Downloader.Sha256(path).Equals(expected, StringComparison.OrdinalIgnoreCase); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>Copies one file, reporting progress, and returns its SHA-256. Leaves nothing behind on failure.</summary>
        private static string CopyFile(string from, string to, long size, string label, bool flushToDisk, CancellationToken ct)
        {
            var buffer = new byte[1024 * 1024];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long done = 0, lastReport = 0;

            try
            {
                // The other game may be open; read with the sharing the client itself allows.
                using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                                  buffer.Length, FileOptions.SequentialScan))
                using (var output = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length))
                using (var sha = SHA256.Create())
                {
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        done += read;

                        if (clock.ElapsedMilliseconds - lastReport >= 250)
                        {
                            lastReport = clock.ElapsedMilliseconds;
                            Ui.Progress(label, done, size, done / Math.Max(0.001, clock.Elapsed.TotalSeconds));
                        }
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);

                    // When the copy is going to be read back, make sure the bytes have actually left the cache first.
                    if (flushToDisk) output.Flush(true);

                    if (done != size)
                        throw new IOException("copied " + done + " of " + size + " bytes - the file changed while it was being copied");

                    Ui.Progress(label, done, size, done / Math.Max(0.001, clock.Elapsed.TotalSeconds));
                    return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
                }
            }
            catch
            {
                try { File.Delete(to); } catch { /* leftover only */ }
                throw;
            }
        }

        /// <summary>
        /// Takes ownership of .mpq files that are already in Data but were never installed
        /// by the updater - hand-downloaded ones.
        ///
        /// With <paramref name="verify"/> the file is identified by its content, using the
        /// server's ETag as a checksum; that reads every byte but settles the question. Without
        /// it, only the size is compared, which cannot tell apart two builds of equal length.
        /// Either way a file that is not the published build is recorded as an unknown older
        /// build, so it shows up as an available update rather than as a fresh install.
        /// </summary>
        public async Task<int> AdoptAsync(List<ModuleStatus> rows, bool verify, CancellationToken ct)
        {
            foreach (var row in rows.Where(r => r.Remote == null))
            {
                try { row.Remote = await _http.ProbeAsync(row.Entry.Url, ct).ConfigureAwait(false); }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    Ui.Warn(row.Entry.Display + ": could not check the server (" + ex.Message + ")");
                }
            }

            int adopted = 0;

            // Variants share one slot (patch-S.mpq), so decide per file, not per module.
            foreach (var slot in rows.GroupBy(r => r.Entry.FileName, StringComparer.OrdinalIgnoreCase))
            {
                string path = _wow.TargetPath(slot.First().Entry);
                if (!File.Exists(path)) continue;

                long size = new FileInfo(path).Length;

                // Content check first: the server's ETag is derived from the bytes, so a
                // match proves the file is the published build rather than merely its size.
                if (verify && AdoptByContent(slot.ToList(), path, size, ref adopted)) continue;

                var matches = slot.Where(r => r.Remote != null && r.Remote.Size == size).ToList();

                if (matches.Count > 1)
                {
                    Ui.Warn(slot.Key + ": several variants have this exact size ("
                            + string.Join(", ", matches.Select(m => m.Entry.Id))
                            + "). Name the one you have, for example \"adopt " + matches[0].Entry.Id + "\".");
                    continue;
                }

                if (matches.Count == 1)
                {
                    Adopt(matches[0], path, size, current: true, hash: verify);
                    adopted++;
                    continue;
                }

                // No size match. With one candidate we can still adopt it as an older build;
                // with several we cannot tell which variant the file is.
                var candidates = slot.ToList();
                if (candidates.Count > 1)
                {
                    Ui.Warn(slot.Key + ": does not match any current variant, so the updater cannot tell which one it is. "
                            + "Run \"install " + candidates[0].Entry.Id + "\" to replace it, or delete it first.");
                    continue;
                }

                Adopt(candidates[0], path, size, current: false, hash: verify);
                adopted++;
            }

            Save();
            return adopted;
        }

        /// <summary>
        /// Identifies the file by content rather than by size. Returns true when the slot was
        /// settled here - either adopted, or proven to be none of the known variants.
        /// </summary>
        private bool AdoptByContent(List<ModuleStatus> slot, string path, long size, ref int adopted)
        {
            Ui.Info("  checking " + Path.GetFileName(path) + " (" + Ui.Bytes(size) + ") against the server...");

            bool anyConclusive = false;
            string sha256 = null;

            foreach (var row in slot.Where(r => r.Remote != null && !string.IsNullOrEmpty(r.Remote.ETag)))
            {
                var result = ContentCheck.Check(path, row.Remote.ETag);
                sha256 = sha256 ?? result.Sha256;
                if (result.Status == ContentMatch.Unknown) continue;

                anyConclusive = true;
                if (result.Status != ContentMatch.Match) continue;

                Record(row, size, current: true, sha256: result.Sha256);
                Ui.Good("  adopted " + row.Entry.Display
                        + (row.Entry.Version != null ? " v" + row.Entry.Version : string.Empty)
                        + " (content verified against the server)");
                adopted++;
                return true;
            }

            if (!anyConclusive)
            {
                Ui.Info("  (content could not be verified; falling back to a size comparison)");
                return false;
            }

            // The bytes reproduce no published variant, so this is an older or altered build.
            // With one candidate we can still record it; with several we cannot say which.
            if (slot.Count > 1)
            {
                Ui.Warn(Path.GetFileName(path) + ": content matches no current variant, so the updater cannot tell "
                        + "which one it is. Run \"install " + slot[0].Entry.Id + "\" to replace it, or delete it first.");
                return true;
            }

            Record(slot[0], size, current: false, sha256: sha256);
            Ui.Info("  adopted " + slot[0].Entry.Display + " (" + Ui.Bytes(size)
                    + ", content differs from the published build - it will show as an available update)");
            adopted++;
            return true;
        }

        private void Adopt(ModuleStatus row, string path, long size, bool current, bool hash)
        {
            if (hash) Ui.Info("  hashing " + row.Entry.FileName + " (" + Ui.Bytes(size) + ")...");

            Record(row, size, current, hash ? Downloader.Sha256(path) : null);

            if (current)
                Ui.Good("  adopted " + row.Entry.Display
                        + (row.Entry.Version != null ? " v" + row.Entry.Version : string.Empty)
                        + " (" + Ui.Bytes(size) + ", size matches the current build)");
            else
                Ui.Info("  adopted " + row.Entry.Display + " (" + Ui.Bytes(size)
                        + ", older or modified build - it will show as an available update)");
        }

        private void Record(ModuleStatus row, long size, bool current, string sha256)
        {
            _state.Record(new InstalledFile
            {
                Id = row.Entry.Id,
                FileName = row.Entry.FileName,
                Url = row.Entry.Url,
                // Only claim a version when the bytes match what the site is serving now.
                Version = current ? row.Entry.Version : null,
                ETag = current ? row.Remote?.ETag : null,
                LastModified = current ? row.Remote?.LastModified : null,
                Size = size,
                Sha256 = sha256,
                InstalledAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ContentDiffers = !current
            });
        }

        public bool Remove(CatalogEntry entry, bool deleteFile)
        {
            string target = _wow.TargetPath(entry);
            bool removed = false;

            if (deleteFile && File.Exists(target))
            {
                if (WowInstall.IsLocked(target))
                    throw new UpdaterException(entry.FileName + " is in use. Close World of Warcraft first.");
                File.Delete(target);
                removed = true;
            }

            _state.Forget(entry.Id);
            Save();
            return removed;
        }

        /// <summary>
        /// Reads every installed file and reports what its bytes actually are. The server's
        /// ETag is the stronger test, because it compares against what is published right
        /// now; the SHA-256 recorded at install time is the fallback, and only detects
        /// change since that install.
        /// </summary>
        public void VerifyDeep(List<ModuleStatus> rows, Action<string> report)
        {
            foreach (var row in rows.Where(r => r.Local != null))
            {
                string target = _wow.TargetPath(row.Entry);
                if (!File.Exists(target)) { report(row.Entry.Display + ": file missing"); continue; }

                Ui.Info("  reading " + row.Entry.FileName + " (" + Ui.Bytes(new FileInfo(target).Length) + ")...");

                string remoteTag = row.Remote?.ETag;
                if (!string.IsNullOrEmpty(remoteTag))
                {
                    var result = ContentCheck.Check(target, remoteTag);

                    // Record the verdict, so the cheap status check reflects what the
                    // expensive read just proved instead of going on stale metadata.
                    if (result.Status == ContentMatch.Match)
                    {
                        row.Local.ContentDiffers = false;
                        row.Local.Version = row.Entry.Version;
                        row.Local.ETag = remoteTag;
                        if (result.Sha256 != null) row.Local.Sha256 = result.Sha256;
                        Save();

                        report(row.Entry.Display + ": ok, identical to the published build");
                        continue;
                    }

                    if (result.Status == ContentMatch.Mismatch)
                    {
                        row.Local.ContentDiffers = true;
                        Save();

                        report(row.Entry.Display + ": DIFFERS from the published build - refresh with \"update "
                               + row.Entry.Id + "\"");
                        continue;
                    }
                }

                if (string.IsNullOrEmpty(row.Local.Sha256))
                {
                    report(row.Entry.Display + ": cannot be checked (no server checksum, no recorded hash)");
                    continue;
                }

                string digest = Downloader.Sha256(target);
                if (digest.Equals(row.Local.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    report(row.Entry.Display + ": unchanged since it was installed");
                }
                else
                {
                    row.Local.ContentDiffers = true;
                    Save();
                    report(row.Entry.Display + ": CONTENT CHANGED since install - reinstall with \"update " + row.Entry.Id + "\"");
                }
            }
        }

        public InstallState State => _state;
        public void Save() => Store.Save(_wow.StatePath, _state);
    }
}
