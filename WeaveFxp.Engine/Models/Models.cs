using System.Text.Json.Serialization;

namespace WeaveFxp.Engine.Models;

// ---- enums ----------------------------------------------------------------------------

public enum TlsMode { Off, Explicit, Implicit }

public enum FxpMode { Auto, PasvPort, CpsvSsc }

// Which side opens the listening (passive) data socket in an FXP.
// Default: the SOURCE is passive and the destination connects to it unless the source
// site has broken PASV.
public enum FxpPassiveSide { Auto, Source, Destination }

// Which side acts as the TLS client on the FXP data channel. Auto uses the passive side.
public enum SslDataClientSide { Auto, Source, Destination }

public enum TransferProtocol { PreferIPv4, IPv4Only, IPv6Only, Any }

public enum ApiListenMode { Local, All, Interface }

public enum JobType { Fxp, Race, Download }

public enum JobState { Queued, Running, Succeeded, Failed, Cancelled }

public enum ReleaseState { Unknown, Incomplete, Complete }

// ---- settings -------------------------------------------------------------------------

public sealed class PortRange
{
    public int Start { get; set; } = 47700;
    public int End { get; set; } = 47800;
}

public sealed class AppSettings
{
    public string WebBindAddress { get; set; } = "127.0.0.1";
    public int WebPort { get; set; } = 8788;
    public string BindInterface { get; set; } = "";
    public TransferProtocol LocalTransferProtocol { get; set; } = TransferProtocol.PreferIPv4;
    public PortRange ActiveModePortRange { get; set; } = new();
    public bool UseActiveModeAddress { get; set; } = true;
    public string ActiveModeAddressIPv4 { get; set; } = "";
    public string ActiveModeAddressIPv6 { get; set; } = "";
    public bool EnableHttpsJsonApi { get; set; } = true;
    public int HttpsJsonApiPort { get; set; } = 59010;
    public bool EnableUdpApi { get; set; }
    public string UdpApiMode { get; set; } = "plaintext";
    public int UdpApiPort { get; set; } = 59010;

    // Never serialized to the client; only ever written back from the settings form.
    public string ApiPassword { get; set; } = "";
    public bool ApiPasswordSet { get; set; }

