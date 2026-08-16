using System.Windows.Input;

namespace GenshinBrowser.Services;

/// <summary>
/// 将 Key + ModifierKeys 格式化为用户可读的快捷键文本（如 "Ctrl + F8"）。
/// </summary>
public static class HotkeyFormatter
{
    public static string Format(Key key, ModifierKeys modifiers)
    {
        if (key == Key.None)
        {
            return LocalizationService.Get("Hotkey.NotSet", "未设置");
        }

        return GetModifiersString(modifiers) + GetKeyName(key);
    }

    public static string GetModifiersString(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0) return string.Empty;
        return string.Join(" + ", parts) + " + ";
    }

    public static string GetKeyName(Key key)
    {
        return key switch
        {
            Key.None => LocalizationService.Get("Hotkey.None", "无"),
            Key.OemPeriod => ".",
            Key.OemComma => ",",
            Key.OemQuestion => "/",
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            // 其余可打印 OEM 键按 US 布局主字符显示（与浏览器/游戏快捷键显示惯例一致；
            // 其它物理布局下该 VK 对应的字符不同，此处为通用表示）
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemTilde => "`",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemBackslash => "\\",
            Key.D0 => "0",
            Key.D1 => "1",
            Key.D2 => "2",
            Key.D3 => "3",
            Key.D4 => "4",
            Key.D5 => "5",
            Key.D6 => "6",
            Key.D7 => "7",
            Key.D8 => "8",
            Key.D9 => "9",
            _ => key.ToString()
        };
    }
}
