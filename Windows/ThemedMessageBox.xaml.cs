using System.Windows;
using GenshinBrowser.Services;

namespace GenshinBrowser.Windows;

public partial class ThemedMessageBox : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;

    public ThemedMessageBox()
    {
        InitializeComponent();

        SourceInitialized += ThemedMessageBox_OnSourceInitialized;
        ThemeService.ThemeChanged += ThemeService_OnThemeChanged;

        YesButton.Click += (_, _) =>
        {
            _result = MessageBoxResult.Yes;
            Close();
        };

        NoButton.Click += (_, _) =>
        {
            _result = MessageBoxResult.No;
            Close();
        };

        Closing += (_, _) =>
        {
            // 关闭按钮（X）等其它关闭途径视为「否」，避免回退到 None
            if (_result == MessageBoxResult.None)
            {
                _result = MessageBoxResult.No;
            }
        };
    }

    private void ThemedMessageBox_OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyNativeTitleBarTheme();
    }

    private void ThemeService_OnThemeChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(ApplyNativeTitleBarTheme);
            return;
        }

        ApplyNativeTitleBarTheme();
    }

    private void ApplyNativeTitleBarTheme()
    {
        NativeTitleBarService.Apply(
            this,
            string.Equals(ThemeService.Current, ThemeService.Dark, StringComparison.OrdinalIgnoreCase));
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeService.ThemeChanged -= ThemeService_OnThemeChanged;
        SourceInitialized -= ThemedMessageBox_OnSourceInitialized;
        base.OnClosed(e);
    }

    /// <summary>
    /// 显示一个与应用深色主题一致的「是/否」确认框，并阻塞直到用户选择。
    /// </summary>
    public static MessageBoxResult ShowYesNo(Window owner, string message, string title)
    {
        var box = new ThemedMessageBox
        {
            Owner = owner,
        };
        box.TitleText.Text = title;
        box.MessageText.Text = message;
        box.ShowDialog();
        return box._result;
    }
}
