using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using WeaveFxp.Web.Services;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;
using WeaveFxp.Web.Components;

var appDir = AppContext.BaseDirectory;
var engine = new WeaveEngine();
var startupSettings = engine.Settings(false);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = appDir,
    WebRootPath = ResolveWebRoot(appDir)
});

// The engine addresses its state relative to the executable (data/state.json).
builder.Services.AddSingleton(engine);
builder.Services.AddSingleton<TrayNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrayNotificationService>());
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(engine.DataDir, "keys")));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// WebUI and JSON API can listen separately. WEAVEFXP_URL remains a full manual
// override for unusual launches.
var listen = BuildListenConfig(startupSettings);
var manualUrl = Environment.GetEnvironmentVariable("WEAVEFXP_URL");
var url = string.IsNullOrWhiteSpace(manualUrl)
    ? string.Join(';', listen.Urls)
    : manualUrl.Trim();
if (string.IsNullOrWhiteSpace(manualUrl))
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(ListenAddress(startupSettings.WebBindAddress), startupSettings.WebPort);
        if (startupSettings.EnableHttpsJsonApi)
            options.Listen(ListenAddress(ApiHostForListen(startupSettings)), startupSettings.HttpsJsonApiPort);
    });
}
else
{
    builder.WebHost.UseUrls(url);
}

var app = builder.Build();
var appCss = ReadEmbeddedText("WeaveFxp.Web.wwwroot.app.css");
var favicon = ReadEmbeddedBytes("WeaveFxp.Web.wwwroot.favicon.ico");
var icon192 = ReadEmbeddedBytes("WeaveFxp.Web.wwwroot.icon-192.png");
var icon512 = ReadEmbeddedBytes("WeaveFxp.Web.wwwroot.icon-512.png");
var logoSvg = ReadEmbeddedText("WeaveFxp.Web.wwwroot.weavefxp-logo.svg");

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/app.css", (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-store, max-age=0";
    return Results.Content(appCss, "text/css");
});
app.MapGet("/favicon.ico", () => Results.File(favicon, "image/x-icon"));
app.MapGet("/icon-192.png", () => Results.File(icon192, "image/png"));
app.MapGet("/icon-512.png", () => Results.File(icon512, "image/png"));
app.MapGet("/weavefxp-logo.svg", () => Results.Content(logoSvg, "image/svg+xml"));

app.Use(async (ctx, next) =>
{
    var isApi = ctx.Request.Path.StartsWithSegments("/api");
    var isWeaveFxpApi = IsWeaveFxpApiPath(ctx.Request.Path);
    var localPort = ctx.Connection.LocalPort;
    var apiOnlyPort = listen.ApiPorts.Contains(localPort) && !listen.WebPorts.Contains(localPort);
    var shouldAuthorizeApi = isApi || (apiOnlyPort && isWeaveFxpApi);

    if (apiOnlyPort && !isApi && !isWeaveFxpApi)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (shouldAuthorizeApi)
    {
        var currentApiSettings = engine.Settings(false);
        if (!currentApiSettings.EnableHttpsJsonApi)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        engine.Log("api", "", "info", $"{ctx.Request.Method} {ctx.Request.Path}{ctx.Request.QueryString} from {ctx.Connection.RemoteIpAddress}");

        if (!ApiAuthorized(ctx, currentApiSettings.ApiPassword))
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("API password required");
            return;
        }
    }

    if (apiOnlyPort && isWeaveFxpApi && await TryHandleWeaveFxpApiAsync(ctx, engine))
        return;

    await next();
});

