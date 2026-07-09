using System.Windows;

namespace GenshinBrowser.Windows;

public partial class ThemedMessageBox : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;

    public ThemedMessageBox()
    {
        InitializeComponent();

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
