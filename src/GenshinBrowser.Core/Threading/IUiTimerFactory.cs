namespace GenshinBrowser.Threading;

public interface IUiTimerFactory
{
    IUiTimer Create(TimeSpan interval);
}
