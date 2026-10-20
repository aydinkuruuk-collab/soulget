// soulget - Copyright (C) 2026 the soulget contributors
// SPDX-License-Identifier: GPL-3.0-only (see LICENSE and NOTICE)

using System.Collections.Concurrent;
using Soulseek;

namespace Soulget;

public class LoginFailed(string message) : Exception(message);

/// <summary>What the run loop needs from Soulseek: search, and download one file. The real one is
/// <see cref="SoulseekEngine"/>; tests use a fake.</summary>
public interface IEngine
{
    Task StartAsync();
    Task StopAsync();
    /// <summary>Set when Soulseek ended the session (e.g. the same name logged in elsewhere).</summary>
    string? Problem { get; }
    Task<List<Candidate>> SearchAsync(string query, double wait);
    Task<(bool Ok, string Why)> DownloadAsync(Candidate c, string finalPath, DownloadSettings s, Action<string> say, bool maySwitch);
}

public record DownloadSettings(double StartTimeout, double StallTimeout, double ProgressEvery, double SlowBps);

/// <summary>Download speed (bytes per second) over the last few seconds.</summary>
public class SpeedWatch(double window = SpeedWatch.Window)
{
    /// <summary>Speed is judged over the last 20 seconds, and only after a download has run that long.</summary>
    public const double Window = 20.0;
    readonly Queue<(double t, long bytes)> samples = new();
    (double t, long bytes) lastSample;

    public void Add(double t, long nbytes)
    {
        samples.Enqueue((t, nbytes));
        lastSample = (t, nbytes);
        while (t - samples.Peek().t > window) samples.Dequeue();
    }

    public double Span() => samples.Count > 0 ? lastSample.t - samples.Peek().t : 0.0;

    public double? Speed()
    {
        if (Span() < 1) return null;
        var (t0, b0) = samples.Peek();
        return (lastSample.bytes - b0) / (lastSample.t - t0);
    }

    /// <summary>Don't abandon a slow download that will be done within this many seconds anyway.</summary>
    public const double NearlyDoneSeconds = 60.0;

    /// <summary>Switch users only when speed is known, below the limit, and the file wouldn't finish soon anyway.</summary>
    public static bool TooSlow(double? speed, long remainingBytes, double slowBps)
    {
        if (speed is null || slowBps <= 0 || speed >= slowBps) return false;
        return remainingBytes / Math.Max(speed.Value, 1.0) > NearlyDoneSeconds;
    }
}

/// <summary>Soulseek's server disconnects clients that search too often. This is the budget other
/// well-behaved clients use: at most 34 searches per 220 seconds.</summary>
public class SearchLimiter
{
    public const int PerWindow = 34;
    public const double WindowSeconds = 220.0;
    readonly Queue<DateTime> times = new();
    readonly SemaphoreSlim gate = new(1, 1);

    public async Task WaitAsync()
    {
        await gate.WaitAsync();
        try
        {
            while (times.Count >= PerWindow)
            {
                var sleep = times.Peek().AddSeconds(WindowSeconds) - DateTime.UtcNow;
                if (sleep > TimeSpan.Zero) await Task.Delay(sleep);
                times.Dequeue();
            }
            times.Enqueue(DateTime.UtcNow);
        }
        finally { gate.Release(); }
    }
}

/// <summary>One download as the watch loop sees it, so the loop can be tested with a fake.</summary>
public interface ITransfer
{
    bool Started { get; }          // the other user has begun sending
    long Bytes { get; }
    long Size { get; }
    Task Completion { get; }       // finishes when the file is complete; faults if it failed
    Task<int?> PlaceInQueueAsync();
    void Cancel();
}

/// <summary>Time, replaceable in tests so a 7-minute download can be checked in milliseconds.</summary>
public interface IClock
{
    double Now { get; }
    Task Delay(double seconds);
}

public sealed class RealClock : IClock
{
    public static readonly RealClock Instance = new();
    readonly System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
    public double Now => sw.Elapsed.TotalSeconds;
    public Task Delay(double seconds) => Task.Delay(TimeSpan.FromSeconds(seconds));
}

