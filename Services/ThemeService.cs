using System.Windows;
using Application = System.Windows.Application;

namespace GenshinBrowser.Services;

/// <summary>
/// 运行时切换亮/暗主题：替换 App 资源中的颜色字典。
/// 样式与窗口中的画刷引用应使用 DynamicResource。
/// </summary>
public static class ThemeService
{
    public const string Dark = "Dark";
    public const string Light = "Light";

    private const string DarkSource = "Resources/Themes/Dark.xaml";
    private const string LightSource = "Resources/Themes/Light.xaml";

    public static string Current { get; private set; } = Dark;

    public static event EventHandler? ThemeChanged;

    public static void Apply(string? themeMode)
    {
        var mode = Normalize(themeMode);
        var app = Application.Current;
        if (app is null)
        {
            Current = mode;
            return;
        }

        var source = mode == Light ? LightSource : DarkSource;
        ReplaceMergedDictionary(app.Resources.MergedDictionaries, IsThemeDictionary, source);
        Current = mode;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Normalize(string? themeMode)
    {
        return string.Equals(themeMode, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
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

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString ?? string.Empty;
        return source.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)
               || source.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)
               || source.Contains("Themes\\Dark.xaml", StringComparison.OrdinalIgnoreCase)
               || source.Contains("Themes\\Light.xaml", StringComparison.OrdinalIgnoreCase);
    }
}
