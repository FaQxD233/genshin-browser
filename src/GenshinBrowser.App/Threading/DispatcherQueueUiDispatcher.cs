using GenshinBrowser.Threading;
using Microsoft.UI.Dispatching;

namespace GenshinBrowser.Threading;

internal sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue dispatcherQueue;

    public DispatcherQueueUiDispatcher(DispatcherQueue dispatcherQueue)
    {
        this.dispatcherQueue = dispatcherQueue;
    }

    public bool HasThreadAccess => dispatcherQueue.HasThreadAccess;

    public bool TryEnqueue(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return dispatcherQueue.TryEnqueue(() => callback());
    }

    public Task InvokeAsync(Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (dispatcherQueue.HasThreadAccess)
        {
            return callback();
        }

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await callback().ConfigureAwait(true);
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
        {
            completion.SetException(new InvalidOperationException("The UI dispatcher is shutting down."));
        }

        return completion.Task;
    }
}
