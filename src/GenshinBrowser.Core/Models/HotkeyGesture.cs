namespace GenshinBrowser.Models;

public readonly record struct HotkeyGesture(int VirtualKey, HotkeyModifiers Modifiers)
{
    public bool IsValid => VirtualKey is > 0 and <= 0xFE;
}

public static class VirtualKeyCodes
{
    public const int K = 0x4B;
    public const int F8 = 0x77;
    public const int F9 = 0x78;
}
