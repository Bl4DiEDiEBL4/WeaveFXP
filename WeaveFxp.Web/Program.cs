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
        {
            // The JSON API speaks real HTTPS, like cbftp's REST API: clients such as
            // dtool connect with SSL and fail the handshake on a plain-HTTP listener.
            // Self-signed cert, generated once and persisted in data/keys.
            var apiCert = LoadOrCreateApiCertificate(engine.DataDir);
            options.Listen(ListenAddress(ApiHostForListen(startupSettings)), startupSettings.HttpsJsonApiPort,
                lo => lo.UseHttps(apiCert));
        }
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

    AppSettings? apiSettings = null;
    if (shouldAuthorizeApi)
    {
        apiSettings = engine.Settings(false);
        if (!apiSettings.EnableHttpsJsonApi)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        engine.Log("api", "", "info", $"{ctx.Request.Method} {ctx.Request.Path}{ctx.Request.QueryString} from {ctx.Connection.RemoteIpAddress}");

        if (!ApiAuthorized(ctx, apiSettings.ApiPassword))
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("API password required");
            engine.Log("api", "", "warn", $"→ 401 {ctx.Request.Method} {ctx.Request.Path}: API password required");
            return;
        }
    }

    // Debug logging: capture WHAT the API answers, so client problems (dtool etc.)
    // are diagnosable straight from the Logs page instead of a packet sniffer.
    if (apiSettings?.DebugLogging == true)
    {
        var original = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            var handled = apiOnlyPort && isWeaveFxpApi && await TryHandleWeaveFxpApiAsync(ctx, engine);
            if (!handled) await next();
        }
        finally
        {
            ctx.Response.Body = original;
            var len = (int)Math.Min(buffer.Length, 4000);
            var preview = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, len).Replace('\n', ' ').Replace('\r', ' ');
            if (buffer.Length > 4000) preview += $"… (+{buffer.Length - 4000} bytes)";
            engine.Log("api", "", "info", $"→ {ctx.Response.StatusCode} {ctx.Request.Method} {ctx.Request.Path}: {preview}");
            buffer.Position = 0;
            await buffer.CopyToAsync(original);
        }
        return;
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
    archived_jobs = engine.ArchivedJobCount(),
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
api.MapPost("/upload", (UploadRequest request) => { request.ViaApi = true; return engine.StartUpload(request); });
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
{
    Console.WriteLine($"WeaveFXP JSON API listening on {listen.ApiUrl}");
    engine.Log("api", "http", "info", $"JSON API listening on {listen.ApiUrl}");
}
Console.WriteLine($"WeaveFXP effective listeners: {url}");
Console.WriteLine($"State file: {engine.StatePath}");
Console.WriteLine($"Data folder: {engine.DataDir}");
if (!string.IsNullOrWhiteSpace(engine.LoadWarning))
    Console.WriteLine(engine.LoadWarning);

// Auto-open the dashboard on start (skip with --no-browser).
if (!args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase))
    OpenBrowser(BrowserUrl(listen.WebUrl));

// cbftp-compatible UDP API: dtool and friends send plaintext datagrams like
// "<password> race <section> <release> <site1>,<site2>". Enable it in Settings.
StartUdpApi(engine);

app.Run();

// Self-signed certificate for the HTTPS JSON API (cbftp-style). Generated once,
// persisted in data/keys/api-cert.pfx so the fingerprint stays stable.
static System.Security.Cryptography.X509Certificates.X509Certificate2 LoadOrCreateApiCertificate(string dataDir)
{
    const string pfxPassword = "weavefxp";
    var dir = Path.Combine(dataDir, "keys");
    Directory.CreateDirectory(dir);
    var pfxPath = Path.Combine(dir, "api-cert.pfx");
    if (File.Exists(pfxPath))
    {
        try { return new System.Security.Cryptography.X509Certificates.X509Certificate2(pfxPath, pfxPassword); }
        catch { /* unreadable — regenerate below */ }
    }
    using var rsa = RSA.Create(2048);
    var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
        "CN=WeaveFXP API", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost");
    san.AddDnsName(Environment.MachineName);
    req.CertificateExtensions.Add(san.Build());
    using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
    var pfx = cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, pfxPassword);
    try { File.WriteAllBytes(pfxPath, pfx); } catch { /* still usable in-memory */ }
    // Re-import from PFX: Kestrel on Windows needs the persisted private key handle.
    return new System.Security.Cryptography.X509Certificates.X509Certificate2(pfx, pfxPassword);
}

