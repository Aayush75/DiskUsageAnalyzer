using System.Windows;
using DiskUsageAnalyzer.Core;

namespace DiskUsageAnalyzer.App;

public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog(CleanupManifest manifest)
    {
        InitializeComponent(); Preview.ItemsSource = manifest.Entries; Preview.SelectedItem = manifest.Entries[0];
        Summary.Text = $"{manifest.Entries.Length:N0} {(manifest.Entries.Length == 1 ? "file" : "files")} · {ByteSize.Format(manifest.Bytes)}";
        PositiveButton.Content = manifest.Mode == DeleteMode.Permanent ? $"Permanently delete {manifest.Entries.Length:N0} {(manifest.Entries.Length == 1 ? "file" : "files")}" : "Delete to local recovery";
        Explanation.Text = manifest.Mode == DeleteMode.Permanent ? "PERMANENT DELETION CANNOT BE UNDONE. These exact files will be removed. Changed, locked or unsupported files are skipped."
            : "These exact files will move to a local recovery folder on the same disk. Restore from History. No space is freed until permanent deletion is deliberately chosen.";
        if (manifest.Mode == DeleteMode.Permanent) Explanation.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        Loaded += (_, _) => CancelButton.Focus();
    }
    private void OnPositive(object sender, RoutedEventArgs e) { if (Acknowledgment.IsChecked == true) DialogResult = true; }
}
