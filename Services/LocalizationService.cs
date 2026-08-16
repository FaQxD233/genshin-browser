using System.Windows;
using Application = System.Windows.Application;

namespace GenshinBrowser.Services;

/// <summary>
/// 运行时切换界面语言：替换 App 资源中的字符串字典。
/// XAML 文案使用 DynamicResource；代码通过 Get 读取。
/// </summary>
public static class LocalizationService
{
    public const string ZhCn = "zh-CN";
    public const string EnUs = "en-US";

    private const string ZhSource = "Resources/i18n/Strings.zh-CN.xaml";
    private const string EnSource = "Resources/i18n/Strings.en-US.xaml";

    public static string Current { get; private set; } = ZhCn;

    public static event EventHandler? LanguageChanged;

    public static void Apply(string? language)
    {
        var lang = Normalize(language);
        var app = Application.Current;
        if (app is null)
        {
            Current = lang;
            return;
        }

        // 合并字典属 DispatcherObject，必须封送到 UI 线程替换（与 ThemeService.ApplyEffective 一致）
        void ApplyOnUi()
        {
            // 已是该语言时跳过：省一次字符串字典 BAML 加载与全应用 DynamicResource 重算
            //（App.xaml 默认合并 zh-CN 字典，中文用户的常见启动路径）
            if (string.Equals(Current, lang, StringComparison.Ordinal))
            {
                return;
            }

            var source = lang == EnUs ? EnSource : ZhSource;
            ThemeService.ReplaceMergedDictionary(app.Resources.MergedDictionaries, IsStringDictionary, source);
            Current = lang;
            LanguageChanged?.Invoke(null, EventArgs.Empty);
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

    public static string Normalize(string? language)
    {
        return string.Equals(language, EnUs, StringComparison.OrdinalIgnoreCase) ? EnUs : ZhCn;
    }

    public static string Get(string key, string? fallback = null)
    {
        var app = Application.Current;
        if (app?.TryFindResource(key) is string value && !string.IsNullOrEmpty(value))
        {
            return value;
        }

        return fallback ?? key;
    }

    public static string Format(string key, params object[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static bool IsStringDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString ?? string.Empty;
        return source.Contains("i18n/Strings.", StringComparison.OrdinalIgnoreCase)
               || source.Contains("i18n\\Strings.", StringComparison.OrdinalIgnoreCase);
    }
}
