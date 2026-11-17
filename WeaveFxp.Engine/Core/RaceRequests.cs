using System.Text.Json;
using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Core;

public sealed partial class WeaveEngine
{
    private readonly object _raceAnnounceLock = new();

    private static string RaceRequestKey(TransferRequest req)
    {
        var mesh = req.MeshSites.Count > 1;
        var names = mesh ? req.MeshSites : new List<string> { req.FromSite, req.ToSite };
        string PathOn(string site)
        {
            var fallback = site.Equals(req.FromSite, StringComparison.OrdinalIgnoreCase) ? req.SourcePath : req.DestPath;
            if (req.SitePaths.TryGetValue(site, out var path) && !string.IsNullOrWhiteSpace(path)) fallback = path;
            return string.IsNullOrWhiteSpace(fallback) ? "/" : fallback.Trim().TrimEnd('/') is { Length: > 0 } clean ? clean : "/";
        }
        return JsonSerializer.Serialize(new
        {
            mesh,
            direction = mesh ? "" : req.FromSite.Trim().ToUpperInvariant() + ">" + req.ToSite.Trim().ToUpperInvariant(),
            paths = names.Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => new { site = n.ToUpperInvariant(), path = PathOn(n) }),
            sourceOnly = (req.DownloadOnlySites ?? new()).Select(n => n.Trim().ToUpperInvariant()).Distinct().OrderBy(n => n),
            req.DryRun,
        });
    }

    private Job? FindActiveApiRace(TransferRequest request)
    {
        var key = RaceRequestKey(request);
        return _store.ActiveApiRaceHeaders().FirstOrDefault(job => RaceRequestKey(job.Request) == key);
    }

    private static TransferRequest SpreadRaceIdentity(SpreadRequest req)
    {
        var from = (req.FromSite ?? "").Trim();
        var names = new[] { from }.Concat(req.ToSites ?? new()).Select(n => n.Trim()).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new TransferRequest { FromSite = from, ToSite = "mesh", MeshSites = names,
            SourcePath = req.SourcePath ?? "", DestPath = string.IsNullOrWhiteSpace(req.DestPath) ? req.SourcePath ?? "" : req.DestPath,
            SitePaths = req.SitePaths ?? new(), DownloadOnlySites = req.DownloadOnlySites ?? new(), DryRun = req.DryRun, Race = true };
    }
}
