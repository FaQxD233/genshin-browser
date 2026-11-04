using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace GenshinBrowser.Services;

/// <summary>
/// 以桌面用户的普通权限打开文件或网址。本程序以管理员身份运行（app.manifest），直接 ShellExecute 打开的程序同样带管理员权限：
/// 已提权时改由桌面资源管理器（explorer.exe，普通权限）执行打开，做法同微软 Windows SDK 示例 ExecInExplorer。
/// </summary>
internal static class ShellLauncher
{
    private const int SwShowNormal = 1;
    private const int SwcDesktop = 0x8;
    private const int SwfoNeedDispatch = 0x1;
    private const uint SvgioBackground = 0;
    private const int ClsctxLocalServer = 0x4;
    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidSTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidIDispatch = new("00020400-0000-0000-C000-000000000046");

    /// <summary>本进程是否以管理员身份运行（令牌里 Administrators 组生效，即已提权）。</summary>
    public static bool IsElevated { get; } = DetectElevation();

    /// <summary>
    /// 用默认程序打开文件或网址（mailto:、bilibili:// 等）。已提权时经资源管理器以普通权限打开；
    /// 资源管理器不在运行或调用失败时返回 false，不退回提权打开。
    /// </summary>
    public static bool TryOpen(string target, string? workingDirectory = null)
    {
        try
        {
            if (IsElevated)
            {
                ShellExecuteInExplorer(target, arguments: null, workingDirectory, SwShowNormal);
            }
            else
            {
                using var process = Process.Start(new ProcessStartInfo(target)
                {
                    UseShellExecute = true,
                    WorkingDirectory = workingDirectory ?? string.Empty,
                });
            }

            return true;
        }
        catch (Exception ex)
        {
            // COM / RPC 失败、资源管理器不在运行（自定义外壳）、没有关联程序等
            FileLogger.LogException(ex, "Open as desktop user");
            return false;
        }
    }

    /// <summary>
    /// 在桌面资源管理器的进程里执行 Shell.Application.ShellExecute：被打开的程序由 explorer.exe 拉起，带它的普通权限。
    /// 失败时抛异常（COMException、InvalidCastException 等）。
    /// </summary>
    internal static void ShellExecuteInExplorer(string file, string? arguments, string? workingDirectory, int showCommand)
    {
        var comObjects = new List<object>();
        try
        {
            // 必须连到资源管理器进程里的实例（CLSCTX_LOCAL_SERVER），不能在本进程里新建
            var clsidShellWindows = ClsidShellWindows;
            var iidShellWindows = typeof(IShellWindows).GUID;
            var shellWindows = (IShellWindows)CoCreateInstance(ref clsidShellWindows, IntPtr.Zero, ClsctxLocalServer, ref iidShellWindows);
            comObjects.Add(shellWindows);

            object empty = null!; // VT_EMPTY
            var desktop = shellWindows.FindWindowSW(ref empty, ref empty, SwcDesktop, out _, SwfoNeedDispatch)
                          ?? throw new InvalidOperationException("The desktop shell window was not found.");
            comObjects.Add(desktop);

            var sid = SidSTopLevelBrowser;
            var iidShellBrowser = typeof(IShellBrowser).GUID;
            ((IComServiceProvider)desktop).QueryService(ref sid, ref iidShellBrowser, out var browserObject);
            comObjects.Add(browserObject);

            var view = ((IShellBrowser)browserObject).QueryActiveShellView();
            comObjects.Add(view);

            var iidDispatch = IidIDispatch;
            var folderView = view.GetItemObject(SvgioBackground, ref iidDispatch);
            comObjects.Add(folderView);

            // IShellFolderViewDual.Application → IShellDispatch2（只有 IDispatch，按名字晚绑定调用）
            dynamic shell = ((dynamic)folderView).Application;
            comObjects.Add(shell);
            shell.ShellExecute(file, arguments ?? string.Empty, workingDirectory ?? string.Empty, string.Empty, showCommand);
        }
        finally
        {
            foreach (var comObject in comObjects)
            {
                if (Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
        }
    }

    private static bool DetectElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // 读不到令牌时按已提权处理：宁可多绕一道资源管理器，也不提权打开
            return true;
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    private static extern object CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext, ref Guid riid);

    // 下面几个接口只声明用到的方法；之前的方法按虚表顺序占位，不会被调用。

    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        void VtblCount();

        void VtblItem();

        void VtblNewEnum();

        void VtblRegister();

        void VtblRegisterPending();

        void VtblRevoke();

        void VtblOnNavigate();

        void VtblOnActivated();

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW(ref object pvarLoc, ref object pvarLocRoot, int swClass, out int phwnd, int swfwOptions);
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        void QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void VtblGetWindow();

        void VtblContextSensitiveHelp();

        void VtblInsertMenusSB();

        void VtblSetMenuSB();

        void VtblRemoveMenusSB();

        void VtblSetStatusTextSB();

        void VtblEnableModelessSB();

        void VtblTranslateAcceleratorSB();

        void VtblBrowseObject();

        void VtblGetViewStateStream();

        void VtblGetControlWindow();

        void VtblSendControlMsg();

        IShellView QueryActiveShellView();
    }

    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void VtblGetWindow();

        void VtblContextSensitiveHelp();

        void VtblTranslateAccelerator();

        void VtblEnableModeless();

        void VtblUIActivate();

        void VtblRefresh();

        void VtblCreateViewWindow();

        void VtblDestroyViewWindow();

        void VtblGetCurrentInfo();

        void VtblAddPropertySheetPages();

        void VtblSaveViewState();

        void VtblSelectItem();

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetItemObject(uint uItem, ref Guid riid);
    }
}
