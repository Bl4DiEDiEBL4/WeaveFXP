using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Ftp;

public sealed class FtpEndpoint
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
}

public enum ResponseTraceMode { Full, ListingSummary }

public sealed class Cp437Encoding : Encoding
{
    public static readonly Cp437Encoding Instance = new();

    private static readonly char[] Table =
    {
        '\u00C7','\u00FC','\u00E9','\u00E2','\u00E4','\u00E0','\u00E5','\u00E7',
        '\u00EA','\u00EB','\u00E8','\u00EF','\u00EE','\u00EC','\u00C4','\u00C5',
        '\u00C9','\u00E6','\u00C6','\u00F4','\u00F6','\u00F2','\u00FB','\u00F9',
        '\u00FF','\u00D6','\u00DC','\u00A2','\u00A3','\u00A5','\u20A7','\u0192',
        '\u00E1','\u00ED','\u00F3','\u00FA','\u00F1','\u00D1','\u00AA','\u00BA',
        '\u00BF','\u2310','\u00AC','\u00BD','\u00BC','\u00A1','\u00AB','\u00BB',
        '\u2591','\u2592','\u2593','\u2502','\u2524','\u2561','\u2562','\u2556',
        '\u2555','\u2563','\u2551','\u2557','\u255D','\u255C','\u255B','\u2510',
        '\u2514','\u2534','\u252C','\u251C','\u2500','\u253C','\u255E','\u255F',
        '\u255A','\u2554','\u2569','\u2566','\u2560','\u2550','\u256C','\u2567',
        '\u2568','\u2564','\u2565','\u2559','\u2558','\u2552','\u2553','\u256B',
        '\u256A','\u2518','\u250C','\u2588','\u2584','\u258C','\u2590','\u2580',
        '\u03B1','\u00DF','\u0393','\u03C0','\u03A3','\u03C3','\u00B5','\u03C4',
        '\u03A6','\u0398','\u03A9','\u03B4','\u221E','\u03C6','\u03B5','\u2229',
        '\u2261','\u00B1','\u2265','\u2264','\u2320','\u2321','\u00F7','\u2248',
        '\u00B0','\u2219','\u00B7','\u221A','\u207F','\u00B2','\u25A0','\u00A0'
    };

    public override int CodePage => 437;
    public override string WebName => "ibm437";

    public override int GetByteCount(char[] chars, int index, int count) => count;
    public override int GetCharCount(byte[] bytes, int index, int count) => count;
    public override int GetMaxByteCount(int charCount) => charCount;
    public override int GetMaxCharCount(int byteCount) => byteCount;

    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
    {
        for (var i = 0; i < charCount; i++)
        {
            var ch = chars[charIndex + i];
            bytes[byteIndex + i] = ch <= 0x7F ? (byte)ch : (byte)'?';
        }
        return charCount;
    }

    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        for (var i = 0; i < byteCount; i++)
        {
            var b = bytes[byteIndex + i];
            chars[charIndex + i] = b < 0x80 ? (char)b : Table[b - 0x80];
        }
        return byteCount;
    }
}

/// <summary>
/// Hand-written FTP control-channel client with the scene features WeaveFXP needs:
/// PASV/EPSV, PRET, SSCN/CPSV, XDUPE, STAT -l inline listing, PORT (for FXP), TLS
/// (explicit AUTH TLS + implicit), and streaming RETR with an idle timeout.
///
/// FTP control/data client used by the engine.
/// </summary>
public sealed class FtpClient : IDisposable
{
    public sealed class Config
    {
        public string Name = "";
        public string Host = "";
        public int Port;
        public string Username = "";
        public string Password = "";
        public TlsMode TlsMode = TlsMode.Off;
        public bool UseEpsv;
        public bool UsePret;
        public bool UseSscn;
        public FxpMode FxpMode = FxpMode.Auto;
        public string PassiveHost = "";
        public string ListCommand = "";
        public bool ForceBinary;
        public bool BrokenPasv;
        public FxpPassiveSide FxpPassiveSide = FxpPassiveSide.Auto;
        public SslDataClientSide SslDataClient = SslDataClientSide.Auto;
        public bool UseXdupe;
        public int XdupeMode;
        public int TimeoutSeconds;
        public bool SkipEmptyFolders = true;
        public List<string> Skiplist = new();
        public List<string> OrderList = new();
        public bool CwdBeforeStatListing;
        public int TcpSendBufferKBytes;
        public int TcpReceiveBufferKBytes;
        public Action<string>? Trace;

