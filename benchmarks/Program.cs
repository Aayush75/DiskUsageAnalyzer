using System.Diagnostics;
using System.Text.Json;
using DiskUsageAnalyzer.Core;
using DiskUsageAnalyzer.Scanning;
using DiskUsageAnalyzer.Storage;
using DiskUsageAnalyzer.Windows;

int count = args.Length > 0 ? int.Parse(args[0]) : 100000;
string output = Path.GetFullPath(args.Length > 1 ? args[1] : "artifacts/benchmarks"); Directory.CreateDirectory(output);
string fixture = Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
File.WriteAllText(Path.Combine(fixture, ".fixture-owner"), "Disposable benchmark fixture; no automatic deletion.");
var generate = Stopwatch.StartNew();
for (int i = 0; i < count; i++)
{
    string directory = Path.Combine(fixture, (i / 1000).ToString("D4")); if (i % 1000 == 0) Directory.CreateDirectory(directory);
    using var file = File.Create(Path.Combine(directory, i.ToString("D8") + ".txt"));
    if (i % 20000 == 0) Console.WriteLine($"Created {i:N0}/{count:N0} fixture files");
}
generate.Stop(); using var store = new ScanStore(Path.Combine(output, Guid.NewGuid().ToString("N"), "scan.db"));
var timer = Stopwatch.StartNew(); double? first = null; var progress = new Callback(p => { if (p.Files > 0) first ??= timer.Elapsed.TotalSeconds; if (p.Finished) Console.WriteLine($"Scanned {p.Files:N0} files"); });
var summary = new DiskScanner(store, new WindowsMetadata()).Scan(fixture, progress); timer.Stop();
using var process = Process.GetCurrentProcess(); process.Refresh();
var result = new { count = summary.Files, folders = summary.Folders, skipped = summary.Skipped, complete = summary.Complete, generationSeconds = generate.Elapsed.TotalSeconds,
    scanSeconds = timer.Elapsed.TotalSeconds, firstProgressSeconds = first, peakWorkingSetMiB = process.PeakWorkingSet64 / 1048576.0,
    databaseMiB = new FileInfo(store.DatabasePath).Length / 1048576.0, os = Environment.OSVersion.ToString(), processors = Environment.ProcessorCount,
    runtime = Environment.Version.ToString(), cache = "warm (fixture created immediately before scan)", fixture };
string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }); File.WriteAllText(Path.Combine(output, $"result-{count}.json"), json); Console.WriteLine(json);
return summary.Complete && summary.Skipped == 0 && process.PeakWorkingSet64 < 512L * 1048576 ? 0 : 1;
file sealed class Callback(Action<ScanProgress> callback) : IProgress<ScanProgress> { public void Report(ScanProgress value) => callback(value); }
