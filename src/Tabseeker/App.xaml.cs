using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Tabseeker;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // elevated helper mode: read the MFT, write the index file, exit (no window)
        if (e.Args.Length >= 2 && e.Args[0] == "--build-index")
        {
            Shutdown(IndexService.BuildIndexFile(e.Args[1]));
            return;
        }
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.AppendAllText(Path.Combine(AppSettings.Dir, "error.log"), $"[{DateTime.Now:s}] {e.Exception}\n\n");
        }
        catch { }
        MessageBox.Show(e.Exception.Message, "Tabseeker 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
