using GenshinBrowser.Hosting;
using GenshinBrowser.Interop;
using GenshinBrowser.Localization;
using GenshinBrowser.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace GenshinBrowser;

public sealed partial class App : Application
{
    private const string SingleInstanceKey = "GenshinBrowser.Main";
    private readonly ApplicationHost applicationHost;
    private AppInstance? currentInstance;
    private DispatcherQueue? dispatcherQueue;
    private bool exitRequested;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_OnUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_OnUnobservedTaskException;
        applicationHost = new ApplicationHost(ExitApplication);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            dispatcherQueue = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("WinUI dispatcher queue is unavailable during launch.");
            AppActivationArguments activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
            AppInstance registeredInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
            if (!registeredInstance.IsCurrent)
            {
                await registeredInstance.RedirectActivationToAsync(activationArguments);
                ExitApplication();
                return;
            }

            currentInstance = registeredInstance;
            currentInstance.Activated += CurrentInstance_OnActivated;
            applicationHost.Start();
        }
        catch (Exception ex)
        {
            FileLogger.LogException(ex, "Start WinUI application host");
            ITextResourceService? text = LocalizationBinding.Provider;
            Win32MessageBox.ShowError(
                text?.Format(
                    "Status.StartupFailed",
                    "应用启动失败：\n{0}: {1}",
                    ex.GetType().Name,
                    ex.Message) ?? $"应用启动失败：\n{ex.GetType().Name}: {ex.Message}",
                text?.Get("Status.ErrorTitle", "Genshin Browser 错误") ?? "Genshin Browser 错误");
            ExitApplication();
        }
    }

    private void CurrentInstance_OnActivated(object? sender, AppActivationArguments args)
    {
        dispatcherQueue?.TryEnqueue(applicationHost.Activate);
    }

    private void ExitApplication()
    {
        if (exitRequested)
        {
            return;
        }

        exitRequested = true;
        if (currentInstance is not null)
        {
            currentInstance.Activated -= CurrentInstance_OnActivated;
            currentInstance = null;
        }
        Exit();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        FileLogger.LogException(args.Exception, "Unhandled WinUI exception");
        ITextResourceService? text = LocalizationBinding.Provider;
        Win32MessageBox.ShowError(
            text?.Format(
                "Status.UnhandledException",
                "发生未处理异常：\n{0}: {1}\n\n详情已写入日志。",
                args.Exception.GetType().Name,
                args.Exception.Message) ??
            $"发生未处理异常：\n{args.Exception.GetType().Name}: {args.Exception.Message}\n\n详情已写入日志。",
            text?.Get("Status.ErrorTitle", "Genshin Browser 错误") ?? "Genshin Browser 错误");
        args.Handled = true;
    }

    private static void CurrentDomain_OnUnhandledException(object sender, System.UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            FileLogger.LogException(exception, "Unhandled application-domain exception");
        }
    }

    private static void TaskScheduler_OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        FileLogger.LogException(args.Exception, "Unobserved task exception");
        args.SetObserved();
    }
}
