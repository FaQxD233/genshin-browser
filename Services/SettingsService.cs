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
            return new AppSettings();
        }

        try
        {
            var json = JsonFileWriter.ReadAllTextBounded(_settingsPath, AppConfig.Data.MaxSettingsFileSizeBytes);
            return DeserializeAndSanitize(json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            FileLogger.LogException(ex, "Load settings (sync)");
            return new AppSettings();
        }
    }

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = await JsonFileWriter.ReadAllTextBoundedAsync(
                _settingsPath,
                AppConfig.Data.MaxSettingsFileSizeBytes).ConfigureAwait(false);
            return DeserializeAndSanitize(json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            FileLogger.LogException(ex, "Load settings");
            // 文件损坏或无法访问，使用默认设置
            return new AppSettings();
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
            ThemeMode = settings.ThemeMode,
            Language = settings.Language,
            HasSeenFloatingModeHint = settings.HasSeenFloatingModeHint,
            LastWebView2CacheCheckUtc = settings.LastWebView2CacheCheckUtc,
        };

        snapshot = Sanitize(snapshot);
        var requestVersion = Interlocked.Increment(ref _saveRequestVersion);

        await _saveGate.WaitAsync().ConfigureAwait(false);
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
            _saveGate.Release();
        }
    }

    public void Dispose()
    {
        _saveGate.Dispose();
    }

    private static AppSettings DeserializeAndSanitize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonFileWriter.SharedOptions);
        if (settings is not null && UsesWinUiVirtualKeySchema(document.RootElement))
        {
            // WinUI schema 2 存的是 VK；System.Text.Json 会把 119/75 填进 Key 枚举槽位，
            // 显示成 RightCtrl / NumPad1。这里按 VK→Key 纠正后再 Sanitize。
            settings.ToggleModeKey = KeyFromVirtualKey((int)settings.ToggleModeKey);
            settings.TogglePlaybackKey = KeyFromVirtualKey((int)settings.TogglePlaybackKey);
        }

        return Sanitize(settings);
    }

    private static bool UsesWinUiVirtualKeySchema(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(nameof(AppSettings.SchemaVersion), out var schemaElement) ||
            !schemaElement.TryGetInt32(out var schemaVersion))
        {
            return false;
        }

        return schemaVersion >= 2;
    }

    private static System.Windows.Input.Key KeyFromVirtualKey(int virtualKey)
    {
        if (virtualKey is <= 0 or > 0xFE)
        {
            return System.Windows.Input.Key.None;
        }

        var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(virtualKey);
        return key == System.Windows.Input.Key.None ? System.Windows.Input.Key.None : key;
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
        settings.ToggleModeKey = IsValidHotkey(settings.ToggleModeKey) ? settings.ToggleModeKey : defaults.ToggleModeKey;
        settings.TogglePlaybackKey = IsValidHotkey(settings.TogglePlaybackKey) ? settings.TogglePlaybackKey : defaults.TogglePlaybackKey;
        settings.ToggleModeModifiers = NormalizeModifiers(settings.ToggleModeModifiers);
        settings.TogglePlaybackModifiers = NormalizeModifiers(settings.TogglePlaybackModifiers);
        // WinUI 写 VK 后被旧 WPF 当 Key 枚举再被误迁移时，常见损坏结果是 RightCtrl + NumPad1。
        RepairKnownHotkeyCorruption(settings, defaults);
        ResolveHotkeyConflict(settings, defaults);

        settings.ThemeMode = ThemeService.Normalize(settings.ThemeMode);
        settings.Language = LocalizationService.Normalize(settings.Language);
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
        key != System.Windows.Input.Key.None && Enum.IsDefined(key);

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
    /// 消除模式键与播放键的冲突。优先把播放键恢复为默认；若仍冲突（例如模式键本身就是默认播放键），
    /// 再把模式键恢复为默认；最后仍冲突时给播放键一个固定备用键，保证 Sanitize 后永不双触发。
    /// </summary>
    private static void ResolveHotkeyConflict(AppSettings settings, AppSettings defaults)
    {
        if (!HotkeysConflict(settings))
        {
            return;
        }

        settings.TogglePlaybackKey = defaults.TogglePlaybackKey;
        settings.TogglePlaybackModifiers = defaults.TogglePlaybackModifiers;
        if (!HotkeysConflict(settings))
        {
            return;
        }

        settings.ToggleModeKey = defaults.ToggleModeKey;
        settings.ToggleModeModifiers = defaults.ToggleModeModifiers;
        if (!HotkeysConflict(settings))
        {
            return;
        }

        // 默认值本身不应冲突；防御性兜底，避免损坏配置或测试篡改 defaults 时仍双注册。
        settings.TogglePlaybackKey = System.Windows.Input.Key.F9;
        settings.TogglePlaybackModifiers = System.Windows.Input.ModifierKeys.None;
        if (!HotkeysConflict(settings))
        {
            return;
        }

        settings.ToggleModeKey = System.Windows.Input.Key.F8;
        settings.ToggleModeModifiers = System.Windows.Input.ModifierKeys.None;
        settings.TogglePlaybackKey = System.Windows.Input.Key.F9;
        settings.TogglePlaybackModifiers = System.Windows.Input.ModifierKeys.None;
    }

    private static bool HotkeysConflict(AppSettings settings) =>
        settings.ToggleModeKey == settings.TogglePlaybackKey &&
        settings.ToggleModeModifiers == settings.TogglePlaybackModifiers;

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
