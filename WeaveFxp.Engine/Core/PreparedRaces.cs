using System.Collections.Concurrent;
using System.Text.Json;
using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Core;

public sealed class PreparedSpread
{
    public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool WarmupCompleted { get; set; }
    public SpreadRequest Request { get; set; } = new();
}

public sealed partial class WeaveEngine
{
    private readonly ConcurrentDictionary<string, PreparedSpread> _preparedSpreads = new(StringComparer.OrdinalIgnoreCase);
    private static T CopyPrepared<T>(T item) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(item))!;

    public List<PreparedSpread> PreparedSpreads()
    {
        ExpirePreparedSpreads();
        return _preparedSpreads.Values.OrderByDescending(p => p.CreatedAt).Select(CopyPrepared).ToList();
    }

    private void ExpirePreparedSpreads()
    {
        var now = DateTime.UtcNow;
        foreach (var item in _preparedSpreads)
            if (item.Value.ExpiresAt <= now) _preparedSpreads.TryRemove(item.Key, out _);
    }

    // As in cbftp SPREAD_PREPARE: warm the allowed sites, but do not create a job,
    // inspect/make a release directory, reserve transfer slots, or issue RETR/STOR.
    public PreparedSpread PrepareSpread(SpreadRequest request, int ttlSeconds = 300)
    {
        ArgumentNullException.ThrowIfNull(request);
        var req = CopyPrepared(request);
        req.Race = true;
        req.FromSite = (req.FromSite ?? "").Trim();
        req.ToSites = (req.ToSites ?? new()).Select(n => (n ?? "").Trim())
            .Where(n => n.Length > 0 && !n.Equals(req.FromSite, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        req.SourcePath = (req.SourcePath ?? "").Trim();
        req.DestPath = string.IsNullOrWhiteSpace(req.DestPath) ? req.SourcePath : req.DestPath.Trim();
        req.SitePaths ??= new();
        req.DownloadOnlySites ??= new();
        if (req.FromSite.Length == 0 || req.ToSites.Count == 0 || req.SourcePath.Length == 0)
            throw new ArgumentException("prepare requires a source, target sites and source path");
        var names = new[] { req.FromSite }.Concat(req.ToSites).ToList();
        var sites = names.Select(n => _store.Site(n) ?? throw new ArgumentException($"site '{n}' not found")).ToList();
        if (!sites.Any(src => sites.Any(dst => !src.Name.Equals(dst.Name, StringComparison.OrdinalIgnoreCase) &&
            !req.DownloadOnlySites.Contains(dst.Name, StringComparer.OrdinalIgnoreCase) && TransferAllowed(src, dst))))
            throw new InvalidOperationException("prepared spread has no eligible site routes");
        ExpirePreparedSpreads();
        if (_preparedSpreads.Count >= 128) throw new InvalidOperationException("too many prepared spreads");
        var now = DateTime.UtcNow;
        var item = new PreparedSpread { Id = "prepare-" + Guid.NewGuid().ToString("N"), CreatedAt = now,
            ExpiresAt = now.AddSeconds(Math.Clamp(ttlSeconds, 1, 3600)), Request = req };
        _preparedSpreads[item.Id] = item;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(sites.Where(s => sites.Any(other => TransferAllowed(s, other) || TransferAllowed(other, s)))
                    .Select(site => WarmPreparedSiteAsync(item, site.Name))).ConfigureAwait(false);
                item.WarmupCompleted = true;
            }
            catch (Exception ex) { Log("system", "prepare", "warn", "connection warmup failed: " + ex.Message); }
        });
        Log("transfer", "prepare", "info", $"prepared {item.Id}: {req.Label} (expires {item.ExpiresAt:O})");
        return CopyPrepared(item);
    }

    private async Task WarmPreparedSiteAsync(PreparedSpread prepared, string name)
    {
        await _prewarmGate.WaitAsync().ConfigureAwait(false);
        SitePool? pool = null;
        try
        {
            if (prepared.ExpiresAt <= DateTime.UtcNow || !_preparedSpreads.ContainsKey(prepared.Id)) return;
            var site = _store.Site(name);
            if (site is null) return;
            pool = AcquirePool(name, site, FtpConfig(site, name, false));
            await pool.WarmUpAsync(Math.Min(2, Math.Max(1, pool.Max - 1)), CancellationToken.None).ConfigureAwait(false);
        }
        finally { if (pool is not null) ReleasePool(name); _prewarmGate.Release(); }
    }

    public SpreadResult StartPreparedSpread(string id)
    {
        if (!_preparedSpreads.TryRemove(id, out var item)) throw new InvalidOperationException("prepared spread not found or already started");
        if (item.ExpiresAt <= DateTime.UtcNow) throw new InvalidOperationException("prepared spread has expired");
        // StartSpread rechecks the current route policy; preparing never grants a route.
        return StartSpread(item.Request);
    }

    public bool CancelPreparedSpread(string id) => _preparedSpreads.TryRemove(id, out _);
}
