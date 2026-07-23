namespace GenshinBrowser.Threading;

public interface IUiDispatcher
{
    bool HasThreadAccess { get; }

    bool TryEnqueue(Action callback);

    Task InvokeAsync(Func<Task> callback);
}
