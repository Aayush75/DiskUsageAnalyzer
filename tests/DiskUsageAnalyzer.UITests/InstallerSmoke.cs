using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Microsoft.Win32;

namespace DiskUsageAnalyzer.UITests;

// Opt-in, owned-fixture installer journey. Never takes over an existing installation.
internal static class InstallerSmoke
{
    private const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7792FB74-6CE5-49CF-9BDC-E8515345C27F}_is1";
    private static int _checks;
    public static void Run(string setup, string output, Action<string, string, string> packagedSmoke, string? resumeRoot = null)
    {
        string root = resumeRoot ?? Path.Combine(output, "installer-fixture-" + Guid.NewGuid().ToString("N"));
        const string marker = "Owned installer test fixture; intentionally retained.";
        if (resumeRoot is not null && (!root.StartsWith(Path.TrimEndingDirectorySeparator(output) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(root).Replace("installer-fixture-", ""), "N", out _) || File.ReadAllText(Path.Combine(root, ".fixture-owner")) != marker))
            throw new InvalidOperationException("Resume requires the exact retained owned test fixture.");
        string install = Path.Combine(root, "Application with spaces α");
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var registry = RegistryKey.OpenBaseKey(hive, view);
            using var existing = registry.OpenSubKey(RegistryPath);
            if (existing is not null && (resumeRoot is null || hive != RegistryHive.CurrentUser || existing.GetValue("InstallLocation") is not string location
                || !Path.TrimEndingDirectorySeparator(location).Equals(install, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("An existing installation is registered. Installer tests will not replace it.");
        }
        Directory.CreateDirectory(root);
        if (resumeRoot is null) File.WriteAllText(Path.Combine(root, ".fixture-owner"), marker);
        Install(setup, install);
        string executable = Path.Combine(install, "DiskUsageAnalyzer.App.exe");
        Check(File.Exists(executable) && File.Exists(Path.Combine(install, "coreclr.dll")), "Per-user installation contains app and bundled runtime");
        using (var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
        using (var installed = registry.OpenSubKey(RegistryPath))
            Check(installed?.GetValue("InstallLocation") is string location && Path.TrimEndingDirectorySeparator(location).Equals(install, StringComparison.OrdinalIgnoreCase), "Registration identifies this exact owned installation");
        string loose = Path.Combine(install, "user-created-file.txt"); File.WriteAllText(loose, "PRESERVE USER CONTENT");
        Install(setup, install);
        Check(File.ReadAllText(loose) == "PRESERVE USER CONTENT", "Same-version reinstall preserves unowned files");
        string fixture = Path.Combine(root, "scan-fixture"); Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "archive.zip"), "UNAPPROVED SCAN CONTENT");
        string recovery = Path.Combine(fixture, ".DiskUsageAnalyzer-Recovery"); Directory.CreateDirectory(recovery);
        File.WriteAllText(Path.Combine(recovery, "retained.txt"), "RETAIN RECOVERY CONTENT");
        packagedSmoke(executable, fixture, root);
        var retained = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        string uninstaller = Path.Combine(install, "unins000.exe");
        Uninstall(uninstaller, approve: false);
        Check(File.Exists(executable) && File.ReadAllText(loose) == "PRESERVE USER CONTENT", "Uninstall cancellation removes nothing");
        Uninstall(uninstaller, approve: true);
        Check(!File.Exists(executable), "Explicit Yes removes installed program files");
        Check(File.ReadAllText(loose) == "PRESERVE USER CONTENT", "Approved uninstall preserves unowned files within install directory");
        Check(retained.All(item => File.Exists(item.Key) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.Key))) == item.Value), "Approved uninstall preserves scan content, application data and recovery fixture");
        using (var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
        using (var removed = registry.OpenSubKey(RegistryPath)) Check(removed is null, "Approved uninstall removes only its program registration");
        string result = $"PASS: {_checks} installer assertions plus 6 installed-executable UI assertions.\nFixture retained: {root}\n";
        File.WriteAllText(Path.Combine(root, "results.txt"), result); Console.WriteLine(result);
    }
    private static void Install(string setup, string directory)
    {
        var start = new ProcessStartInfo(setup) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string arg in new[] { "/VERYSILENT", "/SP-", "/NORESTART", "/NOICONS", "/DIR=" + directory }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Installer failed to start.");
        Check(process.WaitForExit(60000) && process.ExitCode == 0, "Installer completes without administrator elevation");
    }
    private static void Uninstall(string executable, bool approve)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("/VERYSILENT"); start.ArgumentList.Add("/NORESTART");
        using var process = Process.Start(start) ?? throw new IOException("Uninstaller failed to start.");
        var watch = Stopwatch.StartNew(); AutomationElement? dialog = null; HashSet<string> diagnostics = [];
        while (dialog is null && !process.HasExited && watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            // STARTUPINFO hides the first native dialog. Find only this process family,
            // then expose its interactive confirmation; never invoke arbitrary desktop windows.
            var ownedIds = ProcessFamily(process.Id);
            IntPtr confirmation = IntPtr.Zero;
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out uint owner);
                if (!ownedIds.Contains(owner)) return true;
                var caption = new StringBuilder(512); GetWindowText(handle, caption, caption.Capacity);
                diagnostics.Add($"Owner {owner}: {caption}");
                if (caption.ToString() == "Uninstall")
                {
                    confirmation = handle;
                }
                return true;
            }, IntPtr.Zero);
            if (confirmation != IntPtr.Zero)
            {
                ShowWindow(confirmation, 4); // Show only our confirmation, without activating unrelated apps.
                var window = AutomationElement.FromHandle(confirmation);
                var text = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty,
                    "Uninstall DiskUsageAnalyzer? Only installed program files will be removed. Scan history, recovery folders, and your files will be kept."));
                if (text is not null) dialog = window;
                else foreach (AutomationElement child in window.FindAll(TreeScope.Descendants, Condition.TrueCondition)) diagnostics.Add($"Control {child.Current.AutomationId}: {child.Current.Name}");
            }
            if (dialog is null) Thread.Sleep(100);
        }
        if (dialog is null) Console.Error.WriteLine(string.Join(Environment.NewLine, diagnostics));
        Check(dialog is not null, "Uninstaller still asks explicit confirmation in silent mode");
        var button = dialog!.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, approve ? "6" : "7"));
        Check(button is not null, approve ? "Explicit Yes button available" : "Explicit No button available");
        ((InvokePattern)button!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Check(process.WaitForExit(30000), approve ? "Confirmed uninstall completes" : "Canceled uninstall exits");
    }
    private static HashSet<uint> ProcessFamily(int parent)
    {
        var children = new Dictionary<uint, uint>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new IOException("Cannot scope installer process family.");
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32First(snapshot, ref entry)) do { children[entry.Id] = entry.Parent; } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        HashSet<uint> result = [(uint)parent]; bool changed;
        do { changed = false; foreach (var item in children) if (result.Contains(item.Value)) changed |= result.Add(item.Key); } while (changed);
        return result;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id; public UIntPtr Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    private delegate bool WindowCallback(IntPtr handle, IntPtr parameter);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++; Console.WriteLine("PASS " + message);
    }
}
