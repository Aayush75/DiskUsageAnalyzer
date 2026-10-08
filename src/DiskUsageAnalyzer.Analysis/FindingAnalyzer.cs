using DiskUsageAnalyzer.Core;
using DiskUsageAnalyzer.Storage;

namespace DiskUsageAnalyzer.Analysis;

public sealed class FindingAnalyzer(ScanStore store)
{
    public static readonly Finding[] Definitions = [
        new("temp", "Old temporary files", "Files in your user temporary folder unchanged for at least seven days.", "Applications may still need these files. Locked or changed files are skipped.", Risk.Low, 0, 0),
        new("dotnet", ".NET build output", "bin and obj files alongside a recognized .NET project.", "Rebuilding takes time. Manually added output files will also be lost; inspect the preview.", Risk.Caution, 0, 0),
        new("node", "JavaScript dependencies", "node_modules alongside package.json and a lockfile.", "Reinstall needs network access and possibly private registry credentials. Local modifications will be lost.", Risk.Caution, 0, 0),
        new("python", "Python environments", "A Python environment identified by pyvenv.cfg.", "Age does not prove disuse. Recreating packages or environment state may be difficult.", Risk.Caution, 0, 0),
        new("pip", "Python download cache", "Recognized files in the current user's pip cache.", "Packages will need downloading again. Offline installation may stop working.", Risk.Caution, 0, 0)
    ];
    public IReadOnlyList<Finding> Analyze(CancellationToken cancellation = default)
    {
        Dictionary<string, (long Bytes, long Count)> totals = [];
        Dictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);
        long after = 0;
        while (!cancellation.IsCancellationRequested)
        {
            var page = store.FilesAfter(after); if (page.Count == 0) break;
            foreach (var file in page)
            {
                if (cancellation.IsCancellationRequested) break;
                var rule = file.Exclusion is null ? Detect(file.Path, file.Identity, DateTimeOffset.UtcNow, cache) : null;
                if (rule is not null)
                {
                    var definition = Definitions.First(d => d.Rule == rule);
                    store.SetRule(file.Id, rule, definition.Consequence, definition.Risk);
                    totals.TryGetValue(rule, out var t); totals[rule] = (checked(t.Bytes + file.Bytes), t.Count + 1);
                }
            }
            after = page[^1].Id;
            if (cache.Count > 4096) cache.Clear();
        }
        return Definitions.Where(d => totals.ContainsKey(d.Rule)).Select(d => d with { Bytes = totals[d.Rule].Bytes, Count = totals[d.Rule].Count }).OrderByDescending(d => d.Bytes).ToArray();
    }
    public static string? Detect(string path, FileIdentity? identity, DateTimeOffset now, Dictionary<string, string?>? cache = null)
    {
        if (identity is null || PathPolicy.ProtectedReason(path) is not null) return null;
        string temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        if (PathPolicy.Within(path, temp) && identity.LastWrite > 0 && DateTimeOffset.FromFileTime(identity.LastWrite) < now.AddDays(-7)) return "temp";
        string pip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pip", "Cache");
        if (PathPolicy.Within(path, pip)) return "pip";
        for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            if (cache is not null && cache.TryGetValue(parent, out var known)) { if (known is not null) return known; continue; }
            string? found = null;
            try
            {
                string name = Path.GetFileName(parent), project = Path.GetDirectoryName(parent) ?? parent;
                if ((name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                    && Directory.EnumerateFiles(project, "*.*proj", SearchOption.TopDirectoryOnly).Any(p => new[] { ".csproj", ".fsproj", ".vbproj" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))) found = "dotnet";
                else if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(project, "package.json"))
                    && new[] { "package-lock.json", "pnpm-lock.yaml", "yarn.lock" }.Any(n => File.Exists(Path.Combine(project, n)))) found = "node";
                else if (File.Exists(Path.Combine(parent, "pyvenv.cfg"))) found = "python";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (cache is not null) cache[parent] = found;
            if (found is not null) return found;
        }
        return null;
    }
}
