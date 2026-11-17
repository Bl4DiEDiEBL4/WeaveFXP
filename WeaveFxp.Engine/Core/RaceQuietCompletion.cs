namespace WeaveFxp.Engine.Core;

// Without an SFV/marker, require several seconds of unchanged, fully covered
// listings. Any failed/stale snapshot or new evidence restarts that window.
internal sealed class RaceQuietCompletion
{
    private readonly TimeSpan _window;
    private DateTime? _since;
    private long _version;

    public RaceQuietCompletion(TimeSpan idleTimeout) => _window = idleTimeout < TimeSpan.FromSeconds(5)
        ? idleTimeout : TimeSpan.FromSeconds(5);

    public bool Observe(bool eligible, long version, DateTime now)
    {
        if (!eligible) { _since = null; return false; }
        if (_since is null || _version != version) { _since = now; _version = version; }
        return now - _since.Value >= _window;
    }
}
