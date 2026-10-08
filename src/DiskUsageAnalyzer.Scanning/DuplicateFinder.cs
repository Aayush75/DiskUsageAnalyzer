using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Diagnostics;
using DiskUsageAnalyzer.Core;
using DiskUsageAnalyzer.Storage;

namespace DiskUsageAnalyzer.Scanning;

public sealed class DuplicateFinder(ScanStore store, IFileMetadata metadata)
{
    public IReadOnlyList<DuplicateGroup> Find(IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        List<DuplicateGroup> result = [];
        var throttle = Stopwatch.StartNew();
        foreach (var size in store.DuplicateSizes())
        {
            cancellation.ThrowIfCancellationRequested();
            Dictionary<string, List<FileEntry>> hashes = [];
            // Bound a pathological group. UI discloses limits; no automatic delete selection.
            foreach (var file in store.EligibleBySize(size, limit: 2000))
            {
                cancellation.ThrowIfCancellationRequested();
                if (throttle.ElapsedMilliseconds >= 150) { progress?.Report(file.Path); throttle.Restart(); }
                try
                {
                    if (file.Identity is null || metadata.Read(file.Path) != file.Identity || metadata.Exclusion(file.Path, file.Identity) is not null) continue;
                    using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[65536]; int read;
                    while ((read = stream.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
                    if (metadata.Read(file.Path) != file.Identity) continue;
                    var key = Convert.ToHexString(hash.GetHashAndReset());
                    if (!hashes.TryGetValue(key, out var group)) hashes[key] = group = [];
                    group.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            foreach (var (hash, candidates) in hashes.Where(h => h.Value.Count > 1))
            {
                var exact = candidates.Where(f => f == candidates[0] || EqualContent(candidates[0].Path, f.Path, cancellation)).ToImmutableArray();
                if (exact.Length > 1) result.Add(new(hash, exact));
                if (result.Count >= 500) return result;
            }
        }
        return result;
    }
    public static bool EqualContent(string first, string second, CancellationToken cancellation = default)
    {
        try
        {
            using var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (a.Length != b.Length) return false;
            byte[] x = new byte[65536], y = new byte[65536]; int n;
            while ((n = a.Read(x)) > 0)
            {
                cancellation.ThrowIfCancellationRequested(); int m = 0;
                while (m < n) { int read = b.Read(y, m, n - m); if (read == 0) return false; m += read; }
                if (!x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n))) return false;
            }
            return b.ReadByte() == -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
