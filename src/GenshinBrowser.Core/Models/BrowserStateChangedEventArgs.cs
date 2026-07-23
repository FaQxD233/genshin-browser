namespace GenshinBrowser.Models;

public sealed class BrowserStateChangedEventArgs : EventArgs
{
    public BrowserStateChangedEventArgs(BrowserStateChangeKind kind)
    {
        Kind = kind;
    }

    public BrowserStateChangeKind Kind { get; }
}