var api = app.MapGroup("/api");
api.MapGet("/health", () => new
{
    ok = true,
    version = engine.Version,
    state_path = engine.StatePath,
    data_dir = engine.DataDir,
    load_warning = engine.LoadWarning,
    jobs = engine.Jobs().Count,
    releases = engine.Releases().Count,
});
api.MapGet("/settings", () => engine.Settings(true));
api.MapPut("/settings", (AppSettings settings) => engine.UpdateSettings(settings).Public());
api.MapGet("/sites", () => engine.Sites(true));
api.MapPost("/sites", (Site site) => engine.AddSite(site).Public());
api.MapPut("/sites/{name}", (string name, Site site) => engine.SaveSite(name, site).Public());
api.MapDelete("/sites/{name}", (string name) =>
{
    engine.RemoveSite(name);
    return Results.NoContent();
});
api.MapGet("/jobs", () => engine.Jobs());
api.MapDelete("/jobs/{id}", (string id) => engine.RemoveJob(id) ? Results.Ok(new { removed = true, id }) : Results.NotFound());
api.MapPost("/jobs/{id}/cancel", (string id) =>
{
    return engine.CancelJob(id)
        ? Results.Ok(new { id, cancelled = true })
        : Results.NotFound();
});
api.MapPost("/jobs/{id}/retry", (string id) =>
{
    return engine.RetryJob(id)
        ? Results.Ok(new { id, retried = true })
        : Results.NotFound();
});
api.MapPost("/jobs/{id}/restart", (string id) =>
{
    return engine.RestartJob(id)
        ? Results.Ok(new { id, restarted = true })
        : Results.NotFound();
});
api.MapDelete("/jobs", () => Results.Ok(new { cleared = engine.ClearJobs() }));
api.MapGet("/releases", () => engine.Releases());
api.MapDelete("/releases", () => Results.Ok(new { cleared = engine.ClearReleases() }));
api.MapGet("/logs", (long? after, int? limit, string? category, string? level) =>
{
    if (after is not null)
    {
        var (entries, seq) = engine.Logs(after.Value, limit ?? 500);
        return Results.Ok(new { seq, entries });
    }
    return Results.Ok(engine.RecentLogs(limit ?? 500, category ?? "", level ?? ""));
});
api.MapDelete("/logs", () => Results.Ok(new { cleared = engine.ClearLogs() }));
api.MapDelete("/dupes", () => Results.Ok(new { cleared = engine.ClearDupes() }));
api.MapDelete("/runtime-data", () => Results.Ok(engine.ClearRuntimeData()));
api.MapPost("/fxp", (TransferRequest request) => { request.ViaApi = true; return engine.StartFxp(request); });
api.MapPost("/spread", (SpreadRequest request) => { request.ViaApi = true; return engine.StartSpread(request); });
api.MapPost("/download", (DownloadRequest request) => { request.ViaApi = true; return engine.StartDownload(request); });
api.MapPost("/dupe", async (DupeRequest request) =>
    await engine.CheckDupeAsync(request.Site, request.Path, request.Name));
api.MapPost("/release-check", async (ReleaseCheckRequest request) =>
    await engine.CheckReleaseAsync(request.Site, request.Path));
api.MapPost("/raw-command", async (RawCommandRequest request) =>
    await engine.SendRawCommandAsync(request.Site, request.Command));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Warm the engine and print where state lives.
Console.WriteLine($"WeaveFXP WebUI listening on {listen.WebUrl}");
if (listen.ApiUrl is not null)
    Console.WriteLine($"WeaveFXP JSON API listening on {listen.ApiUrl}");
Console.WriteLine($"WeaveFXP effective listeners: {url}");
Console.WriteLine($"State file: {engine.StatePath}");
Console.WriteLine($"Data folder: {engine.DataDir}");
if (!string.IsNullOrWhiteSpace(engine.LoadWarning))
    Console.WriteLine(engine.LoadWarning);

// Auto-open the dashboard on start (skip with --no-browser).
if (!args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase))
    OpenBrowser(BrowserUrl(listen.WebUrl));

app.Run();

static ListenConfig BuildListenConfig(AppSettings settings)
{
    var webUrl = FormatListenUrl("http", settings.WebBindAddress, settings.WebPort);
    var urls = new List<string> { webUrl };
    var webPorts = new HashSet<int> { settings.WebPort };
    var apiPorts = new HashSet<int>();
    string? apiUrl = null;

    if (settings.EnableHttpsJsonApi)
    {
        apiUrl = FormatListenUrl("http", ApiHostForListen(settings), settings.HttpsJsonApiPort);
        apiPorts.Add(settings.HttpsJsonApiPort);
        if (!urls.Contains(apiUrl, StringComparer.OrdinalIgnoreCase))
            urls.Add(apiUrl);
    }

    return new ListenConfig(urls, webUrl, apiUrl, webPorts, apiPorts);
}

static string ApiHostForListen(AppSettings settings) => settings.ApiListeningMode switch
{
    ApiListenMode.Local => "127.0.0.1",
    ApiListenMode.All => "0.0.0.0",
    ApiListenMode.Interface => InterfaceAddress(settings.BindInterface) ?? HostForListen(settings.WebBindAddress),
    _ => "127.0.0.1",
};

static string? InterfaceAddress(string value)
{
    value = (value ?? "").Trim();
    if (value.Length == 0) return null;
    var comma = value.LastIndexOf(',');
    var address = comma >= 0 ? value[(comma + 1)..].Trim() : value;
    return address.Length == 0 ? null : address;
}

