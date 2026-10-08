using DiskUsageAnalyzer.Cleanup;
using DiskUsageAnalyzer.Core;

namespace DiskUsageAnalyzer.Tests;

public sealed class SafetyTests
{
    private static ManifestEntry Entry(string name = "a.txt") => new(Path.Combine(Path.GetTempPath(), name), new(1, 2, 100, 42, 1, 32, 1, 4096), "User selected");
    private sealed class Spy : ICleanupBackend
    {
        public int Calls;
        public CleanupOutcome Execute(ManifestEntry entry, DeleteMode mode) { Calls++; return new(entry.Path, OutcomeStatus.Recovered, "fake"); }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)]
    public void CancelOrUnacknowledgedConfirmationHasZeroMutationCalls(bool positive, bool acknowledged)
    {
        var gate = new ConsentGate(); var spy = new Spy(); var manifest = new CleanupManifest([Entry()], DeleteMode.Recovery);
        Assert.Throws<InvalidOperationException>(() => new CleanupExecutor(gate, spy).Execute(manifest, gate.Confirm(manifest, positive, acknowledged)));
        Assert.Equal(0, spy.Calls);
    }
    [Fact] public void NullApprovalFailsClosed()
    {
        var spy = new Spy(); Assert.Throws<InvalidOperationException>(() => new CleanupExecutor(new(), spy).Execute(new([Entry()], DeleteMode.Recovery), null)); Assert.Equal(0, spy.Calls);
    }
    [Fact] public void ConsentCannotAuthorizeAnotherManifestOrMode()
    {
        var gate = new ConsentGate(); var a = new CleanupManifest([Entry()], DeleteMode.Recovery); var consent = gate.Confirm(a, true, true);
        Assert.False(gate.Consume(new([Entry()], DeleteMode.Permanent), consent));
        Assert.False(gate.Consume(new([Entry("b.txt")], DeleteMode.Recovery), consent));
        Assert.True(gate.Consume(a, consent));
    }
    [Fact] public void RestartedGateCannotUseOldApproval()
    {
        var gate = new ConsentGate(); var manifest = new CleanupManifest([Entry()], DeleteMode.Recovery);
        Assert.False(new ConsentGate().Consume(manifest, gate.Confirm(manifest, true, true)));
    }
    [Fact] public void ConsentExpiresAndCannotBeReused()
    {
        var clock = new Clock(); var gate = new ConsentGate(clock); var manifest = new CleanupManifest([Entry()], DeleteMode.Recovery);
        var old = gate.Confirm(manifest, true, true); clock.Now += TimeSpan.FromMinutes(6); Assert.False(gate.Consume(manifest, old));
        var valid = gate.Confirm(manifest, true, true); Assert.True(gate.Consume(manifest, valid)); Assert.False(gate.Consume(manifest, valid));
    }
    [Fact] public void ConfirmedBatchHonorsCancellationBeforeAnyMutation()
    {
        var gate = new ConsentGate(); var spy = new Spy(); var manifest = new CleanupManifest([Entry()], DeleteMode.Recovery);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var outcomes = new CleanupExecutor(gate, spy).Execute(manifest, gate.Confirm(manifest, true, true), cancellation: canceled.Token);
        Assert.Equal(0, spy.Calls); Assert.All(outcomes, x => Assert.Equal(OutcomeStatus.Skipped, x.Status));
    }
    [Fact] public void ParallelConsumptionAuthorizesExactlyOnce()
    {
        var gate = new ConsentGate(); var manifest = new CleanupManifest([Entry()], DeleteMode.Recovery); var consent = gate.Confirm(manifest, true, true); int successes = 0;
        Parallel.For(0, 100, _ => { if (gate.Consume(manifest, consent)) Interlocked.Increment(ref successes); }); Assert.Equal(1, successes);
    }
    [Fact] public void RandomConsentSequencesNeverMutateWithoutMatchingPositiveApproval()
    {
        var rng = new Random(34792);
        for (int i = 0; i < 1000; i++)
        {
            var gate = new ConsentGate(); var spy = new Spy(); var manifest = new CleanupManifest([Entry()], DeleteMode.Recovery);
            bool positive = rng.Next(2) == 1, acknowledged = rng.Next(2) == 1;
            var token = gate.Confirm(manifest, positive, acknowledged);
            if (positive && acknowledged) { new CleanupExecutor(gate, spy).Execute(manifest, token); Assert.Equal(1, spy.Calls); }
            else { Assert.Throws<InvalidOperationException>(() => new CleanupExecutor(gate, spy).Execute(manifest, token)); Assert.Equal(0, spy.Calls); }
        }
    }
    [Fact] public void ManifestCannotDeleteItsDuplicateKeeper()
    {
        var a = Entry(); var b = Entry("b.txt") with { Keeper = a.Path, KeeperIdentity = a.Identity };
        Assert.Throws<ArgumentException>(() => new CleanupManifest([a, b], DeleteMode.Recovery));
    }
    [Fact] public void ManifestCannotContainHardlinksDuplicatesOrInvalidMode()
    {
        Assert.Throws<ArgumentException>(() => new CleanupManifest([Entry(), Entry()], DeleteMode.Recovery));
        Assert.Throws<ArgumentException>(() => new CleanupManifest([Entry() with { Identity = Entry().Identity with { Links = 2 } }], DeleteMode.Recovery));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CleanupManifest([Entry()], (DeleteMode)100));
    }
    [Theory]
    [InlineData("C:\\safe\\a.txt", "C:\\safe", true)]
    [InlineData("C:\\safely\\a.txt", "C:\\safe", false)]
    [InlineData("C:\\SAFE\\a.txt", "C:\\safe", true)]
    public void PathContainmentRequiresSegmentBoundary(string path, string root, bool expected) => Assert.Equal(expected, PathPolicy.Within(path, root));
    [Theory] [InlineData("\\\\server\\share\\file")] [InlineData("C:\\file:stream")] [InlineData("\\\\?\\C:\\file")]
    public void DangerousPathSyntaxRejected(string path) => Assert.Throws<ArgumentException>(() => PathPolicy.Canonical(path));
}
