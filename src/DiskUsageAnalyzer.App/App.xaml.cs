using System.IO;
using System.Windows;

namespace DiskUsageAnalyzer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskUsageAnalyzer");
            string? scan = null;
            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--data-dir" && i + 1 < e.Args.Length) data = Path.GetFullPath(e.Args[++i]);
                else if (e.Args[i] == "--scan" && i + 1 < e.Args.Length) scan = Path.GetFullPath(e.Args[++i]);
                else throw new ArgumentException("Supported options: --data-dir <folder> and --scan <folder>. Command-line deletion is unavailable.");
            }
            var window = new MainWindow(data, scan); MainWindow = window; window.Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            MessageBox.Show("DiskUsageAnalyzer could not start. Your files have not been cleaned up.\n\n" + ex.Message, "Startup problem", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1);
        }
    }
}
