using WeaveFxp.Engine.Models;
using System.Linq;
using System.Text.RegularExpressions;

namespace WeaveFxp.Engine.Ftp;

/// <summary>
/// Site-to-site FXP transfer: destination enters passive, source is told to connect to
/// it (PORT), then STOR/RETR run in parallel. Handles single files and whole directories
/// recursively.
/// </summary>
public static class FxpTransfer
{
    private const int MaxDepth = 16;

    public delegate void Logger(string level, string message);

    public static async Task TransferAsync(FtpClient.Config source, FtpClient.Config dest,
        TransferRequest req, Logger log, CancellationToken ct = default,
        Action<int>? onFilesFound = null, Action<string>? onFileDone = null)
    {
        log("info", $"connecting to source site {source.Name}");
        using var src = await FtpClient.DialAndLoginAsync(source, ct).ConfigureAwait(false);
        log("info", $"connecting to destination site {dest.Name}");
        using var dst = await FtpClient.DialAndLoginAsync(dest, ct).ConfigureAwait(false);

        // SSCN is handled per transfer inside TransferFileAsync, on one side only.
        // Enabling it on both sides can make both peers act as TLS client.
        if (dest.UseXdupe)
        {
            log("info", "enabling XDUPE on destination");
            try { await dst.MaybeXdupeAsync().ConfigureAwait(false); }
            catch (Exception ex) { log("warn", ex.Message); }
        }

        if (ShouldSkip(req.SourcePath, RemoteBase(req.SourcePath), MergeSkiplists(src, dst)))
        {
            log("info", $"skiplist skipped {req.SourcePath}");
            return;
        }

        if (await IsRemoteDirAsync(src, req.SourcePath).ConfigureAwait(false))
        {
            log("info", $"source {req.SourcePath} is a directory, transferring recursively");
            await TransferDirAsync(src, dst, dest, req.SourcePath, req.DestPath, MaxDepth, log, ct, onFilesFound, onFileDone).ConfigureAwait(false);
            return;
        }
        onFilesFound?.Invoke(1);
        await TransferFileAsync(src, dst, dest, req.SourcePath, req.DestPath, log, ct).ConfigureAwait(false);
        onFileDone?.Invoke(RemoteBase(req.SourcePath));
    }

    private static async Task<bool> IsRemoteDirAsync(FtpClient src, string path)
    {
        var (code, _) = await src.CommandAsync("CWD " + path).ConfigureAwait(false);
        return code / 100 == 2;
    }

    private static async Task TransferDirAsync(FtpClient src, FtpClient dst, FtpClient.Config destCfg,
        string srcPath, string dstPath, int depth, Logger log, CancellationToken ct,
        Action<int>? onFilesFound = null, Action<string>? onFileDone = null)
    {
        if (depth <= 0) throw new IOException($"maximum directory depth reached at {srcPath}");
        var entries = await src.ListAsync(srcPath, ct).ConfigureAwait(false);
        var skiplist = MergeSkiplists(src, dst);
        var orderList = MergeOrderLists(src, dst);
        var transferable = new List<RemoteEntry>();
        foreach (var entry in entries)
        {
            if (entry.Name is "." or "..") continue;
            var childSrc = JoinPath(srcPath, entry.Name);
            // glftpd marks missing pieces with a 0-byte "<file>-missing" placeholder.
            // They are not real data and can't be FXP'd — skip them automatically.
            if (entry.Type is not ("dir" or "link") && IsIncompleteMarker(entry.Name))
            {
                log("info", $"skipped incomplete marker {entry.Name}");
                continue;
            }
            if (ShouldSkip(childSrc, entry.Name, skiplist))
            {
                log("info", $"skiplist skipped {childSrc}");
                continue;
            }
            transferable.Add(entry);
        }
        transferable = ApplyOrderList(transferable, srcPath, orderList);

        if (destCfg.SkipEmptyFolders && transferable.Count == 0)
        {
            log("info", $"skipped empty directory {srcPath}");
            return;
        }

        var (code, msg) = await dst.CommandAsync("MKD " + dstPath).ConfigureAwait(false);
        if (code / 100 != 2) log("warn", $"MKD {dstPath}: {code} {msg} (may already exist)");

        onFilesFound?.Invoke(transferable.Count(e => e.Type is not ("dir" or "link")));

        Exception? firstErr = null;
        var files = 0;
        foreach (var entry in transferable)
        {
            var childSrc = JoinPath(srcPath, entry.Name);
            var childDst = JoinPath(dstPath, entry.Name);
            try
            {
                if (entry.Type is "dir" or "link")
                    await TransferDirAsync(src, dst, destCfg, childSrc, childDst, depth - 1, log, ct, onFilesFound, onFileDone).ConfigureAwait(false);
                else
                {
                    await TransferFileAsync(src, dst, destCfg, childSrc, childDst, log, ct).ConfigureAwait(false);
                    files++;
                    onFileDone?.Invoke(entry.Name);
                }
            }
            catch (Exception ex)
            {
                if ((entry.Type is "dir" or "link") && IsIgnorableDirectoryMiss(ex))
                {
                    log("warn", $"skipped empty/virtual directory {childSrc}: {ex.Message}");
                    continue;
                }

                // X-DUPE / "already exists" on the destination, or a source piece the
                // daemon reports as missing, are expected during racing — skip, don't fail.
                if (IsDupeOrMissing(ex))
                {
                    log("info", $"skipped {entry.Name}: {FirstLine(ex.Message)}");
                    continue;
                }

                log("error", ex.Message);
                firstErr ??= ex;
            }
        }
        if (firstErr is not null)
            throw new IOException($"directory {srcPath} finished with errors: {firstErr.Message}", firstErr);
        log("info", $"directory {srcPath} done ({files} files)");
    }

