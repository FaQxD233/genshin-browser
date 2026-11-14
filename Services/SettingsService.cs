using System.IO;
using System.Text.Json;
using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Utils;

namespace GenshinBrowser.Services;

public sealed class SettingsService : IDisposable
{
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private long _saveRequestVersion;
    private volatile bool _disposed;
    // 读盘失败且原文件没能改名保留时置位：本次运行不写 settings.json，免得覆盖还在的设置
    private volatile bool _writeBlocked;

    public SettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    /// <summary>最近一次 <see cref="Load"/> 读不出来的情况（原文件已改名保留，或改名失败、本次不写盘）；正常为 null。</summary>
    public DataFileProblem? LoadProblem { get; private set; }

    /// <summary>
    /// 同步读取配置。用于主窗构造阶段，在窗口首次显示前恢复位置，
    /// 避免异步 Loaded 期间默认坐标被写回 settings.json。
    /// 文件不存在或读不出来时得到默认值；后者原文件改名保留（见 <see cref="DataFile.Read{T}"/>）。
    /// </summary>
    public AppSettings Load()
    {
        // 缺文件也走 Normalize 而非裸 new：让内存对象与「从磁盘加载并归一」路径语义一致，
        // 尤其 HotkeyCorruptionRepairAttempted 必须在内存对象上置位；否则每次保存的
        // 快照都会重跑一次性修复逻辑，用户日后故意设置的「损坏特征」组合会被静默重置。
        var settings = DataFile.Read<AppSettings>(
            _settingsPath, AppConfig.Data.MaxSettingsFileSizeBytes, JsonFileWriter.SharedOptions, "Load settings (sync)", out var problem);
        LoadProblem = problem;
        _writeBlocked = problem is { PreservedPath: null };
        return Normalize(settings);
    }

    public async Task SaveAsync(AppSettings settings)
    {
        if (_writeBlocked)
        {
            return;
        }

        var snapshot = Sanitize(CreateSnapshot(settings));
        var requestVersion = Interlocked.Increment(ref _saveRequestVersion);

        try
        {
            await _saveGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Dispose 与在途保存的关闭竞态：保存闸已释放，放弃本次写盘（不匹配下方过滤器的异常不能外泄）。
            return;
        }

        try
        {
            if (requestVersion != Volatile.Read(ref _saveRequestVersion))
            {
                return;
            }

            await JsonFileWriter.WriteAtomicAsync(_settingsPath, snapshot, JsonFileWriter.SharedOptions).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLogger.LogException(ex, "Save settings");
            throw;
        }
        finally
        {
            try
            {
                _saveGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // 关闭竞态：Dispose 已先行释放 _saveGate；写盘已成功或被取消，无需 Release。
            }
        }
    }

    /// <summary>
    /// 设置的独立拷贝：落盘与导出配置都用它，序列化期间界面仍可改原对象。
    /// </summary>
    internal static AppSettings CreateSnapshot(AppSettings settings)
    {
        var snapshot = new AppSettings
        {
            // WPF 始终以 Key 枚举落盘（schema 1），避免 WinUI 写入的 VK（schema 2）被二次误迁移。
            SchemaVersion = 1,
            LastUrl = settings.LastUrl,
            LastPlaybackUrl = settings.LastPlaybackUrl,
            LastPlaybackSeconds = settings.LastPlaybackSeconds,
            WindowMode = settings.WindowMode,
            WindowLeft = settings.WindowLeft,
            WindowTop = settings.WindowTop,
            WindowWidth = settings.WindowWidth,
            WindowHeight = settings.WindowHeight,
            ControlWindowLeft = settings.ControlWindowLeft,
            ControlWindowTop = settings.ControlWindowTop,
            HasControlWindowPosition = settings.HasControlWindowPosition,
            ControlWindowWidth = settings.ControlWindowWidth,
            ControlWindowHeight = settings.ControlWindowHeight,
            WindowOpacity = settings.WindowOpacity,
            ZoomFactor = settings.ZoomFactor,
            ThemeMode = settings.ThemeMode,
            Language = settings.Language,
            HotkeyScope = settings.HotkeyScope,
            HotkeysAcceptInjectedInput = settings.HotkeysAcceptInjectedInput,
            FloatingClickThrough = settings.FloatingClickThrough,
            TouchMode = settings.TouchMode,
            FloatingAutoHideTitleBar = settings.FloatingAutoHideTitleBar,
            FloatingTitleBarControls = settings.FloatingTitleBarControls,
            HasSeenFloatingModeHint = settings.HasSeenFloatingModeHint,
            LastWebView2CacheCheckUtc = settings.LastWebView2CacheCheckUtc,
            HotkeyCorruptionRepairAttempted = settings.HotkeyCorruptionRepairAttempted,
        };
        foreach (var slot in HotkeySlots.All)
        {
            snapshot.SetHotkey(slot, settings.GetHotkey(slot));
        }

        return snapshot;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _saveGate.Dispose();
    }

