using System.Threading;
using System.Windows;

namespace RokidControl.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        const string mutexName = "Local\\RokidControl.Windows.SingleInstance";
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            mutexName,
            out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Rokid Controlはすでに起動しています。",
                "Rokid Control",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Contains("--smoke-test", StringComparer.Ordinal))
        {
            _ = CloseAfterSmokeTestAsync(window);
        }
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    private static async Task CloseAfterSmokeTestAsync(Window window)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        await window.Dispatcher.InvokeAsync(window.Close);
    }
}
