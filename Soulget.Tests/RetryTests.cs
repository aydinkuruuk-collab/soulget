using System.Diagnostics;
using System.Text.Json.Nodes;
using Soulget;
using Xunit;

namespace Soulget.Tests;

/// <summary>Retrying missing tracks: miss records, giving up, the waiting line, the check log, the timer file,
/// whole simulated retry rounds, queueing for later and renaming (ported from tests/test_retry.py and
/// tests/test_queue.py). Nothing here logs in to Soulseek or installs the timer.</summary>
public class RetryTests : IDisposable
{
    readonly string tmp = Directory.CreateTempSubdirectory("soulget-test-").FullName;
    static readonly DateTime T0 = new(2026, 10, 15, 9, 0, 0);

    public RetryTests()
    {
        Retry.QtRunning = () => false;
        Retry.Notify = (_, _) => { };   // never a real pop-up from a test
    }

    public void Dispose()
    {
        Retry.QtRunning = Retry.SoulseekQtRunning;
        Retry.MakeEngine = (u, p, i, port, v) => new SoulseekEngine(u, p, i, port, v);
        Retry.BuildTracks = (dg, job) => Catalog.BuildTracks(dg, job).Item1;
        Environment.SetEnvironmentVariable("SOULGET_HOME", null);
        Directory.Delete(tmp, recursive: true);
    }

    static Track T(string a, string t, string r, int y, string catno = "") => Tracks.MakeTrack(a, t, r, y, catno: catno)!;

    static Config Cfg(string root, string extra = "") => Config.Parse($"""
        [soulseek]
        username = "x"
        password = "y"
        download_folder = "{root}"
        [download]
        search_wait_seconds = 0
        start_timeout_seconds = 1
        [retry]
        {extra}
        """);

    static string Output(Action run)
    {
        var old = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { run(); }
        finally { Console.SetOut(old); }
        return sw.ToString();
    }

    [Fact]
    public void Misses_and_giving_up()
    {
        var m = Misses.Load(tmp);
        m.Record("a", found: false, note: "no results", at: T0);
        m.Record("a", found: false, note: "no results", at: T0.AddHours(7));
        m.Record("b", found: false, at: T0);
        m.Record("b", found: true, at: T0.AddHours(7));   // found on the 2nd check
        m.Save();
        m = Misses.Load(tmp);
        Assert.Equal(2, J.Int(m.Data["a"], "checks"));
        Assert.Equal("2026-10-15T09:00:00", J.Str(m.Data["a"], "first_missed"));   // same format as Python
        Assert.Null(m.Data["b"]);
        Assert.False(m.GaveUp("a", 14, T0.AddDays(13)));
        Assert.True(m.GaveUp("a", 14, T0.AddDays(15)));
    }

    [Fact]
    public void Check_log()
    {
        Retry.LogCheck(tmp, "Primate Recordings (label)", 402, 12, 390, 0, "scheduled", T0);
        Retry.LogCheck(tmp, "Primate Recordings (label)", 390, 3, 387, 0, "scheduled", T0.AddHours(7));
        Assert.Equal("when,weekday,hour,folder,searched,found,still_missing,given_up,trigger\r\n" +
                     "2026-10-15 09:00,Thu,9,Primate Recordings (label),402,12,390,0,scheduled\r\n" +
                     "2026-10-15 16:00,Thu,16,Primate Recordings (label),390,3,387,0,scheduled\r\n",
                     File.ReadAllText(Path.Combine(tmp, Retry.RetryLog)));
    }

    [Fact]
    public void Timer_file_is_valid_and_readable()
    {
        var p = Path.Combine(tmp, "t.plist");
        File.WriteAllText(p, Schedule.PlistXml("/Users/x/soulget/soulget", 7, "/Users/x/soulget/retry-output.log", "/Users/x/soulget"));
        using var lint = Process.Start(new ProcessStartInfo("plutil", $"-lint \"{p}\"") { RedirectStandardOutput = true })!;
        lint.WaitForExit();
        Assert.Equal(0, lint.ExitCode);
        var old = Schedule.PlistPath;
        Schedule.PlistPath = p;
        try
        {
            var st = Schedule.Status()!;
            Assert.Equal((7 * 3600.0, "/Users/x/soulget/retry-output.log", "/Users/x/soulget/soulget"),
                         (st.IntervalSeconds, st.LogPath, st.Program));
        }
        finally { Schedule.PlistPath = old; }
    }

