using GenshinBrowser.Constants;
using GenshinBrowser.Models;
using GenshinBrowser.Utils;
using System.IO;
using System.Text.Json;

namespace GenshinBrowser.Services;

public sealed class SettingsService : IDisposable
{
    private readonly string settingsPath;
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private long saveRequestVersion;

    public SettingsService(string settingsPath)
    {
        this.settingsPath = settingsPath;
    }

    public AppSettings Load()
    {
        if (!File.Exists(settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            string json = JsonFileWriter.ReadAllTextBounded(settingsPath, AppConfig.Data.MaxSettingsFileSizeBytes);
            return DeserializeAndSanitize(json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            FileLogger.LogException(ex, "Load settings");
            return new AppSettings();
        }
    }

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            string json = await JsonFileWriter.ReadAllTextBoundedAsync(
                settingsPath,
                AppConfig.Data.MaxSettingsFileSizeBytes).ConfigureAwait(false);
            return DeserializeAndSanitize(json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            FileLogger.LogException(ex, "Load settings");
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        AppSettings snapshot = Sanitize(Clone(settings));
        long requestVersion = Interlocked.Increment(ref saveRequestVersion);

        await saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (requestVersion != Volatile.Read(ref saveRequestVersion))
            {
                return;
            }

            await JsonFileWriter.WriteAtomicAsync(
                settingsPath,
                snapshot,
                JsonFileWriter.SharedOptions).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLogger.LogException(ex, "Save settings");
            throw;
        }
        finally
        {
            saveGate.Release();
        }
    }

    public void Dispose()
    {
        saveGate.Dispose();
    }

    private static AppSettings Clone(AppSettings settings)
    {
        return new AppSettings
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
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
    }

    private static AppSettings DeserializeAndSanitize(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, JsonFileWriter.SharedOptions);
        if (settings is not null && UsesLegacyWpfKeySchema(document.RootElement))
        {
            MigrateLegacyWpfHotkeys(settings, document.RootElement);
        }

        return Sanitize(settings);
    }

    private static bool UsesLegacyWpfKeySchema(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(nameof(AppSettings.SchemaVersion), out JsonElement schemaElement))
        {
            return root.ValueKind == JsonValueKind.Object;
        }

