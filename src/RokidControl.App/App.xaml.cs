using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace RokidControl.App;

public partial class App : Application
{
    private const string MutexName =
        "Local\\RokidControl.Windows.SingleInstance";
    private const string ActivationEventName =
        "Local\\RokidControl.Windows.ActivateExisting";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            MutexName,
            out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
        var window = new MainWindow();
        MainWindow = window;
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, timedOut) =>
            {
                if (!timedOut && state is MainWindow existingWindow)
                {
                    existingWindow.Dispatcher.InvokeAsync(
                        existingWindow.ActivateCurrentSession);
                }
            },
            window,
            Timeout.Infinite,
            executeOnlyOnce: false);
        window.Show();
        if (e.Args.Contains("--smoke-test", StringComparer.Ordinal))
        {
            _ = CloseAfterSmokeTestAsync(window);
        }
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        _activationRegistration?.Unregister(null);
        _activationRegistration = null;
        _activationEvent?.Dispose();
        _activationEvent = null;
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    private static void SignalExistingInstance()
    {
        AllowExistingInstanceToSetForeground();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var activationEvent =
                    EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void AllowExistingInstanceToSetForeground()
    {
        var currentProcessId = Environment.ProcessId;
        var processName = Process.GetCurrentProcess().ProcessName;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.Id != currentProcessId)
                {
                    _ = NativeMethods.AllowSetForegroundWindow(
                        (uint)process.Id);
                }
            }
        }
    }

    private static async Task CloseAfterSmokeTestAsync(Window window)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        await window.Dispatcher.InvokeAsync(window.Close);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AllowSetForegroundWindow(
            uint processId);
    }
}
