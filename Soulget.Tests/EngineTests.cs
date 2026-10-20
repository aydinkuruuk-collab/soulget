using Soulget;
using Xunit;

namespace Soulget.Tests;

/// <summary>Progress lines, switching away from slow users, search reuse and taken files, on a fake clock
/// (ported from tests/test_speed.py). Nothing here touches the network.</summary>
public class EngineTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("soulget-test-").FullName;
    public void Dispose() => Directory.Delete(tmp, recursive: true);

    [Fact]
    public void Speed_watch_uses_recent_seconds_only()
    {
        var w = new SpeedWatch(20);
        for (int t = 0; t <= 30; t++)   // fast for 10 s, then crawling at 8 KB/s
            w.Add(t, t <= 10 ? t * 1_000_000L : 10_000_000L + (t - 10) * 8_000L);
        Assert.InRange(w.Speed()!.Value, 7_000, 9_000);
    }

    [Fact]
    public void Too_slow_rule()
    {
        Assert.True(SpeedWatch.TooSlow(8_000, 3_000_000, 50_000));        // 8 KB/s, 6 min left -> switch
        Assert.False(SpeedWatch.TooSlow(8_000, 300_000, 50_000));         // done in under a minute -> keep
        Assert.False(SpeedWatch.TooSlow(200_000, 30_000_000, 50_000));    // fast enough
        Assert.False(SpeedWatch.TooSlow(null, 30_000_000, 50_000));       // speed not known yet
        Assert.False(SpeedWatch.TooSlow(8_000, 3_000_000, 0));            // switching turned off
    }

    class Clock : IClock
    {
        public double Now { get; private set; }
        public readonly List<FakeTransfer> Transfers = new();
        public Task Delay(double seconds)
        {
            Now += seconds;
            foreach (var t in Transfers) t.Tick(seconds);
            return Task.CompletedTask;
        }
    }

    /// <summary>Like Clock, but really waits a moment, so a download stays in progress while the test goes on.</summary>
    class YieldingClock : IClock
    {
        public double Now { get; private set; }
        public async Task Delay(double seconds) { Now += seconds; await Task.Delay(1); }
    }

    class FakeTransfer(long size, long bps, string file, double startAfter = 0) : ITransfer
    {
        readonly TaskCompletionSource done = new();
        double waited;
        public bool Started { get; private set; }
        public long Bytes { get; private set; }
        public long Size => size;
        public Task Completion => done.Task;
        public Task<int?> PlaceInQueueAsync() => Task.FromResult<int?>(4);
        public void Cancel() => done.TrySetCanceled();

        public void Tick(double secs)
        {
            waited += secs;
            if (waited <= startAfter || done.Task.IsCompleted) return;
            Started = true;
            Bytes = Math.Min(size, Bytes + (long)(bps * secs));
            if (Bytes == size) { File.WriteAllText(file, "x"); done.TrySetResult(); }
        }
    }

    (bool Ok, string Why, List<string> Said, double Secs) Run(long size, long bps, bool maySwitch, double progressEvery, double startAfter = 0)
    {
        var clock = new Clock();
        var engine = new SoulseekEngine(tmp, (_, _) => Task.FromResult(new List<Candidate>()),
            (c, file) => { var t = new FakeTransfer(size, bps, file, startAfter); clock.Transfers.Add(t); return t; }, clock);
        var c = new Candidate { Username = "slowpoke", Path = @"x\Song.mp3", Size = size, Ext = "mp3", Bitrate = 320, Duration = 92 };
        var said = new List<string>();
        var (ok, why) = engine.DownloadAsync(c, Path.Combine(tmp, "out", "Song.mp3"), new DownloadSettings(120, 60, progressEvery, 50_000),
                                             said.Add, maySwitch).GetAwaiter().GetResult();
        return (ok, why, said, clock.Now);
    }

    [Fact]
    public void Slow_user_is_dropped_when_another_has_the_file()
    {
        var (ok, why, said, secs) = Run(3_700_000, 8_000, maySwitch: true, progressEvery: 10);
        Assert.False(ok);
        Assert.StartsWith("too slow (8 KB/s)", why);
        Assert.True(secs < 30);                                            // gave up after ~20 s, not 7 minutes
        Assert.Contains(said, s => s.Contains("% of 3.7 MB, 8 KB/s from slowpoke"));
    }

    [Fact]
    public void Slow_user_is_kept_when_nobody_else_has_it()
    {
        var (ok, _, said, _) = Run(3_700_000, 8_000, maySwitch: false, progressEvery: 30);
        Assert.True(ok);
        Assert.True(File.Exists(Path.Combine(tmp, "out", "Song.mp3")));
        Assert.True(said.Count >= 10);                                     // a progress line every 30 s for ~8 min
    }

    [Fact]
    public void Queue_wait_is_reported()
    {
        var (ok, _, said, _) = Run(1_000_000, 500_000, maySwitch: true, progressEvery: 30, startAfter: 45);
        Assert.True(ok);
        Assert.Equal("waiting for slowpoke to start sending (place 4 in their queue)", said[0]);
    }

    [Fact]
    public void Same_search_is_reused_across_tracks_of_one_record()
    {
        var sent = new List<string>();
        var engine = new SoulseekEngine(tmp, (q, _) =>
        {
            sent.Add(q);
            return Task.FromResult(new List<Candidate> { new() { Username = "u", Path = @"m\Subjected\Vault Series 5.0\A1.flac", Ext = "flac" } });
        }, (_, _) => throw new InvalidOperationException(), new Clock());
        var results = Enumerable.Range(0, 3).Select(_ => engine.SearchAsync("subjected vault series 5 0", 0).GetAwaiter().GetResult()).ToList();
        Assert.Equal(new[] { "subjected vault series 5 0" }, sent);
        Assert.All(results, r => Assert.Single(r));
        results[0][0].Score = 99;                                          // each track gets its own copy
        Assert.Equal(0, results[1][0].Score);
    }

    [Fact]
    public void A_file_already_taken_is_skipped_not_crashed()
    {
        var engine = new SoulseekEngine(tmp, (_, _) => Task.FromResult(new List<Candidate>()),
            (c, file) => new FakeTransfer(1_000_000, 100_000, file, startAfter: 1000), new YieldingClock());
        var c = new Candidate { Username = "someone", Path = @"x\Song.mp3", Size = 1_000_000, Ext = "mp3" };
        var s = new DownloadSettings(120, 60, 30, 50_000);
        var first = engine.DownloadAsync(c, Path.Combine(tmp, "a.mp3"), s, _ => { }, false);   // still waiting...
        var (ok, why) = engine.DownloadAsync(c.Copy(), Path.Combine(tmp, "b.mp3"), s, _ => { }, false).GetAwaiter().GetResult();
        Assert.False(ok);
        Assert.Contains("already taken", why);
        Assert.Equal(0, engine.Failures("someone"));                       // not that user's fault
        first.GetAwaiter().GetResult();
    }

    [Fact]
    public void Library_refusal_becomes_a_normal_failure()
    {
        var engine = new SoulseekEngine(tmp, (_, _) => Task.FromResult(new List<Candidate>()),
            (_, _) => throw new Soulseek.DuplicateTransferException("already"), new Clock());
        var c = new Candidate { Username = "u", Path = @"x\Song.mp3", Size = 100, Ext = "mp3" };
        var (ok, why) = engine.DownloadAsync(c, Path.Combine(tmp, "x.mp3"), new DownloadSettings(1, 1, 30, 0), _ => { }, false)
                              .GetAwaiter().GetResult();
        Assert.False(ok);
        Assert.StartsWith("couldn't start", why);
    }

    [Fact]
    public void Lock_blocks_a_second_run_and_clears_a_dead_one()
    {
        using (new RunLock(tmp).Enter())
        {
            File.WriteAllText(Path.Combine(tmp, RunLock.FileName), "1");   // process 1 is always alive (launchd)
            Assert.Throws<Locked>(() => new RunLock(tmp).Enter());
        }
        using var dead = System.Diagnostics.Process.Start("true")!;
        dead.WaitForExit();
        File.WriteAllText(Path.Combine(tmp, RunLock.FileName), dead.Id.ToString());   // a run that crashed
        using (new RunLock(tmp).Enter())
            Assert.Equal(Environment.ProcessId.ToString(), File.ReadAllText(Path.Combine(tmp, RunLock.FileName)));
        Assert.False(File.Exists(Path.Combine(tmp, RunLock.FileName)));
    }
}

public class RealClientTests
{
    [Fact]
    public void Real_client_accepts_our_settings_and_version()
    {
        // Builds the real Soulseek.NET client the way soulget does, without connecting anywhere.
        using var client = new Soulseek.SoulseekClient(SoulseekEngine.MinorVersion, new Soulseek.SoulseekClientOptions(
            enableListener: true, listenPort: 60000, enableDistributedNetwork: false, acceptDistributedChildren: false));
        Assert.Equal(170, client.MajorVersion);
        Assert.Equal(20261020, client.MinorVersion);
    }
}
