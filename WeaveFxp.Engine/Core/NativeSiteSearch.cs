using System.Text.RegularExpressions;
using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Core;

internal static class NativeSiteSearch
{
    public static string[] Queries(SiteSearchRequest request)
    {
        if (request.Regex || request.CaseSensitive || request.Kind == "file" || request.DateFrom is not null || request.DateTo is not null ||
            request.NotOlderThanDays is not null || request.MinBytes is not null || request.MaxBytes is not null)
            throw new ArgumentException("File, regex, case-sensitive, date and size filters require recursive search");
        if (request.Include.Any(char.IsControl)) throw new ArgumentException("Search terms cannot contain control characters");
        var terms = request.Include.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (terms.Length is < 1 or > 20 || terms.Any(term => term.Length > 256 || term.Contains('*') || term.Contains('?')))
            throw new ArgumentException("SITE SEARCH requires 1-20 plain search terms separated by semicolons; enable recursive search for wildcards");
        return terms;
    }

    public static List<RemoteEntry> Parse(string response)
    {
        var entries = new List<RemoteEntry>();
        foreach (var raw in response.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length >= 4 && line.Take(3).All(char.IsDigit) && line[3] is '-' or ' ') line = line[4..].Trim();
            if (!line.StartsWith('/')) continue;
            // glFTPd/WeaveFTPD append (Files/Megs/Age) to each directory path.
            line = Regex.Replace(line, @"\s+\(\d+F/[^\r\n]*\)$", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            string path;
            try { path = SiteSearchFilter.NormalizePath(line); }
            catch (ArgumentException) { continue; }
            var name = path.Split('/').Last();
            if (!SiteSearchFilter.SafeName(name)) continue;
            entries.Add(new RemoteEntry { Name = name, Path = path, Type = "dir" });
        }
        if (entries.Count == 0 && !Regex.IsMatch(response, @"\b(?:0\s+(?:directories|directory|dirs?|matches|results?)\s+found|no\s+(?:matching\s+)?(?:directories|dirs?|matches|results?)(?:\s+found)?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new IOException("Unrecognized SITE SEARCH response; recursive search is available as an explicit alternative");
        return entries;
    }
}