    [Fact]
    public void Timer_file_written_by_the_python_version_is_understood()
    {
        // What Python's plistlib wrote for the timer that is installed today.
        var p = Path.Combine(tmp, "py.plist");
        File.WriteAllText(p, """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
            	<key>Label</key>
            	<string>com.soulget.retry</string>
            	<key>LowPriorityIO</key>
            	<true/>
            	<key>ProcessType</key>
            	<string>Background</string>
            	<key>ProgramArguments</key>
            	<array>
            		<string>/Users/x/soulget/soulget</string>
            		<string>--retry-missing</string>
            		<string>--scheduled</string>
            	</array>
            	<key>RunAtLoad</key>
            	<false/>
            	<key>StandardErrorPath</key>
            	<string>/Users/x/soulget/retry-output.log</string>
            	<key>StandardOutPath</key>
            	<string>/Users/x/soulget/retry-output.log</string>
            	<key>StartInterval</key>
            	<integer>25200</integer>
            </dict>
            </plist>
            """);
        var old = Schedule.PlistPath;
        Schedule.PlistPath = p;
        try { Assert.Equal((25200.0, "/Users/x/soulget/soulget"), (Schedule.Status()!.IntervalSeconds, Schedule.Status()!.Program)); }
        finally { Schedule.PlistPath = old; }
    }

    [Fact]
    public void Seven_hours_sweeps_the_clock()
    {
        var times = Enumerable.Range(0, 24).Select(i => T0.AddHours(7 * i)).ToList();   // one week of checks
        Assert.Equal(24, times.Select(t => t.Hour).Distinct().Count());
        Assert.Equal(7, times.Select(t => t.DayOfWeek).Distinct().Count());
    }

    static string Folder(string root, string name, List<Track> downloaded, JsonObject? misses = null, string kind = "label")
    {
        var d = Path.Combine(root, name);
        Directory.CreateDirectory(d);
        new Job { Kind = kind, Id = 1, Name = name, Url = "u", MinBitrate = 320, Formats = new() { "flac", "mp3" } }.Save(d);
        var manifest = new JsonObject();
        foreach (var t in downloaded)
        {
            File.WriteAllText(Path.Combine(d, $"{t.Title}.flac"), "x");
            manifest[t.Key] = new JsonObject { ["file"] = $"{t.Title}.flac", ["from"] = "u" };
        }
        Manifest.Save(d, manifest);
        if (misses is not null) File.WriteAllText(Path.Combine(d, Misses.FileName), misses.ToJsonString());
        return d;
    }

    [Fact]
    public void Retry_round()
    {
        var root = Path.Combine(tmp, "music");
        var (a, b, c, gone) = (T("Mould Impression", "Broken Monitor", "6 Hours", 1997), T("Mould Impression", "Old Mould", "6 Hours", 1997),
                               T("Mould Impression", "Lenk's 808", "6 Hours", 1997), T("Mould Impression", "Untraceable", "6 Hours", 1997));
        string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss");
        // A folder from before retries existed: everything not downloaded counts as missing.
        var legacy = Folder(root, "Legacy (label)", new() { a });
        // A newer folder: only recorded misses are retried; one has been missing 20 days. b was never tried.
        var newer = Folder(root, "Newer (label)", new() { a }, new JsonObject
        {
            [c.Key] = new JsonObject { ["first_missed"] = Iso(DateTime.Now.AddDays(-1)), ["checks"] = 3 },
            [gone.Key] = new JsonObject { ["first_missed"] = Iso(DateTime.Now.AddDays(-20)), ["checks"] = 60 },
        });
        var fake = new FakeEngine(new()
        {
            ["mould impression old"] = new() { FlowTests.R("u1", @"m\Mould Impression\Old Mould.flac", bitrate: null) },
            ["mould impression lenk 808"] = new(),
        });
        Retry.MakeEngine = (_, _, _, _, _) => fake;
        var lists = new Dictionary<string, List<Track>> { ["Legacy (label)"] = new() { a, b }, ["Newer (label)"] = new() { a, b, c, gone } };
        Retry.BuildTracks = (_, job) => lists[job.Name];

        var output = Output(() => Retry.Check(Cfg(root), scheduled: false, verbose: false));

        Assert.Contains("Legacy (label): 1 still missing", output);
        Assert.Contains("Newer (label): 1 still missing, 1 given up on", output);
        Assert.True(File.Exists(Path.Combine(legacy, "1997 Mould Impression - Old Mould (6 Hours).flac")), output);
        // main searches, plus the record search (retry rounds skip the title-only one)
        Assert.Equal(new[] { "mould impression old", "mould impression lenk 808", "mould impression 6 hours" }, fake.Queries);
        Assert.Empty(Misses.Load(legacy).Data);
        var m = Misses.Load(newer).Data;
        Assert.Equal(4, J.Int(m[c.Key], "checks"));
        Assert.NotNull(m[gone.Key]);
        var report = File.ReadAllLines(Path.Combine(newer, "report.csv")).Skip(1).Select(l => l.Split(','))
                         .ToDictionary(x => x[2], x => x[0]);
        Assert.Equal(new Dictionary<string, string> { ["Broken Monitor"] = "already had", ["Lenk's 808"] = "not found", ["Untraceable"] = "gave up" }, report);
        var log = File.ReadAllLines(Path.Combine(root, Retry.RetryLog)).Skip(1).Select(l => l.Split(',')).Select(x => (x[3], x[4], x[5], x[7]));
        Assert.Equal(new[] { ("Legacy (label)", "1", "1", "0"), ("Newer (label)", "1", "0", "1") }, log);
        Assert.False(File.Exists(Path.Combine(root, RunLock.FileName)));
    }

