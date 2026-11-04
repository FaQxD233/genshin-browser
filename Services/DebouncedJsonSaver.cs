using System.IO;
using System.Text.Json;

namespace GenshinBrowser.Services;

/// <summary>
/// 数据文件的防抖写盘（收藏、浏览记录、下载记录共用）。调用方改完数据后调 <see cref="MarkChanged"/>：版本号 +1、
/// 当场在 syncRoot 锁内取快照，停手一段时间后原子写盘；写盘过程中又有改动时不会误标为已保存。
/// 快照必须当场取：下载列表是 UI 线程上的 ObservableCollection，到写盘的后台线程上就不能再读了。
/// </summary>
internal sealed class DebouncedJsonSaver<TSnapshot> : IDisposable
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options;
    private readonly int _debounceMs;
    private readonly string _logContext;
    private readonly object _syncRoot;
    private readonly Func<TSnapshot> _createSnapshot;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _debounceCts;
    private int _version;
    private int _savedVersion;
    private bool _writeBlocked;
    private bool _disposed;

    /// <param name="syncRoot">调用方保护数据的锁；版本号也在它里面读写，<paramref name="createSnapshot"/> 在它里面调用。</param>
    public DebouncedJsonSaver(
        string path,
        JsonSerializerOptions options,
        int debounceMs,
        string logContext,
        object syncRoot,
        Func<TSnapshot> createSnapshot)
    {
        _path = path;
        _options = options;
        _debounceMs = debounceMs;
        _logContext = logContext;
        _syncRoot = syncRoot;
        _createSnapshot = createSnapshot;
    }

    /// <summary>不再写盘：读盘失败、原文件又没能改名保留时设置（见 <see cref="DataFileProblem"/>），免得覆盖还在的数据。</summary>
    public void BlockWrites()
    {
        lock (_syncRoot)
        {
            _writeBlocked = true;
        }
    }

    /// <summary>数据有改动：版本号 +1，当场取快照并重新开始防抖计时。</summary>
    public void MarkChanged()
    {
        TSnapshot snapshot;
        int version;
        CancellationToken token;
        lock (_syncRoot)
        {
            // _disposed 检查必须在锁内：否则 Dispose 可能在检查通过后清空 CTS，这里 new 出的 CTS 永远不被取消，稍后向已关闭的服务写盘
            if (_disposed)
            {
                return;
            }

            _version++;
            if (_writeBlocked)
            {
                return;
            }

            version = _version;
            snapshot = _createSnapshot();
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
        }

        _ = SaveDebouncedAsync(snapshot, version, token);
    }

    /// <summary>立即写入尚未落盘的改动。关闭前调用；Dispose 不再写盘。</summary>
    public async Task FlushAsync()
    {
        CancelPendingSave();
        while (true)
        {
            TSnapshot snapshot;
            int version;
            lock (_syncRoot)
            {
                if (_disposed || _writeBlocked || _version == _savedVersion)
                {
                    return;
                }

                version = _version;
                snapshot = _createSnapshot();
            }

            // 回到调用方线程：下一轮还要取快照，下载列表只能在 UI 线程上读
            await SaveAsync(snapshot, version, CancellationToken.None).ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        CancelPendingSave();
        _saveGate.Dispose();
    }

    private void CancelPendingSave()
    {
        lock (_syncRoot)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
    }

    private async Task SaveDebouncedAsync(TSnapshot snapshot, int version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_debounceMs, cancellationToken).ConfigureAwait(false);
            await SaveAsync(snapshot, version, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 被更新的改动或 Flush 取代，预期行为
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ObjectDisposedException)
        {
            // SaveAsync 已记录具体异常；ODE 为 Dispose 与后台写盘的关闭竞态；后台任务不能留下未观察异常。
        }
    }

    private async Task SaveAsync(TSnapshot snapshot, int version, CancellationToken cancellationToken)
    {
        try
        {
            await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Dispose 与在途保存的关闭竞态：保存闸已释放，放弃本次写盘
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                // 已有更新的版本：交给那一次写，这份快照过时了
                if (version != _version || _writeBlocked)
                {
                    return;
                }
            }

            await JsonFileWriter.WriteAtomicAsync(_path, snapshot, _options).ConfigureAwait(false);
            lock (_syncRoot)
            {
                // 仅当写盘期间没有新改动时标记已保存，避免覆盖更新的 dirty 状态
                if (version == _version)
                {
                    _savedVersion = version;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLogger.LogException(ex, _logContext);
            throw;
        }
        finally
        {
            try
            {
                _saveGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // 关闭竞态：Dispose 已先行释放 _saveGate
            }
        }
    }
}
