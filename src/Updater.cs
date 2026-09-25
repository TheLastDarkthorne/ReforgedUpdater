using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
            string target = _wow.TargetPath(row.Entry);
            string backup = target + ".old";

            if (WowInstall.IsLocked(target))
                throw new UpdaterException("Cannot replace " + row.Entry.FileName
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
                throw new UpdaterException("Could not install " + row.Entry.FileName + ": " + ex.Message);
            }

            // Installing one variant of a slot replaces any other variant of the same slot.
            foreach (var sibling in _state.Files
                         .Where(f => !string.Equals(f.Id, row.Entry.Id, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(f.FileName, row.Entry.FileName, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                _state.Forget(sibling.Id);
                Ui.Info("  (replaced " + sibling.Id + ", which used the same " + row.Entry.FileName + " slot)");
            }

            _state.Record(new InstalledFile
            {
                Id = row.Entry.Id,
                FileName = row.Entry.FileName,
                Url = row.Entry.Url,
                Version = row.Entry.Version,
                ETag = row.Remote.ETag,
                LastModified = row.Remote.LastModified,
                Size = new FileInfo(target).Length,
                Sha256 = digest,
                InstalledAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            });
            Save();
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
