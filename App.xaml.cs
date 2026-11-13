using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using Application = System.Windows.Application;
using GenshinBrowser.Services;

namespace GenshinBrowser;

public partial class App : Application
{
    /// <summary>程序自己重启时带的参数：新进程先等旧进程退出，而不是当成「已有实例」直接退出。</summary>
    public const string RestartArgument = "--wait-for-previous";

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

        // 更改数据目录后的自动重启：旧进程还在退出中，等它释放互斥量
        if (!createdNew && e.Args.Contains(RestartArgument))
        {
            createdNew = WaitForPreviousInstance(_singleInstanceMutex);
        }

        // 已有实例在运行：带命令（任务栏跳转列表的菜单项）就把命令转给它，否则激活它；然后退出当前进程
        if (!createdNew)
        {
            var existingWindow = FindExistingInstanceWindow();
            if (existingWindow != IntPtr.Zero)
            {
                if (AppCommands.TryParse(e.Args, out var command))
                {
                    AppCommands.Post(existingWindow, command);
                }
                else
                {
                    ActivateWindow(existingWindow);
                }
            }

            Shutdown();
            return;
        }

        // 主窗创建前定下数据目录；更改过并选了搬运时，先把旧数据复制过去
        AppPaths.RunPendingMove(CopyDataWithProgress);

        base.OnStartup(e);

        // 任务栏跳转列表：界面语言在主窗构造时才定下，启动完再设，之后切换语言时重设。
        // 程序没在运行时点菜单项只是打开程序，带的命令不执行
        _ = Dispatcher.InvokeAsync(() =>
        {
            ApplyJumpList();
            LocalizationService.LanguageChanged += (_, _) => ApplyJumpList();
        }, DispatcherPriority.ApplicationIdle);

        // 启动时清理旧日志与 stale .tmp 文件，延后到后台线程避免阻塞首帧；
        // 之后定时重复：本应用定位为随游戏常驻数周，只在启动清理会让日志
        // 目录在长运行会话中无界增长（每天最多 1 当前 + 3 滚动 × 5MB）。
        RunMaintenanceCleanup();
        _maintenanceTimer = new DispatcherTimer { Interval = MaintenanceInterval };
        _maintenanceTimer.Tick += (_, _) => RunMaintenanceCleanup();
        _maintenanceTimer.Start();
    }

    /// <summary>等旧实例退出（最多 30 秒）。旧进程退出时没释放互斥量会抛 AbandonedMutexException，此时互斥量已归本进程。</summary>
    private static bool WaitForPreviousInstance(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(30));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private void CopyDataWithProgress(string source, string target)
    {
        // 进度窗沿用旧位置里的主题与语言
        using (var settingsService = new SettingsService(Path.Combine(source, "settings.json")))
        {
            var settings = settingsService.Load();
            ThemeService.Apply(settings.ThemeMode);
            LocalizationService.Apply(settings.Language);
        }

        // 进度窗关闭时主窗还没创建：临时改为显式退出，免得「最后一个窗口关闭」把程序关掉
        var previousMode = ShutdownMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            GenshinBrowser.Windows.MigrationWindow.Run(source, target);
        }
        finally
        {
            ShutdownMode = previousMode;
        }
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
                JsonFileWriter.PurgeStaleTempFiles(AppPaths.DataRoot);
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

    /// <summary>
    /// 已在运行的实例的主窗句柄（同名进程的主窗口），没找到时为 IntPtr.Zero。
    /// F7 隐藏是最小化，主窗仍算可见；控制台、标题栏点击层都有 Owner，不会被当成主窗。
    /// </summary>
    private static IntPtr FindExistingInstanceWindow()
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
                    if (handle != IntPtr.Zero)
                    {
                        return handle;
                    }
                }
                catch
                {
                    // 进程已退出等：看下一个
                }
            }
        }

        return IntPtr.Zero;
    }

    private static void ActivateWindow(IntPtr handle)
    {
        if (IsIconic(handle))
        {
            ShowWindow(handle, SwRestore);
        }

        SetForegroundWindow(handle);
    }

    /// <summary>设置任务栏跳转列表（右键程序图标的菜单），失败只记日志。</summary>
    private void ApplyJumpList()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            var jumpList = AppCommands.CreateJumpList(path);
            jumpList.JumpItemsRejected += (_, args) =>
                FileLogger.LogDebug($"Jump list rejected {args.RejectedItems.Count} item(s): {string.Join(", ", args.RejectionReasons)}.");
            // 列表有内容、又不在 BeginInit / EndInit 之间时，SetJumpList 当场交给系统
            JumpList.SetJumpList(this, jumpList);
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Apply jump list");
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