        public static Config FromSite(Site site)
        {
            var useSscn = site.UseSscn || site.SscnSupported;
            var fxpMode = site.FxpMode;
            if (fxpMode == FxpMode.Auto && site.CpsvSupported) fxpMode = FxpMode.CpsvSsc;
            return new Config
            {
                Name = site.Name,
                Host = site.Host,
                Port = site.Port,
                Username = site.Username,
                Password = site.Password,
                TlsMode = site.TlsMode,
                UseEpsv = site.UseEpsv,
                UsePret = site.UsePret,
                UseSscn = useSscn,
                FxpMode = fxpMode,
                PassiveHost = site.PassiveHost,
                ListCommand = site.ListCommand,
                ForceBinary = site.ForceBinary,
                BrokenPasv = site.BrokenPasv,
                FxpPassiveSide = site.FxpPassiveSide,
                SslDataClient = site.SslDataClient,
                UseXdupe = site.UseXdupe,
                XdupeMode = site.XdupeMode,
                TimeoutSeconds = site.TimeoutSeconds,
                SkipEmptyFolders = true,
                Skiplist = new List<string>(site.Skiplist),
                OrderList = new(),
            };
        }
    }

    private readonly Config _cfg;
    private TcpClient _tcp = null!;
    private Stream _stream = null!;
    private LineReader _reader = null!;

    // Minimal line reader with an inspectable buffer. StreamReader can't tell us
    // whether a line is already buffered, which we need to drain servers that send
    // their welcome banner as several INDEPENDENT "220 ..." finals (instead of a
    // proper 220- multiline). An unread banner line shifts every later reply by one
    // ("USER failed: 200 Protection set to Private").
    private sealed class LineReader
    {
        private readonly Stream _stream;
        private readonly Encoding _enc;
        private byte[] _buf = new byte[8192];
        private int _start, _end;

        public LineReader(Stream stream, Encoding enc) { _stream = stream; _enc = enc; }

        public bool HasBufferedLine
        {
            get
            {
                for (var i = _start; i < _end; i++)
                    if (_buf[i] == (byte)'\n') return true;
                return false;
            }
        }

        public bool HasBufferedData => _end > _start;

        public async Task<string?> ReadLineAsync()
        {
            while (true)
            {
                for (var i = _start; i < _end; i++)
                {
                    if (_buf[i] != (byte)'\n') continue;
                    var len = i - _start;
                    if (len > 0 && _buf[_start + len - 1] == (byte)'\r') len--;
                    var line = _enc.GetString(_buf, _start, len);
                    _start = i + 1;
                    return line;
                }
                if (_start > 0) { Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start); _end -= _start; _start = 0; }
                if (_end == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
                var n = await _stream.ReadAsync(_buf.AsMemory(_end, _buf.Length - _end)).ConfigureAwait(false);
                if (n == 0)
                {
                    if (_end == _start) return null;
                    var rest = _enc.GetString(_buf, _start, _end - _start);
                    _start = _end = 0;
                    return rest;
                }
                _end += n;
            }
        }
    }
    private bool _dataTls;
    private SslClientAuthenticationOptions? _sslOptions;
    private static readonly Encoding FtpTextEncoding = Cp437Encoding.Instance;

    // glFTPd/drFTPd-style servers require the data channel to resume the control
    // channel's TLS session. .NET resumes automatically from its per-process session
    // cache when both handshakes use identical client options, so we build one options
    // object per connection and reuse it for the control AND every data handshake.
    // Note: cert acceptance stays on the SslStream ctor callback (AcceptAnyCert); .NET
    // forbids setting RemoteCertificateValidationCallback in both the ctor and options.
    private static SslClientAuthenticationOptions MakeSslOptions(string host) => new()
    {
        TargetHost = host,
        AllowTlsResume = true,
    };

    private FtpClient(Config cfg) => _cfg = cfg;

    public IReadOnlyList<string> ConfiguredSkiplist() => _cfg.Skiplist;
    public IReadOnlyList<string> ConfiguredOrderList() => _cfg.OrderList;

