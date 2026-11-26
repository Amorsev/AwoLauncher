using System.IO;
using System.Net.Http;
using System.Windows;
using Launcher.Core;

namespace Launcher.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var settings = LocalData.Read(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), new Settings());
            var window = new MainWindow { DataContext = new MainViewModel(settings) };
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(exception.ToString());
            MessageBox.Show($"Не удалось открыть лаунчер. {exception.Message}", "AwoLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
