using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using DiskUsageAnalyzer.Core;

namespace DiskUsageAnalyzer.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    public MainWindow(string dataDirectory, string? initialPath = null)
    {
        InitializeComponent();
        ViewModel = new(dataDirectory, Confirm, ConfirmRestore, Browse, SetTheme); DataContext = ViewModel;
        if (initialPath is not null) ViewModel.ScanPath = initialPath;
        if (SystemParameters.HighContrast) SetHighContrast();
        Closed += (_, _) => ViewModel.Dispose();
    }
    private bool Confirm(CleanupManifest manifest) => new ConfirmationDialog(manifest) { Owner = this }.ShowDialog() == true;
    private bool ConfirmRestore(string title, string message) => MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    private string? Browse()
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to analyze", Multiselect = false };
        return picker.ShowDialog(this) == true ? picker.FolderName : null;
    }
    private void OnFileDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    { if (ViewModel.Selected?.IsDirectory == true && ViewModel.OpenFolderCommand.CanExecute(null)) ViewModel.OpenFolderCommand.Execute(null); }
    private void OnDriveChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    { if (sender is System.Windows.Controls.ComboBox { SelectedItem: string path } && DataContext is MainViewModel vm) vm.ScanPath = path; }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (ViewModel.Busy)
        {
            ViewModel.RequestStop(); e.Cancel = true;
            MessageBox.Show(this, "Stopping the current task. Close the window again once it finishes. Already committed file operations may complete.", "Task stopping", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        base.OnClosing(e);
    }
    private void SetTheme(bool light)
    {
        if (SystemParameters.HighContrast) { SetHighContrast(); return; }
        var values = light ? new Dictionary<string, string> { ["BackgroundBrush"] = "#F4F7FB", ["SidebarBrush"] = "#E8EDF5", ["CardBrush"] = "#FFFFFF", ["RaisedBrush"] = "#E4ECF8", ["BorderBrush"] = "#CED8E7", ["TextBrush"] = "#17243B", ["MutedBrush"] = "#53657C", ["AccentBrush"] = "#275ED5", ["AccentTextBrush"] = "#FFFFFF", ["SuccessBrush"] = "#08735D", ["DangerBrush"] = "#B3223B" }
            : new Dictionary<string, string> { ["BackgroundBrush"] = "#0D111A", ["SidebarBrush"] = "#111722", ["CardBrush"] = "#171F2C", ["RaisedBrush"] = "#202C3D", ["BorderBrush"] = "#2A3546", ["TextBrush"] = "#EEF3FA", ["MutedBrush"] = "#A5B2C6", ["AccentBrush"] = "#7FA8FF", ["AccentTextBrush"] = "#101828", ["SuccessBrush"] = "#71DCC0", ["DangerBrush"] = "#FFACB1" };
        foreach (var (key, value) in values) Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    }
    private static void SetHighContrast()
    {
        foreach (var key in new[] { "BackgroundBrush", "SidebarBrush", "CardBrush", "RaisedBrush" }) Application.Current.Resources[key] = SystemColors.WindowBrush;
        foreach (var key in new[] { "TextBrush", "MutedBrush", "SuccessBrush", "DangerBrush", "BorderBrush" }) Application.Current.Resources[key] = SystemColors.WindowTextBrush;
        Application.Current.Resources["AccentBrush"] = SystemColors.HighlightBrush; Application.Current.Resources["AccentTextBrush"] = SystemColors.HighlightTextBrush;
    }
}