    /// <summary>
    /// 归一一份反序列化得到的设置（启动加载与导入配置共用）：schema 2 先转回 Key 枚举，再整套清洗。
    /// </summary>
    internal static AppSettings Normalize(AppSettings? settings)
    {
        if (settings is not null && settings.SchemaVersion >= 2)
        {
            // WinUI schema 2 存的是 VK；System.Text.Json 会把 119/75 填进 Key 枚举槽位，
            // 显示成 RightCtrl / NumPad1。这里按 VK→Key 纠正后再 Sanitize。
            settings.ToggleModeKey = KeyFromVirtualKey((int)settings.ToggleModeKey);
            settings.TogglePlaybackKey = KeyFromVirtualKey((int)settings.TogglePlaybackKey);
        }

        return Sanitize(settings);
    }

    private static System.Windows.Input.Key KeyFromVirtualKey(int virtualKey)
    {
        if (virtualKey is <= 0 or > 0xFE)
        {
            return System.Windows.Input.Key.None;
        }

        return System.Windows.Input.KeyInterop.KeyFromVirtualKey(virtualKey);
    }

    private static AppSettings Sanitize(AppSettings? settings)
    {
        var defaults = new AppSettings();
        settings ??= defaults;
        settings.SchemaVersion = 1;

        settings.LastUrl = EntryText.TryNormalizeHttpUrl(settings.LastUrl, out var lastUrl)
            ? lastUrl
            : string.Empty;
        // 视频进度：网址须是能用 t= 续播的视频页、秒数有限且不为负，否则整对清掉
        if (EntryText.TryNormalizeHttpUrl(settings.LastPlaybackUrl, out var playbackUrl) &&
            PlaybackResume.SupportsTimeParameter(playbackUrl) &&
            double.IsFinite(settings.LastPlaybackSeconds) && settings.LastPlaybackSeconds >= 0)
        {
            settings.LastPlaybackUrl = playbackUrl;
        }
        else
        {
            settings.LastPlaybackUrl = string.Empty;
            settings.LastPlaybackSeconds = 0;
        }
        settings.WindowMode = Enum.IsDefined(settings.WindowMode) ? settings.WindowMode : defaults.WindowMode;
        settings.WindowLeft = NormalizeCoordinate(settings.WindowLeft, defaults.WindowLeft);
        settings.WindowTop = NormalizeCoordinate(settings.WindowTop, defaults.WindowTop);
        settings.WindowWidth = NormalizeDimension(settings.WindowWidth, defaults.WindowWidth);
        settings.WindowHeight = NormalizeDimension(settings.WindowHeight, defaults.WindowHeight);
        settings.ControlWindowWidth = NormalizeDimension(settings.ControlWindowWidth, defaults.ControlWindowWidth);
        settings.ControlWindowHeight = NormalizeDimension(settings.ControlWindowHeight, defaults.ControlWindowHeight);

        var legacyControlPosition = settings.ControlWindowLeft != defaults.ControlWindowLeft ||
                                    settings.ControlWindowTop != defaults.ControlWindowTop;
        settings.HasControlWindowPosition = settings.HasControlWindowPosition || legacyControlPosition;
        if (!IsValidCoordinate(settings.ControlWindowLeft) || !IsValidCoordinate(settings.ControlWindowTop))
        {
            settings.ControlWindowLeft = defaults.ControlWindowLeft;
            settings.ControlWindowTop = defaults.ControlWindowTop;
            settings.HasControlWindowPosition = false;
        }

        settings.WindowOpacity = double.IsFinite(settings.WindowOpacity)
            ? Math.Clamp(settings.WindowOpacity, 0.1, 1.0)
            : defaults.WindowOpacity;
        settings.ZoomFactor = double.IsFinite(settings.ZoomFactor)
            ? Math.Clamp(settings.ZoomFactor, 0.25, 5.0)
            : defaults.ZoomFactor;
        // 注意：不做 [ ]→; ' 的默认键迁移——迁移会在每次保存时改写用户故意设回的旧默认组合。
        // 旧配置仍持 [ ] 的用户通过「恢复默认」或手动改键更新。
        // WinUI 写 VK 后被旧 WPF 当 Key 枚举再被误迁移时，常见损坏结果是 RightCtrl + NumPad1。
        // 仅在一次性迁移标志未置位时尝试，避免覆盖用户后来合法设置的相同组合。
        // 必须先于下面的 IsValidHotkey 归一执行：归一会把修饰键主键重置成默认，
        // 那样损坏特征（RightCtrl）就检测不到了。
        if (!settings.HotkeyCorruptionRepairAttempted)
        {
            RepairKnownHotkeyCorruption(settings, defaults);
            settings.HotkeyCorruptionRepairAttempted = true;
        }
        foreach (var slot in HotkeySlots.All)
        {
            var (key, modifiers) = settings.GetHotkey(slot);
            settings.SetHotkey(slot, new HotkeyBinding(
                IsValidHotkey(key) ? key : defaults.GetHotkey(slot).Key,
                NormalizeModifiers(modifiers)));
        }

        ResolveHotkeyConflicts(settings, defaults);

        settings.ThemeMode = ThemeService.Normalize(settings.ThemeMode);
        settings.Language = LocalizationService.Normalize(settings.Language);
        settings.HotkeyScope = Enum.IsDefined(settings.HotkeyScope) ? settings.HotkeyScope : defaults.HotkeyScope;
        settings.LastWebView2CacheCheckUtc = NormalizeCacheCheckTime(settings.LastWebView2CacheCheckUtc);
        return settings;
    }

