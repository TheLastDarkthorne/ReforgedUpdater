using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgedUpdater
{
    /// <summary>What the server currently says about a file.</summary>
    internal sealed class RemoteInfo
    {
        public long Size = -1;
        public string ETag;
        public string LastModified;
        public bool SupportsRange;
    }

    /// <summary>
    /// Resumable downloader for the multi-gigabyte patch archives. Bytes land in a
    /// .part file beside the destination; a matching .meta file records which remote
    /// version those bytes belong to, so a resume never splices two different builds.
    /// </summary>
    internal sealed class Downloader : IDisposable
    {
        private const int BufferSize = 1024 * 1024;
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
        private const int MaxAttempts = 5;

        private readonly HttpClient _client;

        public Downloader()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 8;
            ServicePointManager.Expect100Continue = false;

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _client = new HttpClient(handler)
            {
                // Per-read stalls are caught below; a global timeout would kill long transfers.
                Timeout = Timeout.InfiniteTimeSpan
            };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("ReforgedUpdater/1.0 (+https://projectreforged.github.io/wotlk/)");
        }

        public async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                using (var response = await _client.GetAsync(url, cts.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>HEAD the file; falls back to a one-byte ranged GET for servers that refuse HEAD.</summary>
        public async Task<RemoteInfo> ProbeAsync(string url, CancellationToken ct)
        {
            var info = await TryProbeAsync(url, HttpMethod.Head, ct).ConfigureAwait(false);
            if (info != null) return info;

            info = await TryProbeAsync(url, HttpMethod.Get, ct, rangeProbe: true).ConfigureAwait(false);
            if (info != null) return info;

            throw new UpdaterException("The server did not answer a metadata request for " + url);
        }

        private async Task<RemoteInfo> TryProbeAsync(string url, HttpMethod method, CancellationToken ct, bool rangeProbe = false)
        {
            using (var request = new HttpRequestMessage(method, url))
            {
                if (rangeProbe) request.Headers.Range = new RangeHeaderValue(0, 0);

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(30));
                    HttpResponseMessage response;
                    try
                    {
                        response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                                .ConfigureAwait(false);
                    }
                    catch (HttpRequestException) { return null; }

                    using (response)
                    {
                        if (!response.IsSuccessStatusCode) return null;

                        var info = new RemoteInfo
                        {
                            ETag = response.Headers.ETag?.Tag,
                            LastModified = response.Content.Headers.LastModified?.UtcDateTime.ToString("R"),
                            SupportsRange = response.Headers.AcceptRanges.Contains("bytes")
                                            || response.StatusCode == HttpStatusCode.PartialContent
                        };

                        info.Size = response.Content.Headers.ContentRange?.Length
                                    ?? response.Content.Headers.ContentLength
                                    ?? -1;

                        return info;
                    }
                }
            }
        }

        /// <summary>
        /// Downloads <paramref name="url"/> into <paramref name="partPath"/>, resuming an
        /// earlier attempt when the remote file is unchanged. Returns the SHA-256 of the
        /// finished file, or null when hashing is turned off.
        /// </summary>
        public async Task<string> FetchAsync(string label, string url, RemoteInfo remote, string partPath,
                                             bool hash, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(partPath));
            string metaPath = partPath + ".meta";

            long offset = ResumeOffset(partPath, metaPath, remote);
            if (offset == 0) SafeDelete(partPath);
            WriteMeta(metaPath, remote);

            var meter = new RateMeter();
            long total = remote.Size;
            Exception lastFailure = null;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                    if (total > 0 && offset >= total) break; // already complete
                    if (offset > 0 && !remote.SupportsRange) { SafeDelete(partPath); offset = 0; }

                    await TransferAsync(label, url, remote, partPath, offset, total, meter, ct).ConfigureAwait(false);

                    long finalSize = new FileInfo(partPath).Length;
                    if (total > 0 && finalSize != total)
                        throw new IOException("Incomplete transfer: got " + finalSize + " of " + total + " bytes.");

                    lastFailure = null;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is OperationCanceledException)
                {
                    lastFailure = ex;
                    Ui.EndProgress();
                    if (attempt == MaxAttempts) break;

                    int delay = Math.Min(30, attempt * 5);
                    Ui.Warn(label + ": " + ex.Message + " - retrying in " + delay + "s (" + attempt + "/" + (MaxAttempts - 1) + ")");
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
                }
            }

            Ui.EndProgress();
            if (lastFailure != null)
                throw new UpdaterException(label + " failed after " + MaxAttempts + " attempts: " + lastFailure.Message);

            // The .meta file stays until the caller has moved the .part into place: if the
            // install step fails (a locked .mpq, say), the next run resumes instead of
            // fetching gigabytes again.
            return hash ? await Task.Run(() => Sha256(partPath), ct).ConfigureAwait(false) : null;
        }

        private async Task TransferAsync(string label, string url, RemoteInfo remote, string partPath,
                                         long offset, long total, RateMeter meter, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (offset > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                    // If the file changed since we started, the server answers 200 with the
                    // whole body instead of 206, and we start over rather than corrupt the mix.
                    if (!string.IsNullOrEmpty(remote.ETag) &&
                        EntityTagHeaderValue.TryParse(remote.ETag, out var tag))
                        request.Headers.IfRange = new RangeConditionHeaderValue(tag);
                }

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(StallTimeout);
                    using (var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                                       .ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                        {
                            SafeDelete(partPath);
                            throw new IOException("The server rejected the resume range; restarting.");
                        }

                        response.EnsureSuccessStatusCode();

                        bool serverIgnoredRange = offset > 0 && response.StatusCode != HttpStatusCode.PartialContent;
                        if (serverIgnoredRange)
                        {
                            Ui.Warn(label + ": the remote file changed, restarting the download.");
                            SafeDelete(partPath);
                            offset = 0;
                        }

                        if (total <= 0)
                            total = (response.Content.Headers.ContentLength ?? 0) + offset;

                        using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var destination = new FileStream(partPath, offset > 0 ? FileMode.Append : FileMode.Create,
                                                                FileAccess.Write, FileShare.Read, BufferSize, FileOptions.SequentialScan))
                        {
                            var buffer = new byte[BufferSize];
                            long done = offset;
                            meter.Start(done);

                            while (true)
                            {
                                cts.CancelAfter(StallTimeout);
                                int read = await source.ReadAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false);
                                if (read == 0) break;

                                await destination.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                                done += read;

                                if (meter.ShouldReport(done))
                                    Ui.Progress(label, done, total, meter.BytesPerSecond);
                            }

                            await destination.FlushAsync(ct).ConfigureAwait(false);
                            Ui.Progress(label, done, total, meter.BytesPerSecond);
                        }
                    }
                }
            }
        }

        /// <summary>Only resume when the .meta file proves the bytes belong to this exact remote version.</summary>
        private static long ResumeOffset(string partPath, string metaPath, RemoteInfo remote)
        {
            if (!File.Exists(partPath) || !remote.SupportsRange) return 0;

            try
            {
                if (!File.Exists(metaPath)) return 0;
                string[] lines = File.ReadAllLines(metaPath);
                if (lines.Length < 2) return 0;

                bool sameTag = string.Equals(lines[0], remote.ETag ?? string.Empty, StringComparison.Ordinal);
                bool sameSize = string.Equals(lines[1], remote.Size.ToString(), StringComparison.Ordinal);
                if (!sameTag || !sameSize) return 0;

                long length = new FileInfo(partPath).Length;
                return remote.Size > 0 && length > remote.Size ? 0 : length;
            }
            catch { return 0; }
        }

        private static void WriteMeta(string metaPath, RemoteInfo remote)
        {
            try { File.WriteAllLines(metaPath, new[] { remote.ETag ?? string.Empty, remote.Size.ToString() }); }
            catch { /* resume is an optimisation, not a requirement */ }
        }

        public static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
            {
                byte[] digest = sha.ComputeHash(stream);
                return BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }

        public void Dispose() => _client.Dispose();

        /// <summary>Throttles progress redraws and reports a smoothed transfer rate.</summary>
        private sealed class RateMeter
        {
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private long _windowStartBytes;
            private TimeSpan _windowStart;
            private TimeSpan _lastReport;

            public double BytesPerSecond { get; private set; }

            public void Start(long bytesSoFar)
            {
                _windowStartBytes = bytesSoFar;
                _windowStart = _clock.Elapsed;
            }

            public bool ShouldReport(long bytesSoFar)
            {
                TimeSpan now = _clock.Elapsed;
                if ((now - _lastReport).TotalMilliseconds < 250) return false;
                _lastReport = now;

                double window = (now - _windowStart).TotalSeconds;
                if (window >= 1)
                {
                    double instant = (bytesSoFar - _windowStartBytes) / window;
                    BytesPerSecond = BytesPerSecond <= 0 ? instant : (BytesPerSecond * 0.7) + (instant * 0.3);
                    _windowStartBytes = bytesSoFar;
                    _windowStart = now;
                }

                return true;
            }
        }
    }
}
