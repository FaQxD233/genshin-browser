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

    public SettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    /// <summary>
    /// 同步读取配置。用于主窗构造阶段，在窗口首次显示前恢复位置，
    /// 避免异步 Loaded 期间默认坐标被写回 settings.json。
    /// </summary>
    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            // 走 Sanitize 而非裸 new：让内存对象与「从磁盘加载并归一」路径语义一致，
            // 尤其 HotkeyCorruptionRepairAttempted 必须在内存对象上置位；否则每次保存的
            // 快照都会重跑一次性修复逻辑，用户日后故意设置的「损坏特征」组合会被静默重置。
            return Sanitize(null);
        }

        try
        {
            var json = JsonFileWriter.ReadAllTextBounded(_settingsPath, AppConfig.Data.MaxSettingsFileSizeBytes);
            return DeserializeAndSanitize(json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            FileLogger.LogException(ex, "Load settings (sync)");
            return Sanitize(null);
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var snapshot = new AppSettings
        {
            // WPF 始终以 Key 枚举落盘（schema 1），避免 WinUI 写入的 VK（schema 2）被二次误迁移。
            SchemaVersion = 1,
            LastUrl = settings.LastUrl,
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
            ToggleModeKey = settings.ToggleModeKey,
            ToggleModeModifiers = settings.ToggleModeModifiers,
            TogglePlaybackKey = settings.TogglePlaybackKey,
            TogglePlaybackModifiers = settings.TogglePlaybackModifiers,
            ToggleHideKey = settings.ToggleHideKey,
            ToggleHideModifiers = settings.ToggleHideModifiers,
            SeekBackwardKey = settings.SeekBackwardKey,
            SeekBackwardModifiers = settings.SeekBackwardModifiers,
            SeekForwardKey = settings.SeekForwardKey,
            SeekForwardModifiers = settings.SeekForwardModifiers,
            ThemeMode = settings.ThemeMode,
            Language = settings.Language,
            HotkeyScope = settings.HotkeyScope,
            HasSeenFloatingModeHint = settings.HasSeenFloatingModeHint,
            LastWebView2CacheCheckUtc = settings.LastWebView2CacheCheckUtc,
            HotkeyCorruptionRepairAttempted = settings.HotkeyCorruptionRepairAttempted,
        };

        snapshot = Sanitize(snapshot);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _saveGate.Dispose();
    }

    private static AppSettings DeserializeAndSanitize(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonFileWriter.SharedOptions);
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
        settings.ToggleModeKey = IsValidHotkey(settings.ToggleModeKey) ? settings.ToggleModeKey : defaults.ToggleModeKey;
        settings.TogglePlaybackKey = IsValidHotkey(settings.TogglePlaybackKey) ? settings.TogglePlaybackKey : defaults.TogglePlaybackKey;
        settings.ToggleHideKey = IsValidHotkey(settings.ToggleHideKey) ? settings.ToggleHideKey : defaults.ToggleHideKey;
        settings.SeekBackwardKey = IsValidHotkey(settings.SeekBackwardKey) ? settings.SeekBackwardKey : defaults.SeekBackwardKey;
        settings.SeekForwardKey = IsValidHotkey(settings.SeekForwardKey) ? settings.SeekForwardKey : defaults.SeekForwardKey;
        settings.ToggleModeModifiers = NormalizeModifiers(settings.ToggleModeModifiers);
        settings.TogglePlaybackModifiers = NormalizeModifiers(settings.TogglePlaybackModifiers);
        settings.ToggleHideModifiers = NormalizeModifiers(settings.ToggleHideModifiers);
        settings.SeekBackwardModifiers = NormalizeModifiers(settings.SeekBackwardModifiers);
        settings.SeekForwardModifiers = NormalizeModifiers(settings.SeekForwardModifiers);
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

    private static bool IsValidHotkey(System.Windows.Input.Key key) =>
        key != System.Windows.Input.Key.None && Enum.IsDefined(key) && !IsModifierKey(key);

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
    /// 五组热键槽位（模式 / 播放 / 隐藏 / 倒退 / 快进）的统一冲突消解。
    /// 前位保留、后位让位：让位者先恢复默认；默认位仍被占用时依次泊到
    /// F9/F10/F11/F12/F6 空闲键。保证 Sanitize 后任意两组 (Key, Modifiers) 不重复。
    /// </summary>
    private const int HotkeySlotCount = 5;

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
        for (var attempt = 0; attempt < HotkeySlotCount * HotkeySlotCount; attempt++)
        {
            var (first, second) = FindFirstHotkeyConflict(settings);
            if (first < 0)
            {
                return;
            }

            // 后位让位：先回默认；若默认位仍被前位占用（前位用户值恰为后位默认），
            // 把前位也拉回其默认；仍冲突才把后位泊到空闲备用键。
            SetHotkeySlot(settings, second,
                GetHotkeySlotKey(defaults, second), GetHotkeySlotModifiers(defaults, second));
            if (!SlotConflictsWithAny(settings, second))
            {
                continue;
            }

            SetHotkeySlot(settings, first,
                GetHotkeySlotKey(defaults, first), GetHotkeySlotModifiers(defaults, first));
            if (!SlotConflictsWithAny(settings, first) && !SlotConflictsWithAny(settings, second))
            {
                continue;
            }

            ParkHotkeySlot(settings, second);
        }
    }

    /// <summary>指定槽位当前组合是否与任一其它槽位冲突。</summary>
    private static bool SlotConflictsWithAny(AppSettings settings, int slot)
    {
        for (var i = 0; i < HotkeySlotCount; i++)
        {
            if (i != slot &&
                GetHotkeySlotKey(settings, i) == GetHotkeySlotKey(settings, slot) &&
                GetHotkeySlotModifiers(settings, i) == GetHotkeySlotModifiers(settings, slot))
            {
                return true;
            }
        }

        return false;
    }

    private static (int First, int Second) FindFirstHotkeyConflict(AppSettings settings)
    {
        for (var i = 0; i < HotkeySlotCount; i++)
        {
            for (var j = i + 1; j < HotkeySlotCount; j++)
            {
                if (GetHotkeySlotKey(settings, i) == GetHotkeySlotKey(settings, j) &&
                    GetHotkeySlotModifiers(settings, i) == GetHotkeySlotModifiers(settings, j))
                {
                    return (i, j);
                }
            }
        }

        return (-1, -1);
    }

    /// <summary>把槽位泊到第一个当前无人使用的备用键（无修饰键）。</summary>
    private static void ParkHotkeySlot(AppSettings settings, int slot)
    {
        foreach (var fallback in HotkeyFallbackKeys)
        {
            var taken = false;
            for (var i = 0; i < HotkeySlotCount; i++)
            {
                if (i != slot && GetHotkeySlotKey(settings, i) == fallback)
                {
                    taken = true;
                    break;
                }
            }

            if (!taken)
            {
                SetHotkeySlot(settings, slot, fallback, System.Windows.Input.ModifierKeys.None);
                return;
            }
        }
    }

    private static System.Windows.Input.Key GetHotkeySlotKey(AppSettings settings, int slot) => slot switch
    {
        0 => settings.ToggleModeKey,
        1 => settings.TogglePlaybackKey,
        2 => settings.ToggleHideKey,
        3 => settings.SeekBackwardKey,
        _ => settings.SeekForwardKey,
    };

    private static System.Windows.Input.ModifierKeys GetHotkeySlotModifiers(AppSettings settings, int slot) => slot switch
    {
        0 => settings.ToggleModeModifiers,
        1 => settings.TogglePlaybackModifiers,
        2 => settings.ToggleHideModifiers,
        3 => settings.SeekBackwardModifiers,
        _ => settings.SeekForwardModifiers,
    };

    private static void SetHotkeySlot(AppSettings settings, int slot, System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        switch (slot)
        {
            case 0:
                settings.ToggleModeKey = key;
                settings.ToggleModeModifiers = modifiers;
                break;
            case 1:
                settings.TogglePlaybackKey = key;
                settings.TogglePlaybackModifiers = modifiers;
                break;
            case 2:
                settings.ToggleHideKey = key;
                settings.ToggleHideModifiers = modifiers;
                break;
            case 3:
                settings.SeekBackwardKey = key;
                settings.SeekBackwardModifiers = modifiers;
                break;
            default:
                settings.SeekForwardKey = key;
                settings.SeekForwardModifiers = modifiers;
                break;
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