    public static async Task<FtpClient> DialAndLoginAsync(Config cfg, CancellationToken ct = default)
    {
        if (cfg.Port == 0) cfg.Port = 21;
        if (cfg.TimeoutSeconds == 0) cfg.TimeoutSeconds = 30;
        if (cfg.XdupeMode == 0) cfg.XdupeMode = 3;
        if (string.IsNullOrWhiteSpace(cfg.ListCommand)) cfg.ListCommand = "LIST";

        var c = new FtpClient(cfg);
        c.Trace($"* connecting to {cfg.Host}:{cfg.Port}");
        var tcp = new TcpClient();
        ConfigureTcp(tcp, cfg);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(cfg.Host, cfg.Port, linked.Token).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        c._tcp = tcp;
        c._tcp.ReceiveTimeout = cfg.TimeoutSeconds * 1000;
        c._tcp.SendTimeout = cfg.TimeoutSeconds * 1000;

        c._sslOptions = MakeSslOptions(cfg.Host);
        if (cfg.TlsMode == TlsMode.Implicit)
        {
            var ssl = new SslStream(tcp.GetStream(), false, AcceptAnyCert);
            await ssl.AuthenticateAsClientAsync(c._sslOptions).ConfigureAwait(false);
            c._stream = ssl;
            c._dataTls = true;
        }
        else
        {
            c._stream = tcp.GetStream();
        }
        c._reader = new LineReader(c._stream, FtpTextEncoding);

        var (code, msg) = await c.ReadResponseAsync().ConfigureAwait(false);
        c.TraceResponse(code, msg);
        if (code / 100 != 2)
        {
            c.Dispose();
            throw new IOException($"welcome failed: {code} {msg}");
        }
        await c.DrainBannerExtrasAsync().ConfigureAwait(false);

        if (cfg.TlsMode == TlsMode.Explicit)
        {
            var (ac, am) = await c.CommandAsync("AUTH TLS").ConfigureAwait(false);
            if (ac != 234 && ac != 334)
            {
                c.Dispose();
                throw new IOException($"AUTH TLS failed: {ac} {am}");
            }
            var ssl = new SslStream(tcp.GetStream(), false, AcceptAnyCert);
            await ssl.AuthenticateAsClientAsync(c._sslOptions).ConfigureAwait(false);
            c._stream = ssl;
            c._reader = new LineReader(c._stream, FtpTextEncoding);
            c._dataTls = true;
            await c.CommandAsync("PBSZ 0").ConfigureAwait(false);
            await c.CommandAsync("PROT P").ConfigureAwait(false);
        }
        else if (cfg.TlsMode == TlsMode.Implicit)
        {
            await c.CommandAsync("PBSZ 0").ConfigureAwait(false);
            await c.CommandAsync("PROT P").ConfigureAwait(false);
        }

        await c.LoginAsync().ConfigureAwait(false);
        // Race/FXP clients must always use binary mode; leaving this optional can make
        // servers default to ASCII, which is slower and unsafe for release files.
        await c.SetBinaryAsync().ConfigureAwait(false);
        return c;
    }

