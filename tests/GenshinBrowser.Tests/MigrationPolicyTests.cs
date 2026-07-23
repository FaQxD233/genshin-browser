using GenshinBrowser.Browser;
using GenshinBrowser.Models;
using GenshinBrowser.Services;
using GenshinBrowser.Windowing;

namespace GenshinBrowser.Tests;

public sealed class MigrationPolicyTests
{
    [Fact]
    public void LegacyWpfHotkeys_AreConvertedToWin32VirtualKeys()
    {
        using TestDirectory directory = new();
        string path = directory.GetPath("settings.json");
        File.WriteAllText(path, """
            {
              "SchemaVersion": 1,
              "ToggleModeKey": 97,
              "ToggleModeModifiers": 0,
              "TogglePlaybackKey": 54,
              "TogglePlaybackModifiers": 0
            }
            """);

        using SettingsService service = new(path);
        AppSettings settings = service.Load();

        Assert.Equal(VirtualKeyCodes.F8, settings.ToggleModeKey);
        Assert.Equal(VirtualKeyCodes.K, settings.TogglePlaybackKey);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void LegacyWpfSettingsWithoutSchema_AreConvertedToWin32VirtualKeys()
    {
        using TestDirectory directory = new();
        string path = directory.GetPath("settings.json");
        File.WriteAllText(path, """
            {
              "ToggleModeKey": 97,
              "ToggleModeModifiers": 0,
              "TogglePlaybackKey": 54,
              "TogglePlaybackModifiers": 0
            }
            """);

        using SettingsService service = new(path);
        AppSettings settings = service.Load();

        Assert.Equal(VirtualKeyCodes.F8, settings.ToggleModeKey);
        Assert.Equal(VirtualKeyCodes.K, settings.TogglePlaybackKey);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void CorruptedRightCtrlNumPad1Hotkeys_AreRestoredToDefaults()
    {
        // WinUI wrote VK F8/K (119/75); old WPF treated them as Key enum and re-saved;
        // a subsequent schema&lt;2 migration turns that into VK_RCONTROL + VK_NUMPAD1.
        using TestDirectory directory = new();
        string path = directory.GetPath("settings.json");
        File.WriteAllText(path, """
            {
              "SchemaVersion": 1,
              "ToggleModeKey": 119,
              "ToggleModeModifiers": 0,
              "TogglePlaybackKey": 75,
              "TogglePlaybackModifiers": 0
            }
            """);

        using SettingsService service = new(path);
        AppSettings settings = service.Load();

        Assert.Equal(VirtualKeyCodes.F8, settings.ToggleModeKey);
        Assert.Equal(VirtualKeyCodes.K, settings.TogglePlaybackKey);
    }

    [Fact]
    public void StaleTempCleanup_DeletesOnlyTopLevelJsonGuidFiles()
    {
        using TestDirectory directory = new();
        string guid = Guid.NewGuid().ToString("N");
        string jsonTemp = directory.GetPath($"settings.json.{guid}.tmp");
        string nonJsonTemp = directory.GetPath($"WebViewProfile.{guid}.tmp");
        string malformedJsonTemp = directory.GetPath("history.json.not-a-guid.tmp");
        string profileDirectory = directory.GetPath("WebViewProfile");
        Directory.CreateDirectory(profileDirectory);
        string nestedProfileTemp = Path.Combine(profileDirectory, $"state.json.{guid}.tmp");
        File.WriteAllText(jsonTemp, "stale");
        File.WriteAllText(nonJsonTemp, "keep");
        File.WriteAllText(malformedJsonTemp, "keep");
        File.WriteAllText(nestedProfileTemp, "keep");

        DataFileMaintenance.PurgeStaleTempFiles(directory.Path);

        Assert.False(File.Exists(jsonTemp));
        Assert.True(File.Exists(nonJsonTemp));
        Assert.True(File.Exists(malformedJsonTemp));
        Assert.True(File.Exists(nestedProfileTemp));
    }

    [Fact]
    public void SettingsMigration_RepairsInvalidWindowBounds()
    {
        using TestDirectory directory = new();
        string path = directory.GetPath("settings.json");
        File.WriteAllText(path, """
            {
              "SchemaVersion": 2,
              "WindowLeft": 100001,
              "WindowTop": -100001,
              "WindowWidth": 100,
              "WindowHeight": 200,
              "ControlWindowLeft": 100001,
              "ControlWindowTop": 0,
              "HasControlWindowPosition": true,
              "ControlWindowWidth": 100,
              "ControlWindowHeight": 200
            }
            """);

        using SettingsService service = new(path);
        AppSettings settings = service.Load();
        AppSettings defaults = new();

        Assert.Equal(defaults.WindowLeft, settings.WindowLeft);
        Assert.Equal(defaults.WindowTop, settings.WindowTop);
        Assert.Equal(defaults.WindowWidth, settings.WindowWidth);
        Assert.Equal(defaults.WindowHeight, settings.WindowHeight);
        Assert.Equal(defaults.ControlWindowLeft, settings.ControlWindowLeft);
        Assert.Equal(defaults.ControlWindowTop, settings.ControlWindowTop);
        Assert.Equal(defaults.ControlWindowWidth, settings.ControlWindowWidth);
        Assert.Equal(defaults.ControlWindowHeight, settings.ControlWindowHeight);
        Assert.False(settings.HasControlWindowPosition);
    }

    [Theory]
    [InlineData(0.1, 26, true)]
    [InlineData(0.5, 128, true)]
    [InlineData(0.999, 254, true)]
    [InlineData(1.0, 255, false)]
    public void WindowTransparencyPolicy_RemovesLayeredPathAtFullOpacity(
        double opacity,
        byte expectedAlpha,
        bool expectedLayered)
    {
        WindowTransparencyDecision decision = WindowTransparencyPolicy.Create(opacity);

        Assert.Equal(expectedAlpha, decision.Alpha);
        Assert.Equal(expectedLayered, decision.UseLayeredWindow);
    }

    [Theory]
    [InlineData(WindowMode.Free, 370, 410)]
    [InlineData(WindowMode.Fixed, 370, 370)]
    public void BrowserWindowMetrics_PreserveLegacyContentHeight(
        WindowMode mode,
        double contentHeight,
        double expectedOuterHeight)
    {
        double outerHeight = BrowserWindowMetrics.ToOuterHeight(contentHeight, mode);

        Assert.Equal(expectedOuterHeight, outerHeight);
        Assert.Equal(contentHeight, BrowserWindowMetrics.ToContentHeight(outerHeight, mode));
    }
}
