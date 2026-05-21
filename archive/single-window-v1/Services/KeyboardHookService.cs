using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GenshinBrowser.Services;

public sealed class KeyboardHookService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int VkK = 0x4B;
    private const int VkF7 = 0x76;
    private const int VkF8 = 0x77;

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _isDisposed;

    public KeyboardHookService()
    {
        _proc = HookCallback;
    }

    public event EventHandler? KPressed;

    public event EventHandler? TemporaryUiRevealPressed;

    public event EventHandler? ModeTogglePressed;

    public void Start()
    {
        if (_hookId != IntPtr.Zero)
        {
            return;
        }

        _hookId = SetHook(_proc);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        return SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(currentModule?.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WmKeyDown)
        {
            var vkCode = Marshal.ReadInt32(lParam);
            if (vkCode == VkK)
            {
                KPressed?.Invoke(this, EventArgs.Empty);
            }
            else if (vkCode == VkF7)
            {
                TemporaryUiRevealPressed?.Invoke(this, EventArgs.Empty);
            }
            else if (vkCode == VkF8)
            {
                ModeTogglePressed?.Invoke(this, EventArgs.Empty);
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
