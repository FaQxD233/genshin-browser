using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using GenshinBrowser.Services;

namespace GenshinBrowser;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\GenshinBrowser_SingleInstance_3F7A2E1D";
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += App_OnUnhandledException;

        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, out var createdNew);

        // 已有实例在运行：激活它并退出当前进程
        if (!createdNew)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 启动时清理旧日志，避免无限增长
        FileLogger.PurgeOldLogs();
    }

    private static void App_OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        FileLogger.LogException(e.Exception, "App.DispatcherUnhandledException");
        System.Windows.MessageBox.Show(
            LocalizationService.Format("Status.UnhandledException", e.Exception.GetType().Name, e.Exception.Message),
            LocalizationService.Get("Status.ErrorTitle", "Genshin Browser 错误"),
            MessageBoxButton.OK);
        e.Handled = true;
    }

    private static void App_OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            FileLogger.LogException(ex, "AppDomain.UnhandledException");
        }
    }

    private static void ActivateExistingInstance()
    {
        var currentId = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
        {
            if (process.Id == currentId)
            {
                continue;
            }

            try
            {
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                if (IsIconic(handle))
                {
                    ShowWindow(handle, SwRestore);
                }

                SetForegroundWindow(handle);
                break;
            }
            catch
            {
                // 激活失败时静默忽略，避免影响现有实例
            }
        }
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
