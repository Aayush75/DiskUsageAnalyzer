using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiskUsageAnalyzer.App;
using DiskUsageAnalyzer.Core;

namespace DiskUsageAnalyzer.UITests;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--installer" && (args.Length == 3 || args.Length == 5 && args[3] == "--resume"))
            {
                InstallerSmoke.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), SmokePackaged, args.Length == 5 ? Path.GetFullPath(args[4]) : null);
                return 0;
            }
            string artifacts = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui"); Directory.CreateDirectory(artifacts);
            string fixture = Path.Combine(artifacts, "fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
            File.WriteAllText(Path.Combine(fixture, ".fixture-owner"), "Disposable DiskUsageAnalyzer UI test fixture.");
            string sentinel = Path.Combine(fixture, "sentinel.txt"); File.WriteAllText(sentinel, "THIS FILE MUST SURVIVE CANCEL");
            File.WriteAllText(Path.Combine(fixture, "copy-a.txt"), "matching duplicate"); File.WriteAllText(Path.Combine(fixture, "copy-b.txt"), "matching duplicate");
            Directory.CreateDirectory(Path.Combine(fixture, "project", "bin")); File.WriteAllText(Path.Combine(fixture, "project", "sample.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(fixture, "project", "bin", "output.dll"), new string('x', 100000));
            File.WriteAllText(Path.Combine(fixture, "archive.zip"), new string('x', 140000));
            Directory.CreateDirectory(Path.Combine(fixture, "photos")); File.WriteAllText(Path.Combine(fixture, "photos", "image.jpg"), new string('p', 80000));
            // Host production windows without App.OnStartup creating a second default window.
            var app = new Application();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/DiskUsageAnalyzer.App;component/Theme.xaml") });
            app.Resources.Add("BoolVisibility", new BooleanToVisibilityConverter());
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow(Path.Combine(artifacts, "data-" + Guid.NewGuid().ToString("N")), fixture); window.Show(); Pump();
            var vm = window.ViewModel;
            Run(vm.ScanCommand.ExecuteAsync(null));
            Check(vm.FileCount != "—" && vm.Coverage.Contains("skipped"), "Real WPF scan completed: " + vm.Status);
            Check(File.ReadAllText(sentinel) == "THIS FILE MUST SURVIVE CANCEL", "Scan preserved sentinel");
            string actualScanPath = vm.ScanPath;
            vm.ScanPath = @"C:\Demo\Storage"; // Public screenshots use a synthetic display path, not a username.
            Capture(window, Path.Combine(artifacts, "overview-dark.png"));
            vm.LightTheme = true; Pump(); Capture(window, Path.Combine(artifacts, "overview-light.png")); vm.LightTheme = false; vm.ScanPath = actualScanPath;
            vm.LargestCommand.Execute(null); Pump(); Check(vm.Rows.Count >= 5, "Largest file view has real results");
            vm.Selected = vm.Rows.Single(f => f.Path == sentinel); Run(vm.AddSelectedCommand.ExecuteAsync(null));
            Check(vm.Review.Count == 1, "Selecting file only queues review"); Check(File.Exists(sentinel), "Queueing preserved sentinel");
            vm.Section = 3; Pump(); Capture(window, Path.Combine(artifacts, "review.png"));
            var deleteButton = Find<Button>(window, "DeleteSelectedButton"); Check(deleteButton is not null && deleteButton.IsEnabled, "Actual Delete button enabled for explicit review");
            // Drive the actual bound Delete command, then the actual modal's Cancel button through its automation peer.
            var cancelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            cancelTimer.Tick += (_, _) =>
            {
                var modal = app.Windows.OfType<ConfirmationDialog>().FirstOrDefault(); if (modal is null) return;
                cancelTimer.Stop();
                Check(modal.Title.Contains("Are you sure"), "Mandatory confirmation opened");
                var cancel = Find<Button>(modal, "CancelDeleteButton")!;
                Check(cancel.IsDefault && cancel.IsCancel, "Cancel is the default and Escape action");
                Check(!Find<Button>(modal, "ConfirmDeleteButton")!.IsEnabled, "Positive button disabled before acknowledgment");
                var fullPath = modal.FindName("PreviewPath") as TextBox;
                Check(fullPath is { IsReadOnly: true } && Path.IsPathFullyQualified(fullPath.Text) && File.Exists(fullPath.Text), "Confirmation exposes the selected full existing path read-only");
                Capture(modal, Path.Combine(artifacts, "confirmation.png")); Invoke(cancel);
            };
            cancelTimer.Start(); Run(vm.DeleteCommand.ExecuteAsync(null));
            Check(File.ReadAllText(sentinel) == "THIS FILE MUST SURVIVE CANCEL", "Cancel has zero filesystem mutations");
            Check(vm.Review.Count == 1 && vm.History.Count == 0, "Cancel did not create an operation or discard review");
            // Repeat with window close, which must also be denial.
            var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            closeTimer.Tick += (_, _) => { var modal = app.Windows.OfType<ConfirmationDialog>().FirstOrDefault(); if (modal is null) return; closeTimer.Stop(); modal.Close(); };
            closeTimer.Start(); Run(vm.DeleteCommand.ExecuteAsync(null)); Check(File.Exists(sentinel) && vm.History.Count == 0, "Closing confirmation does not authorize mutation");
            // Approved recovery journey: checkbox + positive button activation. Only disposable fixture file is affected.
            var approveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            approveTimer.Tick += (_, _) =>
            {
                var modal = app.Windows.OfType<ConfirmationDialog>().FirstOrDefault(); if (modal is null) return; approveTimer.Stop();
                var acknowledgment = Find<CheckBox>(modal, "DeleteAcknowledgment")!;
                acknowledgment.IsChecked = true; Pump(); Invoke(Find<Button>(modal, "ConfirmDeleteButton")!);
            };
            approveTimer.Start(); Run(vm.DeleteCommand.ExecuteAsync(null));
            Check(!File.Exists(sentinel), "Approved recovery removed only the selected original pathname");
            var history = vm.History.Single(); Check(history.Status == "Recovered" && File.ReadAllText(history.RecoveryPath!) == "THIS FILE MUST SURVIVE CANCEL", "Recovery retained exact content");
            Check(File.Exists(Path.Combine(fixture, "archive.zip")), "Unapproved file survived approved cleanup");
            Capture(window, Path.Combine(artifacts, "history.png"));
            // Recovery permanent removal: Cancel preserves recovery, a separate positive action removes only that copy.
            vm.SelectedHistory = history;
            cancelTimer.Start(); Run(vm.PurgeRecoveryCommand.ExecuteAsync(null)); Check(File.Exists(history.RecoveryPath), "Cancel permanent recovery removal preserves content");
            approveTimer.Start(); Run(vm.PurgeRecoveryCommand.ExecuteAsync(null)); Check(!File.Exists(history.RecoveryPath), "New explicit permanent confirmation removed recovered copy");
            Check(vm.History.All(h => !h.CanRestore), "History does not offer restore after permanent removal");
            Run(vm.ScanCommand.ExecuteAsync(null)); vm.LargestCommand.Execute(null); vm.Selected = vm.Rows.Single(f => f.Name == "copy-b.txt"); Run(vm.AddSelectedCommand.ExecuteAsync(null));
            vm.Section = 3; vm.Permanent = true;
            cancelTimer.Start(); Run(vm.DeleteCommand.ExecuteAsync(null)); Check(File.Exists(Path.Combine(fixture, "copy-b.txt")), "Cancel permanent deletion preserves selected file");
            approveTimer.Start(); Run(vm.DeleteCommand.ExecuteAsync(null)); Check(!File.Exists(Path.Combine(fixture, "copy-b.txt")), "Permanent deletion requires and honors separate confirmation");
            Check(File.Exists(Path.Combine(fixture, "copy-a.txt")), "Unselected duplicate survives permanent deletion");
            Run(vm.ScanCommand.ExecuteAsync(null)); vm.Section = 4; Run(vm.DuplicatesCommand.ExecuteAsync(null));
            Check(vm.Status.Contains("verified groups"), "Duplicate UI analysis completed without cleanup");
            // Test narrow viewport and theme layout against the real window.
            window.Width = 1024; window.Height = 768; Pump(); Capture(window, Path.Combine(artifacts, "compact.png"));
            Check(window.ActualWidth >= 1024, "Compact window remains usable");
            window.Close(); app.Shutdown();
            if (args.Length > 2 && args[1] == "--packaged") SmokePackaged(Path.GetFullPath(args[2]), fixture, artifacts);
            File.WriteAllText(Path.Combine(artifacts, "results.txt"), $"PASS: {_checks} actual WPF workflow assertions.\nFixture: {fixture}\n");
            Console.WriteLine($"PASS: {_checks} WPF workflow assertions; screenshots in {artifacts}"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); _checks++; Console.WriteLine("PASS " + message); }
    private static void SmokePackaged(string executable, string fixture, string artifacts)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        start.Environment["DOTNET_ROOT"] = Path.Combine(artifacts, "no-global-runtime");
        start.Environment["DOTNET_ROOT_X64"] = Path.Combine(artifacts, "no-global-runtime");
        start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(Path.Combine(artifacts, "packaged-data-" + Guid.NewGuid().ToString("N")));
        start.ArgumentList.Add("--scan"); start.ArgumentList.Add(fixture);
        using var process = Process.Start(start) ?? throw new IOException("Packaged application did not start.");
        var timeout = Stopwatch.StartNew();
        while (process.MainWindowHandle == IntPtr.Zero && !process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(15)) { Thread.Sleep(100); process.Refresh(); }
        Check(!process.HasExited && process.MainWindowHandle != IntPtr.Zero, "Self-contained packaged application starts without global .NET");
        var root = AutomationElement.FromHandle(process.MainWindowHandle);
        var scan = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ScanButton"));
        Check(scan is not null, "Packaged binary exposes actual accessible scan control");
        ((InvokePattern)scan!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        bool complete = false; timeout.Restart();
        while (!complete && !process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(100);
            var status = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StatusText"));
            complete = status?.Current.Name.Contains("Scan complete", StringComparison.Ordinal) == true;
        }
        Check(complete, "Packaged production startup and scan workflow completed");
        Check(File.Exists(Path.Combine(fixture, "archive.zip")), "Packaged scan preserved unapproved content");
        process.Refresh();
        string directory = Path.GetDirectoryName(executable)!;
        Check(process.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) && m.FileName.StartsWith(directory, StringComparison.OrdinalIgnoreCase)), "Packaged process uses its bundled runtime");
        ((WindowPattern)root.GetCurrentPattern(WindowPattern.Pattern)).Close();
        Check(process.WaitForExit(10000), "Packaged application closes cleanly");
    }
    private static void Run(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted) { Pump(); Thread.Sleep(10); if (watch.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("UI workflow exceeded one minute."); }
        task.GetAwaiter().GetResult(); Pump();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }
    private static T? Find<T>(DependencyObject root, string id) where T : DependencyObject
    {
        if (root is T match && AutomationProperties.GetAutomationId(root) == id) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Find<T>(VisualTreeHelper.GetChild(root, i), id) is { } result) return result;
        return null;
    }
    private static void Invoke(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke(); Pump();
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
