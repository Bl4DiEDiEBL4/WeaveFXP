using System.Text.Json;
using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Compatibility;

public static class CbftpSiteImporter
{
    public static List<Site> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var rows = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : new List<JsonElement> { root };
        if (rows.Count == 0) throw new ArgumentException("cbftp JSON contains no sites");
        return rows.Select(row => Apply(new Site(), row)).ToList();
    }

    public static Site Apply(Site site, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("each cbftp site must be a JSON object");

        if (TryString(root, out var text, "name")) site.Name = text.Trim();
        if (TryString(root, out text, "user", "username")) site.Username = text.Trim();
        if (TryString(root, out text, "password")) site.Password = text;
        if (TryString(root, out text, "base_path")) site.BasePath = text.Trim();
        if (TryString(root, out text, "list_command"))
            site.ListCommand = text.Trim().Equals("STAT_L", StringComparison.OrdinalIgnoreCase) ? "STAT -l" : text.Trim();
        if (TryString(root, out text, "tls_mode")) site.TlsMode = ParseTlsMode(text, site.TlsMode);

        ApplyAddress(site, root);

        if (TryInt(root, out var number, "max_logins")) site.LoginSlots = Math.Max(0, number);
        if (TryInt(root, out number, "max_sim_up")) site.UploadSlots = Math.Max(0, number);
        if (TryInt(root, out number, "max_sim_down")) site.DownloadSlots = Math.Max(0, number);
        if (TryInt(root, out number, "max_idle_time")) site.MaxIdleSeconds = Math.Max(0, number);

        if (TryBool(root, out var flag, "pret")) site.UsePret = flag;
        if (TryBool(root, out flag, "sscn"))
        {
            site.UseSscn = flag;
            site.SscnSupported = flag;
        }
        if (TryBool(root, out flag, "cpsv")) site.CpsvSupported = flag;
        if (TryBool(root, out flag, "cepr")) site.CeprSupported = flag;
        if (TryBool(root, out flag, "broken_pasv")) site.BrokenPasv = flag;
        if (TryBool(root, out flag, "force_binary_mode", "force_binary")) site.ForceBinary = flag;
        if (TryBool(root, out flag, "xdupe")) site.UseXdupe = flag;
        if (TryBool(root, out flag, "allow_upload")) site.BlockTransferTo = !flag;
        if (TryBool(root, out flag, "allow_download")) site.BlockTransferFrom = !flag;

        if (TryString(root, out text, "transfer_source_policy"))
            site.TransferSourcePolicy = ParsePolicy(text);
        if (TryString(root, out text, "transfer_target_policy"))
            site.TransferTargetPolicy = ParsePolicy(text);

        if (TryProperty(root, out _, "affils")) site.Affils = StringList(root, "affils");
        if (TryProperty(root, out _, "except_source_sites"))
            site.ExceptSourceSites = StringList(root, "except_source_sites");
        if (TryProperty(root, out _, "except_target_sites"))
            site.ExceptTargetSites = StringList(root, "except_target_sites");

        if (TryProperty(root, out var sections, "sections") && sections.ValueKind == JsonValueKind.Array)
        {
            site.Sections = sections.EnumerateArray()
                .Where(row => row.ValueKind == JsonValueKind.Object)
                .Select(row => new SiteSection
                {
                    Name = String(row, "name"),
                    Section = String(row, "path", "section"),
                })
                .Where(row => !string.IsNullOrWhiteSpace(row.Name) && !string.IsNullOrWhiteSpace(row.Section))
                .ToList();
        }

        if (TryProperty(root, out var skiplist, "skiplist") && skiplist.ValueKind == JsonValueKind.Array)
        {
            site.Skiplist = skiplist.EnumerateArray()
                .Where(row => row.ValueKind is JsonValueKind.String or JsonValueKind.Object)
                .Where(row => row.ValueKind != JsonValueKind.Object ||
                              !String(row, "action").Equals("ALLOW", StringComparison.OrdinalIgnoreCase))
                .Select(row => row.ValueKind == JsonValueKind.String ? row.GetString() ?? "" : String(row, "pattern"))
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return site.WithDefaults();
    }

    private static void ApplyAddress(Site site, JsonElement root)
    {
        var address = "";
        if (TryProperty(root, out var addresses, "addresses"))
        {
            address = addresses.ValueKind == JsonValueKind.Array
                ? addresses.EnumerateArray().Select(ValueText).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? ""
                : ValueText(addresses);
        }
        else if (TryString(root, out var host, "host")) address = host;

        if (!string.IsNullOrWhiteSpace(address))
        {
            var uriText = address.Contains("://", StringComparison.Ordinal) ? address : $"ftp://{address}";
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                throw new ArgumentException("addresses must contain a valid host[:port]");
            site.Host = uri.Host;
            site.Port = uri.IsDefaultPort ? 0 : uri.Port;
        }
        if (TryInt(root, out var port, "port")) site.Port = port;
    }

    private static bool TryProperty(JsonElement root, out JsonElement value, params string[] names)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryString(JsonElement root, out string value, params string[] names)
    {
        if (TryProperty(root, out var json, names) && json.ValueKind != JsonValueKind.Null)
        {
            value = ValueText(json);
            return true;
        }
        value = "";
        return false;
    }

    private static bool TryInt(JsonElement root, out int value, params string[] names)
    {
        if (TryProperty(root, out var json, names))
        {
            if (json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out value)) return true;
            var text = ValueText(json).Trim();
            if (text.Equals("ALL", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("UNLIMITED", StringComparison.OrdinalIgnoreCase))
            {
                value = 0;
                return true;
            }
            if (int.TryParse(text, out value)) return true;
        }
        value = 0;
        return false;
    }

    private static bool TryBool(JsonElement root, out bool value, params string[] names)
    {
        if (TryProperty(root, out var json, names))
        {
            if (json.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = json.GetBoolean();
                return true;
            }
            var text = ValueText(json).Trim();
            if (text.Equals("YES", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("ON", StringComparison.OrdinalIgnoreCase) || text == "1")
            {
                value = true;
                return true;
            }
            if (text.Equals("NO", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("OFF", StringComparison.OrdinalIgnoreCase) || text == "0")
            {
                value = false;
                return true;
            }
            if (bool.TryParse(text, out value)) return true;
        }
        value = false;
        return false;
    }

    private static string String(JsonElement root, params string[] names)
        => TryProperty(root, out var value, names) ? ValueText(value) : "";

    private static List<string> StringList(JsonElement root, params string[] names)
    {
        if (!TryProperty(root, out var value, names)) return new List<string>();
        var values = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(ValueText)
            : new[] { ValueText(value) };
        return values.SelectMany(SplitList).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> SplitList(string value) => value
        .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => item.Length > 0);

    private static string ValueText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };

    private static TlsMode ParseTlsMode(string value, TlsMode fallback) => value.Trim().ToUpperInvariant() switch
    {
        "AUTH_TLS" or "EXPLICIT" or "EXPLICIT_TLS" or "TLS" => TlsMode.Explicit,
        "IMPLICIT" or "IMPLICIT_TLS" => TlsMode.Implicit,
        "NONE" or "OFF" or "PLAIN" => TlsMode.Off,
        _ => fallback,
    };

    private static SiteTransferPolicy ParsePolicy(string value)
        => value.Trim().Equals("BLOCK", StringComparison.OrdinalIgnoreCase)
            ? SiteTransferPolicy.Block
            : SiteTransferPolicy.Allow;
}
