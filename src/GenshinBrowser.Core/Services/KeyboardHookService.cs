using System.Diagnostics;
using System.Runtime.InteropServices;
#if WINUI_CORE
using ModifierKeys = GenshinBrowser.Models.HotkeyModifiers;
#else
using System.Windows.Input;
#endif

namespace GenshinBrowser.Services;

public sealed class KeyboardHookService : IDisposable
{
    private const string ToggleModeRegistrationId = "toggle-mode";
    private const string TogglePlaybackRegistrationId = "toggle-playback";
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    /// <summary>Start() 在已 Dispose 时返回的伪错误码，便于 UI 区分。</summary>
    internal const int ObjectDisposedErrorCode = unchecked((int)0x80000013);

    private volatile int _toggleModeVk = 0x77; // Default F8 (VK_F8)
    private volatile int _togglePlaybackVk = 0x4B; // Default K (VK_K)
    private volatile ModifierKeys _toggleModeModifiers = ModifierKeys.None;
    private volatile ModifierKeys _togglePlaybackModifiers = ModifierKeys.None;

    public int ToggleModeVk
    {
        get => _toggleModeVk;
        set => TrySetToggleModeHotkey(value, _toggleModeModifiers);
    }

    public int TogglePlaybackVk
    {
        get => _togglePlaybackVk;
        set => TrySetTogglePlaybackHotkey(value, _togglePlaybackModifiers);
    }

    public ModifierKeys ToggleModeModifiers
    {
        get => _toggleModeModifiers;
        set => TrySetToggleModeHotkey(_toggleModeVk, value);
    }

    public ModifierKeys TogglePlaybackModifiers
    {
        get => _togglePlaybackModifiers;
        set => TrySetTogglePlaybackHotkey(_togglePlaybackVk, value);
    }

    /// <summary>
    /// 原子更新模式热键 (VK + 修饰键)。与播放热键最终组合冲突时返回 false，不改任何状态。
    /// </summary>
    public bool TrySetToggleModeHotkey(int virtualKey, ModifierKeys modifiers)
    {
        if (virtualKey is <= 0 or > 0xFF)
        {
            return false;
        }

        lock (_registrationLock)
        {
            if (_isDisposed)
            {
                return false;
            }

            if (virtualKey == _toggleModeVk && modifiers == _toggleModeModifiers)
            {
                return true;
            }

            if (HasConflictingRegistrationLocked(ToggleModeRegistrationId, virtualKey, modifiers))
            {
                return false;
            }

            _toggleModeVk = virtualKey;
            _toggleModeModifiers = modifiers;
            _registrations[ToggleModeRegistrationId] = CreateBuiltInRegistration(ToggleModeRegistrationId);
            PublishSnapshotLocked();
            return true;
        }
    }

    /// <summary>
    /// 原子更新播放热键 (VK + 修饰键)。与模式热键最终组合冲突时返回 false，不改任何状态。
    /// </summary>
    public bool TrySetTogglePlaybackHotkey(int virtualKey, ModifierKeys modifiers)
    {
        if (virtualKey is <= 0 or > 0xFF)
        {
            return false;
        }

        lock (_registrationLock)
        {
            if (_isDisposed)
            {
                return false;
            }

            if (virtualKey == _togglePlaybackVk && modifiers == _togglePlaybackModifiers)
            {
                return true;
            }

            if (HasConflictingRegistrationLocked(TogglePlaybackRegistrationId, virtualKey, modifiers))
            {
                return false;
            }

            _togglePlaybackVk = virtualKey;
            _togglePlaybackModifiers = modifiers;
            _registrations[TogglePlaybackRegistrationId] = CreateBuiltInRegistration(TogglePlaybackRegistrationId);
            PublishSnapshotLocked();
            return true;
        }
    }

    /// <summary>
    /// 为 true 时内置模式/播放热键不触发（录制快捷键期间使用），不影响已注册的其它热键。
    /// </summary>
    public bool SuspendBuiltInHotkeys
    {
        get => _suspendBuiltInHotkeys;
        set => _suspendBuiltInHotkeys = value;
    }

    private readonly LowLevelKeyboardProc _proc;
    private readonly object _registrationLock = new();
    private readonly Dictionary<string, HotkeyRegistration> _registrations = new(StringComparer.Ordinal);
    private HotkeySnapshot _hotkeySnapshot = HotkeySnapshot.Empty;
    private readonly HashSet<int> _pressedKeys = new();
    private readonly object _keyStateLock = new();
    private IntPtr _hookId = IntPtr.Zero;
    private bool _isDisposed;
    private volatile bool _suspendBuiltInHotkeys;