public static class Watch
{
    /// <summary>Follow one download until it is done or should be given up. Every ProgressEvery seconds it
    /// says how far along it is. If maySwitch is set (another user has the file too) and the speed stays
    /// under SlowBps, it gives up so the next user can be tried.</summary>
    public static async Task<(bool Ok, string Why)> FollowAsync(ITransfer tr, Candidate c, DownloadSettings s,
                                                                Action<string> say, bool maySwitch, IClock clock)
    {
        double t0 = clock.Now, lastChange = t0, lastReport = t0;
        long lastBytes = 0;
        double? began = null;
        var watch = new SpeedWatch();
        int? place = null;
        while (true)
        {
            await clock.Delay(1);
            var now = clock.Now;
            if (tr.Completion.IsCompletedSuccessfully) return (true, "");
            if (tr.Completion.IsFaulted || tr.Completion.IsCanceled)
                return (false, $"the other user refused or failed ({Reason(tr.Completion.Exception?.GetBaseException())})");
            if (tr.Bytes != lastBytes) { lastBytes = tr.Bytes; lastChange = now; }
            var started = lastBytes > 0 || tr.Started;
            var total = tr.Size > 0 ? tr.Size : c.Size;
            if (started)
            {
                began ??= now;
                watch.Add(now, lastBytes);
            }
            if (now - lastReport >= s.ProgressEvery)
            {
                lastReport = now;
                if (started)
                {
                    var speed = watch.Speed();
                    var kbs = speed is not null ? $", {speed / 1000:0} KB/s" : "";
                    var pct = total > 0 ? 100.0 * lastBytes / total : 0;
                    say($"{pct:0}% of {total / 1e6:0.0} MB{kbs} from {c.Username}");
                }
                else
                {
                    place = await tr.PlaceInQueueAsync();
                    say($"waiting for {c.Username} to start sending" + (place is > 0 ? $" (place {place} in their queue)" : ""));
                }
            }
            if (!started && now - t0 > s.StartTimeout)
                return (false, $"didn't start within {(int)s.StartTimeout}s" + (place is > 0 ? $", place in their queue: {place}" : ""));
            if (started && now - lastChange > s.StallTimeout)
                return (false, $"stalled for {(int)s.StallTimeout}s");
            if (maySwitch && began is not null && now - began >= SpeedWatch.Window)
            {
                var speed = watch.Speed();
                if (SpeedWatch.TooSlow(speed, total - lastBytes, s.SlowBps))
                    return (false, $"too slow ({(speed ?? 0) / 1000:0} KB/s), switching user");
            }
        }
    }

    /// <summary>A short, plain reason for a failed transfer.</summary>
    public static string Reason(Exception? e) => e switch
    {
        null => "failed",
        TransferRejectedException r => string.IsNullOrWhiteSpace(r.Message) ? "rejected" : r.Message.Trim().TrimEnd('.'),
        UserOfflineException => "user is offline",
        TimeoutException => "no answer",
        OperationCanceledException => "cancelled",
        _ => string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message.Trim().TrimEnd('.'),
    };
}

/// <summary>Soulseek through Soulseek.NET: log in, search, and download one file at a time per track.</summary>
public sealed class SoulseekEngine : IEngine
{
    /// <summary>soulget's own client version, as the Soulseek.NET license requires (see NOTICE).</summary>
    public const int MinorVersion = 20261020;
    /// <summary>Several tracks of one record send the same record search; reuse its answer this long.</summary>
    const double SearchCacheSeconds = 600.0;

    readonly string username, password, incomplete;
    readonly int listenPort;
    readonly bool verbose;
    SoulseekClient? client;
    readonly SearchLimiter limiter = new();
    readonly ConcurrentDictionary<string, int> userFailures = new();
    readonly object sync = new();
    readonly Dictionary<string, (DateTime at, List<Candidate> found)> cache = new();
    readonly Dictionary<string, Task<List<Candidate>>> inflight = new();
    readonly HashSet<(string, string)> claimed = new();   // (user, file) downloaded or downloading this run
    readonly Func<string, double, Task<List<Candidate>>> rawSearch;
    readonly Func<Candidate, string, ITransfer> startTransfer;
    readonly IClock clock;

