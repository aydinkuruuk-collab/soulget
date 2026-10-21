using System.Text.Json.Nodes;
using Soulget;
using Xunit;

// Some tests change shared settings (free disk space): run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Soulget.Tests;

/// <summary>A pretend Soulseek: canned search results, and per-user download outcomes.</summary>
class FakeEngine(Dictionary<string, List<Candidate>> results) : IEngine
{
    public readonly List<string> Queries = new();
    public readonly List<(string User, string Ext, bool MaySwitch)> Tried = new();
    /// <summary>"ok" (default), "fail", or "slow" (too slow while switching is allowed).</summary>
    public Dictionary<string, string> Script = new();
    public Func<string, Exception?> SearchError = _ => null;

    public Task StartAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public string? Problem => null;

    public Task<List<Candidate>> SearchAsync(string query, double wait)
    {
        lock (Queries) Queries.Add(query);
        if (SearchError(query) is { } e) throw e;
        return Task.FromResult(results.GetValueOrDefault(query, new()).Select(c => c.Copy()).ToList());
    }

    public Task<(bool Ok, string Why)> DownloadAsync(Candidate c, string finalPath, DownloadSettings s, Action<string> say, bool maySwitch)
    {
        lock (Tried) Tried.Add((c.Username, c.Ext, maySwitch));
        var mode = Script.GetValueOrDefault(c.Username, "ok");
        if (mode == "fail") return Task.FromResult((false, "didn't start within 120s"));
        if (mode == "slow" && maySwitch) return Task.FromResult((false, "too slow (8 KB/s), switching user"));
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        File.WriteAllBytes(finalPath, new byte[1000]);
        return Task.FromResult((true, ""));
    }
}

