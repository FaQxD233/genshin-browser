namespace GenshinBrowser.Localization;

public interface ITextResourceService
{
    event EventHandler? LanguageChanged;

    string CurrentLanguage { get; }

    void Apply(string? language);

    string Get(string key, string fallback);

    string Format(string key, string fallback, params object[] args);
}
