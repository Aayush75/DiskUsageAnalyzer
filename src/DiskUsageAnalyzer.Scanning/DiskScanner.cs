using System.Diagnostics;
using DiskUsageAnalyzer.Core;
using DiskUsageAnalyzer.Storage;

namespace DiskUsageAnalyzer.Scanning;

public sealed class DiskScanner(ScanStore store, IFileMetadata metadata)
{
    public ScanSummary Scan(string path, IProgress<ScanProgress>? progress = null, CancellationToken cancellation = default)
    {
        string root = PathPolicy.Canonical(path);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The selected folder is unavailable.");
        // Reject a linked root and linked ancestors before any traversal.
        for (string? p = root; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Select a real folder rather than a link or cloud placeholder.");
        var started = DateTimeOffset.UtcNow;
        store.BeginScan(root);
        long next = 1, files = 0, folders = 1, bytes = 0, skipped = 0;
        store.InsertBatch([new(1, 0, root, Path.GetFileName(root) is { Length: > 0 } n ? n : root, true, 0, null, null)]);
        store.Enqueue(1, root, 0);
        List<FileEntry> batch = new(256);
        Dictionary<string, (long Bytes, long Count)> types = new(StringComparer.OrdinalIgnoreCase);
        var throttle = Stopwatch.StartNew();
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        void Flush() { if (batch.Count > 0) { store.InsertBatch(batch); batch.Clear(); } }
        while (!cancellation.IsCancellationRequested && store.Dequeue() is { } dir)
        {
            try
            {
                // Recheck immediately before enumeration so a changed link is not knowingly followed.
                if ((File.GetAttributes(dir.Path) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                foreach (var info in new DirectoryInfo(dir.Path).EnumerateFileSystemInfos("*", options))
                {
                    if (cancellation.IsCancellationRequested) break;
                    try
                    {
                        string full = info.FullName;
                        if (full.StartsWith(Path.GetDirectoryName(store.DatabasePath)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                        if (info.Name.Equals(PathPolicy.RecoveryDirectory, StringComparison.OrdinalIgnoreCase) || info.Name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                            || info.Name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                        bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) { skipped++; continue; }
                        long id = ++next;
                        if (isDir)
                        {
                            folders++; batch.Add(new(id, dir.Id, full, info.Name, true, 0, null, null)); store.Enqueue(id, full, dir.Id);
                        }
                        else
                        {
                            FileIdentity? identity = null;
                            string? exclusion;
                            try { identity = metadata.Read(full); exclusion = metadata.Exclusion(full, identity); }
                            catch (IOException) { exclusion = "Metadata inaccessible; cleanup disabled."; }
                            long length = identity?.Length ?? ((FileInfo)info).Length;
                            bytes = checked(bytes + length); files++;
                            batch.Add(new(id, dir.Id, full, info.Name, false, length, identity?.Links == 1 ? identity.Allocated : null, identity, exclusion));
                            var ext = Path.GetExtension(info.Name).ToLowerInvariant(); if (ext.Length == 0) ext = "(no extension)";
                            if (types.Count >= 512 && !types.ContainsKey(ext)) ext = "(other types)";
                            types.TryGetValue(ext, out var total); types[ext] = (checked(total.Bytes + length), total.Count + 1);
                        }
                        if (batch.Count >= 256) Flush();
                        if (throttle.ElapsedMilliseconds >= 150) { progress?.Report(new(files, folders, bytes, skipped, dir.Path)); throttle.Restart(); }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        Flush(); store.Aggregate();
        store.SaveTypes(types.OrderByDescending(x => x.Value.Bytes).Select(x => (x.Key, x.Value.Bytes, x.Value.Count)));
        bool canceled = cancellation.IsCancellationRequested;
        progress?.Report(new(files, folders, bytes, skipped, root, true, canceled));
        return new(root, bytes, files, folders, skipped, !canceled, started);
    }
}
