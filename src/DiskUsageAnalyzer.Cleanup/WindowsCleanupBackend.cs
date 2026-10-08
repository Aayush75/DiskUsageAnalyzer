using System.Security.AccessControl;
using System.Security.Principal;
using DiskUsageAnalyzer.Core;
using DiskUsageAnalyzer.Storage;
using DiskUsageAnalyzer.Windows;
using Microsoft.Win32.SafeHandles;

namespace DiskUsageAnalyzer.Cleanup;

/// <summary>All mutations target retained, validated handles. No recursive or path-based deletion.</summary>
public sealed class WindowsCleanupBackend(ScanStore store, WindowsMetadata metadata) : ICleanupBackend
{
    public CleanupOutcome Execute(ManifestEntry entry, DeleteMode mode)
    {
        if (metadata.Exclusion(entry.Path, entry.Identity) is { } reason) return new(entry.Path, OutcomeStatus.Skipped, reason);
        using var ancestors = new AncestorGuard(entry.Path);
        using var file = NativeFiles.Open(entry.Path, NativeFiles.DeleteAccess | NativeFiles.ReadData | NativeFiles.ReadAttributes, 0);
        var actual = NativeFiles.Identity(file);
        if (actual != entry.Identity || metadata.Exclusion(entry.Path, actual) is not null || (actual.Attributes & 0x10) != 0)
            return new(entry.Path, OutcomeStatus.Skipped, "File changed since review, or is not an eligible regular file. Scan again.");
        using var keeperAncestors = entry.Keeper is null ? null : new AncestorGuard(entry.Keeper);
        using var keeper = entry.Keeper is null ? null : NativeFiles.Open(entry.Keeper, NativeFiles.ReadData | NativeFiles.ReadAttributes, NativeFiles.ShareRead);
        if (keeper is not null)
        {
            if (entry.KeeperIdentity is null || NativeFiles.Identity(keeper) != entry.KeeperIdentity || !SameContents(file, keeper))
                return new(entry.Path, OutcomeStatus.Skipped, "The duplicate keeper changed or no longer matches. Nothing removed.");
        }
        if (mode == DeleteMode.Permanent)
        {
            long journal = store.Journal("Permanent delete", entry, null); // Durable intent before destructive call.
            try
            {
                NativeFiles.DeleteByHandle(file);
                file.Dispose(); // Commit disposition before recording success.
                store.CompleteJournal(journal, "Deleted", "Permanently deleted after explicit confirmation; no built-in undo.");
                return new(entry.Path, OutcomeStatus.Deleted, "Permanently deleted. Actual free-space change can vary.");
            }
            catch
            {
                // Do not retry: the OS may have committed before a journal failure.
                TryJournal(journal, "Unknown", "Operation interrupted. Inspect the original path; never automatically retried.");
                throw;
            }
        }
        string recoveryFolder = Path.Combine(Path.GetDirectoryName(entry.Path)!, PathPolicy.RecoveryDirectory);
        PrepareRecoveryFolder(recoveryFolder);
        string destination = Path.Combine(recoveryFolder, Guid.NewGuid().ToString("N") + ".recovered");
        using var recoveryGuard = new AncestorGuard(destination);
        long id = store.Journal("Delete to recovery", entry, destination);
        try
        {
            NativeFiles.RenameByHandle(file, destination);
            store.CompleteJournal(id, "Recovered", "Moved into the local recovery folder. Still occupies disk space; Restore is available in History.");
            return new(entry.Path, OutcomeStatus.Recovered, "Moved to recovery. No disk space reclaimed yet.", destination);
        }
        catch
        {
            TryJournal(id, "Unknown", "Inspect original and recovery paths. Automatic replay is disabled.");
            throw;
        }
    }

    public CleanupOutcome Restore(HistoryEntry entry, bool explicitApproval)
    {
        if (!explicitApproval) throw new InvalidOperationException("Explicit restore approval is required.");
        if (!entry.CanRestore || entry.RecoveryPath is null) throw new InvalidOperationException("This entry has no confirmed recovery file.");
        if (PathPolicy.ProtectedReason(entry.OriginalPath) is not null) throw new IOException("Original location is now protected.");
        using var sourceGuard = new AncestorGuard(entry.RecoveryPath);
        using var targetGuard = new AncestorGuard(entry.OriginalPath);
        using var source = NativeFiles.Open(entry.RecoveryPath, NativeFiles.DeleteAccess | NativeFiles.ReadAttributes, 0);
        if (NativeFiles.Identity(source) != entry.Identity) throw new IOException("Recovery file changed. Restore blocked.");
        // RenameByHandle never replaces an existing destination. Collision leaves both files intact.
        NativeFiles.RenameByHandle(source, entry.OriginalPath);
        store.CompleteJournal(entry.Id, "Restored", "Restored to the original location without overwriting anything.");
        return new(entry.OriginalPath, OutcomeStatus.Restored, "Restored to its original path.");
    }

    private void TryJournal(long id, string status, string detail)
    {
        try { store.CompleteJournal(id, status, detail); }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { /* Pending intent remains for manual inspection. */ }
    }
    private static bool SameContents(SafeFileHandle first, SafeFileHandle second)
    {
        byte[] a = new byte[65536], b = new byte[65536]; long offset = 0;
        while (true)
        {
            int n = RandomAccess.Read(first, a, offset), m = RandomAccess.Read(second, b, offset);
            if (n != m || !a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, m))) return false;
            if (n == 0) return true;
            offset += n;
        }
    }
    private static void PrepareRecoveryFolder(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new IOException("Current Windows identity unavailable.");
        string marker = Path.Combine(path, ".owner");
        string expected = "DiskUsageAnalyzer recovery v1\n" + sid.Value;
        if (!Directory.Exists(path))
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false); acl.SetOwner(sid);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(acl);
            using var guard = new AncestorGuard(marker);
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream); writer.Write(expected); writer.Flush(); stream.Flush(true);
        }
        using var validation = new AncestorGuard(marker);
        using var h = NativeFiles.Open(marker, NativeFiles.ReadAttributes, NativeFiles.ShareRead);
        if (NativeFiles.Identity(h).Links != 1 || (NativeFiles.Identity(h).Attributes & 0x400) != 0 || File.ReadAllText(marker) != expected)
            throw new IOException("An existing recovery folder is not owned by this application. No files moved.");
        var directoryAcl = new DirectoryInfo(path).GetAccessControl();
        if (!directoryAcl.AreAccessRulesProtected || !sid.Equals(directoryAcl.GetOwner(typeof(SecurityIdentifier)))) throw new IOException("Recovery folder ownership or permissions changed.");
    }
}
