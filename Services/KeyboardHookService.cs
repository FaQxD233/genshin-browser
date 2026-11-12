using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using GenshinBrowser.Models;

namespace GenshinBrowser.Services;

public sealed class KeyboardHookService : IDisposable
{
    private const string BuiltInRegistrationPrefix = "builtin:";
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    // KBDLLHOOKSTRUCT.flags（偏移 8）：注入事件标志。
    private const int LlkhfInjected = 0x10;
    private const int LlkhfLowerIlInjected = 0x02;
    /// <summary>Start() 在已 Dispose 时返回的伪错误码，便于 UI 区分。</summary>
    internal const int ObjectDisposedErrorCode = unchecked((int)0x80000013);
    /// <summary>Start() 在非 UI 线程调用时返回的伪错误码（LL 钩子须经安装线程的消息泵派发）。</summary>
    internal const int WrongThreadErrorCode = unchecked((int)0x80000014);

    /// <summary>
    /// 热键主键的合法 VK：非零、单字节，不是修饰键，也不是 VK_PROCESSKEY。
    /// 修饰键作主键是永不触发的死键：其 KeyDown 时修饰状态必然为按下，
    /// 与任何修饰期望都矛盾（VK_CONTROL 泛型状态覆盖 LCtrl/RCtrl）。
    /// VK_PROCESSKEY 是输入法处理按键后的占位键，低级钩子收到的是真实键码，同样永不触发。
    /// </summary>
    private static bool IsValidHotkeyVirtualKey(int virtualKey) =>
        virtualKey is > 0 and <= 0xFF and not VkProcessKey && !IsModifierVirtualKey(virtualKey);

    private const int VkProcessKey = 0xE5;

