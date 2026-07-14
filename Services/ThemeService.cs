using System.Windows;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace GenshinBrowser.Services;

/// <summary>
/// 运行时切换亮/暗/跟随系统主题：替换 App 资源中的颜色字典。
/// 样式与窗口中的画刷引用应使用 DynamicResource。
/// </summary>
public static class ThemeService
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string System = "System";

    private const string DarkSource = "Resources/Themes/Dark.xaml";
    private const string LightSource = "Resources/Themes/Light.xaml";

    private static readonly object SyncRoot = new();
    private static bool _watchingSystem;
    private static string _preference = Dark;

    /// <summary>
    /// 当前实际生效的主题（Dark / Light）。System 偏好会解析为二者之一。
    /// </summary>
    public static string Current { get; private set; } = Dark;

    /// <summary>
    /// 用户选择的偏好：Dark / Light / System。
    /// </summary>
    public static string Preference => _preference;

    public static event EventHandler? ThemeChanged;

    public static void Apply(string? themeMode)
    {
        var preference = Normalize(themeMode);
        lock (SyncRoot)
        {
            _preference = preference;
            EnsureSystemWatchLocked();
        }

        var effective = preference == System ? ResolveSystemTheme() : preference;
        ApplyEffective(effective);
    }

    public static string Normalize(string? themeMode)
    {
        if (string.Equals(themeMode, Light, StringComparison.OrdinalIgnoreCase))
        {
            return Light;
        }

        if (string.Equals(themeMode, System, StringComparison.OrdinalIgnoreCase))
        {
            return System;
        }

        return Dark;
    }

    /// <summary>
    /// 读取 Windows「应用默认使用浅色」设置。失败时回退暗色（与本应用默认一致）。
    /// </summary>
    public static string ResolveSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i)
            {
                return i != 0 ? Light : Dark;
            }

            if (value is not null && int.TryParse(value.ToString(), out var parsed))
            {
                return parsed != 0 ? Light : Dark;
            }
        }
        catch
        {
            // 注册表不可用时保持暗色
        }

        return Dark;
    }

    internal static void ReplaceMergedDictionary(
        System.Collections.ObjectModel.Collection<ResourceDictionary> dictionaries,
        Func<ResourceDictionary, bool> match,
        string relativeSource)
    {
        var uri = new Uri(relativeSource, UriKind.Relative);
        var replacement = new ResourceDictionary { Source = uri };

        for (var i = 0; i < dictionaries.Count; i++)
        {
            if (match(dictionaries[i]))
            {
                dictionaries[i] = replacement;
                return;
            }
        }

        dictionaries.Insert(0, replacement);
    }

    private static void ApplyEffective(string effective)
    {
        effective = effective == Light ? Light : Dark;
        var app = Application.Current;
        if (app is null)
        {
            Current = effective;
            return;
        }

        void ApplyOnUi()
        {
            var source = effective == Light ? LightSource : DarkSource;
            ReplaceMergedDictionary(app.Resources.MergedDictionaries, IsThemeDictionary, source);
            Current = effective;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        if (app.Dispatcher.CheckAccess())
        {
            ApplyOnUi();
        }
        else
        {
            app.Dispatcher.Invoke(ApplyOnUi);
        }
    }

    private static void EnsureSystemWatchLocked()
    {
        if (_watchingSystem)
        {
            return;
        }

        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _watchingSystem = true;
        }
        catch
        {
            // 部分托管环境无法订阅 SystemEvents
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
        {
            return;
        }

        string preference;
        lock (SyncRoot)
        {
            preference = _preference;
        }

        if (preference != System)
        {
            return;
        }

        var app = Application.Current;
        if (app is null)
        {
            ApplyEffective(ResolveSystemTheme());
            return;
        }

        _ = app.Dispatcher.BeginInvoke(() =>
        {
            lock (SyncRoot)
            {
                if (_preference != System)
                {
                    return;
                }
            }

            ApplyEffective(ResolveSystemTheme());
        });
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString ?? string.Empty;
        return source.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)
               || source.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)
               || source.Contains("Themes\\Dark.xaml", StringComparison.OrdinalIgnoreCase)
               || source.Contains("Themes\\Light.xaml", StringComparison.OrdinalIgnoreCase);
    }
}
