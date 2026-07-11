using System.IO;
using System.Text.Json;
using GenshinBrowser.Models;

namespace GenshinBrowser.Services;

public sealed class SettingsService : IDisposable
{
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public SettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream).ConfigureAwait(false) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
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
            ControlWindowWidth = settings.ControlWindowWidth,
            ControlWindowHeight = settings.ControlWindowHeight,
            WindowOpacity = settings.WindowOpacity,
            ToggleModeKey = settings.ToggleModeKey,
            ToggleModeModifiers = settings.ToggleModeModifiers,
            TogglePlaybackKey = settings.TogglePlaybackKey,
            TogglePlaybackModifiers = settings.TogglePlaybackModifiers,
            ThemeMode = settings.ThemeMode,
            Language = settings.Language,
        };

        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await JsonFileWriter.WriteAtomicAsync(_settingsPath, snapshot, _jsonOptions).ConfigureAwait(false);
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
}
