using DiskUsageAnalyzer.Core;
using DiskUsageAnalyzer.Cleanup;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace DiskUsageAnalyzer.Tests;

public sealed class PolicyTests
{
    private static ManifestEntry Entry(string path = "C:\\ordinary\\file.txt") => new(path, new(1, 2, 123, 3, 4, 32, 1, 4096), "Selected");
    [Fact] public void EmptyOversizedAndInvalidEntriesCannotBeConfirmed()
    {
        Assert.Throws<ArgumentException>(() => new CleanupManifest([], DeleteMode.Recovery));
        Assert.Throws<ArgumentException>(() => new CleanupManifest(Enumerable.Repeat(Entry(), 10001), DeleteMode.Recovery));
        Assert.Throws<ArgumentException>(() => new CleanupManifest([Entry(" ")], DeleteMode.Recovery));
        Assert.Throws<ArgumentException>(() => new CleanupManifest([Entry() with { Identity = Entry().Identity with { Links = 0 } }], DeleteMode.Recovery));
    }
    [Fact] public void PreviewIsCopiedAndFingerprintIncludesModeAndContent()
    {
        var entries = new List<ManifestEntry> { Entry() }; var manifest = new CleanupManifest(entries, DeleteMode.Recovery); entries.Clear();
        Assert.Single(manifest.Entries); Assert.Equal(123, manifest.Bytes); Assert.Equal(DeleteMode.Recovery, manifest.Mode);
        Assert.NotEqual(manifest.Fingerprint, new CleanupManifest([Entry()], DeleteMode.Permanent).Fingerprint);
    }
    [Theory]
    [InlineData("C:\\folder\\file.")] [InlineData("C:\\folder\\file ")] [InlineData("C:\\CON.txt")]
    [InlineData("C:\\LPT9")] [InlineData("C:\\COM1.txt")] [InlineData(" ")]
    public void AmbiguousWin32NamesFailClosed(string path) => Assert.Throws<ArgumentException>(() => PathPolicy.Canonical(path));
    [Fact] public void RootDriveContainmentWorks() => Assert.True(PathPolicy.Within("C:\\folder\\file", "C:\\"));
    [Fact] public void ProtectedRootsAndCloudLocationsAreNotCleanupTargets()
    {
        Assert.NotNull(PathPolicy.ProtectedReason("C:\\folder\\.DiskUsageAnalyzer-Recovery\\file"));
        Assert.NotNull(PathPolicy.ProtectedReason("C:\\$Recycle.Bin\\file"));
        Assert.NotNull(PathPolicy.ProtectedReason("C:\\Dropbox\\file"));
        Assert.NotNull(PathPolicy.ProtectedReason("\\\\server\\file"));
        Assert.NotNull(PathPolicy.ProtectedReason(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "system-file")));
        Assert.NotNull(PathPolicy.ProtectedReason("C:\\custom\\file", ["C:\\custom"]));
        Assert.Null(PathPolicy.ProtectedReason("C:\\customary\\file", ["C:\\custom"]));
        string? previous = Environment.GetEnvironmentVariable("OneDriveCommercial");
        try { Environment.SetEnvironmentVariable("OneDriveCommercial", "C:\\synchronized"); Assert.NotNull(PathPolicy.ProtectedReason("C:\\synchronized\\file")); }
        finally { Environment.SetEnvironmentVariable("OneDriveCommercial", previous); }
    }
    [Theory] [InlineData("io")] [InlineData("unauthorized")] [InlineData("invalid")]
    public void BackendFailureIsAnOutcomeAndOtherFilesRemainExplicitlyScoped(string failure)
    {
        var gate = new ConsentGate(); var manifest = new CleanupManifest([Entry()], DeleteMode.Permanent);
        var outcomes = new CleanupExecutor(gate, new Failing(failure)).Execute(manifest, gate.Confirm(manifest, true, true));
        Assert.Equal(OutcomeStatus.Failed, Assert.Single(outcomes).Status);
    }
    [Fact] public void UnexpectedBackendFailureStopsTheBatch()
    {
        var gate = new ConsentGate(); var manifest = new CleanupManifest([Entry()], DeleteMode.Permanent);
        Assert.Throws<ArgumentException>(() => new CleanupExecutor(gate, new Failing("argument")).Execute(manifest, gate.Confirm(manifest, true, true)));
    }
    private sealed class Failing(string failure) : ICleanupBackend
    {
        public CleanupOutcome Execute(ManifestEntry entry, DeleteMode mode) => throw failure switch
        { "io" => new IOException("Fixture failure"), "unauthorized" => new UnauthorizedAccessException("Fixture failure"), "invalid" => new InvalidOperationException("Fixture failure"), _ => new ArgumentException("Fixture failure") };
    }
}
