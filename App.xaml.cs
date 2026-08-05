using System.Threading;
using System.Windows;
using System.Runtime.InteropServices;

namespace GmailDesktop;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _ = SetCurrentProcessExplicitAppUserModelID("GmailDesktop.App");

        const string mutexName = "GmailDesktop.SingleInstance.7043D4A4-3AB5-4DF8-B439-82E0B8E83138";
        _singleInstanceMutex = new Mutex(true, mutexName, out var isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Gmail Desktop is already running. Check the taskbar or notification area.",
                "Gmail Desktop",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"Gmail Desktop encountered an unexpected error.\n\n{args.Exception.Message}",
                "Gmail Desktop",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex) _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);
}