    // Some daemons send the welcome banner as SEVERAL independent "220 ..." finals
    // instead of one 220- multiline. Anything left unread shifts every later reply
    // by one ("USER failed: 200 Protection set to Private"). Only safe to call when
    // no command is outstanding — anything arriving now IS banner.
    private async Task DrainBannerExtrasAsync()
    {
        for (var round = 0; round < 30; round++)
        {
            if (!_reader.HasBufferedLine)
            {
                // Give a same-burst line a moment to land, then peek the socket.
                if (!_reader.HasBufferedData && _tcp.Available == 0)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    if (!_reader.HasBufferedData && _tcp.Available == 0) return;
                }
            }
            var line = await _reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null) return;
            Trace("< " + line + " [extra banner line]");
        }
    }

    private static bool AcceptAnyCert(object sender, X509Certificate? cert, X509Chain? chain,
        SslPolicyErrors errors) => true; // scene sites use self-signed certs

    public void Dispose()
    {
        // Dispose is also the race engine's emergency drop path. QUIT can block behind
        // an outstanding transfer reply and delay releasing the worker slot.
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
    }

    public string Name => string.IsNullOrEmpty(_cfg.Name) ? _cfg.Host : _cfg.Name;

    private void Trace(string line) => _cfg.Trace?.Invoke(line);

    private void TraceResponse(int code, string msg, ResponseTraceMode mode = ResponseTraceMode.Full)
    {
        if (_cfg.Trace is null) return;
        if (mode == ResponseTraceMode.ListingSummary)
        {
            _cfg.Trace.Invoke($"< {code} listing received ({CountListingRows(msg)} entries)");
            return;
        }

        foreach (var line in ResponseLines(code, msg))
            _cfg.Trace.Invoke(line);
    }

    private static string MaskCommand(string line)
        => line.Length >= 5 && line[..5].Equals("PASS ", StringComparison.OrdinalIgnoreCase)
            ? "PASS ****" : line;

    private static IEnumerable<string> ResponseLines(int code, string msg)
    {
        msg = msg.Replace("\r\n", "\n").TrimEnd('\r', '\n');
        if (msg.Length == 0)
        {
            yield return $"< {code}";
            yield break;
        }

        var prefix = code.ToString("D3");
        foreach (var raw in msg.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length >= 4 && line.StartsWith(prefix, StringComparison.Ordinal) &&
                (line[3] == '-' || line[3] == ' '))
            {
                line = line[4..];
            }
            yield return $"< {code} {line}";
        }
    }

    // Surfaces STAT -l lines the parser could not turn into an entry. On a healthy
    // listing this logs nothing; when a daemon uses an unexpected file-line format it
    // reveals that format in the FTP log so it can be diagnosed instead of silently
    // dropping every file (the "0 files" FXP symptom).
    private void TraceUnparsedListing(string msg, int parsedCount)
    {
        if (_cfg.Trace is null) return;
        var shown = 0;
        foreach (var raw in msg.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (LooksLikeUnixListLine(line)) continue; // parsed into an entry
            // Only flag lines that actually look like a listing row (≥8 whitespace
            // fields). STAT/FEAT headers, "213 End of status.", "total N" etc. have
            // fewer fields and are just protocol noise — never report those.
            if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 8) continue;
            _cfg.Trace.Invoke($"! listing line not parsed ({shown + 1}): {line}");
            if (++shown >= 8) break;
        }
        if (shown > 0)
            _cfg.Trace.Invoke($"! parsed {parsedCount} entries; {shown}+ line(s) above were ignored — file-line format not recognised");
    }

    private static int CountListingRows(string msg)
    {
        var count = 0;
        foreach (var raw in msg.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("total ", StringComparison.OrdinalIgnoreCase)) continue;
            count++;
        }
        return count;
    }

    private int Timeout => _cfg.TimeoutSeconds <= 0 ? 30 : _cfg.TimeoutSeconds;

    private async Task SendLineAsync(string line)
    {
        var bytes = FtpTextEncoding.GetBytes(line + "\r\n");
        await _stream.WriteAsync(bytes).ConfigureAwait(false);
        await _stream.FlushAsync().ConfigureAwait(false);
    }

    // Reads a full (possibly multi-line) FTP response and returns (code, text).
    private async Task<(int code, string msg)> ReadResponseAsync()
    {
        var first = await _reader.ReadLineAsync().ConfigureAwait(false)
            ?? throw new IOException("connection closed by server");
        if (first.Length < 4 || !int.TryParse(first.AsSpan(0, 3), out var code))
            return (0, first);

        var sb = new StringBuilder(first.Length > 4 ? first[4..] : "");
        if (first[3] == '-')
        {
            // Multi-line: read until a line begins with "<code> ".
            var terminator = first[..3] + " ";
            while (true)
            {
                var line = await _reader.ReadLineAsync().ConfigureAwait(false)
                    ?? throw new IOException("connection closed mid-response");
                sb.Append('\n').Append(line);
                if (line.StartsWith(terminator, StringComparison.Ordinal)) break;
            }
        }
        return (code, sb.ToString());
    }

    public async Task<(int code, string msg)> CommandAsync(string line, ResponseTraceMode traceMode = ResponseTraceMode.Full)
    {
        Trace("> " + MaskCommand(line));
        try
        {
            await SendLineAsync(line).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace("! " + ex.Message);
            throw;
        }
        var (code, msg) = await ReadResponseAsync().ConfigureAwait(false);
        TraceResponse(code, msg, traceMode);
        // Track the working directory (cbftp keeps per-conn CWD state the same way) so
        // EnsureCwdAsync can skip redundant CWDs on repeat transfers in the same dir.
        if (line.StartsWith("CWD ", StringComparison.OrdinalIgnoreCase))
            _currentDir = code / 100 == 2 ? line[4..].Trim() : null;
        return (code, msg);
    }

    private string? _currentDir;

    // cbftp-style transfer addressing: CWD into the directory, then STOR/RETR the BARE
    // filename. Absolute STOR/RETR/PRET paths through a symlinked section dir (glftpd /
    // weaveftpd dated sections like "/!0day_today.") are not resolved by every daemon,
    // while CWD always is. No-op when the connection is already in the right dir.
    public async Task EnsureCwdAsync(string dir)
    {
        dir = (dir ?? "/").Trim();
        if (dir.Length == 0) dir = "/";
        if (_currentDir is not null && _currentDir.Equals(dir, StringComparison.Ordinal)) return;
        var (code, msg) = await CommandAsync("CWD " + dir).ConfigureAwait(false);
        if (code / 100 != 2) throw new IOException($"CWD {dir} failed: {code} {msg}");
    }

    // Sends a command that starts a transfer (1xx/2xx expected).
    public async Task<(int code, string msg)> StartCommandAsync(string line)
    {
        var (code, msg) = await CommandAsync(line).ConfigureAwait(false);
        if (code / 100 != 1 && code / 100 != 2)
            throw new IOException($"command failed: {code} {msg}");
        return (code, msg);
    }

    // Split start: send the transfer command now, read its reply later. Lets both
    // sides of an FXP have their commands in flight at once instead of serializing
    // two full round trips per file.
    public Task BeginStartCommandAsync(string line)
    {
        Trace("> " + MaskCommand(line));
        return SendLineAsync(line);
    }

    public async Task<(int code, string msg)> FinishStartCommandAsync()
    {
        var (code, msg) = await ReadResponseAsync().ConfigureAwait(false);
        TraceResponse(code, msg);
        if (code / 100 != 1 && code / 100 != 2)
            throw new IOException($"command failed: {code} {msg}");
        return (code, msg);
    }

    // Fire an ABOR without reading any reply — used to kill a stalled transfer whose
    // final replies are being awaited elsewhere (slow-skip). The connection must be
    // DROPPED afterwards: its reply stream is no longer strictly command-paired.
    public Task NudgeAbortAsync()
    {
        Trace("> ABOR (slow-skip)");
        return SendLineAsync("ABOR");
    }

    // Abort a transfer this connection started (1xx received) whose FXP peer failed —
    // without this the connection is left with unread replies and desyncs the pool.
    // Drains until a 2xx lands. Throws if the channel can't be brought back to clean.
    public async Task AbortTransferAsync()
    {
        Trace("> ABOR");
        await SendLineAsync("ABOR").ConfigureAwait(false);
        for (var i = 0; i < 4; i++)
        {
            var (code, msg) = await ReadResponseAsync().ConfigureAwait(false);
            TraceResponse(code, msg);
            if (code / 100 == 2) return; // 226/225 after the 426 — clean again
        }
        throw new IOException("ABOR did not settle the control channel");
    }

    // Reads control replies until a final (non-1xx) response arrives.
    public async Task<(int code, string msg)> WaitFinalAsync()
    {
        while (true)
        {
            var (code, msg) = await ReadResponseAsync().ConfigureAwait(false);
            TraceResponse(code, msg);
            if (code / 100 != 1)
            {
                if (code / 100 >= 4) throw new IOException($"transfer failed: {code} {msg}");
                return (code, msg);
            }
        }
    }

    private async Task LoginAsync()
    {
        var user = string.IsNullOrWhiteSpace(_cfg.Username) ? "anonymous" : _cfg.Username;
        var (code, msg) = await CommandAsync("USER " + user).ConfigureAwait(false);
        if (code == 230) return;
        if (code != 331) throw new IOException($"USER failed: {code} {msg}");
        var pass = _cfg.Password;
        if (pass.Length == 0 && user == "anonymous") pass = "anonymous@";
        (code, msg) = await CommandAsync("PASS " + pass).ConfigureAwait(false);
        if (code / 100 != 2) throw new IOException($"PASS failed: {code} {msg}");
    }

    private bool _binarySet;

    public async Task SetBinaryAsync()
    {
        if (_binarySet) return; // TYPE is sticky per control connection — set once
        var (code, msg) = await CommandAsync("TYPE I").ConfigureAwait(false);
        if (code / 100 != 2) throw new IOException($"TYPE I failed: {code} {msg}");
        _binarySet = true;
    }

    public async Task MaybePretAsync(string command)
    {
        if (!_cfg.UsePret) return;
        var (code, msg) = await CommandAsync("PRET " + command).ConfigureAwait(false);
        if (code / 100 != 2 && code / 100 != 3) throw new IOException($"PRET failed: {code} {msg}");
    }

    // Whether this connection's data channel runs TLS (PROT P negotiated).
    public bool DataTls => _dataTls;

    // Whether the site is marked as supporting SSCN.
    public bool SupportsSscn => _cfg.UseSscn;

    public bool SupportsCpsv => _cfg.FxpMode == FxpMode.CpsvSsc;
    public bool BrokenPasv => _cfg.BrokenPasv;
    public FxpPassiveSide PassiveSidePreference => _cfg.FxpPassiveSide;
    public SslDataClientSide SslDataClientPreference => _cfg.SslDataClient;

    private bool _sscnOn;

    // SSCN is toggled per transfer, on exactly ONE side of an FXP (the passive side,
    // which then acts as TLS client on the data connection). Idempotent -
    // only sends the command when the state actually changes.
    public async Task SetSscnAsync(bool on)
    {
        if (!_cfg.UseSscn || _sscnOn == on) return;
        var (code, msg) = await CommandAsync(on ? "SSCN ON" : "SSCN OFF").ConfigureAwait(false);
        if (code / 100 != 2) throw new IOException($"SSCN {(on ? "ON" : "OFF")} failed: {code} {msg}");
        _sscnOn = on;
    }

    public Task MaybeSscnAsync() => SetSscnAsync(true);

    // Lightweight liveness probe for pooled connections. Returns false if the control
    // connection is dead (server dropped an idle conn).
    public async Task<bool> TryNoopAsync()
    {
        try
        {
            var (code, _) = await CommandAsync("NOOP").ConfigureAwait(false);
            return code > 0;
        }
        catch { return false; }
    }

    public async Task MaybeXdupeAsync()
    {
        if (!_cfg.UseXdupe) return;
        var (code, msg) = await CommandAsync($"SITE XDUPE {_cfg.XdupeMode}").ConfigureAwait(false);
        if (code / 100 != 2) throw new IOException($"SITE XDUPE failed: {code} {msg}");
    }

    // ---- passive / active -------------------------------------------------------------

    public async Task<(FtpEndpoint ep, string method)> EnterPassiveAsync(string command)
    {
        if (!string.IsNullOrWhiteSpace(command))
        {
            var (code, msg) = await CommandAsync(command).ConfigureAwait(false);
            if (code / 100 == 2)
            {
                var ep = ParsePassive(code, msg);
                return (RewritePassiveHost(ep), command);
            }
        }
        if (_cfg.UseEpsv)
        {
            var (code, msg) = await CommandAsync("EPSV").ConfigureAwait(false);
            if (code / 100 == 2)
            {
                var ep = ParsePassive(code, msg);
                return (RewritePassiveHost(ep), "EPSV");
            }
        }
        var (pc, pm) = await CommandAsync("PASV").ConfigureAwait(false);
        if (pc / 100 != 2) throw new IOException($"PASV failed: {pc} {pm}");
        return (RewritePassiveHost(ParsePassive(pc, pm)), "PASV");
    }

    public async Task SetActiveAsync(FtpEndpoint ep)
    {
        if (!IPAddress.TryParse(ep.Host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new IOException($"PORT requires an IPv4 endpoint, got \"{ep.Host}\"");
        var b = ip.GetAddressBytes();
        var p1 = ep.Port / 256;
        var p2 = ep.Port % 256;
        var (code, msg) = await CommandAsync($"PORT {b[0]},{b[1]},{b[2]},{b[3]},{p1},{p2}").ConfigureAwait(false);
        if (code / 100 != 2) throw new IOException($"PORT failed: {code} {msg}");
    }

    private FtpEndpoint ParsePassive(int code, string msg)
    {
        if (code == 229)
        {
            var port = ParseEpsv(msg);
            var host = (_tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? _cfg.Host;
            return new FtpEndpoint { Host = host, Port = port };
        }
        return ParsePasv(msg);
    }

    private FtpEndpoint RewritePassiveHost(FtpEndpoint ep)
    {
        if (!string.IsNullOrEmpty(_cfg.PassiveHost))
        {
            ep.Host = _cfg.PassiveHost;
            return ep;
        }
        if (_cfg.BrokenPasv && _tcp.Client.RemoteEndPoint is IPEndPoint remote)
            ep.Host = remote.Address.ToString();
        return ep;
    }

    public static FtpEndpoint ParsePasv(string msg)
    {
        var start = msg.IndexOf('(');
        var end = msg.LastIndexOf(')');
        if (start < 0 || end <= start) throw new IOException($"could not parse PASV response \"{msg}\"");
        var parts = msg[(start + 1)..end].Split(',');
        if (parts.Length != 6) throw new IOException($"could not parse PASV tuple \"{msg}\"");
        var nums = new int[6];
        for (var i = 0; i < 6; i++)
        {
            if (!int.TryParse(parts[i].Trim(), out var n) || n is < 0 or > 255)
                throw new IOException($"invalid PASV number \"{parts[i]}\"");
            nums[i] = n;
        }
        return new FtpEndpoint
        {
            Host = $"{nums[0]}.{nums[1]}.{nums[2]}.{nums[3]}",
            Port = nums[4] * 256 + nums[5],
        };
    }

    public static int ParseEpsv(string msg)
    {
        var start = msg.IndexOf('(');
        var end = msg.LastIndexOf(')');
        if (start < 0 || end <= start + 3) throw new IOException($"could not parse EPSV response \"{msg}\"");
        var body = msg[(start + 1)..end];
        var delim = body[0];
        var parts = body.Split(delim);
        if (parts.Length < 5) throw new IOException($"could not parse EPSV tuple \"{msg}\"");
        if (!int.TryParse(parts[^2], out var port) || port is < 1 or > 65535)
            throw new IOException($"invalid EPSV port \"{parts[^2]}\"");
        return port;
    }

    // ---- data connection --------------------------------------------------------------

    private TimeSpan DataTimeout => TimeSpan.FromSeconds(Timeout);

    // TCP-only connect to the data endpoint. TLS is negotiated separately AFTER the
    // transfer command has been sent (WrapDataTlsAsync) — many servers only start
    // their data-channel TLS handshake once RETR/STOR/LIST is in flight, so
    // handshaking right after connect stalls until timeout.
    private async Task<TcpClient> OpenDataTcpAsync(FtpEndpoint ep, CancellationToken ct)
    {
        Trace($"* opening data connection to {ep.Host}:{ep.Port}");
        var dtcp = new TcpClient();
        ConfigureTcp(dtcp, _cfg);
        try
        {
            using var timeout = new CancellationTokenSource(DataTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await dtcp.ConnectAsync(ep.Host, ep.Port, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            dtcp.Dispose();
            Trace("! data connection failed: " + ex.Message);
            throw;
        }
        dtcp.ReceiveTimeout = (int)DataTimeout.TotalMilliseconds;
        dtcp.SendTimeout = (int)DataTimeout.TotalMilliseconds;
        return dtcp;
    }

    private static void ConfigureTcp(TcpClient tcp, Config cfg)
    {
        tcp.NoDelay = true;
        if (cfg.TcpReceiveBufferKBytes > 0)
            tcp.ReceiveBufferSize = Math.Clamp(cfg.TcpReceiveBufferKBytes, 1, 16384) * 1024;
        if (cfg.TcpSendBufferKBytes > 0)
            tcp.SendBufferSize = Math.Clamp(cfg.TcpSendBufferKBytes, 1, 16384) * 1024;
    }

    private async Task<Stream> WrapDataTlsAsync(TcpClient dtcp, CancellationToken ct)
    {
        Stream stream = dtcp.GetStream();
        if (!_dataTls) return stream;
        var ssl = new SslStream(stream, false, AcceptAnyCert);
        try
        {
            using var timeout = new CancellationTokenSource(DataTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await ssl.AuthenticateAsClientAsync(_sslOptions ?? MakeSslOptions(_cfg.Host), linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            dtcp.Dispose();
            Trace("! data TLS handshake failed: " + ex.Message);
            throw;
        }
        return ssl;
    }

    // ---- listings ---------------------------------------------------------------------

    public async Task<List<RemoteEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var listCommand = string.IsNullOrWhiteSpace(_cfg.ListCommand) ? "LIST" : _cfg.ListCommand.Trim();
        var isStat = listCommand.TrimStart().StartsWith("STAT", StringComparison.OrdinalIgnoreCase);
        if (isStat && _cfg.CwdBeforeStatListing)
        {
            var cwdPath = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
            var (cwdCode, cwdMsg) = await CommandAsync("CWD " + cwdPath).ConfigureAwait(false);
            if (cwdCode / 100 != 2) throw new IOException($"CWD {cwdPath} failed: {cwdCode} {cwdMsg}");
        }

        var command = isStat && _cfg.CwdBeforeStatListing
            ? listCommand
            : CommandWithPath(listCommand, path);
        if (command.TrimStart().StartsWith("STAT", StringComparison.OrdinalIgnoreCase))
        {
            var (code, msg) = await CommandAsync(command, ResponseTraceMode.ListingSummary).ConfigureAwait(false);
            if (code / 100 != 2) throw new IOException($"{command} failed: {code} {msg}");
            var entries = ParseList(msg, path);
            TraceUnparsedListing(msg, entries.Count);
            return entries;
        }

        await MaybePretAsync(command).ConfigureAwait(false);
        var (ep, _) = await EnterPassiveAsync("").ConfigureAwait(false);
        var dtcp = await OpenDataTcpAsync(ep, ct).ConfigureAwait(false);
        Stream? stream = null;
        try
        {
            await StartCommandAsync(command).ConfigureAwait(false);
            stream = await WrapDataTlsAsync(dtcp, ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            await WaitFinalAsync().ConfigureAwait(false);
            return ParseList(FtpTextEncoding.GetString(ms.ToArray()), path);
        }
        finally
        {
            stream?.Dispose();
            dtcp.Dispose();
        }
    }

    // Returns the remote file size via SIZE, or -1 when the server rejects it.
    public async Task<long> SizeAsync(string path)
    {
        await SetBinaryAsync().ConfigureAwait(false);
        var (code, msg) = await CommandAsync("SIZE " + path).ConfigureAwait(false);
        if (code != 213) return -1;
        var digits = new string(msg.Trim().TakeWhile(char.IsDigit).ToArray());
        return long.TryParse(digits, out var size) ? size : -1;
    }

    // progress, when set, receives the cumulative byte count as the file streams.
    public async Task<long> RetrieveToAsync(string path, Stream output, CancellationToken ct = default,
        IProgress<long>? progress = null, long maxBytes = 0)
    {
        await SetBinaryAsync().ConfigureAwait(false);
        var command = "RETR " + path;
        await MaybePretAsync(command).ConfigureAwait(false);
        var (ep, _) = await EnterPassiveAsync("").ConfigureAwait(false);
        var dtcp = await OpenDataTcpAsync(ep, ct).ConfigureAwait(false);
        Stream? stream = null;
        try
        {
            await StartCommandAsync(command).ConfigureAwait(false);
            stream = await WrapDataTlsAsync(dtcp, ct).ConfigureAwait(false);
            long total = 0;
            var buffer = new byte[64 * 1024];
            while (true)
            {
                using var idle = new CancellationTokenSource(DataTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested)
                {
                    throw new IOException($"data transfer stalled after {total} bytes");
                }
                if (read == 0) break;
                if (maxBytes > 0 && total + read > maxBytes)
                    throw new IOException($"remote file {path} exceeds {maxBytes} bytes");
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                progress?.Report(total);
            }
            await WaitFinalAsync().ConfigureAwait(false);
            return total;
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new IOException($"data transfer failed: {ex.Message}", ex);
        }
        finally
        {
            stream?.Dispose();
            dtcp.Dispose();
        }
    }

    public async Task<long> StoreFromAsync(string path, Stream input, CancellationToken ct = default,
        IProgress<long>? progress = null)
    {
        await SetBinaryAsync().ConfigureAwait(false);
        var command = "STOR " + path;
        await MaybePretAsync(command).ConfigureAwait(false);
        var (ep, _) = await EnterPassiveAsync("").ConfigureAwait(false);
        var dtcp = await OpenDataTcpAsync(ep, ct).ConfigureAwait(false);
        Stream? stream = null;
        try
        {
            await StartCommandAsync(command).ConfigureAwait(false);
            stream = await WrapDataTlsAsync(dtcp, ct).ConfigureAwait(false);
            long total = 0;
            var buffer = new byte[64 * 1024];
            while (true)
            {
                using var idle = new CancellationTokenSource(DataTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
                int read;
                try
                {
                    read = await input.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested)
                {
                    throw new IOException($"local file read stalled after {total} bytes");
                }
                if (read == 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                progress?.Report(total);
            }
            await stream.FlushAsync(ct).ConfigureAwait(false);
            await WaitFinalAsync().ConfigureAwait(false);
            return total;
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new IOException($"data transfer failed: {ex.Message}", ex);
        }
        finally
        {
            stream?.Dispose();
            dtcp.Dispose();
        }
    }

    public async Task<string> RetrieveTextAsync(string path, long maxBytes, CancellationToken ct = default)
    {
        if (maxBytes <= 0) maxBytes = 1024 * 1024;
        using var ms = new MemoryStream();
        await RetrieveToAsync(path, ms, ct, maxBytes: maxBytes).ConfigureAwait(false);
        return FtpTextEncoding.GetString(ms.ToArray());
    }

    // ---- parsing helpers --------------------------------------------------------------

    private static string CommandWithPath(string command, string path)
    {
        command = command.Trim();
        if (command.Length == 0) command = "LIST";
        path = (path ?? "").Trim();
        return path.Length == 0 ? command : command + " " + path;
    }

    public static List<RemoteEntry> ParseList(string raw, string basePath)
    {
        var entries = new List<RemoteEntry>();
        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (!LooksLikeUnixListLine(line)) continue;
            var entry = ParseUnixListLine(line);
            entry.Path = JoinRemote(basePath, entry.Name);
            entries.Add(entry);
        }
        return entries;
    }

    private static bool LooksLikeUnixListLine(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 9) return false;
        return fields[0][0] is 'd' or '-' or 'l';
    }

    private static RemoteEntry ParseUnixListLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var entry = new RemoteEntry { Name = line, Type = "unknown", Raw = line };
        if (parts.Length < 9) return entry;
        entry.Name = string.Join(' ', parts[8..]);
        entry.Type = parts[0][0] switch
        {
            'd' => "dir",
            '-' => "file",
            'l' => "link",
            _ => "unknown",
        };
        entry.Owner = parts[2];
        entry.Group = parts[3];
        if (long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            entry.Size = size;
        if (entry.Type == "link")
        {
            var arrow = entry.Name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                entry.LinkTarget = entry.Name[(arrow + 4)..].Trim();
                entry.Name = entry.Name[..arrow].Trim();
            }
        }
        entry.Modified = ParseUnixModified(parts[5], parts[6], parts[7]);
        return entry;
    }

    private static DateTime ParseUnixModified(string month, string day, string timeOrYear)
    {
        var now = DateTime.Now;
        var raw = $"{month} {day} {timeOrYear}";
        if (timeOrYear.Contains(':'))
        {
            if (DateTime.TryParseExact(
                    $"{month} {day} {now.Year} {timeOrYear}",
                    "MMM d yyyy H:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                    out var withTime))
            {
                if (withTime > now.AddDays(1)) withTime = withTime.AddYears(-1);
                return withTime;
            }
        }
        else if (DateTime.TryParseExact(
                     raw,
                     "MMM d yyyy",
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                     out var withYear))
        {
            return withYear;
        }

        return default;
    }

    public static string JoinRemote(string basePath, string name)
    {
        basePath = (basePath ?? "").Trim();
        if (basePath.Length == 0 || basePath == ".") return name;
        if (basePath == "/") return "/" + name.TrimStart('/');
        return basePath.TrimEnd('/') + "/" + name.TrimStart('/');
    }
}
