using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace GenshinBrowser.Services;

public sealed class KeyboardHookService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private volatile int _toggleModeVk = 0x77; // Default F8 (VK_F8)
    private volatile int _togglePlaybackVk = 0x4B; // Default K (VK_K)
    private volatile ModifierKeys _toggleModeModifiers = ModifierKeys.None;
    private volatile ModifierKeys _togglePlaybackModifiers = ModifierKeys.None;

    public int ToggleModeVk
    {
        get => _toggleModeVk;
        set => _toggleModeVk = value;
    }

    public int TogglePlaybackVk
    {
        get => _togglePlaybackVk;
        set => _togglePlaybackVk = value;
    }

    public ModifierKeys ToggleModeModifiers
    {
        get => _toggleModeModifiers;
        set => _toggleModeModifiers = value;
    }

    public ModifierKeys TogglePlaybackModifiers
    {
        get => _togglePlaybackModifiers;
        set => _togglePlaybackModifiers = value;
    }

    private readonly LowLevelKeyboardProc _proc;
    private readonly HashSet<int> _pressedKeys = new();
    private readonly object _keyStateLock = new();
    private IntPtr _hookId = IntPtr.Zero;
    private bool _isDisposed;

    public KeyboardHookService()
    {
        _proc = HookCallback;
    }

    public event EventHandler? KPressed;

    public event EventHandler? ModeTogglePressed;

    // 由 UI 层维护：浮窗模式下用户在游戏中，K 必须全局生效；
    // 浏览模式下仅在应用处于前台时生效，避免在 QQ/密码框等输入 k 误触发播放控制。
    private volatile bool _isGamingMode;
    private volatile bool _isAppActive = true;

    public bool IsGamingMode
    {
        get => _isGamingMode;
        set => _isGamingMode = value;
    }

    public bool IsAppActive
    {
        get => _isAppActive;
        set => _isAppActive = value;
    }

    public bool Start(out int errorCode)
    {
        errorCode = 0;

        if (_hookId != IntPtr.Zero)
        {
            return true;
        }

        _hookId = SetHook(_proc);
        if (_hookId != IntPtr.Zero)
        {
            return true;
        }

        errorCode = Marshal.GetLastWin32Error();
        return false;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        lock (_keyStateLock)
        {
            _pressedKeys.Clear();
        }

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        return SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(currentModule?.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vkCode = Marshal.ReadInt32(lParam);

            if (wParam == (IntPtr)WmKeyUp || wParam == (IntPtr)WmSysKeyUp)
            {
                lock (_keyStateLock)
                {
                    _pressedKeys.Remove(vkCode);
                }
            }
            else if (wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown)
            {
                var isFirstKeyDown = false;
                lock (_keyStateLock)
                {
                    isFirstKeyDown = _pressedKeys.Add(vkCode);
                }

                if (isFirstKeyDown && vkCode == _togglePlaybackVk && IsModifierPressed(_togglePlaybackModifiers))
                {
                    // 浮窗模式下且前台为任意游戏，或应用本身处于前台时才触发，避免影响其它软件输入
                    if (IsGameOrBrowserForeground())
                    {
                        Raise(KPressed);
                    }
                }
                else if (isFirstKeyDown && vkCode == _toggleModeVk && IsModifierPressed(_toggleModeModifiers))
                {
                    Raise(ModeTogglePressed);
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VkControl = 0x11;
    private const int VkMenu = 0x12; // Alt
    private const int VkShift = 0x10;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;

    private static bool IsModifierPressed(ModifierKeys modifiers)
    {
        var controlPressed = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
        var altPressed = (GetAsyncKeyState(VkMenu) & 0x8000) != 0;
        var shiftPressed = (GetAsyncKeyState(VkShift) & 0x8000) != 0;
        var winPressed = (GetAsyncKeyState(VkLwin) & 0x8000) != 0 || (GetAsyncKeyState(VkRwin) & 0x8000) != 0;

        var expectedControl = modifiers.HasFlag(ModifierKeys.Control);
        var expectedAlt = modifiers.HasFlag(ModifierKeys.Alt);
        var expectedShift = modifiers.HasFlag(ModifierKeys.Shift);
        var expectedWin = modifiers.HasFlag(ModifierKeys.Windows);

        return controlPressed == expectedControl &&
               altPressed == expectedAlt &&
               shiftPressed == expectedShift &&
               winPressed == expectedWin;
    }

    private static readonly HashSet<string> NonGameProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "taskmgr", "cmd", "powershell", "wt", "bash",
        "chrome", "firefox", "msedge", "opera", "brave", "iexplore", "safari", "360se", "sogouexplorer",
        "qq", "tim", "wechat", "discord", "feishu", "dingtalk", "slack", "teams", "telegram", "whatsapp", "line",
        "notepad", "notepad++", "code", "devenv", "rider", "sublime_text",
        "wps", "winword", "excel", "powerpnt"
    };

    private bool IsGameOrBrowserForeground()
    {
        var foregroundHWnd = GetForegroundWindow();
        if (foregroundHWnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foregroundHWnd, out uint pid);
        var currentPid = (uint)Process.GetCurrentProcess().Id;

        // 1. 如果前台窗口是我们自己的进程，允许触发
        if (pid == currentPid)
        {
            return true;
        }

        // 2. 如果处于浮窗模式，且前台窗口是非排他性进程（即可能是任意游戏），允许触发
        if (_isGamingMode)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                var processName = process.ProcessName;

                // 排除已知的非游戏常用软件（如浏览器、聊天软件、文本编辑器等）
                if (!NonGameProcessNames.Contains(processName))
                {
                    return true;
                }
            }
            catch
            {
                // 如果无法获取进程信息（通常是高权限进程，比如管理员身份运行的游戏），
                // 且当前正处于置顶浮窗模式下，我们默认将其视为游戏，允许触发。
                // 这样即使用户以管理员运行了其他游戏，热键也能正常工作。
                return true;
            }
        }

        return false;
    }

    private void Raise(EventHandler? handler)
    {
        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // The low-level hook must never be broken by a subscriber exception.
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
