using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace DiskUsageAnalyzer.Core;

public enum Risk { Low, Caution, Protected }
public enum DeleteMode { Recovery, Permanent }
public enum OutcomeStatus { Recovered, Deleted, Restored, Skipped, Failed, Unknown }

public sealed record FileIdentity(uint Volume, ulong FileId, long Length, long LastWrite, long Creation, uint Attributes, uint Links, long? Allocated);
public sealed record FileEntry(long Id, long ParentId, string Path, string Name, bool IsDirectory, long Bytes,
    long? Allocated, FileIdentity? Identity, string? Exclusion = null, string? Rule = null, string? Explanation = null,
    Risk Risk = Risk.Caution, long FileCount = 0)
{
    public string Size => ByteSize.Format(Bytes);
    public string AllocatedSize => Allocated is { } n ? ByteSize.Format(n) : "Unknown";
    public string Kind => IsDirectory ? "Folder" : System.IO.Path.GetExtension(Name) is { Length: > 0 } e ? e.ToUpperInvariant() : "File";
    public string RiskLabel => Exclusion is not null ? "Protected / unsupported" : Risk == Risk.Low ? "Low risk" : "Review carefully";
}

public sealed record ScanProgress(long Files, long Folders, long Bytes, long Skipped, string Current, bool Finished = false, bool Cancelled = false);
public sealed record ScanSummary(string Root, long Bytes, long Files, long Folders, long Skipped, bool Complete, DateTimeOffset Started);
public sealed record Finding(string Rule, string Title, string Description, string Consequence, Risk Risk, long Bytes, long Count)
{
    public string Size => ByteSize.Format(Bytes);
    public string RiskLabel => Risk == Risk.Low ? "Low risk" : "Review carefully";
}
public sealed record DuplicateGroup(string Hash, ImmutableArray<FileEntry> Files)
{
    public long RedundantBytes => (Files.Length - 1) * Files[0].Bytes;
    public string Size => ByteSize.Format(RedundantBytes);
}

public sealed record ManifestEntry(string Path, FileIdentity Identity, string Reason, string? Keeper = null, FileIdentity? KeeperIdentity = null);

public sealed class CleanupManifest
{
    public Guid Id { get; } = Guid.NewGuid();
    public ImmutableArray<ManifestEntry> Entries { get; }
    public DeleteMode Mode { get; }
    public string Fingerprint { get; }
    public long Bytes => Entries.Sum(e => e.Identity.Length);
    public CleanupManifest(IEnumerable<ManifestEntry> entries, DeleteMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Entries = entries.ToImmutableArray();
        if (Entries.IsEmpty || Entries.Length > 10000) throw new ArgumentException("Review between 1 and 10,000 files per operation.");
        if (Entries.Any(e => string.IsNullOrWhiteSpace(e.Path) || e.Identity.Links != 1)) throw new ArgumentException("Each entry needs a unique regular-file identity.");
        if (Entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Entries.Length) throw new ArgumentException("Duplicate manifest paths.");
        if (Entries.Any(e => e.Keeper is not null && Entries.Any(x => string.Equals(x.Path, e.Keeper, StringComparison.OrdinalIgnoreCase)))) throw new ArgumentException("A duplicate keeper cannot be selected for deletion.");
        Mode = mode;
        Fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Id, Mode, Entries })));
    }
}

// An in-memory capability, never serialized. Only the explicit confirmation handler calls Confirm.
public sealed class ConfirmedCleanup
{
    internal Guid GateId { get; }
    internal string Fingerprint { get; }
    internal DateTimeOffset Issued { get; }
    internal int Consumed;
    internal ConfirmedCleanup(Guid gateId, string fingerprint, DateTimeOffset issued) => (GateId, Fingerprint, Issued) = (gateId, fingerprint, issued);
}

public sealed class ConsentGate(TimeProvider? clock = null)
{
    private readonly Guid _id = Guid.NewGuid();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public ConfirmedCleanup? Confirm(CleanupManifest manifest, bool explicitPositiveAction, bool consequencesAcknowledged)
        => explicitPositiveAction && consequencesAcknowledged ? new(_id, manifest.Fingerprint, _clock.GetUtcNow()) : null;
    public bool Consume(CleanupManifest manifest, ConfirmedCleanup? consent)
    {
        if (consent is null || consent.GateId != _id || consent.Fingerprint != manifest.Fingerprint) return false;
        var age = _clock.GetUtcNow() - consent.Issued;
        return age >= TimeSpan.Zero && age <= TimeSpan.FromMinutes(5) && Interlocked.CompareExchange(ref consent.Consumed, 1, 0) == 0;
    }
}

public sealed record CleanupOutcome(string Path, OutcomeStatus Status, string Detail, string? RecoveryPath = null);
public sealed record HistoryEntry(long Id, string Operation, string OriginalPath, string? RecoveryPath, FileIdentity Identity, string Status, string Detail, DateTimeOffset Time)
{
    public string Size => ByteSize.Format(Identity.Length);
    public bool CanRestore => Status == "Recovered";
}

public interface IFileMetadata
{
    FileIdentity Read(string path);
    string? Exclusion(string path, FileIdentity identity);
}

public static class ByteSize
{
    public static string Format(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double n = Math.Max(0, value); int u = 0;
        while (n >= 1024 && u < units.Length - 1) { n /= 1024; u++; }
        return u == 0 ? $"{n:0} {units[u]}" : $"{n:0.0} {units[u]}";
    }
}
