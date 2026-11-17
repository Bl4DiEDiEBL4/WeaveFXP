using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WeaveFxp.Engine.Models;

// cbftp-style skiplist. Rules are checked top to bottom, the FIRST matching rule
// decides, matching is case-insensitive. A pattern is plain text (matches anywhere in
// the name) or a * / ? wildcard, unless Regex is ticked: then it is a .NET regex such
// as (?i).*\.nfo$ . File / Dir choose which kind of entry a rule applies to.
//   Deny   - skip the entry (never raced, never descended into)
//   Allow  - keep the entry (stops evaluation, overrides rules further down)
//   Unique - only the FIRST matching entry per directory per race passes, the rest
//            are skipped (e.g. one .nfo / one .sfv; trash uploaded later is ignored)
public enum SkiplistAction { Deny, Allow, Unique }

[JsonConverter(typeof(SkiplistEntryJsonConverter))]
public sealed class SkiplistEntry
{
    public string Pattern { get; set; } = "";
    public bool Regex { get; set; }
    public bool File { get; set; } = true;
    public bool Dir { get; set; } = true;
    public SkiplistAction Action { get; set; } = SkiplistAction.Deny;

    public SkiplistEntry Clone() => new()
    {
        Pattern = Pattern, Regex = Regex, File = File, Dir = Dir, Action = Action,
    };

    public static SkiplistEntry DenyPattern(string pattern) => new() { Pattern = (pattern ?? "").Trim() };

    public static SkiplistAction ParseAction(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "allow" => SkiplistAction.Allow,
        "unique" => SkiplistAction.Unique,
        _ => SkiplistAction.Deny,
    };

    // Trim + drop empty rows. Order is meaningful (first match wins) and duplicates are
    // allowed, exactly like cbftp, so nothing is sorted or de-duplicated here.
    public static List<SkiplistEntry> NormalizeList(IEnumerable<SkiplistEntry?>? rows)
    {
        var result = new List<SkiplistEntry>();
        if (rows is null) return result;
        foreach (var row in rows)
        {
            if (row is null) continue;
            var pattern = (row.Pattern ?? "").Trim();
            if (pattern.Length == 0) continue;
            var copy = row.Clone();
            copy.Pattern = pattern;
            result.Add(copy);
        }
        return result;
    }
}

public enum SkiplistVerdict { NoMatch, Deny, Allow, Unique }

public readonly record struct SkiplistResult(SkiplistVerdict Verdict, int Index, SkiplistEntry? Entry);

