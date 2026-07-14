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
            return Sanitize(JsonSerializer.Deserialize<AppSettings>(json, JsonFileWriter.SharedOptions));
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
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonFileWriter.SharedOptions);
            return Sanitize(settings);
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

    private static AppSettings Sanitize(AppSettings? settings)
    {
        var defaults = new AppSettings();
        settings ??= defaults;

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
        if (settings.ToggleModeKey == settings.TogglePlaybackKey &&
            settings.ToggleModeModifiers == settings.TogglePlaybackModifiers)
        {
            settings.TogglePlaybackKey = defaults.TogglePlaybackKey;
            settings.TogglePlaybackModifiers = defaults.TogglePlaybackModifiers;
        }

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