static string FormatListenUrl(string scheme, string host, int port)
{
    host = HostForListen(host);
    if (host.Contains(':') && !host.StartsWith('['))
        host = $"[{host}]";
    return $"{scheme}://{host}:{port}";
}

static IPAddress ListenAddress(string bind)
{
    bind = HostForListen(bind).Trim('[', ']');
    if (bind is "0.0.0.0" or "*") return IPAddress.Any;
    if (bind is "::") return IPAddress.IPv6Any;
    if (bind.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
    return IPAddress.TryParse(bind, out var ip) ? ip : IPAddress.Loopback;
}

static bool ApiAuthorized(HttpContext ctx, string password)
{
    password = (password ?? "").Trim();
    if (password.Length == 0) return true;

    if (SecretMatches(ctx.Request.Headers["X-WeaveFXP-API-Key"].ToString(), password))
        return true;
    if (SecretMatches(ctx.Request.Headers["X-API-Key"].ToString(), password))
        return true;

    var auth = ctx.Request.Headers.Authorization.ToString();
    if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
        SecretMatches(auth["Bearer ".Length..].Trim(), password))
        return true;

    if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth["Basic ".Length..].Trim()));
            var separator = decoded.IndexOf(':');
            var supplied = separator >= 0 ? decoded[(separator + 1)..] : decoded;
            if (SecretMatches(supplied, password))
                return true;
        }
        catch
        {
            return false;
        }
    }

    return false;
}