        return !schemaElement.TryGetInt32(out int schemaVersion) ||
               schemaVersion < AppSettings.CurrentSchemaVersion;
    }

    private static void MigrateLegacyWpfHotkeys(AppSettings settings, JsonElement root)
    {
        AppSettings defaults = new();
        settings.ToggleModeKey = ReadLegacyWpfKey(
            root,
            nameof(AppSettings.ToggleModeKey),
            defaults.ToggleModeKey);
        settings.TogglePlaybackKey = ReadLegacyWpfKey(
            root,
            nameof(AppSettings.TogglePlaybackKey),
            defaults.TogglePlaybackKey);
    }

    private static int ReadLegacyWpfKey(JsonElement root, string propertyName, int fallback)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement keyElement) ||
            !keyElement.TryGetInt32(out int legacyKey))
        {
            return fallback;
        }

        int virtualKey = LegacyWpfKeyConverter.ToVirtualKey(legacyKey);
        return virtualKey == 0 ? fallback : virtualKey;
    }

    private static AppSettings Sanitize(AppSettings? settings)
    {
        AppSettings defaults = new();
        settings ??= defaults;

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        settings.LastUrl = EntryText.TryNormalizeHttpUrl(settings.LastUrl, out string lastUrl)
            ? lastUrl
            : string.Empty;
        settings.WindowMode = Enum.IsDefined(settings.WindowMode) ? settings.WindowMode : defaults.WindowMode;
        settings.WindowLeft = NormalizeCoordinate(settings.WindowLeft, defaults.WindowLeft);
        settings.WindowTop = NormalizeCoordinate(settings.WindowTop, defaults.WindowTop);
        settings.WindowWidth = NormalizeDimension(settings.WindowWidth, defaults.WindowWidth, 640);
        settings.WindowHeight = NormalizeDimension(settings.WindowHeight, defaults.WindowHeight, 360);
        settings.ControlWindowWidth = NormalizeDimension(settings.ControlWindowWidth, defaults.ControlWindowWidth, 520);
        settings.ControlWindowHeight = NormalizeDimension(settings.ControlWindowHeight, defaults.ControlWindowHeight, 600);

        bool legacyControlPosition = settings.ControlWindowLeft != defaults.ControlWindowLeft ||
                                     settings.ControlWindowTop != defaults.ControlWindowTop;
        settings.HasControlWindowPosition |= legacyControlPosition;
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
        settings.ToggleModeKey = IsValidVirtualKey(settings.ToggleModeKey)
            ? settings.ToggleModeKey
            : defaults.ToggleModeKey;
        settings.TogglePlaybackKey = IsValidVirtualKey(settings.TogglePlaybackKey)
            ? settings.TogglePlaybackKey
            : defaults.TogglePlaybackKey;
        settings.ToggleModeModifiers = NormalizeModifiers(settings.ToggleModeModifiers);
        settings.TogglePlaybackModifiers = NormalizeModifiers(settings.TogglePlaybackModifiers);
        // 旧 WPF 把 WinUI 的 VK 119/75 当 Key 枚举保存后再被 schema&lt;2 迁移 → VK RightCtrl + NumPad1。
        RepairKnownHotkeyCorruption(settings, defaults);
        ResolveHotkeyConflict(settings, defaults);
        settings.ThemeMode = NormalizeTheme(settings.ThemeMode);
        settings.Language = NormalizeLanguage(settings.Language);
        settings.LastWebView2CacheCheckUtc = NormalizeCacheCheckTime(settings.LastWebView2CacheCheckUtc);
        return settings;
    }

    private static double NormalizeCoordinate(double value, double fallback)
    {
        return IsValidCoordinate(value) ? value : fallback;
    }

    private static bool IsValidCoordinate(double value)
    {
        return double.IsFinite(value) && Math.Abs(value) <= 100_000;
    }

    private static double NormalizeDimension(double value, double fallback, double minimum)
    {
        return double.IsFinite(value) && value >= minimum && value <= 10_000 ? value : fallback;
    }

    private static bool IsValidVirtualKey(int virtualKey)
    {
        return virtualKey is > 0 and <= 0xFE;
    }

    private static HotkeyModifiers NormalizeModifiers(HotkeyModifiers modifiers)
    {
        const HotkeyModifiers valid =
            HotkeyModifiers.Alt |
            HotkeyModifiers.Control |
            HotkeyModifiers.Shift |
            HotkeyModifiers.Windows;
        return modifiers & valid;
    }

    private static void RepairKnownHotkeyCorruption(AppSettings settings, AppSettings defaults)
    {
        // VK_RCONTROL=0xA3, VK_NUMPAD1=0x61 — 见 WPF Key.RightCtrl/NumPad1 与 VK F8/K 的交叉错位。
        if (settings.ToggleModeKey == 0xA3 &&
            settings.ToggleModeModifiers == HotkeyModifiers.None &&
            settings.TogglePlaybackKey == 0x61 &&
            settings.TogglePlaybackModifiers == HotkeyModifiers.None)
        {
            settings.ToggleModeKey = defaults.ToggleModeKey;
            settings.ToggleModeModifiers = defaults.ToggleModeModifiers;
            settings.TogglePlaybackKey = defaults.TogglePlaybackKey;
            settings.TogglePlaybackModifiers = defaults.TogglePlaybackModifiers;
        }
    }

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

        settings.TogglePlaybackKey = VirtualKeyCodes.F9;
        settings.TogglePlaybackModifiers = HotkeyModifiers.None;
    }

    private static bool HotkeysConflict(AppSettings settings)
    {
        return settings.ToggleModeKey == settings.TogglePlaybackKey &&
               settings.ToggleModeModifiers == settings.TogglePlaybackModifiers;
    }

    private static string NormalizeTheme(string? theme)
    {
        return theme?.Trim().ToUpperInvariant() switch
        {
            "LIGHT" => "Light",
            "SYSTEM" => "System",
            _ => "Dark",
        };
    }

    private static string NormalizeLanguage(string? language)
    {
        return string.Equals(language?.Trim(), "en-US", StringComparison.OrdinalIgnoreCase)
            ? "en-US"
            : "zh-CN";
    }

    private static DateTime NormalizeCacheCheckTime(DateTime value)
    {
        if (value == DateTime.MinValue)
        {
            return value;
        }

        DateTime utc = value.Kind switch
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