    private static double NormalizeCoordinate(double value, double fallback) =>
        IsValidCoordinate(value) ? value : fallback;

    private static bool IsValidCoordinate(double value) =>
        double.IsFinite(value) && Math.Abs(value) <= 100_000;

    private static double NormalizeDimension(double value, double fallback) =>
        double.IsFinite(value) && value is >= 100 and <= 10_000 ? value : fallback;

    /// <summary>
    /// 能作热键主键的键：不是修饰键，也不是 ImeProcessed / DeadCharProcessed 这类占位键——
    /// 中文输入法开着时 WPF 把字母键报成 ImeProcessed，旧版录制会把它存下来，这种热键永远不触发，加载时恢复默认。
    /// </summary>
    private static bool IsValidHotkey(System.Windows.Input.Key key) =>
        key is not (System.Windows.Input.Key.None or System.Windows.Input.Key.ImeProcessed or System.Windows.Input.Key.DeadCharProcessed) &&
        Enum.IsDefined(key) && !IsModifierKey(key);

    /// <summary>
    /// 修饰键不能作热键主键：其 KeyDown 到来时对应修饰状态必然为按下，
    /// 与「无修饰键」期望矛盾（带修饰键期望则其它修饰判定也被破坏），永不触发。
    /// </summary>
    private static bool IsModifierKey(System.Windows.Input.Key key) =>
        key is System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin
            or System.Windows.Input.Key.System;

    private static System.Windows.Input.ModifierKeys NormalizeModifiers(System.Windows.Input.ModifierKeys modifiers)
    {
        const System.Windows.Input.ModifierKeys valid =
            System.Windows.Input.ModifierKeys.Alt |
            System.Windows.Input.ModifierKeys.Control |
            System.Windows.Input.ModifierKeys.Shift |
            System.Windows.Input.ModifierKeys.Windows;
        return modifiers & valid;
    }

