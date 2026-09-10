namespace WeaveFxp.Engine.Core;

internal sealed class ManualTransferQueue
{
    internal sealed record Limit(string Key, int Capacity);

    private sealed class Entry
    {
        public required string Id;
        public required Limit[] Limits;
        public required CancellationToken Token;
        public readonly TaskCompletionSource<IDisposable> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private readonly object _sync = new();
    private readonly List<Entry> _pending = new();
    private readonly Dictionary<string, int> _active = new(StringComparer.OrdinalIgnoreCase);
    private int _suspended;

    // Enqueue synchronously, before Task.Run, so thread-pool timing cannot reorder jobs.
    public Task<IDisposable> Enqueue(string id, CancellationToken token, params Limit[] limits)
    {
        var entry = new Entry { Id = id, Token = token, Limits = limits };
        lock (_sync)
        {
            _pending.Add(entry);
            Dispatch();
        }
        return WaitAsync(entry);
    }

    private async Task<IDisposable> WaitAsync(Entry entry)
    {
        using var cancellation = entry.Token.Register(() =>
        {
            lock (_sync)
            {
                if (_pending.Remove(entry)) entry.Ready.TrySetCanceled(entry.Token);
                Dispatch();
            }
        });
        return await entry.Ready.Task.ConfigureAwait(false);
    }

    public Dictionary<string, int> Positions()
    {
        lock (_sync)
            return _pending.Select((entry, index) => (entry.Id, index))
                .ToDictionary(x => x.Id, x => x.index, StringComparer.OrdinalIgnoreCase);
    }

    public bool CanMove(string id, int direction)
    {
        lock (_sync) return MoveIndex(id, direction) >= 0;
    }

    public bool Move(string id, int direction)
    {
        lock (_sync)
        {
            var index = MoveIndex(id, direction);
            if (index < 0) return false;
            (_pending[index], _pending[index + direction]) = (_pending[index + direction], _pending[index]);
            return true;
        }
    }

    private int MoveIndex(string id, int direction)
    {
        if (direction is not (-1 or 1)) return -1;
        var index = _pending.FindIndex(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + direction >= 0 && index + direction < _pending.Count ? index : -1;
    }

    // Bulk cancellation must not dispatch the next queued job between removals.
    public IDisposable SuspendDispatch()
    {
        lock (_sync) _suspended++;
        return new Lease(() =>
        {
            lock (_sync)
            {
                _suspended--;
                Dispatch();
            }
        });
    }

    private void Dispatch()
    {
        if (_suspended > 0) return;
        for (var i = 0; i < _pending.Count;)
        {
            var entry = _pending[i];
            if (entry.Token.IsCancellationRequested)
            {
                _pending.RemoveAt(i);
                entry.Ready.TrySetCanceled(entry.Token);
                continue;
            }
            if (entry.Limits.Any(limit => _active.GetValueOrDefault(limit.Key) >= Math.Max(1, limit.Capacity)))
            {
                i++;
                continue;
            }
            _pending.RemoveAt(i);
            foreach (var limit in entry.Limits) _active[limit.Key] = _active.GetValueOrDefault(limit.Key) + 1;
            entry.Ready.SetResult(new Lease(() =>
            {
                lock (_sync)
                {
                    foreach (var limit in entry.Limits)
                    {
                        if (--_active[limit.Key] == 0) _active.Remove(limit.Key);
                    }
                    Dispatch();
                }
            }));
        }
    }
}
