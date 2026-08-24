using WeaveFxp.Engine.Models;

namespace WeaveFxp.Web.Services;

// Shared browser-pane state, driven by the Browser page and rendered by PaneView.
public sealed class PaneModel
{
    public string Side { get; set; } = "";
    public string Site { get; set; } = "";
    public string LastSite { get; set; } = "";
    public string Path { get; set; } = "/";
    public List<RemoteEntry> Entries { get; set; } = new();
    public HashSet<int> Selected { get; set; } = new();
    public bool Loaded { get; set; }
    public bool Loading { get; set; }
    public string Status { get; set; } = "Idle";
    public string? Error { get; set; }
    public string SortColumn { get; set; } = "name";
    public bool SortDesc { get; set; }
    public List<string> ColumnOrder { get; set; } = new() { "name", "owner", "group", "size", "modified" };
    public HashSet<string> CompareMissing { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CompareDifferent { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CompareSame { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Anchor row for shift-range selection (last plain click).
    public int Anchor { get; set; } = -1;
}
