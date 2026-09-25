using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReforgedUpdater
{
    internal enum ContentMatch
    {
        /// <summary>The local bytes reproduce the server's ETag: the file is identical to what is published.</summary>
        Match,

        /// <summary>The ETag was reproducible in principle but the local bytes give a different answer.</summary>
        Mismatch,

        /// <summary>Not enough information to decide - no ETag, or an unfamiliar upload layout.</summary>
        Unknown
    }

    internal sealed class ContentResult
    {
        public ContentMatch Status = ContentMatch.Unknown;
        public string Sha256;
        public string Reason = string.Empty;
        public long PartSize; // 0 when the object was stored in one piece
    }

    /// <summary>
    /// Decides whether a local .mpq is byte-for-byte what the server is serving, without
    /// downloading it again.
    ///
    /// S3-compatible storage (Cloudflare R2 here) publishes an ETag that is derived from the
    /// object's content: for an object uploaded in one request it is the MD5 of the whole
    /// object, and for a multipart upload it is MD5(concatenated part MD5s) followed by
    /// "-" and the part count. Both can be recomputed locally, which turns the ETag into a
    /// checksum the site never had to publish.
    ///
    /// The part size is not advertised, so it is inferred: only a chunk size that yields the
    /// advertised part count is worth trying, which in practice leaves a single candidate.
    /// When nothing reproduces the ETag the answer is Unknown rather than Mismatch - an
    /// unfamiliar upload layout must not be reported as a corrupt file.
    ///
    /// MD5 is used here only because it is the algorithm the ETag format is defined in. It
    /// carries no security role: it detects a stale or truncated download, not a forgery.
    /// </summary>
    internal static class ContentCheck
    {
        private const long MiB = 1024 * 1024;
        private const int BufferSize = 1024 * 1024;

        /// <summary>Chunk sizes used by common S3 clients, smallest first.</summary>
        private static readonly long[] CandidatePartSizes =
        {
            5 * MiB, 8 * MiB, 10 * MiB, 15 * MiB, 16 * MiB, 25 * MiB, 32 * MiB, 64 * MiB, 100 * MiB, 128 * MiB
        };

        public static ContentResult Check(string path, string remoteETag)
        {
            var result = new ContentResult();

            if (!File.Exists(path)) { result.Reason = "the file is missing"; return result; }

            string tag = Normalize(remoteETag);
            if (tag == null) { result.Reason = "the server sent no usable ETag"; return result; }

            try
            {
                int dash = tag.IndexOf('-');
                if (dash < 0)
                {
                    // Stored in one piece: the ETag is the MD5 of the whole object.
                    WholeFile(path, out string md5, out string sha);
                    result.Sha256 = sha;
                    result.Status = md5.Equals(tag, StringComparison.OrdinalIgnoreCase)
                        ? ContentMatch.Match : ContentMatch.Mismatch;
                    return result;
                }

                string expected = tag.Substring(0, dash);
                if (!int.TryParse(tag.Substring(dash + 1), out int parts) || parts < 1)
                {
                    result.Reason = "the ETag is not in a recognised format";
                    return result;
                }

                long length = new FileInfo(path).Length;
                var candidates = CandidatePartSizes.Where(size => PartCount(length, size) == parts).ToList();
                if (candidates.Count == 0)
                {
                    result.Reason = "the upload's chunk size could not be determined";
                    result.Sha256 = Sha256Only(path);
                    return result;
                }

                foreach (long partSize in candidates)
                {
                    string composite = MultipartTag(path, partSize, out string sha, out int counted);
                    result.Sha256 = result.Sha256 ?? sha;

                    if (counted == parts && composite.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = ContentMatch.Match;
                        result.PartSize = partSize;
                        return result;
                    }
                }

                // Every plausible chunk size was tried and none reproduced the tag. With a
                // single candidate that is a real content difference; with several it is
                // still the most likely reading, so report it as one.
                result.Status = ContentMatch.Mismatch;
                result.PartSize = candidates[0];
                return result;
            }
            catch (Exception ex)
            {
                // FIPS-restricted machines refuse MD5 outright; that is not a file problem.
                result.Status = ContentMatch.Unknown;
                result.Reason = ex.Message;
                return result;
            }
        }

        private static long PartCount(long length, long partSize) => (length + partSize - 1) / partSize;

        private static string Normalize(string etag)
        {
            if (string.IsNullOrWhiteSpace(etag)) return null;

            string tag = etag.Trim();
            if (tag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) tag = tag.Substring(2); // weak validator
            tag = tag.Trim('"');
            return tag.Length == 0 ? null : tag;
        }

        private static void WholeFile(string path, out string md5, out string sha256)
        {
            using (var md5Hash = MD5.Create())
            using (var shaHash = SHA256.Create())
            using (var stream = Open(path))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    md5Hash.TransformBlock(buffer, 0, read, null, 0);
                    shaHash.TransformBlock(buffer, 0, read, null, 0);
                }
                md5Hash.TransformFinalBlock(buffer, 0, 0);
                shaHash.TransformFinalBlock(buffer, 0, 0);

                md5 = Hex(md5Hash.Hash);
                sha256 = Hex(shaHash.Hash);
            }
        }

        /// <summary>Rebuilds the multipart ETag body, hashing each chunk and then the joined digests.</summary>
        private static string MultipartTag(string path, long partSize, out string sha256, out int partCount)
        {
            var partDigests = new List<byte[]>();

            using (var shaHash = SHA256.Create())
            using (var stream = Open(path))
            {
                var buffer = new byte[BufferSize];
                var md5Hash = MD5.Create();
                long remaining = partSize;

                try
                {
                    while (true)
                    {
                        int want = (int)Math.Min(buffer.Length, remaining);
                        int read = stream.Read(buffer, 0, want);
                        if (read == 0) break;

                        shaHash.TransformBlock(buffer, 0, read, null, 0);
                        md5Hash.TransformBlock(buffer, 0, read, null, 0);
                        remaining -= read;

                        if (remaining == 0)
                        {
                            md5Hash.TransformFinalBlock(buffer, 0, 0);
                            partDigests.Add(md5Hash.Hash);
                            md5Hash.Dispose();
                            md5Hash = MD5.Create();
                            remaining = partSize;
                        }
                    }

                    if (remaining != partSize) // trailing, shorter part
                    {
                        md5Hash.TransformFinalBlock(buffer, 0, 0);
                        partDigests.Add(md5Hash.Hash);
                    }
                }
                finally { md5Hash.Dispose(); }

                shaHash.TransformFinalBlock(buffer, 0, 0);
                sha256 = Hex(shaHash.Hash);
            }

            partCount = partDigests.Count;

            var joined = new byte[partDigests.Sum(d => d.Length)];
            int offset = 0;
            foreach (byte[] digest in partDigests)
            {
                Buffer.BlockCopy(digest, 0, joined, offset, digest.Length);
                offset += digest.Length;
            }

            using (var md5Hash = MD5.Create())
                return Hex(md5Hash.ComputeHash(joined));
        }

        private static string Sha256Only(string path)
        {
            try { return Downloader.Sha256(path); }
            catch { return null; }
        }

        private static FileStream Open(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
