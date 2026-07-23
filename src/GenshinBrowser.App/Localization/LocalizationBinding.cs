using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GenshinBrowser.Localization;

public enum LocalizationTarget
{
    Automatic,
    Text,
    Content,
    Placeholder,
    Header,
    ToolTip,
    AutomationName,
}

public static class LocalizationBinding
{
    private static readonly List<WeakReference<DependencyObject>> RegisteredObjects = [];
    private static ITextResourceService? provider;

    private static readonly DependencyProperty IsLoadedHookedProperty = DependencyProperty.RegisterAttached(
        "IsLoadedHooked",
        typeof(bool),
        typeof(LocalizationBinding),
        new PropertyMetadata(false));

    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key",
        typeof(string),
        typeof(LocalizationBinding),
        new PropertyMetadata(string.Empty, OnBindingChanged));

    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target",
        typeof(LocalizationTarget),
        typeof(LocalizationBinding),
        new PropertyMetadata(LocalizationTarget.Automatic, OnBindingChanged));

    public static readonly DependencyProperty ToolTipKeyProperty = DependencyProperty.RegisterAttached(
        "ToolTipKey",
        typeof(string),
        typeof(LocalizationBinding),
        new PropertyMetadata(string.Empty, OnBindingChanged));

    public static ITextResourceService? Provider
    {
        get => provider;
        set
        {
            if (ReferenceEquals(provider, value))
            {
                return;
            }

            if (provider is not null)
            {
                provider.LanguageChanged -= ProviderOnLanguageChanged;
            }

            provider = value;
            if (provider is not null)
            {
                provider.LanguageChanged += ProviderOnLanguageChanged;
            }
            RefreshAll();
        }
    }

    public static string GetKey(DependencyObject obj) => (string)obj.GetValue(KeyProperty);

    public static void SetKey(DependencyObject obj, string value) => obj.SetValue(KeyProperty, value);

    public static LocalizationTarget GetTarget(DependencyObject obj) =>
        (LocalizationTarget)obj.GetValue(TargetProperty);

    public static void SetTarget(DependencyObject obj, LocalizationTarget value) => obj.SetValue(TargetProperty, value);

    public static string GetToolTipKey(DependencyObject obj) => (string)obj.GetValue(ToolTipKeyProperty);

    public static void SetToolTipKey(DependencyObject obj, string value) => obj.SetValue(ToolTipKeyProperty, value);

    private static void OnBindingChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (!RegisteredObjects.Any(reference => reference.TryGetTarget(out DependencyObject? target) && ReferenceEquals(target, dependencyObject)))
        {
            RegisteredObjects.Add(new WeakReference<DependencyObject>(dependencyObject));
        }

        if (dependencyObject is FrameworkElement element &&
            !(bool)element.GetValue(IsLoadedHookedProperty))
        {
            element.SetValue(IsLoadedHookedProperty, true);
            element.Loaded += ElementOnLoaded;
        }
        Apply(dependencyObject);

        // XAML can assign Key/Target before Content, Header or Text. Re-apply after
        // the current parser turn so the localized value remains the final value.
        dependencyObject.DispatcherQueue?.TryEnqueue(() => Apply(dependencyObject));
    }

    private static void ElementOnLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is DependencyObject target)
        {
            Apply(target);
        }
    }

    private static void ProviderOnLanguageChanged(object? sender, EventArgs e) => RefreshAll();

    private static void RefreshAll()
    {
        for (int index = RegisteredObjects.Count - 1; index >= 0; index--)
        {
            if (RegisteredObjects[index].TryGetTarget(out DependencyObject? target))
            {
                Apply(target);
            }
            else
            {
                RegisteredObjects.RemoveAt(index);
            }
        }
    }

    private static void Apply(DependencyObject target)
    {
        if (provider is null)
        {
            return;
        }

        string key = GetKey(target);
        if (!string.IsNullOrWhiteSpace(key))
        {
            LocalizationTarget destination = GetTarget(target);
            if (destination == LocalizationTarget.Automatic)
            {
                destination = target switch
                {
                    TextBlock => LocalizationTarget.Text,
                    TextBox => LocalizationTarget.Placeholder,
                    MenuFlyoutItem => LocalizationTarget.Text,
                    ContentControl => LocalizationTarget.Content,
                    _ => LocalizationTarget.AutomationName,
                };
            }

            string fallback = GetCurrentValue(target, destination);
            string value = provider.Get(key, fallback);
            ApplyValue(target, destination, value);
        }

        string toolTipKey = GetToolTipKey(target);
        if (!string.IsNullOrWhiteSpace(toolTipKey))
        {
            string fallback = ToolTipService.GetToolTip(target) as string ?? string.Empty;
            string value = provider.Get(toolTipKey, fallback);
            ToolTipService.SetToolTip(target, value);
            AutomationProperties.SetName(target, value);
        }
    }

    private static void ApplyValue(DependencyObject target, LocalizationTarget destination, string value)
    {
        switch (destination)
        {
            case LocalizationTarget.Text when target is TextBlock textBlock:
                textBlock.Text = value;
                break;
            case LocalizationTarget.Text when target is MenuFlyoutItem menuItem:
                menuItem.Text = value;
                break;
            case LocalizationTarget.Content when target is ContentControl contentControl:
                contentControl.Content = value;
                AutomationProperties.SetName(contentControl, value);
                break;
            case LocalizationTarget.Placeholder when target is TextBox textBox:
                textBox.PlaceholderText = value;
                AutomationProperties.SetName(textBox, value);
                break;
            case LocalizationTarget.Header when target is TextBox headerTextBox:
                headerTextBox.Header = value;
                AutomationProperties.SetName(headerTextBox, value);
                break;
            case LocalizationTarget.ToolTip:
                ToolTipService.SetToolTip(target, value);
                AutomationProperties.SetName(target, value);
                break;
            case LocalizationTarget.AutomationName:
                AutomationProperties.SetName(target, value);
                break;
        }
    }

    private static string GetCurrentValue(DependencyObject target, LocalizationTarget destination)
    {
        return destination switch
        {
            LocalizationTarget.Text when target is TextBlock textBlock => textBlock.Text,
            LocalizationTarget.Text when target is MenuFlyoutItem menuItem => menuItem.Text,
            LocalizationTarget.Content when target is ContentControl { Content: string content } => content,
            LocalizationTarget.Placeholder when target is TextBox textBox => textBox.PlaceholderText,
            LocalizationTarget.Header when target is TextBox { Header: string header } => header,
            LocalizationTarget.ToolTip when ToolTipService.GetToolTip(target) is string toolTip => toolTip,
            _ => AutomationProperties.GetName(target) ?? string.Empty,
        };
    }
}