    // Public entry for the race loop: move one file over two already-open connections.
    // dataGate, when set, caps how many transfers may STREAM at once while letting
    // callers pre-negotiate (PRET/PASV/PORT) ungated — the next file's data channel
    // is ready before a data slot even frees up.
    public static Task TransferSingleAsync(FtpClient src, FtpClient dst, FtpClient.Config destCfg,
        string srcPath, string dstPath, Logger log, CancellationToken ct, SemaphoreSlim? dataGate = null)
        => TransferFileAsync(src, dst, destCfg, srcPath, dstPath, log, ct, dataGate);

    // Same dupe/-missing classification the dir walk uses, exposed for the race loop.
    public static bool IsSkippableTransferError(Exception ex) => IsDupeOrMissing(ex);

    // The file exists on source but isn't finished uploading yet (glftpd:
    // "No Permission To Download A File Currently Being Uploaded"). Not an error —
    // just come back for it next poll without burning a retry attempt.
    public static bool IsBeingUploaded(Exception ex)
    {
        var m = ex.Message ?? "";
        return m.Contains("currently being uploaded", StringComparison.OrdinalIgnoreCase)
            || m.Contains("being uploaded", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task TransferFileAsync(FtpClient src, FtpClient dst, FtpClient.Config destCfg,
        string srcPath, string dstPath, Logger log, CancellationToken ct, SemaphoreSlim? dataGate = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var storeCommand = "STOR " + dstPath;
        var retrieveCommand = "RETR " + srcPath;
        // Two different control connections — run the PRETs concurrently.
        await Task.WhenAll(
            dst.MaybePretAsync(storeCommand),
            src.MaybePretAsync(retrieveCommand)).ConfigureAwait(false);

        var pairKey = src.Name + ">" + dst.Name;
        var flipped = SslRoleFlip.TryGetValue(pairKey, out var fl) && fl;

        // --- which side is passive? Auto means source passive unless source PASV is broken. ---
        bool srcPassive = !src.BrokenPasv;
        if (src.PassiveSidePreference == FxpPassiveSide.Source) srcPassive = true;
        else if (src.PassiveSidePreference == FxpPassiveSide.Destination) srcPassive = false;
        else if (dst.PassiveSidePreference == FxpPassiveSide.Source) srcPassive = true;
        else if (dst.PassiveSidePreference == FxpPassiveSide.Destination) srcPassive = false;
        if (srcPassive && src.BrokenPasv) srcPassive = false;   // can't listen there
        if (!srcPassive && dst.BrokenPasv) srcPassive = true;

        var passiveClient = srcPassive ? src : dst;
        var activeClient = srcPassive ? dst : src;

        // --- which side is the TLS client? Auto means the passive side. ---
        // CPSV on the passive side achieves the same thing without SSCN.
        var useCpsv = passiveClient.SupportsCpsv;
        var srcIsTlsClient = srcPassive;
        // Fall back to the other side when the passive side can do neither.
        if (srcPassive && !src.SupportsSscn && !src.SupportsCpsv) srcIsTlsClient = false;
        else if (!srcPassive && !dst.SupportsSscn && !dst.SupportsCpsv) srcIsTlsClient = true;
        // Explicit per-site override wins over the derived value.
        var pref = src.SslDataClientPreference != SslDataClientSide.Auto ? src.SslDataClientPreference : dst.SslDataClientPreference;
        if (pref == SslDataClientSide.Source) srcIsTlsClient = true;
        else if (pref == SslDataClientSide.Destination) srcIsTlsClient = false;
        // Auto-learned correction from a previous handshake failure on this pair.
        else if (flipped) srcIsTlsClient = !srcIsTlsClient;

        if (src.DataTls || dst.DataTls)
        {
            var passiveIsTlsClient = srcPassive ? srcIsTlsClient : !srcIsTlsClient;
            if (useCpsv && passiveIsTlsClient)
            {
                await Task.WhenAll(src.SetSscnAsync(false), dst.SetSscnAsync(false)).ConfigureAwait(false);
            }
            else
            {
                useCpsv = false; // SSCN carries the role instead
                await Task.WhenAll(src.SetSscnAsync(srcIsTlsClient), dst.SetSscnAsync(!srcIsTlsClient)).ConfigureAwait(false);
            }
        }
        else useCpsv = false;

        var passiveCommand = useCpsv ? "CPSV" : "";
        FtpEndpoint ep;
        string method;
        try
        {
            (ep, method) = await passiveClient.EnterPassiveAsync(passiveCommand).ConfigureAwait(false);
        }
        catch when (passiveCommand == "CPSV")
        {
            log("warn", "CPSV failed, falling back to regular passive FXP");
            (ep, method) = await passiveClient.EnterPassiveAsync("").ConfigureAwait(false);
        }
        log("info", $"{(srcPassive ? "source" : "destination")} entered {method} at {ep.Host}:{ep.Port}");

        await activeClient.SetActiveAsync(ep).ConfigureAwait(false);

        // Everything above (PRET/SSCN/PASV/PORT) was pre-negotiation and ran ungated.
        // The gate caps concurrent STREAMS: extra workers wait here fully set up and
        // fire their transfer command the instant a data slot frees.
        if (dataGate is not null) await dataGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {

        // Both transfer commands go on the wire back to back (passive side first),
        // then we read both replies — the two round trips overlap instead of
        // serializing. On a failure the surviving side gets ABORed and drained, so
        // both pooled connections come back clean no matter what happened.
        var passiveCmd = srcPassive ? retrieveCommand : storeCommand;
        var activeCmd = srcPassive ? storeCommand : retrieveCommand;
        await passiveClient.BeginStartCommandAsync(passiveCmd).ConfigureAwait(false);
        await activeClient.BeginStartCommandAsync(activeCmd).ConfigureAwait(false);

        Exception? passiveErr = null, activeErr = null;
        var passiveStarted = false; var activeStarted = false;
        try { var (c, _) = await passiveClient.FinishStartCommandAsync().ConfigureAwait(false); passiveStarted = c / 100 == 1; }
        catch (Exception ex) { passiveErr = ex; }
        try { var (c, _) = await activeClient.FinishStartCommandAsync().ConfigureAwait(false); activeStarted = c / 100 == 1; }
        catch (Exception ex) { activeErr = ex; }

        if (passiveErr is not null || activeErr is not null)
        {
            // One side refused (dupe, permissions, …) while the other may already be
            // mid-transfer-start — abort the survivor so its control channel is clean
            // before these connections go back to the pool.
            var original = passiveErr ?? activeErr;
            try
            {
                if (passiveErr is not null && activeStarted) await activeClient.AbortTransferAsync().ConfigureAwait(false);
                if (activeErr is not null && passiveStarted) await passiveClient.AbortTransferAsync().ConfigureAwait(false);
            }
            catch (Exception abortEx)
            {
                // Couldn't restore the channel — surface a non-skippable error so the
                // caller drops these connections instead of reusing them desynced.
                throw new IOException($"transfer aborted uncleanly ({FirstLine(original!.Message)}; {FirstLine(abortEx.Message)})", original);
            }
            throw original!;
        }
        log("info", $"transferring {srcPath} -> {dstPath}");

        // Drain BOTH control channels concurrently, even when one side fails —
        // an unread final reply desyncs every later command on a pooled connection.
        var srcWait = src.WaitFinalAsync();
        var dstWait = dst.WaitFinalAsync();
        Exception? srcErr = null, dstErr = null;
        try { await srcWait.ConfigureAwait(false); } catch (Exception ex) { srcErr = ex; }
        try { await dstWait.ConfigureAwait(false); } catch (Exception ex) { dstErr = ex; }

        var failure = srcErr ?? dstErr;
        if (failure is not null)
        {
            // Wrong TLS orientation? Remember the opposite for this site pair so the
            // retry (and every later file) negotiates the way these servers want.
            if ((IsTlsRoleFailure(srcErr) || IsTlsRoleFailure(dstErr)) && pref == SslDataClientSide.Auto)
            {
                SslRoleFlip[pairKey] = !flipped;
                RoleFlipLearned?.Invoke(pairKey, !flipped);
                log("warn", $"TLS FXP handshake failed; making the {(srcIsTlsClient ? "destination" : "source")} the TLS client for {pairKey}");
            }
            throw failure;
        }
        log("info", $"completed {dstPath} ({sw.ElapsedMilliseconds}ms)");

        }
        finally { dataGate?.Release(); }
    }

    // Remembers, per source>dest pair, which side must be the data-channel TLS client.
    // Seeded from settings at startup and persisted when learned, so discovering the
    // orientation costs one failed handshake ever — not one per app restart.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> SslRoleFlip = new(StringComparer.OrdinalIgnoreCase);

    public static Action<string, bool>? RoleFlipLearned;

    public static void SeedRoleFlips(IEnumerable<KeyValuePair<string, bool>>? saved)
    {
        if (saved is null) return;
        foreach (var kv in saved)
            if (!string.IsNullOrWhiteSpace(kv.Key)) SslRoleFlip[kv.Key] = kv.Value;
    }

    // Extract "X-DUPE: <filename>" lines from a dupe refusal. The server lists other
    // files that already exist in the target dir, so one refusal teaches us a batch.
    public static List<string> ParseXdupeNames(Exception ex)
    {
        var result = new List<string>();
        foreach (var raw in (ex.Message ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var i = line.IndexOf("X-DUPE:", StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var name = line[(i + 7)..].Trim();
            if (name.Length > 0) result.Add(name);
        }
        return result;
    }

    private static bool IsTlsRoleFailure(Exception? ex)
    {
        if (ex is null) return false;
        var m = ex.Message ?? "";
        return m.Contains("handshake", StringComparison.OrdinalIgnoreCase)
            || (m.Contains("TLS", StringComparison.OrdinalIgnoreCase) && m.Contains("fail", StringComparison.OrdinalIgnoreCase))
            || (m.Contains("SSL", StringComparison.OrdinalIgnoreCase) && m.Contains("fail", StringComparison.OrdinalIgnoreCase));
    }

    private static string JoinPath(string basePath, string name)
    {
        if (basePath.Length == 0 || basePath == "/") return "/" + name;
        return basePath.TrimEnd('/') + "/" + name;
    }

    private static string RemoteBase(string path)
    {
        path = (path ?? "").Trim().TrimEnd('/');
        if (path.Length == 0) return "";
        var i = path.LastIndexOf('/');
        return i >= 0 ? path[(i + 1)..] : path;
    }

    private static List<string> MergeSkiplists(FtpClient src, FtpClient dst)
    {
        var srcPatterns = src.ConfiguredSkiplist();
        var dstPatterns = dst.ConfiguredSkiplist();
        return srcPatterns.Concat(dstPatterns)
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> MergeOrderLists(FtpClient src, FtpClient dst)
    {
        var srcPatterns = src.ConfiguredOrderList();
        var dstPatterns = dst.ConfiguredOrderList();
        return srcPatterns.Concat(dstPatterns)
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<RemoteEntry> ApplyOrderList(List<RemoteEntry> entries, string basePath, List<string> patterns)
    {
        if (patterns.Count == 0 || entries.Count <= 1) return entries;
        return entries
            .Select((entry, index) => new
            {
                Entry = entry,
                Index = index,
                Rank = OrderRank(JoinPath(basePath, entry.Name), entry.Name, patterns),
            })
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Index)
            .Select(x => x.Entry)
            .ToList();
    }

    private static int OrderRank(string path, string name, List<string> patterns)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            if (ShouldSkip(path, name, new[] { patterns[i] }))
                return i;
        }
        return int.MaxValue;
    }

    private static bool ShouldSkip(string path, string name, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
            if (PatternMatches(name, pattern) || PatternMatches(path, pattern))
                return true;
        return false;
    }

    private static bool PatternMatches(string value, string pattern)
    {
        value = (value ?? "").Trim();
        pattern = (pattern ?? "").Trim();
        if (value.Length == 0 || pattern.Length == 0) return false;
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            var regex = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnorableDirectoryMiss(Exception ex)
    {
        var message = ex.Message ?? "";
        return message.Contains("550", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("no such file", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    // glftpd's 0-byte placeholder for a missing piece, e.g. "release.r29-missing".
    public static bool IsIncompleteMarker(string name)
        => (name ?? "").TrimEnd().EndsWith("-missing", StringComparison.OrdinalIgnoreCase);

    // Errors that mean "nothing to do here, keep going" during a race: the dest
    // already has the file (X-DUPE / already exists), the dest rejects a -missing
    // marker, or the source piece is not actually present.
    private static bool IsDupeOrMissing(Exception ex)
    {
        var m = ex.Message ?? "";
        return m.Contains("x-dupe", StringComparison.OrdinalIgnoreCase)
            || m.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || m.Contains("PRET RETR target not found", StringComparison.OrdinalIgnoreCase)
            || m.Contains("-missing", StringComparison.OrdinalIgnoreCase)
            || m.Contains("not allowed here", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLine(string message)
    {
        message = (message ?? "").Trim();
        var idx = message.IndexOfAny(new[] { '\r', '\n' });
        return idx < 0 ? message : message[..idx].Trim();
    }
}
