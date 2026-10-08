namespace DiskUsageAnalyzer.Core;

public static class PathPolicy
{
    public const string RecoveryDirectory = ".DiskUsageAnalyzer-Recovery";
    public static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || (path.Length > 2 && path.AsSpan(2).Contains(':')))
            throw new ArgumentException("Choose a regular local path; device, network and stream paths are unsupported.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        // Inspect raw segments too: GetFullPath may strip ambiguous trailing dots/spaces.
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Concat(full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Where(s => s is not "." and not ".."))
        {
            if (segment.EndsWith(' ') || segment.EndsWith('.')) throw new ArgumentException("Ambiguous trailing-dot or trailing-space paths are unsupported.");
            string basename = segment.Split('.')[0].ToUpperInvariant();
            if (basename is "CON" or "PRN" or "AUX" or "NUL" || basename.Length == 4 && (basename.StartsWith("COM", StringComparison.Ordinal) || basename.StartsWith("LPT", StringComparison.Ordinal)) && basename[3] is >= '1' and <= '9')
                throw new ArgumentException("Windows device names are unsupported.");
        }
        return full;
    }
    public static bool Within(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string? ProtectedReason(string path, IEnumerable<string>? extraRoots = null)
    {
        string p;
        try { p = Canonical(path); } catch (ArgumentException) { return "Unsupported path."; }
        var segments = p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(s => s.Equals(RecoveryDirectory, StringComparison.OrdinalIgnoreCase) || s.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
            || s.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) || s.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Dropbox", StringComparison.OrdinalIgnoreCase) || s.Equals("Google Drive", StringComparison.OrdinalIgnoreCase)))
            return "Recovery, system metadata, or synchronized storage is excluded.";
        string[] protectedRoots = [Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskUsageAnalyzer")];
        foreach (var root in protectedRoots.Concat(extraRoots ?? []))
            if (!string.IsNullOrWhiteSpace(root) && Within(p, Path.TrimEndingDirectorySeparator(root))) return "Protected system or application storage.";
        foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } root && Within(p, Path.TrimEndingDirectorySeparator(root))) return "Cloud-synchronized storage is excluded.";
        return null;
    }
}
