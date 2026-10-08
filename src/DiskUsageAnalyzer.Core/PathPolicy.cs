namespace DiskUsageAnalyzer.Core;

public static class PathPolicy
{
    public const string RecoveryDirectory = ".DiskUsageAnalyzer-Recovery";
    public static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || (path.Length > 2 && path.AsSpan(2).Contains(':')))
            throw new ArgumentException("Choose a regular local path; device, network and stream paths are unsupported.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    public static bool Within(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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
