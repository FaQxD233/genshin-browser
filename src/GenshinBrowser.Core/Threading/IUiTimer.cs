namespace GenshinBrowser.Threading;

public interface IUiTimer : IDisposable
{
    event EventHandler? Tick;

    TimeSpan Interval { get; set; }

    bool IsRunning { get; }

    void Start();

    void Stop();
}