    [Fact]
    public void Scheduled_check_steps_aside_for_soulseekqt()
    {
        Retry.QtRunning = () => true;
        var output = Output(() => Retry.Check(Cfg(tmp), scheduled: true, verbose: false));
        Assert.Contains("Skipped: SoulseekQt is open", output);
    }

    [Fact]
    public void Waiting_line_is_first_come_first_served()
    {
        var q = new WaitQueue(tmp);
        var mine = q.Join();
        var dir = Path.GetDirectoryName(mine)!;
        var other = Path.Combine(dir, $"{long.Parse(Path.GetFileName(mine).Split('-')[0]) + 1}-1");   // later; pid 1 is alive
        File.WriteAllText(other, "");
        Assert.True(q.First(mine));
        Assert.False(q.First(other));
        using var dead = Process.Start("true")!;
        dead.WaitForExit();
        var early = Path.Combine(dir, $"1-{dead.Id}");   // joined first, but its run has ended
        File.WriteAllText(early, "");
        Assert.True(q.First(mine));
        Assert.False(File.Exists(early));                  // gone runs drop out of the line
        q.Leave(mine);
        Assert.True(q.First(other));
    }

    [Fact]
    public void Timed_check_skips_when_others_are_waiting()
    {
        var root = Path.Combine(tmp, "music");
        var t = T("A", "B", "C", 2000);
        Folder(root, "X (label)", new());
        Directory.CreateDirectory(Path.Combine(root, WaitQueue.DirName));
        File.WriteAllText(Path.Combine(root, WaitQueue.DirName, "1-1"), "");   // someone is waiting (pid 1: alive)
        Retry.BuildTracks = (_, _) => new() { t };
        var output = Output(() => Retry.Check(Cfg(root), scheduled: true, verbose: false));
        Assert.Contains("Skipped: other runs are waiting their turn", output);
    }

    [Fact]
    public void Unplugged_drive_stops_everything_safely()
    {
        Assert.Throws<DriveMissing>(() => Cfg("/Volumes/NotPluggedIn/soulget").DownloadRoot());
        Environment.SetEnvironmentVariable("SOULGET_HOME", tmp);
        File.WriteAllText(Path.Combine(tmp, "config.toml"), "[soulseek]\ndownload_folder = \"/Volumes/NotPluggedIn/soulget\"\n");
        var output = Output(() => Assert.Equal(0, Program.Main(new[] { "--retry-missing", "--scheduled" })));
        Assert.Contains("Skipped: the drive 'NotPluggedIn' isn't connected", output);   // a timed check just notes it and stops
        var err = new StringWriter();
        var oldErr = Console.Error;
        Console.SetError(err);
        try { Assert.Equal(1, Program.Main(new[] { "--queue" })); }
        finally { Console.SetError(oldErr); }
        Assert.Contains("Connect it and try again", err.ToString());
        Assert.Equal(tmp, Cfg(tmp).DownloadRoot());   // Mac folders are fine
    }