    public KeyboardHookService()
    {
        _proc = HookCallback;
        lock (_registrationLock)
        {
            _registrations[ToggleModeRegistrationId] = CreateBuiltInRegistration(ToggleModeRegistrationId);
            _registrations[TogglePlaybackRegistrationId] = CreateBuiltInRegistration(TogglePlaybackRegistrationId);
            PublishSnapshotLocked();
        }
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

        if (_isDisposed)
        {
            errorCode = ObjectDisposedErrorCode;
            return false;
        }

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

    /// <summary>
    /// 注册或更新一个全局快捷键。钩子热路径读取不可变快照，因此新增快捷键不需要修改回调分支。
    /// 与已有其它 id 的 (VK + 修饰键) 冲突时抛出 <see cref="InvalidOperationException"/>，拒绝双注册。
    /// </summary>
    public void RegisterOrUpdateHotkey(
        string id,
        int virtualKey,
        ModifierKeys modifiers,
        Action callback,
        Func<bool>? canExecute = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(callback);
        if (virtualKey is <= 0 or > 0xFF)
        {
            throw new ArgumentOutOfRangeException(nameof(virtualKey));
        }

        lock (_registrationLock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (HasConflictingRegistrationLocked(id, virtualKey, modifiers))
            {
                throw new InvalidOperationException(
                    $"Hotkey conflict: VK 0x{virtualKey:X2} with modifiers {modifiers} is already registered.");
            }

            _registrations[id] = new HotkeyRegistration(virtualKey, modifiers, callback, canExecute);
            PublishSnapshotLocked();
        }
    }

    public bool UnregisterHotkey(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_registrationLock)
        {
            if (!_registrations.Remove(id))
            {
                return false;
            }

            PublishSnapshotLocked();
            return true;
        }
    }

    internal int GetRegistrationCountForVirtualKey(int virtualKey)
    {
        var snapshot = Volatile.Read(ref _hotkeySnapshot);
        return snapshot.ByVirtualKey.TryGetValue(virtualKey, out var registrations)
            ? registrations.Length
            : 0;
    }

    public void Dispose()
    {
        lock (_registrationLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _registrations.Clear();
            Volatile.Write(ref _hotkeySnapshot, HotkeySnapshot.Empty);
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
            var snapshot = Volatile.Read(ref _hotkeySnapshot);
            if (!snapshot.ByVirtualKey.TryGetValue(vkCode, out var registrations))
            {
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

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

                if (isFirstKeyDown)
                {
                    foreach (var registration in registrations)
                    {
                        try
                        {
                            if (IsModifierPressed(registration.Modifiers) &&
                                (registration.CanExecute?.Invoke() ?? true))
                            {
                                registration.Callback();
                            }
                        }
                        catch
                        {
                            // Global hook callbacks must return quickly and never propagate.
                        }
                    }
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
        uint currentPid = (uint)Environment.ProcessId;

        // 1. 如果前台窗口是我们自己的进程，允许触发
        if (pid == currentPid)
        {
            return _isAppActive;
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

    private bool HasConflictingRegistrationLocked(string selfId, int virtualKey, ModifierKeys modifiers)
    {
        foreach (var pair in _registrations)
        {
            if (string.Equals(pair.Key, selfId, StringComparison.Ordinal))
            {
                continue;
            }

            if (pair.Value.VirtualKey == virtualKey && pair.Value.Modifiers == modifiers)
            {
                return true;
            }
        }

        return false;
    }

    private HotkeyRegistration CreateBuiltInRegistration(string id)
    {
        return id switch
        {
            ToggleModeRegistrationId => new HotkeyRegistration(
                _toggleModeVk,
                _toggleModeModifiers,
                () => Raise(ModeTogglePressed),
                CanExecuteBuiltInHotkey),
            TogglePlaybackRegistrationId => new HotkeyRegistration(
                _togglePlaybackVk,
                _togglePlaybackModifiers,
                () => Raise(KPressed),
                CanExecutePlaybackHotkey),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }

    private bool CanExecuteBuiltInHotkey() => !_suspendBuiltInHotkeys;

    private bool CanExecutePlaybackHotkey() => !_suspendBuiltInHotkeys && IsGameOrBrowserForeground();

    private void PublishSnapshotLocked()
    {
        // 同 VK 可挂多个不同修饰键；同 (VK, modifiers) 只保留一条，防止双触发。
        var byVirtualKey = new Dictionary<int, HotkeyRegistration[]>();
        foreach (var group in _registrations.Values.GroupBy(registration => registration.VirtualKey))
        {
            var deduped = new List<HotkeyRegistration>();
            foreach (var registration in group)
            {
                if (deduped.Exists(existing => existing.Modifiers == registration.Modifiers))
                {
                    continue;
                }

                deduped.Add(registration);
            }

            byVirtualKey[group.Key] = deduped.ToArray();
        }

        Volatile.Write(ref _hotkeySnapshot, new HotkeySnapshot(byVirtualKey));

        lock (_keyStateLock)
        {
            _pressedKeys.Clear();
        }
    }

    private sealed record HotkeyRegistration(
        int VirtualKey,
        ModifierKeys Modifiers,
        Action Callback,
        Func<bool>? CanExecute);

    private sealed class HotkeySnapshot
    {
        public static readonly HotkeySnapshot Empty = new(new Dictionary<int, HotkeyRegistration[]>());

        public HotkeySnapshot(IReadOnlyDictionary<int, HotkeyRegistration[]> byVirtualKey)
        {
            ByVirtualKey = byVirtualKey;
        }

        public IReadOnlyDictionary<int, HotkeyRegistration[]> ByVirtualKey { get; }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
