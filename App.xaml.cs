using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
        // 代码里大量 `_ = SomeAsync()` fire-and-forget：观察异常避免完全静默
        // （.NET Core 起未观察异常不再终止进程，这里补记日志）。
        TaskScheduler.UnobservedTaskException += App_OnUnobservedTaskException;

        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, out var createdNew);

        // 已有实例在运行：激活它并退出当前进程
        if (!createdNew)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 启动时清理旧日志与 stale .tmp 文件，延后到后台线程避免阻塞首帧；
        // 之后定时重复：本应用定位为随游戏常驻数周，只在启动清理会让日志
        // 目录在长运行会话中无界增长（每天最多 1 当前 + 3 滚动 × 5MB）。
        RunMaintenanceCleanup();
        _maintenanceTimer = new DispatcherTimer { Interval = MaintenanceInterval };
        _maintenanceTimer.Tick += (_, _) => RunMaintenanceCleanup();
        _maintenanceTimer.Start();
    }

    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(6);
    private DispatcherTimer? _maintenanceTimer;

    private static void RunMaintenanceCleanup()
    {
        _ = Task.Run(() =>
        {
            try
            {
                FileLogger.PurgeOldLogs();
            }
            catch
            {
                // 清理失败不影响启动
            }

            try
            {
                var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinBrowser");
                JsonFileWriter.PurgeStaleTempFiles(dataRoot);
            }
            catch
            {
                // 清理失败不影响启动
            }
        });
    }

    // 弹窗风暴熔断：持续抛异常的 Dispatcher 场景（布局失效、绑定反复出错等）会陷入
    // 「异常 → 弹模态框 → 关闭 → 下一帧再抛」循环；冷却窗口内后续异常只记日志不再弹窗。
    private static readonly TimeSpan ExceptionDialogCooldown = TimeSpan.FromSeconds(5);
    private static DateTime _lastExceptionDialogUtc = DateTime.MinValue;

    private void App_OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        FileLogger.LogException(e.Exception, "App.DispatcherUnhandledException");

        var nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastExceptionDialogUtc >= ExceptionDialogCooldown)
        {
            _lastExceptionDialogUtc = nowUtc;
            System.Windows.MessageBox.Show(
                Application.Current?.MainWindow,
                LocalizationService.Format("Status.UnhandledException", e.Exception.GetType().Name, e.Exception.Message),
                LocalizationService.Get("Status.ErrorTitle", "Genshin Browser 错误"),
                MessageBoxButton.OK);
        }

        e.Handled = true;
    }

    private static void App_OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            FileLogger.LogException(ex, "AppDomain.UnhandledException");
        }
    }

    private static void App_OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        FileLogger.LogException(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    private static void ActivateExistingInstance()
    {
        var currentId = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
        {
            using (process)
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
