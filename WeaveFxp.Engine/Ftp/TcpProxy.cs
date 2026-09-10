using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WeaveFxp.Engine.Ftp;

internal static class TcpProxy
{
    private sealed record Endpoint(string Scheme, string Host, int Port);

    public static string Normalize(string? value, ref string username, ref string password)
    {
        var raw = (value ?? "").Trim();
        if (raw.Length == 0 || raw.Equals("direct", StringComparison.OrdinalIgnoreCase))
            return raw.ToLowerInvariant();

        var uri = ParseUri(raw, "proxy");
        if (uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            username = Uri.UnescapeDataString(parts[0]);
            password = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "";
        }

        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.Host}]" : uri.Host;
        return $"{uri.Scheme.ToLowerInvariant()}://{host}:{uri.Port}";
    }

    public static void Validate(string? value, string label)
    {
        var raw = (value ?? "").Trim();
        if (raw.Length == 0 || raw.Equals("direct", StringComparison.OrdinalIgnoreCase)) return;
        _ = Parse(raw, label);
    }

    public static string Describe(string? value)
    {
        var raw = (value ?? "").Trim();
        if (raw.Length == 0 || raw.Equals("direct", StringComparison.OrdinalIgnoreCase)) return "direct";
        var proxy = Parse(raw, "proxy");
        return $"{proxy.Scheme}://{proxy.Host}:{proxy.Port}";
    }

    public static async Task<TcpClient> ConnectAsync(
        string targetHost,
        int targetPort,
        string? proxyAddress,
        string? proxyUsername,
        string? proxyPassword,
        Action<TcpClient> configure,
        CancellationToken ct)
    {
        var raw = (proxyAddress ?? "").Trim();
        if (raw.Length == 0 || raw.Equals("direct", StringComparison.OrdinalIgnoreCase))
            return await ConnectSocketAsync(targetHost, targetPort, configure, ct).ConfigureAwait(false);

        var proxy = Parse(raw, "proxy");
        var tcp = await ConnectSocketAsync(proxy.Host, proxy.Port, configure, ct).ConfigureAwait(false);
        try
        {
            if (proxy.Scheme == "socks5")
                await ConnectSocks5Async(tcp.GetStream(), targetHost, targetPort,
                    proxyUsername ?? "", proxyPassword ?? "", ct).ConfigureAwait(false);
            else
                await ConnectHttpAsync(tcp.GetStream(), targetHost, targetPort,
                    proxyUsername ?? "", proxyPassword ?? "", ct).ConfigureAwait(false);
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static Endpoint Parse(string value, string label)
    {
        var uri = ParseUri(value, label);
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("socks5" or "http"))
            throw new ArgumentException($"{label} scheme must be socks5 or http");
        if (uri.Port is < 1 or > 65535)
            throw new ArgumentException($"{label} port must be between 1 and 65535");
        return new Endpoint(scheme, uri.Host, uri.Port);
    }

    private static Uri ParseUri(string value, string label)
    {
        var raw = value.Contains("://", StringComparison.Ordinal) ? value : "socks5://" + value;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException($"{label} must look like socks5://host:port or http://host:port");
        if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException($"{label} cannot contain a path, query, or fragment");
        return uri;
    }

    private static async Task<TcpClient> ConnectSocketAsync(
        string host, int port, Action<TcpClient> configure, CancellationToken ct)
    {
        var tcp = new TcpClient();
        configure(tcp);
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task ConnectSocks5Async(
        Stream stream, string host, int port, string username, string password, CancellationToken ct)
    {
        var useAuth = username.Length > 0 || password.Length > 0;
        var greeting = useAuth
            ? new byte[] { 5, 2, 0, 2 }
            : new byte[] { 5, 1, 0 };
        await stream.WriteAsync(greeting, ct).ConfigureAwait(false);

        var response = new byte[2];
        await stream.ReadExactlyAsync(response, ct).ConfigureAwait(false);
        if (response[0] != 5) throw new IOException("SOCKS5 proxy returned an invalid version");
        if (response[1] == 0xFF) throw new IOException("SOCKS5 proxy rejected all authentication methods");

        if (response[1] == 2)
            await AuthenticateSocks5Async(stream, username, password, ct).ConfigureAwait(false);
        else if (response[1] != 0)
            throw new IOException($"SOCKS5 proxy selected unsupported authentication method {response[1]}");

        var request = BuildSocks5ConnectRequest(host, port);
        await stream.WriteAsync(request, ct).ConfigureAwait(false);

        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (header[0] != 5) throw new IOException("SOCKS5 proxy returned an invalid connect response");
        if (header[1] != 0) throw new IOException("SOCKS5 CONNECT failed: " + Socks5Error(header[1]));

        var addressLength = header[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadSocks5DomainLengthAsync(stream, ct).ConfigureAwait(false),
            _ => throw new IOException("SOCKS5 proxy returned an invalid address type"),
        };
        await stream.ReadExactlyAsync(new byte[addressLength + 2], ct).ConfigureAwait(false);
    }

    private static async Task AuthenticateSocks5Async(
        Stream stream, string username, string password, CancellationToken ct)
    {
        var userBytes = Encoding.UTF8.GetBytes(username);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        if (userBytes.Length > 255 || passwordBytes.Length > 255)
            throw new ArgumentException("SOCKS5 proxy username and password must be at most 255 bytes");

        var request = new byte[3 + userBytes.Length + passwordBytes.Length];
        request[0] = 1;
        request[1] = (byte)userBytes.Length;
        userBytes.CopyTo(request, 2);
        request[2 + userBytes.Length] = (byte)passwordBytes.Length;
        passwordBytes.CopyTo(request, 3 + userBytes.Length);
        await stream.WriteAsync(request, ct).ConfigureAwait(false);

        var response = new byte[2];
        await stream.ReadExactlyAsync(response, ct).ConfigureAwait(false);
        if (response[0] != 1 || response[1] != 0)
            throw new IOException("SOCKS5 proxy authentication failed");
    }

    private static byte[] BuildSocks5ConnectRequest(string host, int port)
    {
        byte addressType;
        byte[] address;
        if (IPAddress.TryParse(host, out var ip))
        {
            addressType = ip.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
            address = ip.GetAddressBytes();
        }
        else
        {
            addressType = 3;
            var hostBytes = Encoding.UTF8.GetBytes(host);
            if (hostBytes.Length is 0 or > 255)
                throw new ArgumentException("SOCKS5 target hostname must be between 1 and 255 bytes");
            address = new byte[hostBytes.Length + 1];
            address[0] = (byte)hostBytes.Length;
            hostBytes.CopyTo(address, 1);
        }

        var request = new byte[6 + address.Length];
        request[0] = 5;
        request[1] = 1;
        request[2] = 0;
        request[3] = addressType;
        address.CopyTo(request, 4);
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)port;
        return request;
    }

    private static async Task<int> ReadSocks5DomainLengthAsync(Stream stream, CancellationToken ct)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, ct).ConfigureAwait(false);
        return length[0];
    }

    private static string Socks5Error(byte code) => code switch
    {
        1 => "general failure",
        2 => "connection not allowed",
        3 => "network unreachable",
        4 => "host unreachable",
        5 => "connection refused",
        6 => "TTL expired",
        7 => "command not supported",
        8 => "address type not supported",
        _ => $"error {code}",
    };

    private static async Task ConnectHttpAsync(
        Stream stream, string host, int port, string username, string password, CancellationToken ct)
    {
        var authority = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]:{port}" : $"{host}:{port}";
        var request = new StringBuilder()
            .Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(authority).Append("\r\n")
            .Append("Proxy-Connection: Keep-Alive\r\n");
        if (username.Length > 0 || password.Length > 0)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
            request.Append("Proxy-Authorization: Basic ").Append(token).Append("\r\n");
        }
        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), ct).ConfigureAwait(false);

        var headers = await ReadHttpHeadersAsync(stream, ct).ConfigureAwait(false);
        var firstLineEnd = headers.IndexOf("\r\n", StringComparison.Ordinal);
        var statusLine = firstLineEnd >= 0 ? headers[..firstLineEnd] : headers;
        var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var status))
            throw new IOException("HTTP proxy returned an invalid CONNECT response");
        if (status is < 200 or >= 300)
            throw new IOException($"HTTP CONNECT failed: {status} {(parts.Length == 3 ? parts[2] : "")}".TrimEnd());
    }

    private static async Task<string> ReadHttpHeadersAsync(Stream stream, CancellationToken ct)
    {
        const int maxHeaderBytes = 32768;
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < maxHeaderBytes)
        {
            var read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("HTTP proxy closed before completing CONNECT");
            buffer.WriteByte(one[0]);
            if (buffer.Length < 4) continue;
            var bytes = buffer.GetBuffer();
            var n = (int)buffer.Length;
            if (bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
                return Encoding.ASCII.GetString(bytes, 0, n);
        }
        throw new IOException("HTTP proxy response headers are too large");
    }
}
