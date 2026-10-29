using System.Windows;
using AzimuthTelemetry.Desktop.Theming;

namespace AzimuthTelemetry.Desktop;

public partial class App : Application
{
    const string InstanceName = "AzimuthTelemetry.SingleInstance";
    Mutex? _mutex;
    EventWaitHandle? _show;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One copy only (two would fight over the hotkey and the UDP port);
        // a second launch brings the first window forward.
        _mutex = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(InstanceName + ".Show").Set(); } catch { }
            Shutdown();
            return;
        }
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");

        // This app may sit next to azimuth.exe, beside the render shim's
        // dinput8.dll; load Windows' own first so DirectInput uses that.
        try { System.Runtime.InteropServices.NativeLibrary.Load(System.IO.Path.Combine(Environment.SystemDirectory, "dinput8.dll")); } catch { }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) =>
        {
            MessageBox.Show(a.Exception.Message, "Azimuth Telemetry", MessageBoxButton.OK, MessageBoxImage.Error);
            a.Handled = true;
        };
        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme);
        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();

        var show = _show;
        new Thread(() =>
        {
            while (show.WaitOne())
                Dispatcher.BeginInvoke(() =>
                {
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Activate();
                });
        }) { IsBackground = true }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