    private static bool IsModifierVirtualKey(int virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    // 内置热键（VK + 修饰键），下标为 HotkeySlot；只在 _registrationLock 内读写。
    // 钩子回调不读它，只读发布出去的不可变快照 _hotkeySnapshot。
    private readonly (int VirtualKey, ModifierKeys Modifiers)[] _builtIn = CreateDefaultBuiltIn();

    // 默认值取自 AppSettings（F8 / K / F7 / ; / '），两边不再各写一份
    private static (int VirtualKey, ModifierKeys Modifiers)[] CreateDefaultBuiltIn()
    {
        var defaults = new AppSettings();
        return HotkeySlots.All
            .Select(slot => defaults.GetHotkey(slot))
            .Select(binding => (KeyInterop.VirtualKeyFromKey(binding.Key), binding.Modifiers))
            .ToArray();
    }

    /// <summary>某组内置热键当前的 VK 与修饰键。</summary>
    public (int VirtualKey, ModifierKeys Modifiers) GetHotkey(HotkeySlot slot)
    {
        lock (_registrationLock)
        {
            return _builtIn[(int)slot];
        }
    }

    /// <summary>
    /// 原子更新一组内置热键 (VK + 修饰键)。主键不合法、与任一其它注册（内置或自定义）冲突、或已释放时返回 false，不改任何状态。
    /// </summary>
    public bool TrySetHotkey(HotkeySlot slot, int virtualKey, ModifierKeys modifiers)
    {
        if (!IsValidHotkeyVirtualKey(virtualKey))
        {
            return false;
        }

        lock (_registrationLock)
        {
            if (_isDisposed)
            {
                return false;
            }

            if (_builtIn[(int)slot] == (virtualKey, modifiers))
            {
                return true;
            }

            var id = RegistrationId(slot);
            if (HasConflictingRegistrationLocked(id, virtualKey, modifiers))
            {
                return false;
            }

            _builtIn[(int)slot] = (virtualKey, modifiers);
            _registrations[id] = CreateBuiltInRegistration(slot);
            PublishSnapshotLocked();
            return true;
        }
    }

    /// <summary>
    /// 批量原子应用全部内置热键（每个槽位都要给）。只校验「目标组合彼此互异」与「不与非内置自定义注册冲突」，
    /// 不与替换前的旧内置注册比较——用于启动时从配置整体恢复：旧注册仍是默认组合，
    /// 逐个 TrySet 会被交叉占用配置（如 Mode=K、Playback=F8）整批拒绝。
    /// 任一校验失败返回 false 且不改动任何状态。
    /// </summary>
    public bool TrySetBuiltInHotkeys(IReadOnlyDictionary<HotkeySlot, (int VirtualKey, ModifierKeys Modifiers)> bindings)
    {
        var pairs = new (int VirtualKey, ModifierKeys Modifiers)[HotkeySlots.All.Count];
        foreach (var slot in HotkeySlots.All)
        {
            if (!bindings.TryGetValue(slot, out var pair) || !IsValidHotkeyVirtualKey(pair.VirtualKey))
            {
                return false;
            }

            pairs[(int)slot] = pair;
        }

        if (pairs.Distinct().Count() != pairs.Length)
        {
            return false;
        }

        lock (_registrationLock)
        {
            if (_isDisposed)
            {
                return false;
            }

            foreach (var pair in _registrations)
            {
                if (!IsBuiltInRegistrationId(pair.Key) &&
                    pairs.Contains((pair.Value.VirtualKey, pair.Value.Modifiers)))
                {
                    return false;
                }
            }

            pairs.CopyTo(_builtIn, 0);
            RebuildBuiltInRegistrationsLocked();
            PublishSnapshotLocked();
            return true;
        }
    }

    private static string RegistrationId(HotkeySlot slot) => BuiltInRegistrationPrefix + slot;

    private static bool IsBuiltInRegistrationId(string id) =>
        id.StartsWith(BuiltInRegistrationPrefix, StringComparison.Ordinal);

    private void RebuildBuiltInRegistrationsLocked()
    {
        foreach (var slot in HotkeySlots.All)
        {
            _registrations[RegistrationId(slot)] = CreateBuiltInRegistration(slot);
        }
    }

    /// <summary>
    /// 为 true 时内置热键不触发（录制快捷键期间使用），不影响已注册的其它热键。
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
            RebuildBuiltInRegistrationsLocked();
            PublishSnapshotLocked();
        }
    }

    /// <summary>内置热键按下（在钩子安装线程上触发，订阅方须尽快返回）。</summary>
    public event EventHandler<HotkeySlot>? HotkeyPressed;

    // 由 UI 层维护：浮窗模式下用户在游戏中，K 必须全局生效；
    // 浏览模式下仅在应用处于前台时生效，避免在 QQ/密码框等输入 k 误触发播放控制。
    private volatile bool _isGamingMode;
    private volatile bool _isAppActive = true;
    // 热键生效范围（黑名单外 / 全部应用 / 关闭），由 MainWindow 同步自设置
    private volatile HotkeyScope _hotkeyScope = HotkeyScope.Blacklist;
    // 浮窗被热键隐藏时置 true：此时隐藏键是唯一恢复入口，不受范围/前台限制
    private volatile bool _alwaysAllowHideToggle;
    // 「远程按键」：为 true 时注入的按键也触发热键，由 MainWindow 同步自设置
    private volatile bool _acceptInjectedInput;
    // 开关关着时注入的按键命中热键：每次运行只记一条日志（只在钩子线程读写），用来确认远程软件发的是不是注入的键
    private bool _hasLoggedIgnoredInjectedKey;

    /// <summary>
    /// 为 true 时注入的按键（SendInput / keybd_event 合成：远程控制软件、按键映射工具）也触发热键；
    /// 默认 false 只认键盘上实际按下的键。
    /// </summary>
    public bool AcceptInjectedInput
    {
        get => _acceptInjectedInput;
        set => _acceptInjectedInput = value;
    }

    /// <summary>全局热键生效范围。改变后立即对全部内置热键生效。</summary>
    public HotkeyScope HotkeyScope
    {
        get => _hotkeyScope;
        set => _hotkeyScope = value;
    }

    /// <summary>
    /// 为 true 时隐藏浮窗键无视 HotkeyScope 与前台限制（浮窗已隐藏时它是唯一恢复手段）。
    /// </summary>
    public bool AlwaysAllowHideToggle
    {
        get => _alwaysAllowHideToggle;
        set => _alwaysAllowHideToggle = value;
    }

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
        // WH_KEYBOARD_LL 回调经由安装线程的消息循环派发，前台缓存等无锁字段也假设
        // 全部访问发生在安装线程：只允许在 WPF UI 线程安装。Application.Current 为 null
        // （单测环境无 WPF Application）时跳过该检查以保持可测性。
        if (System.Windows.Application.Current is { } app && !app.Dispatcher.CheckAccess())
        {
            errorCode = WrongThreadErrorCode;
            return false;
        }

        // _hookId 的检查与安装必须在 _registrationLock 内：与 Dispose 的卸钩互斥，
        // 否则并发交错时可能在 Dispose 之后装上永不卸载的钩子。
        lock (_registrationLock)
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
                StartHookHealthMonitor();
                return true;
            }

            errorCode = Marshal.GetLastWin32Error();
            return false;
        }
    }

    // —— 钩子健康监测 ——
    // WH_KEYBOARD_LL 回调超时（LowLevelHooksTimeout，默认约 300ms）后 Windows 会静默摘除钩子，
    // 全局热键随之失效且无任何报错。这里周期性比对「系统整体输入活跃」与「钩子上次收到事件」：
    // 系统持续有输入而钩子长时间静默时判定失联，自动重装。纯鼠标用户会被误判，
    // 但对正常钩子做一次 Unhook+SetHook 无副作用（冷却限频，重装日志走 DEBUG 级）。
    private static readonly TimeSpan HookHealthCheckInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan HookSilenceThreshold = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan HookReinstallCooldown = TimeSpan.FromMinutes(5);
    private const uint SystemInputActiveWindowMs = 5_000;

    private DispatcherTimer? _hookHealthTimer;
    private DateTime _lastHookEventUtc;
    private DateTime _lastHookReinstallUtc;

    private void StartHookHealthMonitor()
    {
        _hookHealthTimer ??= new DispatcherTimer { Interval = HookHealthCheckInterval };
        _hookHealthTimer.Tick -= HookHealthTimer_OnTick;
        _hookHealthTimer.Tick += HookHealthTimer_OnTick;
        _hookHealthTimer.Start();
    }

    private void StopHookHealthMonitor()
    {
        if (_hookHealthTimer is null)
        {
            return;
        }

        try
        {
            _hookHealthTimer.Stop();
            _hookHealthTimer.Tick -= HookHealthTimer_OnTick;
        }
        catch
        {
            // Dispose 可能来自非常规线程：DispatcherTimer 跨线程访问会抛，忽略即可。
        }

        _hookHealthTimer = null;
    }

    private void HookHealthTimer_OnTick(object? sender, EventArgs e)
    {
        if (_isDisposed || _hookId == IntPtr.Zero)
        {
            StopHookHealthMonitor();
            return;
        }

        var info = new LastInputInfo
        {
            cbSize = (uint)Marshal.SizeOf<LastInputInfo>(),
        };
        if (!GetLastInputInfo(ref info))
        {
            return;
        }

        // 系统整体无输入（键盘+鼠标皆静）时无法区分「钩子失联」与「用户没按键」，跳过。
        var systemIdleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        if (systemIdleMs > SystemInputActiveWindowMs)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        // 与 HookCallback 同线程（UI 消息泵派发），普通读即可
        if (nowUtc - _lastHookEventUtc < HookSilenceThreshold ||
            nowUtc - _lastHookReinstallUtc < HookReinstallCooldown)
        {
            return;
        }

        IntPtr newHookId;
        lock (_registrationLock)
        {
            if (_isDisposed || _hookId == IntPtr.Zero)
            {
                return;
            }

            UnhookWindowsHookEx(_hookId);
            newHookId = _hookId = SetHook(_proc);
        }

        _lastHookReinstallUtc = nowUtc;
        if (newHookId == IntPtr.Zero)
        {
            // 重装失败：停止监测，热键保持失效直至进程重启；记录错误码便于排查。
            FileLogger.LogDebug($"Keyboard hook reinstall after silent removal failed: {Marshal.GetLastWin32Error()}");
            StopHookHealthMonitor();
        }
        else
        {
            FileLogger.LogDebug("Keyboard hook reinstalled after silent removal.");
        }
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
        if (!IsValidHotkeyVirtualKey(virtualKey))
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

            // 卸钩也在锁内：HookCallback 不获取 _registrationLock（只读快照 + _keyStateLock），
            // 持锁调用 UnhookWindowsHookEx 无死锁风险，且与 Start 的安装严格互斥。
            if (_hookId != IntPtr.Zero)
            {
                if (!UnhookWindowsHookEx(_hookId))
                {
                    // 卸载失败（极少见）仅记录：句柄随进程退出回收，继续置零避免二次卸载。
                    FileLogger.LogDebug($"UnhookWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
                }

                _hookId = IntPtr.Zero;
            }
        }

        StopHookHealthMonitor();

        lock (_keyStateLock)
        {
            _pressedKeys.Clear();
        }

        GC.SuppressFinalize(this);
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        // WH_KEYBOARD_LL 是全局钩子，hMod 仅作来源标识，用本进程 exe 模块句柄即可；
        // 不要走 Process.MainModule：单文件发布等场景下会抛 Win32Exception。
        return SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(null), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // 心跳先于注入过滤：注入事件同样证明钩子仍挂在链上，供健康监测判断。
            // 回调经安装线程（UI）的消息泵派发，与 _hookHealthTimer 同线程，无需原子读。
            _lastHookEventUtc = DateTime.UtcNow;

            var vkCode = Marshal.ReadInt32(lParam);
            var snapshot = Volatile.Read(ref _hotkeySnapshot);
            if (!snapshot.ByVirtualKey.TryGetValue(vkCode, out var registrations))
            {
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // 注入事件（SendInput/keybd_event 合成）默认不触发热键，只认实际按下的键：密码管理器自动输入、
            // 输入法合成的同名键不该触发。远程控制软件（UU 远程、Parsec 等）和按键映射工具发的键也是注入的，
            // 用户打开「远程按键」（AcceptInjectedInput）后照常处理。
            var flags = Marshal.ReadInt32(lParam, 8);
            if ((flags & (LlkhfInjected | LlkhfLowerIlInjected)) != 0 && !_acceptInjectedInput)
            {
                if (!_hasLoggedIgnoredInjectedKey && (wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown))
                {
                    _hasLoggedIgnoredInjectedKey = true;
                    FileLogger.LogDebug(
                        $"Ignored injected hotkey key VK=0x{vkCode:X2} flags=0x{flags:X2} (remote keys off; logged once per run).");
                }

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
                    if (!isFirstKeyDown && !IsKeyPhysicallyDown(vkCode))
                    {
                        // 上次 KeyUp 丢失（Alt-Tab / 按键瞬间切窗 / 焦点变化等）导致 VK 残留在
                        // _pressedKeys 中，之后该热键会永久失效（Add 一直返回 false）。
                        // 用 GetAsyncKeyState 确认物理按键已弹起后，重置集合并按新的一次按下处理；
                        // 若确实仍在长按则保持不重复触发。
                        _pressedKeys.Remove(vkCode);
                        isFirstKeyDown = _pressedKeys.Add(vkCode);
                    }
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

    /// <summary>
    /// 判断某虚拟键当前是否处于按下状态（异步键状态，注入的按键同样会更新它）。用于区分「长按中的重复 KeyDown」
    /// 与「上次 KeyUp 丢失后的残留状态」。
    /// </summary>
    private static bool IsKeyPhysicallyDown(int vkCode) => (GetAsyncKeyState(vkCode) & 0x8000) != 0;

    private static readonly HashSet<string> NonGameProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // 系统 / 桌面（Windows Terminal 的真实进程是 WindowsTerminal，wt.exe 只是启动别名）
        "explorer", "taskmgr", "cmd", "powershell", "windowsterminal", "bash", "pwsh",
        "runtimebroker", "shellexperiencehost", "searchhost", "textinputhost", "startmenuexperiencehost",
        // 浏览器
        "chrome", "firefox", "msedge", "opera", "brave", "iexplore", "safari", "360se", "sogouexplorer",
        "vivaldi", "centbrowser", "qqbrowser", "maxthon", "ucbrowser", "yandex", "mychrome",
        // 通讯 / 协作（新版 Teams 进程名为 ms-teams；微信 4.x 主进程为 Weixin，
        // 小程序/内嵌浏览器宿主为 WeChatAppEx，旧版才是 wechat）
        "qq", "tim", "wechat", "weixin", "wechatappex", "discord", "feishu", "dingtalk", "slack", "teams", "ms-teams", "telegram", "whatsapp", "line",
        "skype", "zoom", "outlook", "thunderbird", "mstsc",
        // 编辑器 / IDE（Rider 2020.1+ 进程名为 rider64；AI IDE：Trae / Antigravity / ZCode）
        "notepad", "notepad++", "code", "devenv", "rider64", "sublime_text",
        "idea64", "pycharm64", "goland64", "clion64", "webstorm64", "datagrip64", "androidstudio", "vim", "emacs",
        "trae", "antigravity", "zcode",
        // 办公
        "wps", "winword", "excel", "powerpnt", "onenote", "acrobat", "foxitreader",
        // 工具 / 下载 / 媒体（IDM=IDMan，qBittorrent=qbittorrent，PotPlayer 64 位=PotPlayerMini64）
        "baidunetdisk", "thunder", "idman", "qbittorrent", "spotify", "vlc", "potplayer", "potplayermini64", "everything", "ditto",
    };

    // 前台进程名惰性缓存：低级钩子回调必须毫秒级返回，禁止每次按键都做进程枚举。
    // 仅当前台窗口变化或缓存过期时才查询一次，其余命中直接复用。全部访问都发生在
    // 安装钩子的同一 UI 线程（见 Start），无需加锁。缓存键用 HWND 而非 PID：
    // Windows 对 PID 的复用相当积极（进程退出后很快回收），同 PID 新进程会吃到
    // 旧缓存；HWND 键下同一句柄必然属于同一进程，误判窗口远小于 PID 复用周期。
    private IntPtr _cachedForegroundHwnd;
    private string? _cachedForegroundName;
    private DateTime _cacheFetchedAtUtc;
    private static readonly TimeSpan ForegroundCacheTtl = TimeSpan.FromSeconds(2);

    private bool IsGameOrBrowserForeground()
    {
        var foregroundHWnd = GetForegroundWindow();
        if (foregroundHWnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foregroundHWnd, out uint pid);
        // Environment.ProcessId 是进程内常量；Process.GetCurrentProcess().Id 每次按键都分配 Process 对象
        var currentPid = (uint)Environment.ProcessId;

        // 1. 如果前台窗口是我们自己的进程，允许触发
        if (pid == currentPid)
        {
            return _isAppActive;
        }

        // 2. 如果处于浮窗模式，且前台窗口是非排他性进程（即可能是任意游戏），允许触发
        if (_isGamingMode)
        {
            var processName = GetCachedForegroundProcessName(foregroundHWnd, pid);

            // 排除已知的非游戏常用软件（如浏览器、聊天软件、文本编辑器等）。
            // 无法确认进程名（权限不足 / 进程已退出）时保持「浮窗模式下视为游戏」的默认语义，
            // 使以管理员运行的游戏在置顶浮窗模式下也能正常响应热键。
            return processName is null || !NonGameProcessNames.Contains(processName);
        }

        return false;
    }

    /// <summary>
    /// 读取前台进程名，仅在缓存缺失（窗口变化或 TTL 过期）时做进程查询。
    /// 进程枚举是慢操作（打开句柄 + NtQuery），放在低级键盘钩子回调里会拖慢系统输入，
    /// 因此用前台 HWND 作为缓存键：切前台窗口才重新查询，同一前台连续按键命中缓存。
    /// </summary>
    private string? GetCachedForegroundProcessName(IntPtr foregroundHwnd, uint pid)
    {
        if (_cachedForegroundHwnd == foregroundHwnd && DateTime.UtcNow - _cacheFetchedAtUtc < ForegroundCacheTtl)
        {
            return _cachedForegroundName;
        }

        string? name;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (ArgumentException)
        {
            // 进程已退出（前台窗口刚被关闭）：缓存为 null，下一次前台变化再查。
            name = null;
        }
        catch (InvalidOperationException)
        {
            // 进程在 GetProcessById 与读取 ProcessName 之间退出，同上缓存 null。
            name = null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 权限不足（如管理员进程）：无法读取进程名，保持「视为游戏」语义。
            name = null;
        }

        _cachedForegroundHwnd = foregroundHwnd;
        _cachedForegroundName = name;
        _cacheFetchedAtUtc = DateTime.UtcNow;
        return name;
    }

    private void Raise(HotkeySlot slot)
    {
        try
        {
            HotkeyPressed?.Invoke(this, slot);
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

    private HotkeyRegistration CreateBuiltInRegistration(HotkeySlot slot)
    {
        var (virtualKey, modifiers) = _builtIn[(int)slot];
        return new HotkeyRegistration(
            virtualKey,
            modifiers,
            () => Raise(slot),
            slot == HotkeySlot.Hide ? CanExecuteHideToggleHotkey : CanExecuteScopedHotkey);
    }

    /// <summary>
    /// 按生效范围门控全部内置热键：关闭=不触发；全部应用=仅排除录制期挂起；
    /// 黑名单外=沿用播放键的前台判定（浮窗模式排除黑名单软件，浏览模式仅应用前台）。
    /// </summary>
    private bool CanExecuteScopedHotkey() => _hotkeyScope switch
    {
        HotkeyScope.Off => false,
        HotkeyScope.AllApps => !_suspendBuiltInHotkeys,
        _ => !_suspendBuiltInHotkeys && IsGameOrBrowserForeground(),
    };

    /// <summary>隐藏浮窗键的门控：浮窗已被热键隐藏时它是唯一恢复入口，无视范围与前台限制。</summary>
    private bool CanExecuteHideToggleHotkey() =>
        !_suspendBuiltInHotkeys && (_alwaysAllowHideToggle || CanExecuteScopedHotkey());

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

        // 不清空 _pressedKeys：快照变更不影响物理按键状态。
        // 清空会导致正在长按的键失去 "已按下" 标记，下一次自动重复 KeyDown 会误触发热键；
        // 同时在 _keyStateLock 内 Clear 会阻塞低级钩子回调（须 <300ms 返回）。
        // 旧的 VK 残留在 _pressedKeys 中无副作用：HookCallback 仅处理新快照中存在的 VK。
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

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

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