static ListenConfig BuildListenConfig(AppSettings settings)
{
    var webUrl = FormatListenUrl("http", settings.WebBindAddress, settings.WebPort);
    var urls = new List<string> { webUrl };
    var webPorts = new HashSet<int> { settings.WebPort };
    var apiPorts = new HashSet<int>();
    string? apiUrl = null;

    if (settings.EnableHttpsJsonApi)
    {
        apiUrl = FormatListenUrl("https", ApiHostForListen(settings), settings.HttpsJsonApiPort);
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

            // cbftp-compatible site object. dtool parses this response LINE BY LINE, so
            // it MUST be pretty-printed (each array element on its own line) and it reads
            // except_source_sites then except_target_sites, finalising when it sees a
            // "force_binary" line — which therefore has to come AFTER the except lists.
            // Field names mirror cbftp's RestApi::handleSiteGet.
            var siteJson = new
            {
                name = site.Name,
                addresses = new[] { $"{site.Host}:{site.Port}" },
                user = site.Username,
                password = site.Password,
                base_path = site.BasePath,
                max_logins = site.LoginSlots,
                max_sim_up = site.UploadSlots,
                max_sim_down = site.DownloadSlots,
                pret = site.UsePret,
                list_command = string.IsNullOrWhiteSpace(site.ListCommand) ? "STAT_L" : site.ListCommand,
                tls_mode = site.TlsMode.ToString().ToUpperInvariant(),
                sscn = site.UseSscn,
                cpsv = site.CpsvSupported,
                cepr = site.CeprSupported,
                broken_pasv = site.BrokenPasv,
                disabled = false,
                allow_upload = site.BlockTransferTo ? "NO" : "YES",
                allow_download = site.BlockTransferFrom ? "NO" : "YES",
                xdupe = site.UseXdupe,
                sections = site.Sections.Select(s => new { name = s.Name, path = s.Section }).ToArray(),
                affils = site.Affils,
                transfer_source_policy = site.TransferSourcePolicy.ToString().ToUpperInvariant(),
                transfer_target_policy = site.TransferTargetPolicy.ToString().ToUpperInvariant(),
                except_source_sites = site.ExceptSourceSites,
                except_target_sites = site.ExceptTargetSites,
                // dtool's line parser finalises source/target lists on this key.
                force_binary_mode = site.ForceBinary,
                skiplist = site.Skiplist,
            };
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(siteJson, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }

        // dtool syncs source/target route exclusions with PATCH /sites/<name>
        // (cbftp-compatible: except_source_sites / except_target_sites lists).
        if (HttpMethods.IsPatch(ctx.Request.Method) && path.StartsWith("/sites/", StringComparison.OrdinalIgnoreCase))
        {
            var name = Uri.UnescapeDataString(path["/sites/".Length..]);
            var site = engine.Site(name);
            if (site is null)
                return await JsonStatus(ctx, StatusCodes.Status404NotFound, new { error = "site not found" });

            using var doc = await ReadJsonBodyAsync(ctx);
            var root = doc.RootElement;
            if (root.TryGetProperty("except_source_sites", out var es) && es.ValueKind == JsonValueKind.Array)
                site.ExceptSourceSites = es.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (root.TryGetProperty("except_target_sites", out var et) && et.ValueKind == JsonValueKind.Array)
                site.ExceptTargetSites = et.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            // cbftp field names: "ALLOW"/"BLOCK".
            if (root.TryGetProperty("transfer_source_policy", out var sp) && sp.ValueKind == JsonValueKind.String)
                site.TransferSourcePolicy = sp.GetString()!.Equals("BLOCK", StringComparison.OrdinalIgnoreCase)
                    ? SiteTransferPolicy.Block : SiteTransferPolicy.Allow;
            if (root.TryGetProperty("transfer_target_policy", out var tp) && tp.ValueKind == JsonValueKind.String)
                site.TransferTargetPolicy = tp.GetString()!.Equals("BLOCK", StringComparison.OrdinalIgnoreCase)
                    ? SiteTransferPolicy.Block : SiteTransferPolicy.Allow;

            var saved = engine.SaveSite(site.Name, site);
            engine.Log("api", "compat", "info", $"PATCH {site.Name}: srcpol={saved.TransferSourcePolicy} tgtpol={saved.TransferTargetPolicy} except_source=[{string.Join(",", saved.ExceptSourceSites)}] except_target=[{string.Join(",", saved.ExceptTargetSites)}]");
            await Results.Json(new
            {
                name = saved.Name,
                transfer_source_policy = saved.TransferSourcePolicy.ToString().ToUpperInvariant(),
                transfer_target_policy = saved.TransferTargetPolicy.ToString().ToUpperInvariant(),
                except_source_sites = saved.ExceptSourceSites,
                except_target_sites = saved.ExceptTargetSites,
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

            // cbftp RestApi::finalize(RAW_COMMAND): successes [{name,result}],
            // failures [{name,reason}], pretty-printed. dtool reads it line by line.
            var successes = new List<object>();
            var failures = new List<object>();
            foreach (var site in sites)
            {
                try
                {
                    var result = await engine.SendRawCommandAsync(site, command);
                    if (result.Ok)
                        successes.Add(new { name = site, result = result.Message });
                    else
                        failures.Add(new { name = site, reason = result.Message });
                }
                catch (Exception ex)
                {
                    failures.Add(new { name = site, reason = ex.Message });
                }
            }

            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { successes, failures },
                new JsonSerializerOptions { WriteIndented = true }));
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

// ---- cbftp-compatible UDP API ------------------------------------------------------
// Speaks the same plaintext datagram protocol as cbftp's RemoteCommandHandler:
//   <password> race <section> <release> <site1>,<site2>|* [dlonlysites]
//   <password> fxp <srcsite> <path-or-section> <release> <dstsite> <path-or-section> [dstrelease]
//   <password> raw <sitelist>|* <raw command...>
//   <password> download <site> <path-or-section> [name]
// "distribute"/"prepare" are accepted as "race". Encrypted mode is not supported.
static void StartUdpApi(WeaveEngine engine)
{
    var s = engine.Settings(false);
    if (!s.EnableUdpApi) return;
    var port = s.UdpApiPort > 0 ? s.UdpApiPort : 59010;
    _ = Task.Run(async () =>
    {
        System.Net.Sockets.UdpClient udp;
        try
        {
            udp = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Any, port));
        }
        catch (Exception ex)
        {
            engine.Log("api", "udp", "error", $"UDP API could not bind port {port}: {ex.Message}");
            return;
        }
        Console.WriteLine($"WeaveFXP UDP API (cbftp-compatible) listening on 0.0.0.0:{port}");
        engine.Log("api", "udp", "info", $"UDP API listening on port {port}");
        using (udp)
        {
            while (true)
            {
                try
                {
                    var r = await udp.ReceiveAsync().ConfigureAwait(false);
                    var text = Encoding.UTF8.GetString(r.Buffer).Trim();
                    HandleUdpApiMessage(engine, text, r.RemoteEndPoint);
                }
                catch (Exception ex)
                {
                    engine.Log("api", "udp", "warn", "UDP receive failed: " + ex.Message);
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
        }
    });
}

static void HandleUdpApiMessage(WeaveEngine engine, string text, IPEndPoint from)
{
    var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (tokens.Length < 2)
    {
        engine.Log("api", "udp", "warn", $"bad message from {from}");
        return;
    }
    var settings = engine.Settings(false);
    var configured = (settings.ApiPassword ?? "").Trim();
    if (configured.Length > 0 && !SecretMatches(tokens[0], configured))
    {
        engine.Log("api", "udp", "warn", $"invalid password from {from}");
        return;
    }
    var command = tokens[1].ToLowerInvariant();
    var args = tokens.Skip(2).ToArray();
    engine.Log("api", "udp", "info", $"{from.Address}: {command} {string.Join(' ', args)}");
    try
    {
        switch (command)
        {
            case "race":
            case "distribute":
            case "prepare": // no prepared-job queue — start it right away
                UdpRace(engine, args);
                break;
            case "fxp":
                UdpFxp(engine, args);
                break;
            case "raw":
                if (args.Length >= 2)
                {
                    var cmd = string.Join(' ', args.Skip(1));
                    foreach (var site in UdpSiteList(engine, args[0]))
                        _ = engine.SendRawCommandAsync(site, cmd);
                }
                break;
            case "download":
                if (args.Length >= 2)
                {
                    var basePath = UdpTranslate(engine, args[0], args[1]);
                    if (basePath is null) { engine.Log("api", "udp", "warn", $"download: unknown section/path '{args[1]}' on {args[0]}"); return; }
                    var srcPath = args.Length > 2 ? MaybeAppendRelease(basePath, args[2]) : basePath;
                    engine.StartDownload(new DownloadRequest { Site = args[0], SourcePath = srcPath, ViaApi = true });
                }
                break;
            default:
                engine.Log("api", "udp", "warn", $"unsupported command: {command}");
                break;
        }
    }
    catch (Exception ex)
    {
        engine.Log("api", "udp", "warn", $"{command} failed: {ex.Message}");
    }
}

static void UdpRace(WeaveEngine engine, string[] args)
{
    if (args.Length < 3)
    {
        engine.Log("api", "udp", "warn", "bad race format (want: race <section> <release> <sites>)");
        return;
    }
    var section = args[0];
    var release = args[1];
    var sites = UdpSiteList(engine, args[2])
        .Where(n => engine.Site(n) is not null)
        .Where(n => TryResolveRequiredSectionBasePath(engine.Site(n)!, section, out _))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (sites.Count < 2)
    {
        engine.Log("api", "udp", "warn", $"race '{release}': section '{section}' not configured on enough sites ({sites.Count})");
        return;
    }
    // Pick the first site allowed to be a source, then every other site it may reach
    // becomes a target — using the same cbftp policy+exception gate as the mesh.
    var srcName = sites.FirstOrDefault(n => !engine.Site(n)!.BlockTransferFrom);
    if (srcName is null)
    {
        engine.Log("api", "udp", "warn", $"race '{release}': every candidate source is blocked");
        return;
    }
    var src = engine.Site(srcName)!;
    var targets = sites.Where(n => !n.Equals(srcName, StringComparison.OrdinalIgnoreCase))
        .Where(n => engine.TransferAllowed(src, engine.Site(n)!))
        .ToList();
    if (targets.Count == 0)
    {
        engine.Log("api", "udp", "warn", $"race '{release}': no allowed targets from {srcName} (block/except rules)");
        return;
    }
    // ONE-DIRECTIONAL: an announce means the files are on the source. Start src -> each
    // target directly (a race per target). We deliberately do NOT use the bidirectional
    // spread mesh here — that also builds target -> src, which has no files and just
    // sits idle as a dead 0/1 "Running" job until it times out.
    TryResolveRequiredSectionBasePath(src, section, out var srcBase);
    var srcPath = MaybeAppendRelease(srcBase, release);
    foreach (var targetName in targets)
    {
        TryResolveRequiredSectionBasePath(engine.Site(targetName)!, section, out var dstBase);
        engine.StartFxp(new TransferRequest
        {
            FromSite = srcName,
            ToSite = targetName,
            SourcePath = srcPath,
            DestPath = MaybeAppendRelease(dstBase, release),
            Label = release,
            Race = true,
            ViaApi = true,
        });
    }
}

static void UdpFxp(WeaveEngine engine, string[] args)
{
    if (args.Length < 5)
    {
        engine.Log("api", "udp", "warn", "bad fxp format (want: fxp <src> <path> <rls> <dst> <path> [rls])");
        return;
    }
    var srcSite = args[0];
    var dstSite = args[3];
    var srcBase = UdpTranslate(engine, srcSite, args[1]);
    var dstBase = UdpTranslate(engine, dstSite, args[4]);
    if (srcBase is null || dstBase is null)
    {
        engine.Log("api", "udp", "warn", $"fxp: unknown section/path ({args[1]} on {srcSite} / {args[4]} on {dstSite})");
        return;
    }
    var srcRls = args[2];
    var dstRls = args.Length > 5 ? args[5] : args[2];
    engine.StartFxp(new TransferRequest
    {
        FromSite = srcSite,
        ToSite = dstSite,
        SourcePath = MaybeAppendRelease(srcBase, srcRls),
        DestPath = MaybeAppendRelease(dstBase, dstRls),
        Label = srcRls,
        Race = true,
        ViaApi = true,
    });
}

// cbftp's SectionUtil::useOrSectionTranslate: a leading "/" means a literal path,
// anything else is a section NAME looked up in that site's configured sections.
static string? UdpTranslate(WeaveEngine engine, string siteName, string pathOrSection)
{
    var site = engine.Site(siteName);
    if (site is null) return null;
    pathOrSection = (pathOrSection ?? "").Trim();
    if (pathOrSection.StartsWith('/')) return NormalizeRemotePath(pathOrSection);
    return TryResolveRequiredSectionBasePath(site, pathOrSection, out var p) ? p : null;
}

static List<string> UdpSiteList(WeaveEngine engine, string sitestring)
{
    if (sitestring == "*")
        return engine.Sites(false).Select(s => s.Name).ToList();
    return sitestring.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
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
