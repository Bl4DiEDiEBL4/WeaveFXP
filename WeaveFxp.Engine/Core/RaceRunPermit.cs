namespace WeaveFxp.Engine.Core;

public sealed partial class WeaveEngine
{
    // Idle retry delays and read-only completion checks must not occupy a worker slot.
    // Re-enter through the recovery lane before another pass starts FTP workers.
    private sealed class RaceRunPermit(IDisposable initial, Func<Task<IDisposable>> acquire) : IDisposable
    {
        private IDisposable? _lease = initial;
        public async Task AcquireAsync()
        {
            if (_lease is null) _lease = await acquire().ConfigureAwait(false);
        }
        public void Release() => Interlocked.Exchange(ref _lease, null)?.Dispose();
        public void Dispose() => Release();
    }
}
