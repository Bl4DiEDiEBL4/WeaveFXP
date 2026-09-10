using System.Text.RegularExpressions;
using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Core;

internal sealed class SiteSearchFilter
{
    private readonly SiteSearchRequest _request;
    private readonly Regex[] _include;
    private readonly Regex[] _exclude;
    private readonly DateTime? _oldest;

    public SiteSearchFilter(SiteSearchRequest request)
    {
        _request = request;
        if (request.Kind is not ("both" or "file" or "dir")) throw new ArgumentException("kind must be both, file or dir");
        if (request.MaxDepth is < 0 or > 32) throw new ArgumentException("max_depth must be between 0 and 32");
        if (request.MaxResults is < 1 or > 10000) throw new ArgumentException("max_results must be between 1 and 10000");
        if (request.MinBytes < 0 || request.MaxBytes < 0 || request.MinBytes > request.MaxBytes)
            throw new ArgumentException("Invalid file size range");
        if (request.NotOlderThanDays < 0 || request.NotOlderThanDays > 36500 || request.DateFrom?.Date > request.DateTo?.Date)
            throw new ArgumentException("Invalid date range");
        _oldest = request.NotOlderThanDays is { } days ? DateTime.Now.AddDays(-days) : null;
        _include = Compile(request.Include);
        _exclude = Compile(request.Exclude);
    }

    private Regex[] Compile(string patterns)
    {
        if (patterns.Length > 4096) throw new ArgumentException("Search patterns exceed 4096 characters");
        var options = RegexOptions.CultureInvariant | (_request.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        return patterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pattern => new Regex(_request.Regex ? pattern :
                "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", options, TimeSpan.FromMilliseconds(100))).ToArray();
    }

    public bool Excluded(string name) => _exclude.Any(regex => regex.IsMatch(name));

    public bool Matches(RemoteEntry entry)
    {
        if (entry.Type is not ("file" or "dir") || Excluded(entry.Name)) return false;
        if (_request.Kind != "both" && entry.Type != _request.Kind) return false;
        if (_include.Length > 0 && !_include.Any(regex => regex.IsMatch(entry.Name))) return false;
        if (_request.DateFrom is not null || _request.DateTo is not null || _oldest is not null)
        {
            if (entry.Modified == default) return false;
            if (entry.Modified.Date < _request.DateFrom?.Date || entry.Modified.Date > _request.DateTo?.Date || entry.Modified < _oldest) return false;
        }
        if (entry.Type == "file" && (entry.Size < _request.MinBytes || entry.Size > _request.MaxBytes)) return false;
        return true;
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Any(char.IsControl))
            throw new ArgumentException("Search paths must be absolute remote paths without control characters");
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or "..")) throw new ArgumentException("Relative path components are not allowed");
        return "/" + string.Join('/', parts);
    }

    internal static bool SafeName(string name) => name.Length > 0 && name is not ("." or "..") &&
        !name.Contains('/') && !name.Contains('\\') && !name.Any(char.IsControl);
}
