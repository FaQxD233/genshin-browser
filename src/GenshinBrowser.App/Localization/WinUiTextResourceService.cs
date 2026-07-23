using GenshinBrowser.Constants;
using Microsoft.UI.Xaml;
using System.IO;
using System.Xml.Linq;

namespace GenshinBrowser.Localization;

internal sealed class WinUiTextResourceService : ITextResourceService
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly IReadOnlyDictionary<string, string> chinese;
    private readonly IReadOnlyDictionary<string, string> english;

    public WinUiTextResourceService()
    {
        chinese = LoadDictionary("Strings.zh-CN.xaml");
        english = LoadDictionary("Strings.en-US.xaml");
    }

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage { get; private set; } = UiPreferences.Languages.Chinese;

    public void Apply(string? language)
    {
        string normalized = string.Equals(language, UiPreferences.Languages.English, StringComparison.OrdinalIgnoreCase)
            ? UiPreferences.Languages.English
            : UiPreferences.Languages.Chinese;

        if (string.Equals(CurrentLanguage, normalized, StringComparison.Ordinal))
        {
            return;
        }

        CurrentLanguage = normalized;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Get(string key, string fallback)
    {
        IReadOnlyDictionary<string, string> selected = CurrentLanguage == UiPreferences.Languages.English
            ? english
            : chinese;
        if (selected.TryGetValue(key, out string? text) && !string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (Application.Current?.Resources.TryGetValue(key, out object value) == true &&
            value is string resourceText &&
            !string.IsNullOrEmpty(resourceText))
        {
            return resourceText;
        }

        return fallback;
    }

    public string Format(string key, string fallback, params object[] args)
    {
        string template = Get(key, fallback);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static IReadOnlyDictionary<string, string> LoadDictionary(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Resources", "i18n", fileName);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        XDocument document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (XElement element in document.Root?.Elements() ?? [])
        {
            string? key = element.Attribute(XamlNamespace + "Key")?.Value;
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = element.Value;
            }
        }

        return result;
    }
}