    public string? Problem { get; private set; }

    public SoulseekEngine(string username, string password, string incompleteDir, int listenPort = 60000, bool verbose = false)
    {
        (this.username, this.password, incomplete, this.listenPort, this.verbose) = (username, password, incompleteDir, listenPort, verbose);
        rawSearch = SearchOnceAsync;
        startTransfer = (c, tmp) => new SlskTransfer(client ?? throw new InvalidOperationException("not logged in"), c, tmp);
        clock = RealClock.Instance;
    }

    /// <summary>For tests: the network parts replaced by fakes.</summary>
    public SoulseekEngine(string incompleteDir, Func<string, double, Task<List<Candidate>>> search,
                          Func<Candidate, string, ITransfer> transfer, IClock clock)
    {
        (username, password, incomplete, listenPort, verbose) = ("", "", incompleteDir, 0, false);
        (rawSearch, startTransfer, this.clock) = (search, transfer, clock);
    }

    public int Failures(string user) => userFailures.GetValueOrDefault(user);

    public async Task StartAsync()
    {
        client = new SoulseekClient(MinorVersion, new SoulseekClientOptions(
            enableListener: true,
            listenPort: listenPort,
            enableDistributedNetwork: false,   // soulget shares nothing, so it doesn't relay others' searches
            acceptDistributedChildren: false,
            minimumDiagnosticLevel: verbose ? Soulseek.Diagnostics.DiagnosticLevel.Debug : Soulseek.Diagnostics.DiagnosticLevel.None));
        if (verbose) client.DiagnosticGenerated += (_, e) => Console.WriteLine($"  [soulseek] {e.Message}");
        client.Disconnected += (_, e) =>
        {
            if (e.Exception is KickedFromServerException)
                Problem = "Soulseek logged us out because the same name logged in somewhere else (SoulseekQt?)";
            else if (Problem is null && client is not null)
                Problem = "the connection to the Soulseek server was lost" + (e.Exception is null ? "" : $" ({e.Exception.Message})");
        };
        try
        {
            await client.ConnectAsync(username, password);
        }
        catch (LoginRejectedException e)
        {
            throw new LoginFailed($"Soulseek rejected the login ({e.Message}). Check username/password in config.toml.");
        }
        catch (ListenException e)
        {
            throw new LoginFailed($"could not open network port {listenPort} ({e.Message}). " +
                                  "Is another copy of soulget running? You can change listen_port in config.toml.");
        }
        catch (Exception e) when (e is ConnectionException or TimeoutException or SoulseekClientException)
        {
            throw new LoginFailed($"could not reach the Soulseek server ({e.Message})");
        }
    }

    public Task StopAsync()
    {
        var c = client;
        client = null;   // so the disconnect below isn't reported as a problem
        if (c is not null)
        {
            try { c.Disconnect("soulget is done"); } catch (Exception) { }
            c.Dispose();
        }
        return Task.CompletedTask;
    }

    /// <summary>Files matching a query. The same query again within 10 minutes (or while it is still running
    /// for another track) reuses the answer instead of searching again.</summary>
    public async Task<List<Candidate>> SearchAsync(string query, double wait)
    {
        Task<List<Candidate>> task;
        lock (sync)
        {
            if (cache.TryGetValue(query, out var hit) && (DateTime.UtcNow - hit.at).TotalSeconds < SearchCacheSeconds)
                return Fresh(hit.found);
            if (!inflight.TryGetValue(query, out task!))
                inflight[query] = task = rawSearch(query, wait);
        }
        List<Candidate> found;
        try { found = await task; }
        finally { lock (sync) inflight.Remove(query); }
        lock (sync) cache[query] = (DateTime.UtcNow, found);
        return Fresh(found);
    }

    /// <summary>Fresh copies (matching writes into them), without users who keep failing us.</summary>
    List<Candidate> Fresh(List<Candidate> found) =>
        found.Where(c => userFailures.GetValueOrDefault(c.Username) < 3).Select(c => c.Copy()).ToList();