public static class SkiplistRules
{
    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    // cbftp checks rules first, then the complete path and each slash-delimited suffix.
    // Scope always describes the tested entry, not an arbitrary parent directory.
    public static SkiplistResult Evaluate(IReadOnlyList<SkiplistEntry>? entries, string? name, bool isDir)
    {
        name = (name ?? "").Trim();
        if (entries is null || name.Length == 0) return new(SkiplistVerdict.NoMatch, -1, null);
        name = name.Replace('\\', '/').TrimEnd('/');
        var suffixes = new List<string> { name };
        for (var slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
            if (slash + 1 < name.Length) suffixes.Add(name[(slash + 1)..]);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry is null || string.IsNullOrWhiteSpace(entry.Pattern)) continue;
            if (isDir ? !entry.Dir : !entry.File) continue;
            if (!suffixes.Any(suffix => Matches(entry, suffix))) continue;
            var verdict = entry.Action switch
            {
                SkiplistAction.Allow => SkiplistVerdict.Allow,
                SkiplistAction.Unique => SkiplistVerdict.Unique,
                _ => SkiplistVerdict.Deny,
            };
            return new(verdict, i, entry);
        }
        return new(SkiplistVerdict.NoMatch, -1, null);
    }

    // rel is relative to the release root ("Sample/x.mkv" or "x.mkv"). Like cbftp, every
    // parent path is checked as a directory, the leaf as file (or dir). Only a Deny
    // blocks here; Unique is resolved per destination with UniqueBlocks.
    public static bool Blocks(IReadOnlyList<SkiplistEntry>? entries, string? rel, bool isDir = false)
    {
        if (entries is null || entries.Count == 0) return false;
        var parts = (rel ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        for (var i = 0; i < parts.Length - 1; i++)
            if (Evaluate(entries, string.Join('/', parts.Take(i + 1)), isDir: true).Verdict == SkiplistVerdict.Deny) return true;
        return Evaluate(entries, string.Join('/', parts), isDir).Verdict == SkiplistVerdict.Deny;
    }

    public static bool Matches(SkiplistEntry entry, string name)
    {
        var pattern = (entry.Pattern ?? "").Trim();
        if (pattern.Length == 0 || name.Length == 0) return false;
        if (entry.Regex)
        {
            // std::regex_match in cbftp matches a complete suffix, including alternations.
            var rx = CompiledRegex(@"\A(?:" + pattern + @")\z");
            if (rx is null) return false; // invalid regex never matches (the editor flags it)
            try { return rx.IsMatch(name); }
            catch (RegexMatchTimeoutException) { return false; }
        }
        // Non-regex wildcards never cross '/', as in cbftp's fixedSlashCompare.
        if (pattern.Count(c => c == '/') != name.Count(c => c == '/')) return false;
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            var rx = CompiledRegex("^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "$");
            try { return rx is not null && rx.IsMatch(name); }
            catch (RegexMatchTimeoutException) { return false; }
        }
        return name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    // Unique (cbftp): a file whose first matching rule is Unique may only land in a
    // destination directory that holds no OTHER file matching that same rule. destRels are
    // the release-relative files known on that destination; state + destKey also stop two
    // different matches being sent there in parallel (first claim of the race wins).
    // This one CLAIMS the slot - call it where a transfer is actually picked.
    public static bool UniqueBlocks(IReadOnlyList<SkiplistEntry>? entries, string rel,
        IEnumerable<string> destRels, SkiplistUniqueState state, string destKey) =>
        UniqueCheck(entries, rel, destRels, state, destKey, claim: true);

    // Side-effect free twin for coverage/completion checks: true when this file is not
    // needed on the destination because another matching entry is actually present.
    public static bool UniqueSatisfied(IReadOnlyList<SkiplistEntry>? entries, string rel,
        IEnumerable<string> destRels, SkiplistUniqueState state, string destKey) =>
        UniqueCheck(entries, rel, destRels, state, destKey, claim: false);

    private static bool UniqueCheck(IReadOnlyList<SkiplistEntry>? entries, string rel,
        IEnumerable<string> destRels, SkiplistUniqueState state, string destKey, bool claim)
    {
        if (entries is null || entries.Count == 0) return false;
        var parts = (rel ?? "").Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var existing = destRels.Select(r => r.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        for (var depth = 0; depth < parts.Length; depth++)
        {
            var isDir = depth < parts.Length - 1;
            var candidate = string.Join('/', parts.Take(depth + 1));
            var result = Evaluate(entries, candidate, isDir);
            if (result.Verdict != SkiplistVerdict.Unique || result.Entry is null) continue;
            var parent = string.Join('/', parts.Take(depth));
            foreach (var other in existing)
            {
                if (other.Length <= depth || (other.Length > depth + 1) != isDir ||
                    !string.Join('/', other.Take(depth)).Equals(parent, StringComparison.OrdinalIgnoreCase) ||
                    other[depth].Equals(parts[depth], StringComparison.OrdinalIgnoreCase)) continue;
                var otherResult = Evaluate(entries, string.Join('/', other.Take(depth + 1)), isDir);
                if (otherResult.Verdict == SkiplistVerdict.Unique && otherResult.Index == result.Index) return true;
            }
            // Claims prevent concurrent uploads but are not evidence of completed files.
            if (claim && !state.TryPass(result.Entry, destKey + "|" + parent,
                    candidate)) return true;
        }
        return false;
    }

    // Null when the row is fine, otherwise a short message for the editor.
    public static string? ValidationError(SkiplistEntry? entry)
    {
        if (entry is null || !entry.Regex || string.IsNullOrWhiteSpace(entry.Pattern)) return null;
        try
        {
            _ = new Regex(entry.Pattern.Trim(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            return null;
        }
        catch (ArgumentException ex) { return "Invalid regex: " + ex.Message; }
    }

    private static Regex? CompiledRegex(string pattern) => RegexCache.GetOrAdd(pattern, p =>
    {
        try { return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout); }
        catch (ArgumentException) { return null; }
    });
}

// Per-race memory for Unique rules: per destination directory the first file that
// claims a rule's slot wins for the whole race, so a second .nfo is never spread there.
public sealed class SkiplistUniqueState
{
    private readonly ConcurrentDictionary<string, string> _winners = new(StringComparer.OrdinalIgnoreCase);

    public bool TryPass(SkiplistEntry rule, string slotKey, string rel) =>
        _winners.GetOrAdd(Key(rule, slotKey), rel).Equals(rel, StringComparison.OrdinalIgnoreCase);

    public bool ClaimedByOther(SkiplistEntry rule, string slotKey, string rel) =>
        _winners.TryGetValue(Key(rule, slotKey), out var winner) &&
        !winner.Equals(rel, StringComparison.OrdinalIgnoreCase);

    private static string Key(SkiplistEntry rule, string slotKey) =>
        (rule.Regex ? "re:" : "wc:") + (rule.Pattern ?? "").Trim() + $"|{rule.File}|{rule.Dir}|" + slotKey;
}

// Back-compat: older configs stored skiplists as plain strings ("Sample", "*proof*").
// A bare string reads as Deny / file+dir / plain-or-wildcard, objects read fully.
internal sealed class SkiplistEntryJsonConverter : JsonConverter<SkiplistEntry>
{
    public override SkiplistEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return SkiplistEntry.DenyPattern(reader.GetString() ?? "");
        var entry = new SkiplistEntry();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return entry;
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var property = (reader.GetString() ?? "").ToLowerInvariant();
            reader.Read();
            switch (property)
            {
                case "pattern": entry.Pattern = (ReadText(ref reader) ?? "").Trim(); break;
                case "regex": entry.Regex = ReadBool(ref reader, false); break;
                case "file": entry.File = ReadBool(ref reader, true); break;
                case "dir": entry.Dir = ReadBool(ref reader, true); break;
                case "action": entry.Action = SkiplistEntry.ParseAction(ReadText(ref reader)); break;
                default: reader.Skip(); break;
            }
        }
        return entry;
    }

    public override void Write(Utf8JsonWriter writer, SkiplistEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("pattern", value.Pattern ?? "");
        writer.WriteBoolean("regex", value.Regex);
        writer.WriteBoolean("file", value.File);
        writer.WriteBoolean("dir", value.Dir);
        writer.WriteString("action", value.Action.ToString().ToLowerInvariant());
        writer.WriteEndObject();
    }

    private static string? ReadText(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Null => null,
        _ => SkipAndNull(ref reader),
    };

    private static bool ReadBool(ref Utf8JsonReader reader, bool fallback) => reader.TokenType switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        JsonTokenType.String => (reader.GetString() ?? "").Trim().ToLowerInvariant() is "true" or "yes" or "1" or "on"
            || ((reader.GetString() ?? "").Trim().Length == 0 && fallback),
        _ => SkipAndFallback(ref reader, fallback),
    };

    private static string? SkipAndNull(ref Utf8JsonReader reader) { reader.Skip(); return null; }
    private static bool SkipAndFallback(ref Utf8JsonReader reader, bool fallback) { reader.Skip(); return fallback; }
}
