using DiskUsageAnalyzer.Analysis;
using DiskUsageAnalyzer.Cleanup;
using DiskUsageAnalyzer.Core;
using DiskUsageAnalyzer.Scanning;
using DiskUsageAnalyzer.Storage;
using DiskUsageAnalyzer.Windows;

namespace DiskUsageAnalyzer.Tests;

// Fixtures intentionally remain in a unique application-owned temp subtree. No recursive teardown.
public sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "DiskUsageAnalyzer.Tests", Guid.NewGuid().ToString("N"));
    public ScanStore Store { get; }
    public WindowsMetadata Metadata { get; } = new();
    public Fixture() { Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, ".fixture-owner"), "DiskUsageAnalyzer disposable fixture"); Store = new(Path.Combine(Root + ".dbdir", "scan.db")); }
    public string Write(string path, string content = "fixture content") { var p = Path.Combine(Root, path); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, content); return p; }
    public ManifestEntry Entry(string path) => new(path, Metadata.Read(path), "Fixture approved");
    public CleanupOutcome Execute(ManifestEntry entry, DeleteMode mode = DeleteMode.Recovery)
    {
        var gate = new ConsentGate(); var manifest = new CleanupManifest([entry], mode);
        return new CleanupExecutor(gate, new WindowsCleanupBackend(Store, Metadata)).Execute(manifest, gate.Confirm(manifest, true, true))[0];
    }
    public void Dispose() => Store.Dispose();
}