    async Task<List<Candidate>> SearchOnceAsync(string query, double wait)
    {
        var c = client ?? throw new InvalidOperationException("not logged in");
        await limiter.WaitAsync();
        var responses = new ConcurrentBag<SearchResponse>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(wait + 2));
        try
        {
            await c.SearchAsync(SearchQuery.FromText(query), r => responses.Add(r), SearchScope.Network,
                options: new SearchOptions(searchTimeout: (int)(wait * 1000), responseLimit: 100_000, filterResponses: false,
                                           fileLimit: 10_000_000, removeSingleCharacterSearchTerms: false),
                cancellationToken: cts.Token);
        }
        catch (OperationCanceledException) { }   // we stop listening after `wait` seconds
        var outList = new List<Candidate>();
        foreach (var r in responses)
            foreach (var f in r.Files)   // locked (friends-only) files are skipped
                outList.Add(new Candidate
                {
                    Username = r.Username, Path = f.Filename, Size = f.Size, Ext = Tracks.FileExt(f.Filename),
                    Bitrate = f.BitRate is > 0 ? f.BitRate : null, Duration = f.Length is > 0 ? f.Length : null,
                    FreeSlot = r.HasFreeUploadSlot, Speed = r.UploadSpeed, Queue = (int)Math.Min(r.QueueLength, int.MaxValue),
                });
        return outList;
    }

    public async Task<(bool Ok, string Why)> DownloadAsync(Candidate c, string finalPath, DownloadSettings s, Action<string> say, bool maySwitch)
    {
        // Two tracks can match the very same file (e.g. one song listed twice on Discogs): never fetch it twice.
        lock (sync)
        {
            if (!claimed.Add((c.Username, c.Path))) return (false, "that file is already taken for another track");
        }
        var tmp = Path.Combine(incomplete, $"{Environment.ProcessId}-{(DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100}.{c.Ext}");
        ITransfer tr;
        try
        {
            tr = startTransfer(c, tmp);
        }
        catch (Exception e)   // the library refused; this file just doesn't work out
        {
            userFailures.AddOrUpdate(c.Username, 1, (_, n) => n + 1);
            lock (sync) claimed.Remove((c.Username, c.Path));
            return (false, $"couldn't start ({e.GetType().Name})");
        }
        bool ok = false;
        try
        {
            var (done, why) = await Watch.FollowAsync(tr, c, s, say, maySwitch, clock);
            if (done)
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                System.IO.File.Move(tmp, finalPath);
                ok = true;
            }
            return (done, why);
        }
        finally
        {
            if (!ok)
            {
                userFailures.AddOrUpdate(c.Username, 1, (_, n) => n + 1);
                lock (sync) claimed.Remove((c.Username, c.Path));
                tr.Cancel();
                try { await tr.Completion.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
                try { System.IO.File.Delete(tmp); } catch (IOException) { }
            }
        }
    }

    /// <summary>A Soulseek.NET download, seen through <see cref="ITransfer"/>.</summary>
    sealed class SlskTransfer : ITransfer
    {
        readonly SoulseekClient client;
        readonly Candidate c;
        readonly CancellationTokenSource cts = new();
        long bytes;
        volatile bool started;

        public SlskTransfer(SoulseekClient client, Candidate c, string localFile)
        {
            (this.client, this.c) = (client, c);
            Size = c.Size;
            var options = new TransferOptions(
                stateChanged: e => { if (e.Transfer.State.HasFlag(TransferStates.InProgress)) started = true; },
                progressUpdated: e => Interlocked.Exchange(ref bytes, e.Transfer.BytesTransferred),
                disposeOutputStreamOnCompletion: true);
            Completion = client.DownloadAsync(c.Username, c.Path, localFile, c.Size, 0, null, options, cts.Token);
        }

        public bool Started => started;
        public long Bytes => Interlocked.Read(ref bytes);
        public long Size { get; }
        public Task Completion { get; }

        public async Task<int?> PlaceInQueueAsync()
        {
            try { return await client.GetDownloadPlaceInQueueAsync(c.Username, c.Path).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { return null; }
        }

        public void Cancel() { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
    }
}