static bool IsWeaveFxpApiPath(PathString path)
{
    var value = NormalizeWeaveFxpApiPath(path);
    return value.Equals("/sites", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("/sites/", StringComparison.OrdinalIgnoreCase)
           || value.Equals("/sections", StringComparison.OrdinalIgnoreCase)
           || value.Equals("/path", StringComparison.OrdinalIgnoreCase)
           || value.Equals("/file", StringComparison.OrdinalIgnoreCase)
           || value.Equals("/raw", StringComparison.OrdinalIgnoreCase)
           || value.Equals("/weavefxp", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("/weavefxp/", StringComparison.OrdinalIgnoreCase)
           || value.Equals("/spreadjobs", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("/spreadjobs/", StringComparison.OrdinalIgnoreCase)
           || value.Equals("/transferjobs", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("/transferjobs/", StringComparison.OrdinalIgnoreCase);
}

static async Task<bool> TryHandleWeaveFxpApiAsync(HttpContext ctx, WeaveEngine engine)
{
    var path = NormalizeWeaveFxpApiPath(ctx.Request.Path);

    try
    {
        if (HttpMethods.IsGet(ctx.Request.Method) && path.Equals("/sites", StringComparison.OrdinalIgnoreCase))
        {
            await Results.Json(engine.Sites(false).Select(s => s.Name).ToArray()).ExecuteAsync(ctx);
            return true;
        }

        if (HttpMethods.IsGet(ctx.Request.Method) && path.StartsWith("/sites/", StringComparison.OrdinalIgnoreCase))
        {
            var name = Uri.UnescapeDataString(path["/sites/".Length..]);
            var site = engine.Site(name);
            if (site is null)
                return await JsonStatus(ctx, StatusCodes.Status404NotFound, new { error = "site not found" });

            await Results.Json(new
            {
                name = site.Name,
                addresses = new[] { $"{site.Host}:{site.Port}" },
                user = site.Username,
                password = site.Password,
                base_path = site.BasePath,
                disabled = false,
                sections = site.Sections.Select(s => new { name = s.Name, path = s.Section }).ToArray(),
                affils = site.Affils,
                skiplist = site.Skiplist,
            }).ExecuteAsync(ctx);
            return true;
        }

        if (HttpMethods.IsGet(ctx.Request.Method) && path.Equals("/sections", StringComparison.OrdinalIgnoreCase))
        {
            var sections = engine.Sites(false)
                .SelectMany(s => s.Sections)
                .Select(s => s.Name)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await Results.Json(sections).ExecuteAsync(ctx);
            return true;
        }

        if (HttpMethods.IsGet(ctx.Request.Method) && path.Equals("/path", StringComparison.OrdinalIgnoreCase))
        {
            var site = ctx.Request.Query["site"].ToString();
            var remotePath = NormalizeRemotePath(ctx.Request.Query["path"].ToString());
            if (string.IsNullOrWhiteSpace(site))
                return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new { error = "site is required" });
            if (engine.Site(site) is null)
                return await JsonStatus(ctx, StatusCodes.Status404NotFound, new { error = "site not found" });

            var entries = await engine.ListRemoteAsync(site, remotePath, $"api/{site}", ctx.RequestAborted);
            await Results.Json(new
            {
                site,
                path = remotePath,
                entries = entries
                    .Where(e => e.Name is not "." and not "..")
                    .Select(WeaveFxpPathEntry)
                    .ToArray(),
            }).ExecuteAsync(ctx);
            return true;
        }

        if (HttpMethods.IsDelete(ctx.Request.Method) && path.Equals("/path", StringComparison.OrdinalIgnoreCase))
        {
            var site = ctx.Request.Query["site"].ToString();
            var remotePath = NormalizeRemotePath(ctx.Request.Query["path"].ToString());
            if (string.IsNullOrWhiteSpace(site))
                return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new { error = "site is required" });
            if (engine.Site(site) is null)
                return await JsonStatus(ctx, StatusCodes.Status404NotFound, new { error = "site not found" });
            if (remotePath == "/")
                return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new { error = "refusing to delete /" });

            await engine.DeleteRemotePathAsync(site, remotePath, ctx.RequestAborted);
            await Results.Json(new { ok = true, site, path = remotePath }).ExecuteAsync(ctx);
            return true;
        }

        if (HttpMethods.IsGet(ctx.Request.Method) && path.Equals("/file", StringComparison.OrdinalIgnoreCase))
        {
            var site = ctx.Request.Query["site"].ToString();
            var remotePath = NormalizeRemotePath(ctx.Request.Query["path"].ToString());
            if (string.IsNullOrWhiteSpace(site))
                return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new { error = "site is required" });
            if (engine.Site(site) is null)
                return await JsonStatus(ctx, StatusCodes.Status404NotFound, new { error = "site not found" });
            if (remotePath == "/")
                return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new { error = "path must point to a file" });

            var bytes = await engine.RetrieveRemoteFileAsync(site, remotePath, 15 * 1024 * 1024, ctx.RequestAborted);
            await Results.Bytes(bytes, GuessContentType(remotePath)).ExecuteAsync(ctx);
            return true;
        }

        if (HttpMethods.IsPost(ctx.Request.Method) && path.Equals("/raw", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = await ReadJsonBodyAsync(ctx);
            var command = JsonString(doc.RootElement, "command", "cmd");
            var sites = JsonStringList(doc.RootElement, "sites", "site");
            if (string.IsNullOrWhiteSpace(command))
                return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new { error = "command is required" });
            if (sites.Count == 0)
                return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new { error = "sites is required" });

            var successes = new List<object>();
            var failures = new List<object>();
            foreach (var site in sites)
            {
                try
                {
                    var result = await engine.SendRawCommandAsync(site, command);
                    if (result.Ok)
                        successes.Add(new { name = site, result = result.Message, code = result.Code });
                    else
                        failures.Add(new { name = site, error = result.Message, code = result.Code });
                }
                catch (Exception ex)
                {
                    failures.Add(new { name = site, error = ex.Message });
                }
            }

            await Results.Json(new { successes, failures }).ExecuteAsync(ctx);
            return true;
        }

        if (HttpMethods.IsPost(ctx.Request.Method) && path.Equals("/spreadjobs", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = await ReadJsonBodyAsync(ctx);
            var release = JsonString(doc.RootElement, "name", "release", "release_name");
            var section = JsonString(doc.RootElement, "section", "src_section", "dst_section");
            var sites = JsonStringList(doc.RootElement, "sites", "dst_sites", "destination_sites", "target_sites");
            var sourceOnly = JsonStringList(doc.RootElement, "sites_dlonly", "src_sites", "source_sites");
            if (string.IsNullOrWhiteSpace(release))
                return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new { error = "name is required" });
            if (string.IsNullOrWhiteSpace(section))
                return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new { error = "section is required" });
            if (sites.Count < 2)
                return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new { error = "at least two sites are required" });

            var knownSites = sites
                .Where(s => engine.Site(s) is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var unknownSites = sites
                .Where(s => engine.Site(s) is null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unknownSites.Count > 0)
                engine.Log("api", "compat", "warn", $"Ignoring unknown spreadjob site(s): {string.Join(", ", unknownSites)}");
            if (knownSites.Count < 2)
                return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new
                {
                    error = "at least two known sites are required",
                    known_sites = knownSites,
                    ignored_sites = unknownSites,
                });

            var sourceSite = sourceOnly.FirstOrDefault(s => knownSites.Contains(s, StringComparer.OrdinalIgnoreCase)) ?? knownSites[0];
            var targets = knownSites.Where(s => !s.Equals(sourceSite, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (targets.Count == 0)
                return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new { error = "spread has no target sites" });

            var source = engine.Site(sourceSite);
            if (source is null)
                return await JsonStatus(ctx, StatusCodes.Status404NotFound, new { error = $"source site '{sourceSite}' not found" });

            var sectionSites = new[] { sourceSite }.Concat(targets).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var sectionPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var missingSections = new List<object>();
            foreach (var siteName in sectionSites)
            {
                var site = engine.Site(siteName);
                if (site is null) continue;
                if (TryResolveRequiredSectionBasePath(site, section, out var sectionPath))
                {
                    sectionPaths[siteName] = sectionPath;
                }
                else
                {
                    missingSections.Add(new
                    {
                        site = siteName,
                        section,
                        available_sections = site.Sections.Select(s => s.Name).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray(),
                    });
                }
            }

            if (missingSections.Count > 0)
            {
                engine.Log("api", "compat", "error", $"Rejected spreadjob '{release}': section '{section}' is not configured on {string.Join(", ", sectionSites.Where(s => !sectionPaths.ContainsKey(s)))}");
                return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new
                {
                    error = "section is not configured on every requested site",
                    section,
                    missing = missingSections,
                });
            }

            var batchId = "compat-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var compatId = CompatNumericId(batchId);
            var jobs = new List<Job>();
            foreach (var targetName in targets)
            {
                var target = engine.Site(targetName);
                if (target is null)
                    return await JsonStatus(ctx, StatusCodes.Status404NotFound, new { error = $"target site '{targetName}' not found" });

                jobs.Add(engine.StartFxp(new TransferRequest
                {
                    BatchId = batchId,
                    FromSite = sourceSite,
                    ToSite = targetName,
                    SourcePath = MaybeAppendRelease(sectionPaths[sourceSite], release),
                    DestPath = MaybeAppendRelease(sectionPaths[targetName], release),
                    Label = release,
                    Race = true,
                    ViaApi = true,
                }));
            }

            await Results.Json(new
            {
                id = compatId,
                job_id = compatId,
                batch_id = batchId,
                weave_batch_id = batchId,
                state = "STARTED",
                status = "RUNNING",
                name = release,
                section,
                source_site = sourceSite,
                sites = targets,
                ignored_sites = unknownSites,
                jobs = jobs.Select(j => j.Id).ToArray(),
            }).ExecuteAsync(ctx);
            return true;
        }

        if (HttpMethods.IsPost(ctx.Request.Method) && path.Equals("/transferjobs", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = await ReadJsonBodyAsync(ctx);
            var release = JsonString(doc.RootElement, "name", "release", "release_name");
            var sourceSite = JsonString(doc.RootElement, "src_site", "from_site", "source_site");
            var targetSite = JsonString(doc.RootElement, "dst_site", "to_site", "destination_site", "target_site");
            var sourceSection = JsonString(doc.RootElement, "src_section", "section");
            var sourcePath = JsonString(doc.RootElement, "src_path", "source_path");
            var destPath = JsonString(doc.RootElement, "dst_path", "dest_path", "destination_path");

            if (string.IsNullOrWhiteSpace(sourceSite) || string.IsNullOrWhiteSpace(targetSite))
                return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new { error = "src_site and dst_site are required" });

            var source = engine.Site(sourceSite);
            var target = engine.Site(targetSite);
            if (source is null || target is null)
                return await JsonStatus(ctx, StatusCodes.Status404NotFound, new { error = "source or destination site not found" });

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                if (string.IsNullOrWhiteSpace(sourceSection))
                    return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new { error = "src_section is required when src_path is not provided" });
                if (!TryResolveRequiredSectionBasePath(source, sourceSection, out var sourceSectionPath))
                    return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new
                    {
                        error = "source section is not configured",
                        site = sourceSite,
                        section = sourceSection,
                        available_sections = source.Sections.Select(s => s.Name).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray(),
                    });
                sourcePath = MaybeAppendRelease(sourceSectionPath, release);
            }
            else
            {
                sourcePath = MaybeAppendRelease(sourcePath, release);
            }

            if (string.IsNullOrWhiteSpace(destPath))
            {
                if (string.IsNullOrWhiteSpace(sourceSection))
                    return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new { error = "section is required when dst_path is not provided" });
                if (!TryResolveRequiredSectionBasePath(target, sourceSection, out var targetSectionPath))
                    return await JsonStatus(ctx, StatusCodes.Status400BadRequest, new
                    {
                        error = "destination section is not configured",
                        site = targetSite,
                        section = sourceSection,
                        available_sections = target.Sections.Select(s => s.Name).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray(),
                    });
                destPath = MaybeAppendRelease(targetSectionPath, release);
            }
            else
            {
                destPath = MaybeAppendRelease(destPath, release);
            }

            var job = engine.StartFxp(new TransferRequest
            {
                FromSite = sourceSite,
                ToSite = targetSite,
                SourcePath = sourcePath,
                DestPath = destPath,
                Label = release,
                ViaApi = true,
            });

            await Results.Json(new
            {
                id = CompatNumericId(job.Id),
                job_id = CompatNumericId(job.Id),
                weave_job_id = job.Id,
                state = "STARTED",
                status = "RUNNING",
                name = release,
                src_site = sourceSite,
                dst_site = targetSite,
            }).ExecuteAsync(ctx);
            return true;
        }

        if (HttpMethods.IsGet(ctx.Request.Method) && path.StartsWith("/spreadjobs/", StringComparison.OrdinalIgnoreCase))
        {
            var name = Uri.UnescapeDataString(path["/spreadjobs/".Length..]);
            return await WriteWeaveFxpJobStatus(ctx, engine, name);
        }

        if (HttpMethods.IsGet(ctx.Request.Method) && path.StartsWith("/transferjobs/", StringComparison.OrdinalIgnoreCase))
        {
            var name = Uri.UnescapeDataString(path["/transferjobs/".Length..]);
            return await WriteWeaveFxpJobStatus(ctx, engine, name);
        }

        if (HttpMethods.IsPost(ctx.Request.Method) &&
            path.StartsWith("/spreadjobs/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith("/reset", StringComparison.OrdinalIgnoreCase))
        {
            var nameStart = "/spreadjobs/".Length;
            var nameLength = path.Length - nameStart - "/reset".Length;
            var name = Uri.UnescapeDataString(path.Substring(nameStart, Math.Max(0, nameLength)).Trim('/'));
            engine.Log("api", "compat", "warn", $"reset requested for {name}");
            await Results.Json(new { name, state = "RESET", status = "RESET" }).ExecuteAsync(ctx);
            return true;
        }
    }
    catch (Exception ex)
    {
        engine.Log("api", "compat", "error", $"{ctx.Request.Method} {path}: {ex.Message}");
        return await JsonStatus(ctx, StatusCodes.Status500InternalServerError, new { error = ex.Message });
    }

    return false;
}

