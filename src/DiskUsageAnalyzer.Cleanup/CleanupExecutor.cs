using DiskUsageAnalyzer.Core;

namespace DiskUsageAnalyzer.Cleanup;

public interface ICleanupBackend
{
    CleanupOutcome Execute(ManifestEntry entry, DeleteMode mode);
}

public sealed class CleanupExecutor(ConsentGate gate, ICleanupBackend backend)
{
    public IReadOnlyList<CleanupOutcome> Execute(CleanupManifest manifest, ConfirmedCleanup? consent,
        IProgress<CleanupOutcome>? progress = null, CancellationToken cancellation = default)
    {
        if (!gate.Consume(manifest, consent)) throw new InvalidOperationException("Explicit confirmation for this exact preview is required.");
        List<CleanupOutcome> outcomes = [];
        foreach (var entry in manifest.Entries)
        {
            CleanupOutcome outcome;
            if (cancellation.IsCancellationRequested) outcome = new(entry.Path, OutcomeStatus.Skipped, "Canceled before this item started.");
            else
            {
                try { outcome = backend.Execute(entry, manifest.Mode); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                { outcome = new(entry.Path, OutcomeStatus.Failed, ex.Message); }
            }
            outcomes.Add(outcome); progress?.Report(outcome);
        }
        return outcomes;
    }
}
