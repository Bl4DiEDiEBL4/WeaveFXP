using WeaveFxp.Engine.Models;

namespace WeaveFxp.Web.Services;

// Shared browser-pane state, driven by the Browser page and rendered by PaneView.
public sealed class PaneModel
{
    public string Side { get; set; } = "";
    public string Site { get; set; } = "";
    public string Path { get; set; } = "/";
    public List<RemoteEntry> Entries { get; set; } = new();
    public HashSet<int> Selected { get; set; } = new();
    public bool Loaded { get; set; }
    public bool Loading { get; set; }
    public string Status { get; set; } = "Idle";
    public string? Error { get; set; }
    public string SortColumn { get; set; } = "name";
    public bool SortDesc { get; set; }

    // Anchor row for shift-range selection (last plain click).
    public int Anchor { get; set; } = -1;
}
