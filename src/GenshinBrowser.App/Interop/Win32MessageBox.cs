using System.Runtime.InteropServices;

namespace GenshinBrowser.Interop;

internal static class Win32MessageBox
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;

    public static void ShowError(string message, string title = "Genshin Browser", nint ownerWindow = 0)
    {
        MessageBox(ownerWindow, message, title, MbOk | MbIconError);
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint hwnd, string text, string caption, uint type);
}