    public ApiListenMode ApiListeningMode { get; set; } = ApiListenMode.All;
    public string DownloadDir { get; set; } = "downloads";
    public int MaxConcurrentFxpJobs { get; set; } = 2;
    public int MaxConcurrentRaceJobs { get; set; } = 2;
    // Race loop: how often to re-list the source for new files, and how many
    // consecutive no-new-file cycles to allow before giving up.
    // Verbose FTP protocol logging (every command/response into the FTP Log). Costs
    // real throughput and makes the UI crawl during a race — debugging only.
    public bool FtpDebugLog { get; set; }
    // Learned "source>dest" FXP TLS client orientations (see FxpTransfer).
    public Dictionary<string, bool> FxpTlsRoleFlip { get; set; } = new();
    public int RacePollIntervalMs { get; set; } = 500;
    public int RaceMaxIdleCycles { get; set; } = 30;
    public int JobWatchdogTimeoutMinutes { get; set; } = 120;
    public bool SkipEmptyFolders { get; set; } = true;
    public List<string> GlobalSkiplist { get; set; } = new();
    public List<string> GlobalOrderList { get; set; } = new();
    public List<string> SiteOrder { get; set; } = new();
    public bool DebugLogging { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public bool TrayIconEnabled { get; set; } = true;
    public bool TransferNotificationsEnabled { get; set; } = true;
    public bool ApiNotificationsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public AppSettings WithDefaults()
    {
        WebBindAddress = string.IsNullOrWhiteSpace(WebBindAddress) ? "127.0.0.1" : WebBindAddress.Trim();
        BindInterface = (BindInterface ?? "").Trim();
        ActiveModeAddressIPv4 = (ActiveModeAddressIPv4 ?? "").Trim();
        ActiveModeAddressIPv6 = (ActiveModeAddressIPv6 ?? "").Trim();
        ApiPassword = (ApiPassword ?? "").Trim();
        UdpApiMode = string.IsNullOrWhiteSpace(UdpApiMode) ? "plaintext" : UdpApiMode.Trim();
        DownloadDir = string.IsNullOrWhiteSpace(DownloadDir) ? "downloads" : DownloadDir.Trim();
        ActiveModePortRange ??= new PortRange();
        if (ActiveModePortRange.Start == 0) ActiveModePortRange.Start = 47700;
        if (ActiveModePortRange.End == 0) ActiveModePortRange.End = 47800;
        if (WebPort == 0) WebPort = 8788;
        if (HttpsJsonApiPort == 0) HttpsJsonApiPort = 59010;
        if (UdpApiPort == 0) UdpApiPort = 59010;
        if (MaxConcurrentFxpJobs == 0) MaxConcurrentFxpJobs = 2;
        if (MaxConcurrentRaceJobs == 0) MaxConcurrentRaceJobs = 2;
        FxpTlsRoleFlip ??= new Dictionary<string, bool>();
        if (RacePollIntervalMs == 0) RacePollIntervalMs = 500;
        RacePollIntervalMs = Math.Clamp(RacePollIntervalMs, 100, 30000);
        if (RaceMaxIdleCycles == 0) RaceMaxIdleCycles = 30;
        RaceMaxIdleCycles = Math.Clamp(RaceMaxIdleCycles, 1, 100000);
        if (JobWatchdogTimeoutMinutes == 0) JobWatchdogTimeoutMinutes = 120;
        JobWatchdogTimeoutMinutes = Math.Clamp(JobWatchdogTimeoutMinutes, 5, 10080);
        GlobalSkiplist = NormalizeList(GlobalSkiplist);
        GlobalOrderList = NormalizeList(GlobalOrderList);
        SiteOrder = NormalizeList(SiteOrder);
        var now = DateTime.UtcNow;
        if (CreatedAt == default) CreatedAt = now;
        UpdatedAt = now;
        return this;
    }

    private static List<string> NormalizeList(IEnumerable<string>? values)
    {
        return (values ?? Enumerable.Empty<string>())
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Validate()
    {
        if (WebPort is < 1 or > 65535)
            throw new ArgumentException("web port must be within 1-65535");
        if (ActiveModePortRange.Start < 1 || ActiveModePortRange.End > 65535 ||
            ActiveModePortRange.Start > ActiveModePortRange.End)
            throw new ArgumentException("active mode port range must be within 1-65535");
        if (HttpsJsonApiPort is < 1 or > 65535)
            throw new ArgumentException("https/json api port must be within 1-65535");
        if (MaxConcurrentFxpJobs < 1)
            throw new ArgumentException("max concurrent fxp jobs must be at least 1");
        if (MaxConcurrentRaceJobs < 1)
            throw new ArgumentException("max concurrent race jobs must be at least 1");
        if (JobWatchdogTimeoutMinutes < 5)
            throw new ArgumentException("job watchdog timeout must be at least 5 minutes");
    }

    // Public() strips the password and reports only whether one is set.
    public AppSettings Public()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.ApiPasswordSet = !string.IsNullOrEmpty(ApiPassword);
        clone.ApiPassword = "";
        clone.ActiveModePortRange = new PortRange { Start = ActiveModePortRange.Start, End = ActiveModePortRange.End };
        clone.GlobalSkiplist = new List<string>(GlobalSkiplist);
        clone.GlobalOrderList = new List<string>(GlobalOrderList);
        clone.SiteOrder = new List<string>(SiteOrder);
        return clone;
    }
}

// ---- site -----------------------------------------------------------------------------

public sealed class SiteSection
{
    public string Name { get; set; } = "";
    public string Section { get; set; } = "";
}

public sealed class Site
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 21;
    public string Username { get; set; } = "anonymous";
    public string Password { get; set; } = "";
    public TlsMode TlsMode { get; set; } = TlsMode.Off;
    public bool UsePret { get; set; }
    public bool UseEpsv { get; set; }
    public bool UseSscn { get; set; }
    public bool CeprSupported { get; set; }
    public bool SscnSupported { get; set; }
    public bool CpsvSupported { get; set; }
    public FxpMode FxpMode { get; set; } = FxpMode.Auto;
    public string PassiveHost { get; set; } = "";
    public string BasePath { get; set; } = "/";
    public string ListCommand { get; set; } = "STAT -l";
    public int LoginSlots { get; set; } = 1;
    public int UploadSlots { get; set; }
    public int DownloadSlots { get; set; } = 1;
    public bool ForceBinary { get; set; }
    public bool BrokenPasv { get; set; }
    // FXP data-channel roles. Auto = source passive unless it has broken PASV; the
    // passive side is the TLS client.
    public FxpPassiveSide FxpPassiveSide { get; set; } = FxpPassiveSide.Auto;
    public SslDataClientSide SslDataClient { get; set; } = SslDataClientSide.Auto;
    public bool UseXdupe { get; set; }
    public int XdupeMode { get; set; } = 3;
    public int MaxIdleSeconds { get; set; } = 30;
    public bool AllowUpload { get; set; }
    public bool AllowDownload { get; set; } = true;
    // Racing direction blocks: never send TO this site / never use this site as a source.
    public bool BlockTransferTo { get; set; }
    public bool BlockTransferFrom { get; set; }
    // Slow-skip: abort a race transfer that averages below this (KB/s) and move on.
    // FXP bytes don't pass through us, so this is enforced as a per-file time budget
    // (size / threshold + grace). 0 = off.
    public int SlowSkipKBps { get; set; }
    public List<string> CompleteMarkers { get; set; } = DefaultCompleteMarkers();
    public List<string> Affils { get; set; } = new();
    public List<string> Skiplist { get; set; } = new();
    public List<SiteSection> Sections { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 30;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Site WithDefaults()
    {
        Name = (Name ?? "").Trim();
        Host = (Host ?? "").Trim();
        Username = (Username ?? "").Trim();
        PassiveHost = (PassiveHost ?? "").Trim();
        BasePath = (BasePath ?? "").Trim();
        ListCommand = (ListCommand ?? "").Trim();
        if (Port == 0) Port = TlsMode == TlsMode.Implicit ? 990 : 21;
        if (string.IsNullOrEmpty(Username)) Username = "anonymous";
        if (string.IsNullOrEmpty(ListCommand)) ListCommand = "STAT -l";
        if (LoginSlots == 0) LoginSlots = 1;
        if (DownloadSlots == 0) DownloadSlots = 1;
        if (MaxIdleSeconds == 0) MaxIdleSeconds = 30;
        if (XdupeMode == 0) XdupeMode = 3;
        if (TimeoutSeconds == 0) TimeoutSeconds = 30;
        Sections = NormalizeSections(Sections);
        CompleteMarkers = NormalizeList(CompleteMarkers);
        if (CompleteMarkers.Count == 0) CompleteMarkers = DefaultCompleteMarkers();
        Affils = NormalizeList(Affils);
        Skiplist = NormalizeList(Skiplist);
        var now = DateTime.UtcNow;
        if (CreatedAt == default) CreatedAt = now;
        UpdatedAt = now;
        return this;
    }

    public static List<string> DefaultCompleteMarkers() => new() { "COMPLETE", "_COMPLETE", ".complete" };

    private static List<string> NormalizeList(IEnumerable<string>? values)
    {
        return (values ?? Enumerable.Empty<string>())
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<SiteSection> NormalizeSections(IEnumerable<SiteSection>? rows)
    {
        var result = new List<SiteSection>();
        if (rows is null) return result;
        foreach (var row in rows)
        {
            var name = (row?.Name ?? "").Trim();
            var section = (row?.Section ?? "").Trim();
            if (name.Length == 0 && section.Length == 0) continue;
            result.Add(new SiteSection
            {
                Name = name.Length > 0 ? name : section,
                Section = section.Length > 0 ? section : name,
            });
        }
        return result;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("site name is required");
        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("site host is required");
        if (Port is < 1 or > 65535) throw new ArgumentException("site port must be between 1 and 65535");
        if (LoginSlots < 0 || UploadSlots < 0 || DownloadSlots < 0)
            throw new ArgumentException("site slot counts cannot be negative");
        if (XdupeMode is < 0 or > 4) throw new ArgumentException("xdupe mode must be between 0 and 4");
    }

    public string Address() => $"{Host}:{Port}";

    // Public() clears the password for API responses.
    public Site Public()
    {
        var clone = (Site)MemberwiseClone();
        clone.Password = "";
        clone.Sections = new List<SiteSection>(Sections);
        clone.CompleteMarkers = new List<string>(CompleteMarkers);
        clone.Affils = new List<string>(Affils);
        clone.Skiplist = new List<string>(Skiplist);
        return clone;
    }
}

// ---- remote listing -------------------------------------------------------------------

public sealed class RemoteEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "unknown"; // dir | file | link | unknown
    public string LinkTarget { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public string Raw { get; set; } = "";
}

// ---- transfers & jobs -----------------------------------------------------------------

public sealed class TransferRequest
{
    public string BatchId { get; set; } = "";
    public string FromSite { get; set; } = "";
    public string ToSite { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DestPath { get; set; } = "";
    public bool Race { get; set; }
    public bool DryRun { get; set; }
    public string Label { get; set; } = "";
    // Set for jobs triggered over the API. Those are
    // latency-critical, so protocol tracing stays off for them; manual jobs from the
    // browser keep their FTP Log.
    public bool ViaApi { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FromSite)) throw new ArgumentException("from_site is required");
        if (string.IsNullOrWhiteSpace(ToSite)) throw new ArgumentException("to_site is required");
        if (string.IsNullOrWhiteSpace(SourcePath)) throw new ArgumentException("source_path is required");
        if (string.IsNullOrWhiteSpace(DestPath)) throw new ArgumentException("dest_path is required");
    }
}

public sealed class SpreadRequest
{
    public string FromSite { get; set; } = "";
    public List<string> ToSites { get; set; } = new();
    public string SourcePath { get; set; } = "";
    public string DestPath { get; set; } = "";
    public bool Race { get; set; }
    public bool DryRun { get; set; }
    public string Label { get; set; } = "";
    public int MaxParallel { get; set; }
    public bool ViaApi { get; set; }
}

public sealed class SpreadResult
{
    public string BatchId { get; set; } = "";
    public int MaxParallel { get; set; }
    public List<Job> Jobs { get; set; } = new();
}

public sealed class DownloadRequest
{
    public string Site { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DestPath { get; set; } = "";
    public string Label { get; set; } = "";
    public bool ViaApi { get; set; }
}

public sealed class JobEvent
{
    public DateTime Time { get; set; }
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
}

public sealed class Job
{
    public string Id { get; set; } = "";
    public string BatchId { get; set; } = "";
    public JobType Type { get; set; }
    public JobState State { get; set; } = JobState.Queued;
    public TransferRequest Request { get; set; } = new();
    public List<JobEvent> Events { get; set; } = new();
    public string Error { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public DateTime HeartbeatAt { get; set; }

    // Live progress. For downloads BytesDone/BytesTotal track the file currently
    // streaming; CumulativeBytes is everything moved so far and SpeedBps is recent
    // throughput. For FXP (server-to-server, no byte stream through us) only
    // FilesDone/FilesTotal are meaningful — the UI then shows a file-count bar.
    public long BytesDone { get; set; }
    public long BytesTotal { get; set; }
    public long CumulativeBytes { get; set; }
    public double SpeedBps { get; set; }
    public int FilesDone { get; set; }
    public int FilesTotal { get; set; }
    public string CurrentFile { get; set; } = "";
    public bool Paused { get; set; }

    // Per-connection ("thread") live progress for parallel downloads — one entry per
    // active slot, RushFTP style. Empty when idle or for single-stream jobs.
    public List<SlotProgress> Slots { get; set; } = new();

    // Per-file transfer rows for races: every attempt, with size, duration and speed
    // snapshot once done. Capped in the engine.
    public List<FileTransfer> Files { get; set; } = new();

    // 0..100, or -1 when indeterminate (unknown total).
    public int Percent
    {
        get
        {
            if (BytesTotal > 0) return (int)Math.Clamp(BytesDone * 100 / BytesTotal, 0, 100);
            if (FilesTotal > 0) return (int)Math.Clamp((long)FilesDone * 100 / FilesTotal, 0, 100);
            return -1;
        }
    }

    public bool Terminal => State is JobState.Succeeded or JobState.Failed or JobState.Cancelled;
}

// Hourly traffic bucket for one site — feeds the Stats page.
public sealed class SiteHourStat
{
    public string Site { get; set; } = "";
    public DateTime HourUtc { get; set; }
    public long OutBytes { get; set; }   // sent FROM this site (FXP source, downloads)
    public long InBytes { get; set; }    // received TO this site (FXP destination)
    public int Files { get; set; }
    public double Seconds { get; set; }  // summed transfer durations, for avg speed
}

// One file transfer inside a race.
// A new row is added per attempt (failed attempts stay visible), duration and the
// speed snapshot (size / duration) are filled in when the file completes.
public sealed class FileTransfer
{
    public DateTime StartedAt { get; set; }
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public double Seconds { get; set; }              // 0 while in flight
    public double Bps { get; set; }                  // snapshot: size / duration
    public string Status { get; set; } = "active";   // active | done | fail | dupe | wait
    public string Error { get; set; } = "";
}

// One download connection's live state (file, bytes, speed).
public sealed class SlotProgress
{
    public int Slot { get; set; }
    public string File { get; set; } = "";
    public long Done { get; set; }
    public long Total { get; set; }
    public double Bps { get; set; }
    public int Percent => Total > 0 ? (int)Math.Clamp(Done * 100 / Total, 0, 100) : -1;
}

// ---- log entries ----------------------------------------------------------------------

public sealed class LogEntry
{
    public long Seq { get; set; }
    public DateTime Time { get; set; }
    public string Category { get; set; } = "system"; // ftp | transfer | system
    public string Site { get; set; } = "";
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
}

public sealed class DataMaintenanceResult
{
    public int Logs { get; set; }
    public int Jobs { get; set; }
    public int Releases { get; set; }
    public int Dupes { get; set; }
}

// ---- probe / dupe / release -----------------------------------------------------------

public sealed class ProbeCommandResult
{
    public string Command { get; set; } = "";
    public int Code { get; set; }
    public string Message { get; set; } = "";
    public bool Ok { get; set; }
}

public sealed class SiteProbe
{
    public string Site { get; set; } = "";
    public List<string> Features { get; set; } = new();
    public List<ProbeCommandResult> Results { get; set; } = new();
    public DateTime CheckedAt { get; set; }
}

public sealed class DupeResult
{
    public string Site { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Exists { get; set; }
    public List<RemoteEntry> Matches { get; set; } = new();
    public DateTime CheckedAt { get; set; }
}

public sealed class RawCommandResult
{
    public string Site { get; set; } = "";
    public string Command { get; set; } = "";
    public int Code { get; set; }
    public string Message { get; set; } = "";
    public bool Ok { get; set; }
    public DateTime ExecutedAt { get; set; }
}

public sealed class SfvFile
{
    public string Name { get; set; } = "";
    public string Crc { get; set; } = "";
    public bool Seen { get; set; }
}

public sealed class ReleaseCheck
{
    public string Site { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public ReleaseState State { get; set; } = ReleaseState.Unknown;
    public List<RemoteEntry> Files { get; set; } = new();
    public List<SfvFile> Sfv { get; set; } = new();
    public List<string> Missing { get; set; } = new();
    public List<string> Markers { get; set; } = new();
    public DateTime CheckedAt { get; set; }
    public string Description { get; set; } = "";
}

public sealed class NetworkInterfaceInfo
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Loopback { get; set; }
    public bool IPv6 { get; set; }
}