    /// <summary>
    /// 检测「VK 被当成 WPF Key 枚举再 round-trip」的典型损坏：模式=RightCtrl、播放=NumPad1。
    /// </summary>
    private static void RepairKnownHotkeyCorruption(AppSettings settings, AppSettings defaults)
    {
        if (settings.ToggleModeKey == System.Windows.Input.Key.RightCtrl &&
            settings.ToggleModeModifiers == System.Windows.Input.ModifierKeys.None &&
            settings.TogglePlaybackKey == System.Windows.Input.Key.NumPad1 &&
            settings.TogglePlaybackModifiers == System.Windows.Input.ModifierKeys.None)
        {
            settings.ToggleModeKey = defaults.ToggleModeKey;
            settings.ToggleModeModifiers = defaults.ToggleModeModifiers;
            settings.TogglePlaybackKey = defaults.TogglePlaybackKey;
            settings.TogglePlaybackModifiers = defaults.TogglePlaybackModifiers;
        }
    }

    /// <summary>
    /// 五组热键槽位的统一冲突消解（槽位顺序即优先级，见 <see cref="HotkeySlot"/>）。
    /// 前位保留、后位让位：让位者先恢复默认；默认位仍被占用时依次泊到
    /// F9/F10/F11/F12/F6 空闲键。保证 Sanitize 后任意两组 (Key, Modifiers) 不重复。
    /// </summary>
    private static readonly System.Windows.Input.Key[] HotkeyFallbackKeys =
    {
        System.Windows.Input.Key.F9,
        System.Windows.Input.Key.F10,
        System.Windows.Input.Key.F11,
        System.Windows.Input.Key.F12,
        System.Windows.Input.Key.F6,
    };

    private static void ResolveHotkeyConflicts(AppSettings settings, AppSettings defaults)
    {
        for (var attempt = 0; attempt < HotkeySlots.All.Count * HotkeySlots.All.Count; attempt++)
        {
            var conflict = FindFirstHotkeyConflict(settings);
            if (conflict is null)
            {
                return;
            }

            var (first, second) = conflict.Value;
            // 后位让位：先回默认；若默认位仍被前位占用（前位用户值恰为后位默认），
            // 把前位也拉回其默认；仍冲突才把后位泊到空闲备用键。
            settings.SetHotkey(second, defaults.GetHotkey(second));
            if (!SlotConflictsWithAny(settings, second))
            {
                continue;
            }

            settings.SetHotkey(first, defaults.GetHotkey(first));
            if (!SlotConflictsWithAny(settings, first) && !SlotConflictsWithAny(settings, second))
            {
                continue;
            }

            ParkHotkeySlot(settings, second);
        }
    }

    /// <summary>指定槽位当前组合是否与任一其它槽位冲突。</summary>
    private static bool SlotConflictsWithAny(AppSettings settings, HotkeySlot slot) =>
        HotkeySlots.All.Any(other => other != slot && settings.GetHotkey(other) == settings.GetHotkey(slot));

    private static (HotkeySlot First, HotkeySlot Second)? FindFirstHotkeyConflict(AppSettings settings)
    {
        var slots = HotkeySlots.All;
        for (var i = 0; i < slots.Count; i++)
        {
            for (var j = i + 1; j < slots.Count; j++)
            {
                if (settings.GetHotkey(slots[i]) == settings.GetHotkey(slots[j]))
                {
                    return (slots[i], slots[j]);
                }
            }
        }

        return null;
    }

    /// <summary>把槽位泊到第一个当前无人使用的备用键（无修饰键）。</summary>
    private static void ParkHotkeySlot(AppSettings settings, HotkeySlot slot)
    {
        foreach (var fallback in HotkeyFallbackKeys)
        {
            if (!HotkeySlots.All.Any(other => other != slot && settings.GetHotkey(other).Key == fallback))
            {
                settings.SetHotkey(slot, new HotkeyBinding(fallback, System.Windows.Input.ModifierKeys.None));
                return;
            }
        }
    }

    private static DateTime NormalizeCacheCheckTime(DateTime value)
    {
        if (value == DateTime.MinValue)
        {
            return value;
        }

        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return utc > DateTime.UtcNow.AddMinutes(5) || utc < DateTime.UnixEpoch
            ? DateTime.MinValue
            : utc;
    }
}