/// <summary>The download flow with a fake Soulseek (ported from tests/test_flow.py and test_queue.py).</summary>
public class FlowTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("soulget-test-").FullName;
    public void Dispose()
    {
        Runner.FreeGb = path => new DriveInfo(path).AvailableFreeSpace / 1e9;
        Runner.StopRequested = false;
        Directory.Delete(tmp, recursive: true);
    }

    internal static Candidate R(string user, string path, int? bitrate = 320, bool free = true, int speed = 500_000,
                                int? duration = 151, long size = 6_000_000) =>
        new() { Username = user, Path = path, Size = size, Ext = path[(path.LastIndexOf('.') + 1)..].ToLowerInvariant(),
                Bitrate = bitrate, Duration = duration, FreeSlot = free, Speed = speed, Queue = 0 };

    internal static Opts O(params string[] formats) => new(320, formats.Length > 0 ? formats.ToList() : new() { "mp3", "flac" },
        Parallel: 1, SearchWait: 0, MaxTries: 5, StartTimeout: 1, StallTimeout: 1, ProgressEvery: 30, SlowBps: 50_000,
        FallbackQueries: true, FileName: Tracks.DefaultFileName, MinFreeGb: 10);

    static readonly string[] Lossless = { "flac", "wav", "aiff", "aif", "mp3" };

    static Track T(string a, string t, string r, int y, string d = "") => Tracks.MakeTrack(a, t, r, y, d)!;

    Outcome Fetch(FakeEngine sl, Track t, Opts o) =>
        Runner.FetchTrackAsync(sl, t, "[1/1]", tmp, o, new HashSet<string>()).GetAwaiter().GetResult();

    [Fact]
    public void Picks_good_file_and_retries()
    {
        var t = T("Boards Of Canada", "Roygbiv", "Music Has The Right To Children", 1998, "2:31");
        var sl = new FakeEngine(new()
        {
            ["boards of canada roygbiv"] = new()
            {
                R("lowq", @"m\Boards of Canada\MHTRTC\04 Roygbiv.mp3", bitrate: 192),
                R("remixer", @"m\Boards of Canada\Roygbiv (Some Remix).mp3"),
                R("fastbutbroken", @"m\Boards of Canada\MHTRTC\04 - Roygbiv.mp3", speed: 5_000_000),
                R("slowok", @"m\Boards of Canada\MHTRTC\04 - Roygbiv.mp3", free: false, speed: 50_000),
            },
        }) { Script = { ["fastbutbroken"] = "fail" } };
        var o = Fetch(sl, t, O());
        Assert.Equal(new[] { "fastbutbroken", "slowok" }, sl.Tried.Select(x => x.User));
        Assert.Equal(("downloaded", "slowok"), (o.Status, o.User));
        Assert.True(File.Exists(Path.Combine(tmp, "1998 Boards Of Canada - Roygbiv (Music Has The Right To Children).mp3")));
    }

    [Fact]
    public void Reports_why_nothing_matched()
    {
        var t = T("Boards Of Canada", "Roygbiv", "MHTRTC", 1998);
        var sl = new FakeEngine(new() { ["boards of canada roygbiv"] = new() { R("a", @"x\Boards of Canada\Roygbiv.mp3", bitrate: 128) } });
        var o = Fetch(sl, t, O());
        Assert.Equal("not found", o.Status);
        Assert.Contains("bitrate too low", o.Note);
        Assert.Equal(new[] { "boards of canada roygbiv", "roygbiv", "boards of canada mhtrtc" }, sl.Queries);  // title only, then the record
    }

    [Fact]
    public void Full_run_remembers_downloads()
    {
        var fake = new FakeEngine(new() { ["boards of canada roygbiv"] = new() { R("u1", @"m\Boards of Canada\Roygbiv.mp3") } });
        var tracks = new List<Track> { T("Boards Of Canada", "Roygbiv", "MHTRTC", 1998), T("Boards Of Canada", "Olson", "MHTRTC", 1998) };
        var outDir = Path.Combine(tmp, "Boards Of Canada");
        Directory.CreateDirectory(outDir);
        Work Run()
        {
            var (todo, had) = Runner.SplitDone(tracks, outDir);
            var w = new Work(outDir, todo, O() with { Parallel = 2 }, had, Misses.Load(outDir));
            if (todo.Count > 0) Runner.RunWorkAsync(new() { w }, fake, "x").GetAwaiter().GetResult();
            return w;
        }
        Assert.Equal(new[] { "downloaded", "not found" }, Run().Outcomes.Select(o => o.Status).OrderBy(x => x));
        Assert.Equal(new[] { "already had", "not found" }, Run().Outcomes.Select(o => o.Status).OrderBy(x => x));
        Assert.Equal(new[] { "u1" }, fake.Tried.Select(x => x.User));                   // not downloaded twice
        Assert.Equal(new[] { tracks[1].Key }, Misses.Load(outDir).Data.Select(kv => kv.Key));  // Olson remembered as missing
        Assert.True(File.Exists(Path.Combine(outDir, "report.csv")));
    }

    [Fact]
    public void Goes_back_to_slow_user_if_nobody_faster_delivers()
    {
        var t = T("Boards Of Canada", "Melissa Juice", "Twoism", 1995);
        var sl = new FakeEngine(new()
        {
            ["boards of canada melissa juice"] = new()
            {
                R("slow", @"m\Boards of Canada\Melissa Juice.mp3", speed: 900_000),
                R("broken", @"m\Boards of Canada\Melissa Juice.mp3", speed: 100_000),
            },
        }) { Script = { ["slow"] = "slow", ["broken"] = "fail" } };
        var o = Fetch(sl, t, O());
        Assert.Equal(new[] { ("slow", true), ("broken", false), ("slow", false) }, sl.Tried.Select(x => (x.User, x.MaySwitch)));
        Assert.Equal(("downloaded", "slow"), (o.Status, o.User));
    }

    [Fact]
    public void Lossless_beats_faster_mp3()
    {
        var t = T("Ben Sims", "Manipulated", "Manipulated Remixes", 2000);
        var sl = new FakeEngine(new()
        {
            ["ben sims manipulated"] = new()
            {
                R("fastmp3", @"m\Ben Sims\Manipulated.mp3", speed: 5_000_000),
                R("slowflac", @"m\Ben Sims\Manipulated.flac", bitrate: null, free: false, speed: 50_000),
            },
        });
        var o = Fetch(sl, t, O(Lossless));
        Assert.Equal("flac", o.Fmt);
        Assert.Equal(new[] { "slowflac" }, sl.Tried.Select(x => x.User));
    }

    [Fact]
    public void Falls_back_to_best_mp3_after_lossless_users_fail()
    {
        var t = T("Ben Sims", "Manipulated", "Manipulated Remixes", 2000);
        var files = Enumerable.Range(0, 6).Select(i => R($"flac{i}", @"m\Ben Sims\Manipulated.flac", bitrate: null)).ToList();
        files.Add(R("mp3_256", @"m\Ben Sims\Manipulated.mp3", bitrate: 256));
        files.Add(R("mp3_320", @"m\Ben Sims\Manipulated.mp3", bitrate: 320));
        var sl = new FakeEngine(new() { ["ben sims manipulated"] = files });
        for (int i = 0; i < 6; i++) sl.Script[$"flac{i}"] = "fail";
        var o = Fetch(sl, t, O(Lossless));
        // 5 lossless tries (the per-quality limit), then the 320 mp3; 256 is below the minimum
        Assert.Equal(new[] { "flac0", "flac1", "flac2", "flac3", "flac4", "mp3_320" }, sl.Tried.Select(x => x.User));
        Assert.Equal(("downloaded", "mp3", 320), (o.Status, o.Fmt, o.Bitrate ?? 0));
    }

    [Fact]
    public void Slow_flac_preferred_over_mp3()
    {
        var t = T("Ben Sims", "Manipulated", "Manipulated Remixes", 2000);
        var sl = new FakeEngine(new()
        {
            ["ben sims manipulated"] = new()
            {
                R("slowflac", @"m\Ben Sims\Manipulated.flac", bitrate: null, speed: 900_000),
                R("brokenflac", @"m\Ben Sims\Manipulated.flac", bitrate: null, speed: 100_000),
                R("goodmp3", @"m\Ben Sims\Manipulated.mp3"),
            },
        }) { Script = { ["slowflac"] = "slow", ["brokenflac"] = "fail" } };
        var o = Fetch(sl, t, O(Lossless));
        Assert.Equal(new[] { ("slowflac", "flac", true), ("brokenflac", "flac", false), ("slowflac", "flac", false) }, sl.Tried);
        Assert.Equal(("flac", "slowflac"), (o.Fmt, o.User));
    }

    [Fact]
    public void An_unexpected_error_fails_one_track_not_the_run()
    {
        var tracks = new List<Track> { T("Raime", "Raime", "Raime EP", 2010), T("Raime", "This Foundry", "Raime EP", 2010) };
        var fake = new FakeEngine(new() { ["raime this foundry"] = new() { R("u1", @"m\Raime\Raime EP\This Foundry.mp3") } })
        {
            SearchError = q => q == "raime" ? new InvalidOperationException("boom") : null,   // "Raime - Raime" is sent as one word
        };
        var outDir = Path.Combine(tmp, "Blackest Ever Black (label)");
        Directory.CreateDirectory(outDir);
        var w = new Work(outDir, tracks, O(), new(), Misses.Load(outDir));
        Runner.RunWorkAsync(new() { w }, fake, "x").GetAwaiter().GetResult();
        Assert.Equal(new[] { "downloaded", "failed" }, w.Outcomes.Select(o => o.Status).OrderBy(x => x));
        Assert.Contains("unexpected error", J.Str(Misses.Load(outDir).Data[tracks[0].Key], "note"));   // still listed for retries
    }

    /// <summary>Downloads like FakeEngine, but the music drive disappears when the second download starts.</summary>
    class DriveGoesAway(FakeEngine inner, string drive) : IEngine
    {
        int downloads;
        public Task StartAsync() => inner.StartAsync();
        public Task StopAsync() => inner.StopAsync();
        public string? Problem => inner.Problem;
        public Task<List<Candidate>> SearchAsync(string q, double wait) => inner.SearchAsync(q, wait);
        public Task<(bool Ok, string Why)> DownloadAsync(Candidate c, string finalPath, DownloadSettings s, Action<string> say, bool maySwitch)
        {
            if (++downloads == 2) { Directory.Delete(drive, recursive: true); return Task.FromResult((false, "connection lost")); }
            return inner.DownloadAsync(c, finalPath, s, say, maySwitch);
        }
    }

    [Fact]
    public void A_disconnected_drive_stops_the_run_cleanly()
    {
        var tracks = new List<Track> { T("Raime", "This Foundry", "Raime EP", 2010), T("Raime", "Passed Over", "Raime EP", 2010),
                                       T("Raime", "Exist", "Raime EP", 2010) };
        var fake = new FakeEngine(new()
        {
            ["raime this foundry"] = new() { R("u1", @"m\Raime\Raime EP\This Foundry.mp3") },
            ["raime passed over"] = new() { R("u2", @"m\Raime\Raime EP\Passed Over.mp3") },
            ["raime exist"] = new() { R("u3", @"m\Raime\Raime EP\Exist.mp3") },
        });
        var drive = Path.Combine(tmp, "MusicDrive");
        var outDir = Path.Combine(drive, "Blackest Ever Black (label)");
        Directory.CreateDirectory(outDir);
        var w = new Work(outDir, tracks, O(), new(), Misses.Load(outDir));
        Assert.True(Runner.RunWorkAsync(new() { w }, new DriveGoesAway(fake, drive), "x").GetAwaiter().GetResult());   // no crash
        Assert.Equal(new[] { "downloaded", "failed" }, w.Outcomes.Select(o => o.Status));   // the third track was never started
        Assert.False(Directory.Exists(drive));   // nothing written anywhere else
    }

    static string Folder(string root, string name, Job job, params Track[] downloaded)
    {
        var d = Path.Combine(root, name);
        Directory.CreateDirectory(d);
        job.Save(d);
        var manifest = new JsonObject();
        foreach (var t in downloaded)
        {
            File.WriteAllText(Path.Combine(d, $"{t.Title}.mp3"), "x");
            manifest[t.Key] = new JsonObject { ["file"] = $"{t.Title}.mp3", ["from"] = "u" };
        }
        Manifest.Save(d, manifest);
        return d;
    }

    [Fact]
    public void One_run_never_fetches_a_track_for_two_folders()
    {
        var root = Path.Combine(tmp, "music");
        var t = T("Ben Sims", "Manipulated", "Manipulated Remixes", 2000);
        var a = Folder(root, "Ben Sims", new Job { Kind = "artist", Id = 1, Name = "Ben Sims", Formats = new() { "mp3" } });
        var b = Folder(root, "Primate Recordings (label)", new Job { Kind = "label", Id = 363, Name = "Primate Recordings", Formats = new() { "mp3" } });
        var fake = new FakeEngine(new() { ["ben sims manipulated"] = new() { R("u1", @"m\Ben Sims\Manipulated.mp3") } });
        var works = new[] { a, b }.Select(d => new Work(d, new() { t }, O(), new(), Misses.Load(d))).ToList();
        Runner.RunWorkAsync(works, fake, "x").GetAwaiter().GetResult();
        Assert.Equal(new[] { "u1" }, fake.Tried.Select(x => x.User));
        Assert.Equal(new[] { "already had" }, works[1].Outcomes.Select(o => o.Status));
    }

    [Fact]
    public void Already_downloaded_elsewhere_counts_as_had()
    {
        var root = Path.Combine(tmp, "music");
        var math = T("Boards Of Canada", "Music Is Math", "Geogaddi", 2002);
        var gyro = T("Boards Of Canada", "Gyroscope", "Geogaddi", 2002);
        Folder(root, "Boards Of Canada", new Job { Kind = "artist", Id = 307, Name = "Boards Of Canada" }, math);
        var album = Folder(root, "Boards Of Canada - Geogaddi", new Job { Kind = "master", Id = 2129, Name = "Boards Of Canada - Geogaddi" });
        var (todo, had) = Runner.SplitDone(new() { math, gyro }, album);
        Assert.Equal(new[] { "Gyroscope" }, todo.Select(x => x.Title));
        Assert.Equal("already in Boards Of Canada", had.Single().Note);
    }

    [Fact]
    public void Low_disk_stops_downloads_but_keeps_tracks_listed()
    {
        var t = T("Ben Sims", "Manipulated", "Manipulated Remixes", 2000);
        var d = Folder(Path.Combine(tmp, "music"), "Ben Sims", new Job { Kind = "artist", Id = 1, Name = "Ben Sims" });
        var fake = new FakeEngine(new() { ["ben sims manipulated"] = new() { R("u1", @"m\Ben Sims\Manipulated.mp3") } });
        Runner.FreeGb = _ => 5.0;
        var w = new Work(d, new() { t }, O(), new(), Misses.Load(d));
        Runner.RunWorkAsync(new() { w }, fake, "x").GetAwaiter().GetResult();
        Assert.Empty(fake.Queries);
        Assert.Equal(0, J.Int(Misses.Load(d).Data[t.Key], "checks"));   // still on the list for later
    }
}
