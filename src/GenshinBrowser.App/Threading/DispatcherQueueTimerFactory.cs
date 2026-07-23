using Microsoft.UI.Dispatching;

namespace GenshinBrowser.Threading;

internal sealed class DispatcherQueueTimerFactory : IUiTimerFactory
{
    private readonly DispatcherQueue dispatcherQueue;

    public DispatcherQueueTimerFactory(DispatcherQueue dispatcherQueue)
    {
        this.dispatcherQueue = dispatcherQueue;
    }

    public IUiTimer Create(TimeSpan interval)
    {
        DispatcherQueueTimer timer = dispatcherQueue.CreateTimer();
        timer.Interval = interval;
        return new DispatcherQueueTimerAdapter(timer);
    }

    private sealed class DispatcherQueueTimerAdapter : IUiTimer
    {
        private readonly DispatcherQueueTimer timer;
        private bool disposed;

        public DispatcherQueueTimerAdapter(DispatcherQueueTimer timer)
        {
            this.timer = timer;
            this.timer.Tick += OnTick;
        }

        public event EventHandler? Tick;

        public TimeSpan Interval
        {
            get => timer.Interval;
            set => timer.Interval = value;
        }

        public bool IsRunning => timer.IsRunning;

        public void Start() => timer.Start();

        public void Stop() => timer.Stop();

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            timer.Stop();
            timer.Tick -= OnTick;
        }

        private void OnTick(DispatcherQueueTimer sender, object args)
        {
            Tick?.Invoke(this, EventArgs.Empty);
        }
    }
}
