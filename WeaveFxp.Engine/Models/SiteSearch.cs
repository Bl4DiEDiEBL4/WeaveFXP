using System.Text.Json.Serialization;

namespace WeaveFxp.Engine.Models;

public sealed record SiteSearchRequest
{
    public List<string> Sites { get; init; } = new();
    public List<string> Paths { get; init; } = new() { "/" };
    public string Include { get; init; } = "";
    public string Exclude { get; init; } = "";
    public bool Regex { get; init; }
    public bool Recursive { get; init; }
    [JsonPropertyName("case_sensitive")] public bool CaseSensitive { get; init; }
    public string Kind { get; init; } = "both";
    [JsonPropertyName("max_depth")] public int MaxDepth { get; init; } = 4;
    [JsonPropertyName("date_from")] public DateTime? DateFrom { get; init; }
    [JsonPropertyName("date_to")] public DateTime? DateTo { get; init; }
    [JsonPropertyName("not_older_than_days")] public int? NotOlderThanDays { get; init; }
    [JsonPropertyName("min_bytes")] public long? MinBytes { get; init; }
    [JsonPropertyName("max_bytes")] public long? MaxBytes { get; init; }
    [JsonPropertyName("max_results")] public int MaxResults { get; init; } = 1000;
}

public sealed record SiteSearchResult(int Id, string Site, string Name, string Path, string Kind,
    long Size, DateTime Modified, string Owner, string Group);

public sealed record SiteSearchSnapshot(string Id, string Status, SiteSearchRequest Request,
    [property: JsonPropertyName("started_at")] DateTime StartedAt,
    [property: JsonPropertyName("directories_scanned")] int DirectoriesScanned,
    [property: JsonPropertyName("pending_directories")] int PendingDirectories,
    [property: JsonPropertyName("total_matches")] int TotalMatches,
    [property: JsonPropertyName("current_path")] string CurrentPath, List<SiteSearchResult> Results, List<string> Errors,
    [property: JsonPropertyName("commands_completed")] int CommandsCompleted = 0,
    [property: JsonPropertyName("commands_pending")] int CommandsPending = 0);

public sealed record SiteSearchQueueRequest
{
    [JsonPropertyName("result_ids")] public List<int> ResultIds { get; init; } = new();
    [JsonPropertyName("destination_site")] public string DestinationSite { get; init; } = "local";
    [JsonPropertyName("destination_path")] public string DestinationPath { get; init; } = "";
}
