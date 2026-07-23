namespace GenshinBrowser.Models;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1 << 0,
    Control = 1 << 1,
    Shift = 1 << 2,
    Windows = 1 << 3,
}
