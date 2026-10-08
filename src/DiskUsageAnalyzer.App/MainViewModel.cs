using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskUsageAnalyzer.Analysis;
using DiskUsageAnalyzer.Cleanup;
using DiskUsageAnalyzer.Core;
using DiskUsageAnalyzer.Scanning;
using DiskUsageAnalyzer.Storage;
using DiskUsageAnalyzer.Windows;

namespace DiskUsageAnalyzer.App;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ScanStore _store;
    private readonly WindowsMetadata _metadata = new();
    private readonly ConsentGate _gate = new();
    private readonly Func<CleanupManifest, bool> _confirm;
    private readonly Func<string, string, bool> _confirmRestore;
    private readonly Func<string?> _browse;
    private readonly Action<bool> _theme;
    private CancellationTokenSource? _cancellation;
    private ScanSummary? _summary;
    private long _parent = 1;
    private int _offset;
    private string? _rule;
    private bool _largest;
    private readonly Dictionary<string, (string Path, FileIdentity Identity)> _keepers = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<FileEntry> Rows { get; } = [];
    public ObservableCollection<FileEntry> MapItems { get; } = [];
    public ObservableCollection<FileEntry> Review { get; } = [];
    public ObservableCollection<Finding> Findings { get; } = [];
    public ObservableCollection<DuplicateGroup> DuplicateGroups { get; } = [];
    public ObservableCollection<HistoryEntry> History { get; } = [];
    public ObservableCollection<TypeItem> FileTypes { get; } = [];
    public ObservableCollection<string> Drives { get; } = new(DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.Name));
    private string _scanPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string ScanPath { get => _scanPath; set => SetProperty(ref _scanPath, value); }
    private bool _busy;
    public bool Busy { get => _busy; private set { if (SetProperty(ref _busy, value)) { OnPropertyChanged(nameof(Idle)); RefreshCommands(); } } }
    public bool Idle => !Busy;
    private string _status = "Choose a folder or drive. Scanning is read-only.";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    private string _scope = "No scan yet";
    public string Scope { get => _scope; private set => SetProperty(ref _scope, value); }
    private string _total = "—", _files = "—", _free = "—", _coverage = "Not scanned";
    public string Total { get => _total; private set => SetProperty(ref _total, value); }
    public string FileCount { get => _files; private set => SetProperty(ref _files, value); }
    public string FreeSpace { get => _free; private set => SetProperty(ref _free, value); }
    public string Coverage { get => _coverage; private set => SetProperty(ref _coverage, value); }
    private int _section;
    public int Section
    {
        get => _section;
        set
        {
            if (!SetProperty(ref _section, value)) return;
            foreach (var p in new[] { nameof(SectionTitle), nameof(SectionDescription), nameof(ShowOverview), nameof(ShowFiles), nameof(ShowFindings), nameof(ShowReview), nameof(ShowDuplicates), nameof(ShowHistory) }) OnPropertyChanged(p);
            if (value == 5) LoadHistory();
        }
    }
    public string SectionTitle => Section switch { 1 => "Explore your storage", 2 => "Room to reclaim", 3 => "Review before you delete", 4 => "Find repeated files", 5 => "Cleanup & recovery history", _ => "Make room for what matters." };
    public string SectionDescription => Section switch
    {
        1 => "Follow the space. Inspect the files. Stay in control.", 2 => "Understand what each finding is, and what removing it means.",
        3 => "Only these exact files are included. Nothing is deleted until you confirm.", 4 => "Identical contents, different paths. Keep at least one copy.",
        5 => "Recover moved files or inspect previous outcomes. Nothing resumes automatically.", _ => "A clear picture of your disk, with thoughtful cleanup guidance."
    };
    public bool ShowOverview => Section == 0;
    public bool ShowFiles => Section is 1 or 2 or 4;
    public bool ShowFindings => Section == 2;
    public bool ShowReview => Section == 3;
    public bool ShowDuplicates => Section == 4;
    public bool ShowHistory => Section == 5;
    private FileEntry? _selected;
    public FileEntry? Selected { get => _selected; set { if (SetProperty(ref _selected, value)) { OnPropertyChanged(nameof(SelectedDescription)); RefreshCommands(); } } }
    public string SelectedDescription => Selected is null ? "Select a file or folder to inspect its size, location, and cleanup eligibility."
        : Selected.Exclusion ?? Selected.Explanation ?? (Selected.IsDirectory ? "Folders are expanded into an exact file preview. New files created later are never included." : "A file's age or size does not tell us whether you need it. Review it carefully.");
    private FileEntry? _selectedReview;
    public FileEntry? SelectedReview { get => _selectedReview; set { SetProperty(ref _selectedReview, value); RemoveReviewCommand.NotifyCanExecuteChanged(); } }
    private Finding? _selectedFinding;
    public Finding? SelectedFinding { get => _selectedFinding; set { if (SetProperty(ref _selectedFinding, value) && value is not null) { _rule = value.Rule; _offset = 0; _largest = false; LoadRows(); } } }
    private DuplicateGroup? _selectedGroup;
    public DuplicateGroup? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value)) return;
            OnPropertyChanged(nameof(KeeperChoices)); Keeper = value?.Files.FirstOrDefault(); _offset = 0;
            if (Section == 4) LoadRows();
        }
    }
    public IReadOnlyList<FileEntry> KeeperChoices => SelectedGroup?.Files.ToArray() ?? [];
    private FileEntry? _keeper;
    public FileEntry? Keeper { get => _keeper; set { SetProperty(ref _keeper, value); AddSelectedCommand.NotifyCanExecuteChanged(); } }
    private HistoryEntry? _selectedHistory;
    public HistoryEntry? SelectedHistory { get => _selectedHistory; set { SetProperty(ref _selectedHistory, value); RestoreCommand.NotifyCanExecuteChanged(); PurgeRecoveryCommand.NotifyCanExecuteChanged(); } }
    private string _filter = "";
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value)) { _offset = 0; if (!Busy) LoadRows(); } } }
    public string ReviewSummary => $"{Review.Count:N0} selected files · {ByteSize.Format(Review.Sum(f => f.Bytes))}";
    public string PageLabel => $"Showing {_offset + (Rows.Count > 0 ? 1 : 0):N0}–{_offset + Rows.Count:N0} · pages of 200";
    private bool _permanent;
    public bool Permanent { get => _permanent; set { if (SetProperty(ref _permanent, value)) OnPropertyChanged(nameof(DeleteModeDescription)); } }
    public string DeleteModeDescription => Permanent ? "Permanent deletion frees eligible storage and cannot be undone. A second confirmation is required."
        : "Default: move to a local recovery folder. Restore from History. This keeps the bytes on disk.";
    private bool _light;
    public bool LightTheme { get => _light; set { if (SetProperty(ref _light, value)) _theme(value); } }

    public IAsyncRelayCommand ScanCommand { get; }
    public IRelayCommand BrowseCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand NavigateCommand { get; }
    public IRelayCommand LargestCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }
    public IRelayCommand UpCommand { get; }
    public IAsyncRelayCommand AddSelectedCommand { get; }
    public IRelayCommand RemoveReviewCommand { get; }
    public IRelayCommand ClearReviewCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand DuplicatesCommand { get; }
    public IAsyncRelayCommand RestoreCommand { get; }
    public IAsyncRelayCommand PurgeRecoveryCommand { get; }
    public IRelayCommand NextPageCommand { get; }
    public IRelayCommand PreviousPageCommand { get; }
    public IRelayCommand RevealCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }

    public MainViewModel(string dataDirectory, Func<CleanupManifest, bool> confirm, Func<string, string, bool> confirmRestore,
        Func<string?> browse, Action<bool> theme)
    {
        _store = new(Path.Combine(dataDirectory, "scan.db")); _confirm = confirm; _confirmRestore = confirmRestore; _browse = browse; _theme = theme;
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !Busy);
        BrowseCommand = new RelayCommand(() => { if (_browse() is { } path) ScanPath = path; }, () => !Busy);
        CancelCommand = new RelayCommand(() => { _cancellation?.Cancel(); Status = "Stopping after the current operation. Your files are not automatically cleaned up."; }, () => Busy);
        NavigateCommand = new RelayCommand<string>(s => { if (!Busy && int.TryParse(s, out var section)) { Section = section; if (section == 1) { _rule = null; LoadRows(); } } });
        LargestCommand = new RelayCommand(() => { Section = 1; _largest = true; _rule = null; _offset = 0; LoadRows(); }, () => !Busy && _summary is not null);
        OpenFolderCommand = new RelayCommand(() => { if (Selected is { IsDirectory: true } f) { _parent = f.Id; _offset = 0; _largest = false; _rule = null; Section = 1; LoadRows(); } }, () => !Busy && Selected?.IsDirectory == true);
        UpCommand = new RelayCommand(() => { _parent = Math.Max(1, _store.Get(_parent)?.ParentId ?? 1); _largest = false; _rule = null; _offset = 0; LoadRows(); }, () => !Busy && _summary is not null);
        AddSelectedCommand = new AsyncRelayCommand(AddSelectedAsync, () => !Busy && Selected is { Exclusion: null } && (Section != 4 || Keeper is not null && Selected.Path != Keeper.Path));
        RemoveReviewCommand = new RelayCommand(() => { if (SelectedReview is { } f) { Review.Remove(f); _keepers.Remove(f.Path); ReviewChanged(); } }, () => !Busy && SelectedReview is not null);
        ClearReviewCommand = new RelayCommand(() => { Review.Clear(); _keepers.Clear(); ReviewChanged(); }, () => !Busy && Review.Count > 0);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => !Busy && Review.Count > 0);
        DuplicatesCommand = new AsyncRelayCommand(FindDuplicatesAsync, () => !Busy && _summary is not null);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !Busy && SelectedHistory?.CanRestore == true);
        PurgeRecoveryCommand = new AsyncRelayCommand(PurgeRecoveryAsync, () => !Busy && SelectedHistory?.CanRestore == true);
        NextPageCommand = new RelayCommand(() => { _offset += 200; LoadRows(); }, () => !Busy && Rows.Count == 200);
        PreviousPageCommand = new RelayCommand(() => { _offset = Math.Max(0, _offset - 200); LoadRows(); }, () => !Busy && _offset > 0);
        RevealCommand = new RelayCommand(Reveal, () => !Busy && Selected is not null);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !Busy && _summary is not null);
        LoadHistory();
        if (History.Any(h => h.Status is "Pending" or "Unknown")) Status = "History contains an interrupted operation. Inspect its original and recovery paths; nothing will be retried.";
    }
    private void RefreshCommands()
    {
        foreach (var c in new IRelayCommand?[] { ScanCommand, BrowseCommand, CancelCommand, LargestCommand, OpenFolderCommand, UpCommand, AddSelectedCommand, RemoveReviewCommand, ClearReviewCommand, DeleteCommand, DuplicatesCommand, RestoreCommand, PurgeRecoveryCommand, NextPageCommand, PreviousPageCommand, RevealCommand, ExportCommand }) c?.NotifyCanExecuteChanged();
    }
    private void ReviewChanged() { OnPropertyChanged(nameof(ReviewSummary)); RefreshCommands(); }
    private async Task ScanAsync()
    {
        Busy = true; _cancellation = new(); Review.Clear(); _keepers.Clear(); ReviewChanged(); Findings.Clear(); DuplicateGroups.Clear(); Rows.Clear(); MapItems.Clear(); FileTypes.Clear(); Selected = null; _rule = null; _largest = false; _parent = 1; _offset = 0;
        try
        {
            Status = "Reading file metadata. No cleanup is running.";
            var liveMap = Stopwatch.StartNew();
            var progress = new Progress<ScanProgress>(p =>
            {
                Total = ByteSize.Format(p.Bytes); FileCount = $"{p.Files:N0}"; Coverage = p.Finished ? p.Cancelled ? "Partial scan" : $"{p.Skipped:N0} skipped" : "Scanning…";
                Status = p.Finished ? "Analyzing the scan locally…" : $"{p.Folders:N0} folders · {p.Skipped:N0} skipped · {p.Current}";
                if (!p.Finished && Section == 0 && liveMap.ElapsedMilliseconds >= 750 && p.Files > 0)
                {
                    MapItems.Clear(); foreach (var f in _store.Largest(limit: 20)) MapItems.Add(f); liveMap.Restart();
                }
            });
            _summary = await Task.Run(() => new DiskScanner(_store, _metadata).Scan(ScanPath, progress, _cancellation.Token));
            Scope = _summary.Root; Total = ByteSize.Format(_summary.Bytes); FileCount = $"{_summary.Files:N0}"; Coverage = _summary.Complete ? $"{_summary.Skipped:N0} skipped" : "Partial scan";
            if (!_cancellation.IsCancellationRequested)
            {
                var findings = await Task.Run(() => new FindingAnalyzer(_store).Analyze(_cancellation.Token)); foreach (var f in findings) Findings.Add(f);
            }
            foreach (var (ext, size, count) in _store.Types().Take(8)) FileTypes.Add(new(ext, ByteSize.Format(size), count.ToString("N0"), _summary.Bytes > 0 ? (double)size / _summary.Bytes * 100 : 0));
            UpdateFree(); Section = 0; LoadRows();
            Status = _summary.Complete ? $"Scan complete · {_summary.Files:N0} files · {_summary.Skipped:N0} skipped. Nothing has been deleted." : "Partial scan saved. Nothing has been deleted.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException) { _summary = null; Status = "Scan could not finish: " + ex.Message; Coverage = "Incomplete"; }
        finally { Busy = false; _cancellation.Dispose(); _cancellation = null; }
    }
    private void LoadRows()
    {
        Rows.Clear(); MapItems.Clear(); Selected = null;
        if (_summary is null) { OnPropertyChanged(nameof(PageLabel)); return; }
        IReadOnlyList<FileEntry> rows = Section == 4 ? SelectedGroup?.Files.Where(f => f.Path.Contains(Filter, StringComparison.OrdinalIgnoreCase)).ToArray() ?? []
            : _rule is not null ? _store.RuleFiles(_rule, _offset) : _largest ? _store.Largest(Filter, _offset) : _store.Children(_parent, Filter, _offset);
        foreach (var f in rows) Rows.Add(f);
        if (Section != 4)
        {
            var visible = _store.Children(_parent, limit: 119); foreach (var f in visible) MapItems.Add(f);
            long remainder = Math.Max(0, (_store.Get(_parent)?.Bytes ?? 0) - visible.Sum(f => f.Bytes));
            if (remainder > 0) MapItems.Add(new(-1, _parent, "", "Other files · use the list", false, remainder, null, null, "Use the paged file list to inspect these smaller entries."));
        }
        Scope = _largest ? _summary.Root + " · Largest files" : _rule is not null ? _summary.Root + " · " + _rule : _store.Get(_parent)?.Path ?? _summary.Root;
        OnPropertyChanged(nameof(PageLabel)); RefreshCommands();
    }
    private async Task AddSelectedAsync()
    {
        if (Selected is not { } selected) return;
        if (Section == 4)
        {
            if (Keeper?.Identity is not { } ki || SelectedGroup is null || selected.Path == Keeper.Path) return;
            if (Review.Any(f => f.Path == Keeper.Path) || _keepers.Values.Any(k => k.Path == selected.Path)) { Status = "A keeper cannot also be queued for deletion. Remove that selection first."; return; }
            _keepers[selected.Path] = (Keeper.Path, ki);
        }
        Busy = true;
        try
        {
            var candidates = await Task.Run(() =>
            {
                List<FileEntry> list = [];
                if (!selected.IsDirectory) { list.Add(selected); return list; }
                long after = 0;
                while (true)
                {
                    var page = _store.FilesAfter(after); if (page.Count == 0) break;
                    foreach (var f in page.Where(f => PathPolicy.Within(f.Path, selected.Path) && f.Exclusion is null && f.Identity is not null))
                    { list.Add(f); if (list.Count >= 10000) return list; }
                    after = page[^1].Id;
                }
                return list;
            });
            int skipped = 0;
            var queuedPaths = Review.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keeperPaths = _keepers.Values.Select(k => k.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in candidates)
            {
                if (Review.Count >= 10000) break;
                if (file.Exclusion is not null || file.Identity is null || queuedPaths.Contains(file.Path) || keeperPaths.Contains(file.Path)) { skipped++; continue; }
                Review.Add(file);
                queuedPaths.Add(file.Path);
            }
            Status = $"{Review.Count:N0} exact files in review; {skipped:N0} unsupported or repeated entries skipped. Limit: 10,000 per batch. Nothing deleted.";
            ReviewChanged();
        }
        finally { Busy = false; }
    }
    private async Task DeleteAsync()
    {
        try
        {
            var entries = Review.Select(f => new ManifestEntry(f.Path, f.Identity!, f.Explanation ?? "User selected this file.", _keepers.TryGetValue(f.Path, out var k) ? k.Path : null, _keepers.TryGetValue(f.Path, out var k2) ? k2.Identity : null));
            var manifest = new CleanupManifest(entries, Permanent ? DeleteMode.Permanent : DeleteMode.Recovery);
            if (!_confirm(manifest)) { Status = "Deletion canceled. No files were changed."; return; }
            var consent = _gate.Confirm(manifest, true, true); // Only reached from positive modal button.
            Busy = true; _cancellation = new(); long? before = AvailableFree();
            var progress = new Progress<CleanupOutcome>(o => Status = $"{o.Status}: {o.Path}");
            var outcomes = await Task.Run(() => new CleanupExecutor(_gate, new WindowsCleanupBackend(_store, _metadata)).Execute(manifest, consent, progress, _cancellation.Token));
            foreach (var outcome in outcomes.Where(o => o.Status is OutcomeStatus.Recovered or OutcomeStatus.Deleted))
            { var row = Review.FirstOrDefault(f => f.Path == outcome.Path); if (row is not null) Review.Remove(row); _keepers.Remove(outcome.Path); }
            long? after = AvailableFree(); UpdateFree(); LoadHistory(); ReviewChanged();
            int success = outcomes.Count(o => o.Status is OutcomeStatus.Recovered or OutcomeStatus.Deleted);
            string delta = before.HasValue && after.HasValue ? ByteSize.Format(Math.Max(0, after.Value - before.Value)) : "unknown";
            Status = $"{success:N0} completed · {outcomes.Count - success:N0} skipped/failed. " + (Permanent ? $"Observed free-space increase: {delta}; other activity can affect this." : "Files moved to recovery; no space reclaimed. Restore from History.");
            // Retire stale views instead of presenting deleted files as still live.
            Rows.Clear(); MapItems.Clear(); DuplicateGroups.Clear(); Findings.Clear(); _summary = null; Coverage = "Scan again";
            Section = 5;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { Status = "Cleanup stopped. Inspect History before retrying: " + ex.Message; LoadHistory(); }
        finally { Busy = false; _cancellation?.Dispose(); _cancellation = null; }
    }
    private async Task FindDuplicatesAsync()
    {
        Busy = true; _cancellation = new(); DuplicateGroups.Clear(); SelectedGroup = null;
        try
        {
            var progress = new Progress<string>(p => Status = "Comparing contents: " + p);
            var groups = await Task.Run(() => new DuplicateFinder(_store, _metadata).Find(progress, _cancellation.Token));
            foreach (var group in groups) DuplicateGroups.Add(group);
            Status = $"{groups.Count:N0} verified groups. Nothing selected for deletion. Limits: 500 groups, 10,000 result files, 2,000 candidates per size, 5,000 sizes.";
        }
        catch (OperationCanceledException) { Status = "Duplicate analysis canceled. No files changed."; }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { Status = "Duplicate analysis could not finish: " + ex.Message; }
        finally { Busy = false; _cancellation.Dispose(); _cancellation = null; }
    }
    private async Task RestoreAsync()
    {
        if (SelectedHistory is not { CanRestore: true } history || !_confirmRestore("Restore this file?", "Restore to its original path without overwriting anything?\n\n" + history.OriginalPath)) return;
        Busy = true;
        try { var outcome = await Task.Run(() => new WindowsCleanupBackend(_store, _metadata).Restore(history, true)); Status = outcome.Detail; LoadHistory(); UpdateFree(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { Status = "Restore blocked: " + ex.Message; }
        finally { Busy = false; }
    }
    private async Task PurgeRecoveryAsync()
    {
        if (SelectedHistory is not { CanRestore: true, RecoveryPath: not null } history) return;
        try
        {
            var entry = new ManifestEntry(history.RecoveryPath, history.Identity, "Recovered copy of " + history.OriginalPath, RecoveryHistoryId: history.Id);
            var manifest = new CleanupManifest([entry], DeleteMode.Permanent);
            if (!_confirm(manifest)) { Status = "Permanent removal canceled. Your recovery file is intact."; return; }
            var consent = _gate.Confirm(manifest, true, true); Busy = true;
            var outcome = (await Task.Run(() => new CleanupExecutor(_gate, new WindowsCleanupBackend(_store, _metadata)).Execute(manifest, consent)))[0];
            Status = outcome.Detail; LoadHistory(); UpdateFree();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        { Status = "Recovery removal stopped. Inspect History: " + ex.Message; LoadHistory(); }
        finally { Busy = false; }
    }
    private void LoadHistory() { History.Clear(); foreach (var entry in _store.History()) History.Add(entry); }
    private long? AvailableFree() { try { return new DriveInfo(Path.GetPathRoot(_summary?.Root ?? ScanPath)!).AvailableFreeSpace; } catch (Exception ex) when (ex is IOException or ArgumentException) { return null; } }
    private void UpdateFree() => FreeSpace = AvailableFree() is { } n ? ByteSize.Format(n) : "Unknown";
    private void Reveal()
    {
        if (Selected is not { } file) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + file.Path + "\"", UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { Status = "Explorer could not open: " + ex.Message; }
    }
    private async Task ExportAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV report|*.csv", FileName = "disk-usage.csv", Title = "Export paths and sizes (contains private filenames)", OverwritePrompt = true };
        if (dialog.ShowDialog() != true) return;
        try
        {
            Busy = true; _cancellation = new();
            await Task.Run(() =>
            {
                // Never overwrite a user file, including a scanned file, through report export.
                using var stream = new FileStream(dialog.FileName, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream); writer.WriteLine("Path,LogicalBytes,AllocatedBytes,Eligibility,Finding");
                long after = 0;
                while (true)
                {
                    _cancellation.Token.ThrowIfCancellationRequested();
                    var page = _store.FilesAfter(after); if (page.Count == 0) break;
                    foreach (var f in page) writer.WriteLine($"{Csv(f.Path)},{f.Bytes},{f.Allocated},{Csv(f.Exclusion ?? "Review carefully")},{Csv(f.Rule ?? "")}");
                    after = page[^1].Id;
                }
            });
            Status = "Report exported locally. It contains private file paths; review before sharing.";
        }
        catch (OperationCanceledException) { Status = "Export stopped. The partial report is retained; choose a new filename to export again."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { Status = "Export failed; choose a new unused filename: " + ex.Message; }
        finally { Busy = false; _cancellation?.Dispose(); _cancellation = null; }
    }
    private static string Csv(string s) => "\"" + (s.Length > 0 && "=+-@".Contains(s[0]) ? "'" : "") + s.Replace("\"", "\"\"") + "\"";
    public void RequestStop() => _cancellation?.Cancel();
    public void Dispose() { if (!Busy) _store.Dispose(); }
}

public sealed record TypeItem(string Extension, string Size, string Count, double Percent);