    [Fact]
    public void Queue_later_then_timer_finds_it_and_skips_what_other_folders_have()
    {
        var root = Path.Combine(tmp, "music");
        var math = T("Boards Of Canada", "Music Is Math", "Geogaddi", 2002);
        var gyro = T("Boards Of Canada", "Gyroscope", "Geogaddi", 2002);
        Folder(root, "Boards Of Canada", new() { math }, kind: "artist");   // the artist folder already has "Music Is Math"
        var album = Folder(root, "Boards Of Canada - Geogaddi", new(), kind: "master");
        Assert.Equal((1, 1), Retry.QueueForLater(new() { math, gyro }, album));
        var entry = Misses.Load(album).Data[gyro.Key]!;
        Assert.Equal((0, "Boards Of Canada - Gyroscope"), (J.Int(entry, "checks"), J.Str(entry, "name")));

        var fake = new FakeEngine(new() { ["boards of canada gyroscope"] = new() { FlowTests.R("u1", @"m\Boards of Canada\Geogaddi\Gyroscope.mp3") } });
        Retry.MakeEngine = (_, _, _, _, _) => fake;
        var notes = new List<string>();
        Retry.Notify = (_, text) => notes.Add(text);
        var lists = new Dictionary<string, List<Track>> { ["Boards Of Canada"] = new() { math }, ["Boards Of Canada - Geogaddi"] = new() { math, gyro } };
        Retry.BuildTracks = (_, job) => lists[job.Name];
        Output(() => Retry.Check(Cfg(root), scheduled: true, verbose: false));

        Assert.Equal(new[] { "boards of canada gyroscope" }, fake.Queries);    // "Music Is Math" never searched
        Assert.True(File.Exists(Path.Combine(album, "2002 Boards Of Canada - Gyroscope (Geogaddi).mp3")));
        var rows = File.ReadAllLines(Path.Combine(album, "report.csv")).Skip(1).Select(l => l.Split(','))
                       .ToDictionary(x => x[2], x => (x[0], x[^1]));
        Assert.Equal(("downloaded", ""), rows["Gyroscope"]);
        Assert.Equal(("already had", "already in Boards Of Canada"), rows["Music Is Math"]);
        Assert.Empty(Misses.Load(album).Data);
        Assert.Equal(new[] { "Found 1 more track: Boards Of Canada - Geogaddi" }, notes);

        var queue = Output(() => Retry.Queue(Cfg(root)));
        Assert.Contains("Boards Of Canada - Geogaddi", queue);
        Assert.Contains("Timer:", queue);
    }

    [Fact]
    public void Wanted_tracks_list()
    {
        var job = Catalog.WantedTracksJob(tmp, new() { "Mould Impression - Lenk's 808", "Ben Sims - Manipulated" }, 320, new() { "flac" });
        Directory.CreateDirectory(Path.Combine(tmp, Program.Wanted));
        job.Save(Path.Combine(tmp, Program.Wanted));
        job = Catalog.WantedTracksJob(tmp, new() { "mould impression - LENK'S 808", "Plastikman - Smak" }, 320, new() { "flac" });
        Assert.Equal(new[] { "Mould Impression|Lenk's 808", "Ben Sims|Manipulated", "Plastikman|Smak" },
                     job.TrackPairs!.Select(p => string.Join("|", p)));
        Assert.Throws<Exit>(() => Catalog.WantedTracksJob(tmp, new() { "just a title" }, 320, new() { "flac" }));
    }

    [Fact]
    public void Rename_plan_follows_pattern_and_never_overwrites()
    {
        var a = T("Mould Impression", "Broken Monitor", "6 Hours", 1996, catno: "PRMT 010");
        var b = T("Mould Impression", "6 Hours", "6 Hours", 1996, catno: "PRMT 010");
        var d = Folder(tmp, "Primate Recordings (label)", new());
        var manifest = new JsonObject();
        foreach (var t in new[] { a, b })
        {
            var name = $"{t.Artist} - {t.Title}.flac";
            File.WriteAllText(Path.Combine(d, name), "x");
            manifest[t.Key] = new JsonObject { ["file"] = name, ["from"] = "u" };
        }
        File.WriteAllText(Path.Combine(d, "Mould Impression - Broken Monitor (6 Hours) [PRMT 010].flac"), "other");   // in the way
        Manifest.Save(d, manifest);
        var plan = Retry.PlanRenames(d, new() { a, b }, "{artist} - {title} ({release}) [{catno}]").ToDictionary(x => x.Old, x => x.New);
        Assert.Equal(new Dictionary<string, string>
        {
            ["Mould Impression - Broken Monitor.flac"] = "Mould Impression - Broken Monitor (6 Hours) [PRMT 010] (2).flac",
            ["Mould Impression - 6 Hours.flac"] = "Mould Impression - 6 Hours [PRMT 010].flac",   // release = title: left out
        }, plan);
    }

