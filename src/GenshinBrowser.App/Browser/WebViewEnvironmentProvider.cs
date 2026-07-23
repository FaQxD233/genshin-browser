using GenshinBrowser.Localization;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GenshinBrowser.Browser;

internal sealed class WebViewEnvironmentProvider
{
    private readonly string userDataFolder;
    private readonly ITextResourceService text;
    private readonly Lazy<Task<CoreWebView2Environment>> environmentTask;

    public WebViewEnvironmentProvider(string userDataFolder, ITextResourceService text)
    {
        this.userDataFolder = userDataFolder;
        this.text = text;
        environmentTask = new Lazy<Task<CoreWebView2Environment>>(
            CreateEnvironmentAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string UserDataFolder => userDataFolder;

    public Task<CoreWebView2Environment> GetAsync() => environmentTask.Value;

    private async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        // The WinUI WebView2 control and its environment belong to the owning UI apartment.
        // Runtime installation can perform background work internally, but environment creation
        // must resume on the caller's WinUI thread.
        await EnsureRuntimeAsync().ConfigureAwait(true);
        Directory.CreateDirectory(userDataFolder);
        CoreWebView2EnvironmentOptions options = new()
        {
            AdditionalBrowserArguments = "--do-not-de-elevate --autoplay-policy=no-user-gesture-required",
        };
        return await CoreWebView2Environment.CreateWithOptionsAsync(
            null,
            userDataFolder,
            options);
    }

    private async Task EnsureRuntimeAsync()
    {
        if (IsRuntimeInstalled())
        {
            return;
        }

        string bootstrapper = Path.Combine(AppContext.BaseDirectory, "MicrosoftEdgeWebview2Setup.exe");
        if (!File.Exists(bootstrapper))
        {
            string manualHint = text.Get(
                "Status.WebView2ManualHint",
                "请手动下载安装：https://developer.microsoft.com/microsoft-edge/webview2/");
            throw new InvalidOperationException(text.Format(
                "Status.WebView2MissingInstaller",
                "未检测到 WebView2 Runtime（浏览器核心组件），且未找到自动安装程序。\n\n{0}",
                manualHint));
        }

        ProcessStartInfo startInfo = new(bootstrapper)
        {
            UseShellExecute = true,
            Arguments = "/silent /install",
        };
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(text.Get(
                "Status.WebView2InstallerStartFailed",
                "无法启动 WebView2 Runtime 安装程序。"));
        await process.WaitForExitAsync().ConfigureAwait(true);
        if (process.ExitCode != 0 || !IsRuntimeInstalled())
        {
            string message = text.Get(
                "Status.WebView2InstallFailed",
                "WebView2 Runtime 自动安装失败，请手动安装。");
            throw new InvalidOperationException($"{message} ({process.ExitCode})");
        }
    }

    internal static bool IsRuntimeInstalled()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (COMException)
        {
            return false;
        }
    }
}