public sealed class WindowsIntegrationTests
{
    [Fact] public void NativeRenameRetainsFileContent()
    {
        using var f = new Fixture(); var original = f.Write("original.txt", "rename content"); var target = Path.Combine(f.Root, "renamed.txt");
        using (var handle = NativeFiles.Open(original, NativeFiles.DeleteAccess | NativeFiles.ReadData, 0)) NativeFiles.RenameByHandle(handle, target);
        Assert.False(File.Exists(original)); Assert.Equal("rename content", File.ReadAllText(target));
    }
    [Fact] public void NativeCrossDirectoryRenameRetainsFileContent()
    {
        using var f = new Fixture(); var original = f.Write("original.txt", "rename content"); var directory = Path.Combine(f.Root, "destination"); Directory.CreateDirectory(directory); var target = Path.Combine(directory, "renamed.txt");
        using (var handle = NativeFiles.Open(original, NativeFiles.DeleteAccess | NativeFiles.ReadData, 0)) NativeFiles.RenameByHandle(handle, target);
        Assert.Equal("rename content", File.ReadAllText(target));
    }
    [Fact] public void ScanIsReadOnlyAndAccountsForNestedFiles()
    {
        using var f = new Fixture(); var a = f.Write("nested/a.txt", new string('a', 200)); var b = f.Write("b.txt", new string('b', 300));
        var summary = new DiskScanner(f.Store, f.Metadata).Scan(f.Root);
        Assert.True(summary.Complete); Assert.Equal(3, summary.Files); Assert.Equal(summary.Bytes, f.Store.Get(1)!.Bytes);
        Assert.Equal(new string('a', 200), File.ReadAllText(a)); Assert.Equal(new string('b', 300), File.ReadAllText(b));
        Assert.Equal(200, f.Store.Children(1).Single(x => x.Name == "nested").Bytes);
    }
    [Fact] public void PagingAndLiteralFilterDoNotBroadenResults()
    {
        using var f = new Fixture(); f.Write("100%.txt"); f.Write("other.txt"); new DiskScanner(f.Store, f.Metadata).Scan(f.Root);
        Assert.Single(f.Store.Largest("%")); Assert.Equal("100%.txt", f.Store.Largest("%")[0].Name);
        Assert.Single(f.Store.Largest(limit: 1)); Assert.NotEqual(f.Store.Largest(limit: 1)[0].Id, f.Store.Largest(offset: 1, limit: 1)[0].Id);
    }
    [Fact] public void UnconfirmedExecutionPreservesActualFile()
    {
        using var f = new Fixture(); var path = f.Write("sentinel.txt"); var manifest = new CleanupManifest([f.Entry(path)], DeleteMode.Permanent);
        Assert.Throws<InvalidOperationException>(() => new CleanupExecutor(new(), new WindowsCleanupBackend(f.Store, f.Metadata)).Execute(manifest, null)); Assert.True(File.Exists(path));
    }
    [Fact] public void RecoveryPreservesContentAndCanRestore()
    {
        using var f = new Fixture(); var path = f.Write("a.txt"); var before = File.ReadAllText(path);
        var outcome = f.Execute(f.Entry(path)); Assert.True(outcome.Status == OutcomeStatus.Recovered, outcome.Detail); Assert.False(File.Exists(path));
        Assert.Equal(before, File.ReadAllText(outcome.RecoveryPath!));
        var history = Assert.Single(f.Store.History()); new WindowsCleanupBackend(f.Store, f.Metadata).Restore(history, true);
        Assert.Equal(before, File.ReadAllText(path)); Assert.Equal("Restored", Assert.Single(f.Store.History()).Status);
    }
    [Fact] public void RestoreNeverOverwritesNewFile()
    {
        using var f = new Fixture(); var path = f.Write("a.txt", "original"); var outcome = f.Execute(f.Entry(path)); Assert.True(outcome.Status == OutcomeStatus.Recovered, outcome.Detail);
        File.WriteAllText(path, "replacement"); Assert.Throws<IOException>(() => new WindowsCleanupBackend(f.Store, f.Metadata).Restore(f.Store.History()[0], true));
        Assert.Equal("replacement", File.ReadAllText(path)); Assert.Equal("original", File.ReadAllText(outcome.RecoveryPath!));
    }
    [Fact] public void PermanentDeletionAffectsOnlyApprovedFile()
    {
        using var f = new Fixture(); var path = f.Write("delete.txt"); var sentinel = f.Write("sentinel.txt", "untouched");
        Assert.Equal(OutcomeStatus.Deleted, f.Execute(f.Entry(path), DeleteMode.Permanent).Status); Assert.False(File.Exists(path)); Assert.Equal("untouched", File.ReadAllText(sentinel));
    }
    [Fact] public void ChangedFileIsSkipped()
    {
        using var f = new Fixture(); var path = f.Write("a.txt"); var entry = f.Entry(path); File.WriteAllText(path, "changed and longer");
        Assert.Equal(OutcomeStatus.Skipped, f.Execute(entry, DeleteMode.Permanent).Status); Assert.Equal("changed and longer", File.ReadAllText(path));
    }
    [Fact] public void ReplacedFileIsSkipped()
    {
        using var f = new Fixture(); var path = f.Write("a.txt"); var entry = f.Entry(path); File.Move(path, path + ".original"); File.WriteAllText(path, "replacement");
        Assert.Equal(OutcomeStatus.Skipped, f.Execute(entry, DeleteMode.Permanent).Status); Assert.Equal("replacement", File.ReadAllText(path)); Assert.True(File.Exists(path + ".original"));
    }
    [Fact] public void OpenFileIsNotForciblyDeleted()
    {
        using var f = new Fixture(); var path = f.Write("a.txt"); var entry = f.Entry(path); using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Assert.Equal(OutcomeStatus.Failed, f.Execute(entry, DeleteMode.Permanent).Status); Assert.True(File.Exists(path));
    }
    [Fact] public void GuardPreventsAncestorRename()
    {
        using var f = new Fixture(); var path = f.Write("nested/a.txt"); using var guard = new AncestorGuard(path);
        Assert.Throws<IOException>(() => Directory.Move(Path.GetDirectoryName(path)!, Path.Combine(f.Root, "moved"))); Assert.True(File.Exists(path));
    }
    [Fact] public void RecoveryFolderCollisionDoesNotMoveFiles()
    {
        using var f = new Fixture(); var path = f.Write("a.txt"); Directory.CreateDirectory(Path.Combine(f.Root, PathPolicy.RecoveryDirectory));
        Assert.Equal(OutcomeStatus.Failed, f.Execute(f.Entry(path)).Status); Assert.True(File.Exists(path));
    }
    [Fact] public void DuplicateKeeperContentChangeBlocksDeletion()
    {
        using var f = new Fixture(); var a = f.Write("a.txt", "same"); var b = f.Write("b.txt", "same");
        var entry = f.Entry(b) with { Keeper = a, KeeperIdentity = f.Metadata.Read(a) }; File.WriteAllText(a, "different");
        Assert.Equal(OutcomeStatus.Skipped, f.Execute(entry, DeleteMode.Permanent).Status); Assert.True(File.Exists(a)); Assert.True(File.Exists(b));
    }
    [Fact] public void DuplicateDiscoveryUsesContentNotNamesOrSizes()
    {
        using var f = new Fixture(); f.Write("a.txt", "same"); f.Write("b.txt", "same"); f.Write("c.txt", "else");
        new DiskScanner(f.Store, f.Metadata).Scan(f.Root); var groups = new DuplicateFinder(f.Store, f.Metadata).Find();
        var group = Assert.Single(groups); Assert.Equal(2, group.Files.Length); Assert.Equal(4, group.RedundantBytes);
    }
    [Fact] public void FolderNameAloneDoesNotBecomeBuildFinding()
    {
        using var f = new Fixture(); var ordinary = f.Write("photos/bin/a.txt"); var identity = f.Metadata.Read(ordinary);
        Assert.NotEqual("dotnet", FindingAnalyzer.Detect(ordinary, identity, DateTimeOffset.UtcNow));
        f.Write("project/test.csproj", "<Project />"); var output = f.Write("project/bin/Release/a.dll");
        Assert.Equal("dotnet", FindingAnalyzer.Detect(output, f.Metadata.Read(output), DateTimeOffset.UtcNow));
    }
    [Fact] public void NodeDependenciesRequireManifestAndLockfile()
    {
        using var f = new Fixture(); var path = f.Write("project/node_modules/p/file.js"); f.Write("project/package.json", "{}");
        Assert.NotEqual("node", FindingAnalyzer.Detect(path, f.Metadata.Read(path), DateTimeOffset.UtcNow));
        f.Write("project/package-lock.json", "{}"); Assert.Equal("node", FindingAnalyzer.Detect(path, f.Metadata.Read(path), DateTimeOffset.UtcNow));
    }
    [Fact] public void CanceledScanIsExplicitlyPartial()
    {
        using var f = new Fixture(); f.Write("a.txt"); using var c = new CancellationTokenSource(); c.Cancel();
        var summary = new DiskScanner(f.Store, f.Metadata).Scan(f.Root, cancellation: c.Token); Assert.False(summary.Complete); Assert.True(File.Exists(Path.Combine(f.Root, "a.txt")));
    }
}