    [Fact]
    public void Rename_applies_and_updates_the_record()
    {
        var root = Path.Combine(tmp, "music");
        var a = T("Mould Impression", "Broken Monitor", "6 Hours", 1996, catno: "PRMT 010");
        var d = Folder(root, "Primate Recordings (label)", new() { a });
        Retry.BuildTracks = (_, _) => new() { a };
        Output(() => Retry.Rename(Cfg(root), apply: false));
        Assert.True(File.Exists(Path.Combine(d, "Broken Monitor.flac")));                 // preview changes nothing
        var output = Output(() => Retry.Rename(Cfg(root), apply: true));
        const string nw = "1996 [PRMT 010] Mould Impression - Broken Monitor (6 Hours).flac";
        Assert.True(File.Exists(Path.Combine(d, nw)));
        Assert.False(File.Exists(Path.Combine(d, "Broken Monitor.flac")));
        Assert.Equal(nw, J.Str(Manifest.Load(d)[a.Key], "file"));
        Assert.Contains("Renamed 1 files.", output);
        Assert.Equal("already had", Runner.SplitDone(new() { a }, d).Had.Single().Status);   // still recognised after renaming
    }
}

public class JsonReadingTests
{
    [Fact]
    public void Numbers_read_the_same_from_a_file_or_from_memory()
    {
        var fromFile = (JsonObject)JsonNode.Parse("""{"checks": 3, "year": 0, "name": "x", "flag": false}""")!;
        var inMemory = new JsonObject { ["checks"] = 3, ["year"] = 0, ["name"] = "x", ["flag"] = false };
        foreach (var o in new[] { fromFile, inMemory })
        {
            Assert.Equal(3, J.Int(o, "checks"));
            Assert.False(J.Truthy(o["year"]));   // 0 counts as "nothing", like in Python
            Assert.False(J.Truthy(o["flag"]));
            Assert.Equal("x", J.Str(o, "name"));
        }
    }
}

public class OtherSystemsTimerTests
{
    [Fact]
    public void Windows_task_is_valid_xml_every_7_hours_without_a_window()
    {
        var xml = Schedule.WindowsTaskXml(@"C:\Users\x\soulget\soulget.exe", 7, @"C:\Users\x\soulget", new DateTime(2026, 10, 20, 23, 0, 0));
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("PT7H", doc.Descendants(ns + "Interval").Single().Value);
        Assert.Equal("conhost.exe", doc.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--headless \"C:\\Users\\x\\soulget\\soulget.exe\" --retry-missing --scheduled --home \"C:\\Users\\x\\soulget\"",
                     doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal("2026-10-20T23:00:00", doc.Descendants(ns + "StartBoundary").Single().Value);
        Assert.Equal("PT6H", doc.Descendants(ns + "ExecutionTimeLimit").Single().Value);   // a stuck check never blocks the next
        Assert.Equal("PT90M", System.Xml.Linq.XDocument.Parse(Schedule.WindowsTaskXml("p", 1.5, "h", DateTime.Now))
                                  .Descendants(ns + "Interval").Single().Value);
    }

    [Fact]
    public void Linux_timer_runs_every_7_hours()
    {
        var (service, timer) = Schedule.SystemdUnits("/home/x/soulget/soulget", 7, "/home/x/soulget");
        Assert.Contains("ExecStart=\"/home/x/soulget/soulget\" --retry-missing --scheduled --home \"/home/x/soulget\"", service);
        Assert.Contains("Type=oneshot", service);
        Assert.Contains("OnUnitActiveSec=420min", timer);
        Assert.Contains("Unit=soulget-retry.service", timer);
        Assert.Contains("WantedBy=timers.target", timer);
    }

    [Fact]
    public void Home_option_points_soulget_at_another_settings_folder()
    {
        var dir = Directory.CreateTempSubdirectory("soulget-home-").FullName;
        try
        {
            var old = Console.Out;
            Console.SetOut(new StringWriter());
            try { Assert.Equal(0, Program.Main(new[] { "--home", dir, "--version" })); }
            finally { Console.SetOut(old); Config.HomeOverride = null; }
            Assert.True(File.Exists(Path.Combine(dir, "config.toml")));   // a first run creates it there
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