static string NormalizeWeaveFxpApiPath(PathString rawPath)
{
    var path = (rawPath.Value ?? "").TrimEnd('/');
    if (path.Length == 0) return "/";

    if (path.StartsWith("/api/weavefxp/", StringComparison.OrdinalIgnoreCase))
        return path["/api/weavefxp".Length..];
    if (path.Equals("/api/weavefxp", StringComparison.OrdinalIgnoreCase))
        return "/";

    if (path.StartsWith("/weavefxp/", StringComparison.OrdinalIgnoreCase))
        return path["/weavefxp".Length..];
    if (path.Equals("/weavefxp", StringComparison.OrdinalIgnoreCase))
        return "/";

    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
    {
        var withoutApi = path["/api".Length..];
        if (IsWeaveFxpApiAlias(withoutApi))
            return withoutApi;
    }

    return path;
}

static bool IsWeaveFxpApiAlias(string path)
{
    return path.Equals("/sections", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/path", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/file", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/raw", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/spreadjobs", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("/spreadjobs/", StringComparison.OrdinalIgnoreCase)
           || path.Equals("/transferjobs", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("/transferjobs/", StringComparison.OrdinalIgnoreCase);
}

static async Task<bool> WriteWeaveFxpJobStatus(HttpContext ctx, WeaveEngine engine, string name)
{
    var jobs = engine.Jobs()
        .Where(j => JobMatchesName(j, name))
        .OrderBy(j => j.CreatedAt)
        .ToList();

    if (jobs.Count == 0)
        return await JsonStatus(ctx, StatusCodes.Status404NotFound, new { error = "job not found" });

    var status = WeaveFxpStatus(jobs);
    var first = jobs.First();
    var last = jobs.Last();
    var start = jobs.Where(j => j.StartedAt != default).Select(j => j.StartedAt).DefaultIfEmpty(first.CreatedAt).Min();
    var end = jobs.Where(j => j.FinishedAt != default).Select(j => j.FinishedAt).DefaultIfEmpty(DateTime.UtcNow).Max();
    var seconds = Math.Max(0, (long)(end - start).TotalSeconds);
    var targets = jobs.Select(j => j.Request.ToSite)
        .Where(s => !string.IsNullOrWhiteSpace(s) && !s.Equals("local", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    await Results.Json(new
    {
        id = CompatNumericId(string.IsNullOrWhiteSpace(first.BatchId) ? first.Id : first.BatchId),
        job_id = CompatNumericId(string.IsNullOrWhiteSpace(first.BatchId) ? first.Id : first.BatchId),
        batch_id = first.BatchId,
        weave_job_id = first.Id,
        name,
        status,
        state = status,
        sites = targets,
        destination_sites = targets,
        section = SectionFromPath(first.Request.SourcePath),
        size_estimated_bytes = jobs.Sum(EstimatedBytes),
        time_spent_seconds = seconds,
        files_total = jobs.Count,
        files_progress = jobs.Count(j => j.State is JobState.Succeeded or JobState.Cancelled),
        subpaths = jobs.Select(j => new { path = j.Request.DestPath, site = j.Request.ToSite, status = WeaveFxpStatus(new[] { j }) }).ToArray(),
        result = last.Error,
    }).ExecuteAsync(ctx);
    return true;
}

static string WeaveFxpStatus(IEnumerable<Job> jobs)
{
    var list = jobs.ToList();
    if (list.Any(j => j.State is JobState.Running or JobState.Queued)) return "RUNNING";
    if (list.Any(j => j.State == JobState.Failed)) return "FAILED";
    if (list.All(j => j.State == JobState.Cancelled)) return "ABORTED";
    return "DONE";
}

static bool JobMatchesName(Job job, string name)
{
    if (string.IsNullOrWhiteSpace(name)) return false;
    return job.Request.Label.Equals(name, StringComparison.OrdinalIgnoreCase)
           || job.BatchId.Equals(name, StringComparison.OrdinalIgnoreCase)
           || job.Id.Equals(name, StringComparison.OrdinalIgnoreCase)
           || RemoteBase(job.Request.SourcePath).Equals(name, StringComparison.OrdinalIgnoreCase)
           || RemoteBase(job.Request.DestPath).Equals(name, StringComparison.OrdinalIgnoreCase);
}

static int CompatNumericId(string seed)
{
    seed = string.IsNullOrWhiteSpace(seed) ? Guid.NewGuid().ToString("N") : seed;
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    var value = BitConverter.ToInt32(hash, 0) & 0x7fffffff;
    return value == 0 ? 1 : value;
}

static long EstimatedBytes(Job job) => 0;

static object WeaveFxpPathEntry(RemoteEntry entry)
{
    var type = entry.Type.Equals("dir", StringComparison.OrdinalIgnoreCase) ? "DIR"
        : entry.Type.Equals("link", StringComparison.OrdinalIgnoreCase) ? "LINK"
        : entry.Type.Equals("file", StringComparison.OrdinalIgnoreCase) ? "FILE"
        : "UNKNOWN";
    string? modified = entry.Modified == default ? null : entry.Modified.ToString("yyyy-MM-dd HH:mm:ss");
    var timestamp = entry.Modified == default ? 0 : new DateTimeOffset(entry.Modified).ToUnixTimeSeconds();
    var linkTarget = string.IsNullOrWhiteSpace(entry.LinkTarget) ? null : entry.LinkTarget;

    return new
    {
        name = entry.Name,
        path = NormalizeRemotePath(entry.Path),
        full_path = NormalizeRemotePath(entry.Path),
        type,
        size = entry.Size,
        modified,
        last_modified = modified,
        timestamp,
        link_target = linkTarget,
        raw = entry.Raw,
    };
}

static string GuessContentType(string path)
{
    return Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".nfo" or ".diz" or ".sfv" or ".log" or ".m3u" or ".md" or ".ini" or ".cfg" => "text/plain",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };
}

static bool TryResolveRequiredSectionBasePath(Site site, string section, out string path)
{
    path = "";
    section = (section ?? "").Trim();
    if (section.Length == 0) return false;

    var match = site.Sections.FirstOrDefault(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase));
    if (match is null || string.IsNullOrWhiteSpace(match.Section))
        return false;

    path = NormalizeRemotePath(match.Section);
    return true;
}

static string MaybeAppendRelease(string path, string release)
{
    path = NormalizeRemotePath(path);
    release = (release ?? "").Trim().Trim('/');
    if (release.Length == 0) return path;
    return RemoteBase(path).Equals(release, StringComparison.OrdinalIgnoreCase) ? path : JoinRemotePath(path, release);
}

static string JoinRemotePath(string basePath, string name)
{
    basePath = NormalizeRemotePath(basePath);
    name = (name ?? "").Trim().Trim('/');
    if (name.Length == 0) return basePath;
    return basePath.EndsWith('/') ? basePath + name : basePath + "/" + name;
}

static string NormalizeRemotePath(string path)
{
    path = (path ?? "").Trim().Replace('\\', '/');
    if (path.Length == 0) return "/";
    if (!path.StartsWith('/')) path = "/" + path;
    while (path.Contains("//", StringComparison.Ordinal)) path = path.Replace("//", "/");
    return path;
}

static string RemoteBase(string path)
{
    path = NormalizeRemotePath(path).TrimEnd('/');
    var idx = path.LastIndexOf('/');
    return idx >= 0 ? path[(idx + 1)..] : path;
}

static string SectionFromPath(string path)
{
    var parts = NormalizeRemotePath(path).Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    return parts.Length > 1 ? parts[^2] : "";
}

static async Task<JsonDocument> ReadJsonBodyAsync(HttpContext ctx)
{
    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();
    return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
}

static string JsonString(JsonElement root, params string[] names)
{
    return TryGetJsonProperty(root, out var value, names) ? JsonValueToString(value) : "";
}

static List<string> JsonStringList(JsonElement root, params string[] names)
{
    if (!TryGetJsonProperty(root, out var value, names)) return new List<string>();
    if (value.ValueKind == JsonValueKind.Array)
    {
        return value.EnumerateArray()
            .Select(JsonValueToString)
            .SelectMany(SplitList)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    return SplitList(JsonValueToString(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

static bool TryGetJsonProperty(JsonElement root, out JsonElement value, params string[] names)
{
    if (root.ValueKind == JsonValueKind.Object)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (names.Any(n => prop.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            {
                value = prop.Value;
                return true;
            }
        }
    }
    value = default;
    return false;
}

static string JsonValueToString(JsonElement value)
{
    return value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };
}

static IEnumerable<string> SplitList(string value)
    => (value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

static async Task<bool> JsonStatus(HttpContext ctx, int status, object body)
{
    await Results.Json(body, statusCode: status).ExecuteAsync(ctx);
    return true;
}

static bool SecretMatches(string? supplied, string expected)
{
    supplied = (supplied ?? "").Trim();
    if (supplied.Length == 0) return false;
    var left = Encoding.UTF8.GetBytes(supplied);
    var right = Encoding.UTF8.GetBytes(expected);
    return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}

static string BrowserUrl(string listenUrl)
{
    try
    {
        var uri = new Uri(listenUrl);
        var host = uri.Host is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : uri.Host;
        return $"{uri.Scheme}://{host}:{uri.Port}";
    }
    catch { return listenUrl; }
}

static string HostForListen(string bind)
{
    bind = string.IsNullOrWhiteSpace(bind) ? "127.0.0.1" : bind.Trim();
    return bind.Equals("*", StringComparison.Ordinal) ? "0.0.0.0" : bind;
}

static void OpenBrowser(string target)
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(500);
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo("rundll32", $"url.dll,FileProtocolHandler {target}"));
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", target);
            else
                Process.Start("xdg-open", target);
        }
        catch { /* headless / no browser available */ }
    });
}

static string ResolveWebRoot(string appDir)
{
    var visible = Path.Combine(appDir, "wwwroot");
    if (Directory.Exists(visible)) return visible;

    var extracted = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    if (Directory.Exists(extracted)) return extracted;

    var empty = Path.Combine(Path.GetTempPath(), "WeaveFXP", "wwwroot");
    Directory.CreateDirectory(empty);
    return empty;
}

static string ReadEmbeddedText(string name)
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Embedded asset '{name}' was not found.");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}

static byte[] ReadEmbeddedBytes(string name)
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Embedded asset '{name}' was not found.");
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
}

public sealed record DupeRequest(string Site, string Path, string Name);
public sealed record ReleaseCheckRequest(string Site, string Path);
public sealed record RawCommandRequest(string Site, string Command);

sealed record ListenConfig(
    List<string> Urls,
    string WebUrl,
    string? ApiUrl,
    HashSet<int> WebPorts,
    HashSet<int> ApiPorts);
