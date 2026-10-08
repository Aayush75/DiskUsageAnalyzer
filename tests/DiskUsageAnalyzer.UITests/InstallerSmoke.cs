using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Automation;
using Microsoft.Win32;

namespace DiskUsageAnalyzer.UITests;

// Opt-in, owned-fixture installer journey. Never takes over an existing installation.
internal static class InstallerSmoke
{
    private const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7792FB74-6CE5-49CF-9BDC-E8515345C27F}_is1";
    private static int _checks;
    public static void Run(string setup, string output, Action<string, string, string> packagedSmoke)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var registry = RegistryKey.OpenBaseKey(hive, view);
            using var existing = registry.OpenSubKey(RegistryPath);
            if (existing is not null) throw new InvalidOperationException("An existing installation is registered. Installer tests will not replace it.");
        }
        string root = Path.Combine(output, "installer-fixture-" + Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "Application with spaces α");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".fixture-owner"), "Owned installer test fixture; intentionally retained.");
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
        var watch = Stopwatch.StartNew(); AutomationElement? dialog = null;
        while (dialog is null && !process.HasExited && watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            // Scope invocation to the exact confirmation text of our newly registered owned fixture.
            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                if (!window.Current.Name.Contains("DiskUsageAnalyzer", StringComparison.Ordinal)) continue;
                var text = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty,
                    "Uninstall DiskUsageAnalyzer? Only installed program files will be removed. Scan history, recovery folders, and your files will be kept."));
                if (text is not null) { dialog = window; break; }
            }
            if (dialog is null) Thread.Sleep(100);
        }
        Check(dialog is not null, "Uninstaller still asks explicit confirmation in silent mode");
        var button = dialog!.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, approve ? "6" : "7"));
        Check(button is not null, approve ? "Explicit Yes button available" : "Explicit No button available");
        ((InvokePattern)button!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Check(process.WaitForExit(30000), approve ? "Confirmed uninstall completes" : "Canceled uninstall exits");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++; Console.WriteLine("PASS " + message);
    }
}
