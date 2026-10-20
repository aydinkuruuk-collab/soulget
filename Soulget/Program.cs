using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Soulget;

/// <summary>Command-line options, parsed like the Python version's argparse setup.</summary>
public class Args
{
    public string? Name;
    public int? MinBitrate;
    public bool Label, DryRun, NoRemixes, NoCompilations, TheirRemixes, Later, Queue, Rename, Yes,
                RetryMissing, Scheduled, Verbose, Help, Version, License;
    public string? Years, Formats, Album, Schedule, DumpTracks;
    public int? Limit;
    public List<string> Track = new();

    public static Args Parse(string[] argv)
    {
        var a = new Args();
        var positional = new List<string>();
        for (int i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            string? inline = null;
            if (arg.StartsWith("--") && arg.Contains('='))
            {
                inline = arg[(arg.IndexOf('=') + 1)..];
                arg = arg[..arg.IndexOf('=')];
            }
            string Value()
            {
                if (inline is not null) return inline;
                if (i + 1 >= argv.Length) throw new UsageError($"argument {arg}: expected one argument");
                return argv[++i];
            }
            switch (arg)
            {
                case "-h": case "--help": a.Help = true; break;
                case "--version": a.Version = true; break;
                case "--license": a.License = true; break;
                case "--label": a.Label = true; break;
                case "--dry-run": a.DryRun = true; break;
                case "--no-remixes": a.NoRemixes = true; break;
                case "--no-compilations": a.NoCompilations = true; break;
                case "--their-remixes": a.TheirRemixes = true; break;
                case "--later": a.Later = true; break;
                case "--queue": a.Queue = true; break;
                case "--rename": a.Rename = true; break;
                case "--yes": a.Yes = true; break;
                case "--retry-missing": a.RetryMissing = true; break;
                case "--scheduled": a.Scheduled = true; break;
                case "--verbose": a.Verbose = true; break;
                case "--years": a.Years = Value(); break;
                case "--formats": a.Formats = Value(); break;
                case "--album": a.Album = Value(); break;
                case "--track": a.Track.Add(Value()); break;
                case "--dump-tracks": a.DumpTracks = Value(); break;
                case "--home": Value(); break;   // read before everything else, see Main
                case "--limit":
                    var l = Value();
                    a.Limit = int.TryParse(l, out var n) ? n : throw new UsageError($"argument --limit: invalid int value: '{l}'");
                    break;
                case "--schedule":
                    var s = Value();
                    a.Schedule = s is "on" or "off" or "status" ? s
                        : throw new UsageError($"argument --schedule: invalid choice: '{s}' (choose from 'on', 'off', 'status')");
                    break;
                default:
                    if (arg.StartsWith("--")) throw new UsageError($"unrecognized arguments: {arg}");
                    positional.Add(arg);
                    break;
            }
        }
        if (positional.Count > 2) throw new UsageError($"unrecognized arguments: {string.Join(" ", positional.Skip(2))}");
        if (positional.Count >= 1) a.Name = positional[0];
        if (positional.Count == 2)
            a.MinBitrate = int.TryParse(positional[1], out var br) ? br
                : throw new UsageError($"argument min_bitrate: invalid int value: '{positional[1]}'");
        return a;
    }
}

public class UsageError(string message) : Exception(message);

public static class Program
{
    public const string AppVersion = "2.0.0";
    public const string Wanted = "Wanted tracks";  // folder for single tracks added with --track

    public const string Usage = """
        usage: soulget [-h] [--label] [--dry-run] [--no-remixes] [--no-compilations]
                       [--their-remixes] [--years YEARS] [--formats FORMATS]
                       [--limit LIMIT] [--album "ARTIST - TITLE"]
                       [--track "ARTIST - TITLE"] [--later] [--queue] [--rename]
                       [--retry-missing] [--schedule {on,off,status}] [--verbose]
                       [name] [min_bitrate]
        """;

    public const string HelpText = Usage + """


        Download one copy of every track by an artist (or on a label) from Soulseek,
        using Discogs for the track list.

        positional arguments:
          name                  artist name, label name (with --label), or a Discogs
                                artist/label URL
          min_bitrate           lowest acceptable bitrate in kbps, e.g. 320 (lossless
                                files always pass)

        options:
          -h, --help            show this help message and exit
          --label               NAME is a record label, not an artist
          --dry-run             only build and save the track list; don't log in or
                                download
          --no-remixes          skip remixes, live and alternate versions; keep only
                                the original of each song
          --no-compilations     skip compilations and DJ-mix CDs (artist mode: their
                                tracks that only appear on other people's
                                compilations)
          --their-remixes       artist mode: also get remixes this artist made for
                                other artists
          --years YEARS         only releases from these years, e.g. 1995-2005
          --formats FORMATS     allowed file types, best first, e.g. mp3,flac
                                (default from config)
          --limit LIMIT         only the first N tracks (handy for a test run)
          --album "ARTIST - TITLE"
                                one album, EP or single instead of a whole
                                discography (or paste its Discogs master/release
                                link)
          --track "ARTIST - TITLE"
                                a single track; collected in the 'Wanted tracks'
                                folder (repeat for several)
          --later               don't search now: put the tracks on the list for the
                                timer's next check
          --queue               show what the timer is still looking for, per folder
          --rename              rename files from earlier runs to the file_name
                                pattern in config.toml (shows a preview; add --yes to
                                do it)
          --retry-missing       search again for tracks earlier runs couldn't get
                                (all folders, same settings)
          --schedule {on,off,status}
                                turn the automatic retry timer on or off, or show it
                                and the last checks
          --verbose             show the Soulseek library's own log
          --version             show the program's version and license notice
          --home DIR            where config.toml lives (default: next to soulget)
          --license             show the full license (GPLv3 and Soulseek.NET's
                                Additional Terms)
        """;

    /// <summary>The license notice GPLv3 asks interactive programs to show.</summary>
    public const string LegalNotice =
        "soulget " + AppVersion + " - Copyright (C) 2026 the soulget contributors. Free software under the\n" +
        "GNU GPL v3 only, with ABSOLUTELY NO WARRANTY; run `soulget --license` for the terms.\n" +
        "Built on Soulseek.NET (unmodified); not maintained by, endorsed by, or affiliated with\n" +
        "the Soulseek.NET project or its author(s).";

    public static int Main(string[] argv)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Native.RaiseOpenFileLimit();
        // --home must be known before config.toml is read.
        for (int i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--home" && i + 1 < argv.Length) Config.HomeOverride = argv[i + 1];
            else if (argv[i].StartsWith("--home=")) Config.HomeOverride = argv[i]["--home=".Length..];
        }
        // On a Mac the timer itself keeps the log; on Windows and Linux a timed check writes its own.
        if (argv.Contains("--scheduled") && !OperatingSystem.IsMacOS())
        {
            try
            {
                var log = new StreamWriter(Schedule.LogPath, append: true, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(log);
                Console.SetError(log);
            }
            catch (IOException) { }
        }
        try
        {
            var cfg = Config.Load(); // before parsing, so even --help creates config.toml on a first run
            var args = Args.Parse(argv);
            if (args.Help) { Console.WriteLine(HelpText); return 0; }
            if (args.Version) { Console.WriteLine(LegalNotice); return 0; }
            if (args.License) { Console.WriteLine(EmbeddedText("Soulget.LICENSE")); return 0; }
            if (args.DumpTracks is not null) return DumpTracks(cfg, args.DumpTracks);
            return Run(cfg, args);
        }
        catch (UsageError e)
        {
            Console.Error.WriteLine(Usage);
            Console.Error.WriteLine($"soulget: error: {e.Message}");
            return 2;
        }
        catch (Exit e)
        {
            if (e.Message.Length > 0) Console.Error.WriteLine(e.Message);
            return e.Code;
        }
    }

    static int Run(Config cfg, Args args)
    {
        if (args.Schedule is "off" or "status") return Schedule.Command(args.Schedule, cfg);   // works without the drive
        try { cfg.DownloadRoot(); }
        catch (DriveMissing e)
        {
            if (args.Scheduled)
            {
                Console.WriteLine($"\n=== soulget retry check, {Retry.En(Misses.Now(), "ddd dd MMM yyyy HH:mm")} ===\nSkipped: {e.Message}.");
                return 0;
            }
            throw new Exit($"Not now: {e.Message}. Connect it and try again.", 1);
        }
        if (args.Schedule is not null) return Schedule.Command(args.Schedule, cfg);
        if (args.RetryMissing) return Retry.Check(cfg, args.Scheduled, args.Verbose);
        if (args.Queue) return Retry.Queue(cfg);
        if (args.Rename) return Retry.Rename(cfg, args.Yes);
        if (args.Name is null && args.Album is null && args.Track.Count == 0)
            throw new UsageError("give an artist or label name, --album or --track (or use --retry-missing, --queue or --schedule)");

        var minBr = args.MinBitrate is > 0 ? args.MinBitrate.Value : cfg.Int("download", "default_min_bitrate", 320);
        var formats = (args.Formats is not null ? args.Formats.Split(',').ToList() : cfg.List("download", "formats", new() { "mp3", "flac" }))
            .Select(f => Text.Lower(f.Trim()).TrimStart('.')).Where(f => f.Length > 0).ToList();

        string root;
        try { root = cfg.DownloadRoot(); }
        catch (DriveMissing e) { throw new Exit($"Not now: {e.Message}. Connect it and try again.", 1); }

        var dg = new Discogs(cfg.DiscogsToken, Config.CacheDir);
        var years = Catalog.ParseYears(args.Years);
        Job job;
        List<Track> tracks;
        TrackList tl;
        int releasesRead;
        try
        {
            if (args.Track.Count > 0)
                job = Catalog.WantedTracksJob(root, args.Track, minBr, formats);
            else
            {
                var found = args.Album is not null ? dg.FindRelease(args.Album)
                    : dg.Find(args.Label ? "label" : "artist", args.Name!);
                var kind = found.Kind is "master" or "release" ? "album" : found.Kind;
                Console.WriteLine($"Discogs {kind}: {found.Name}   {found.Url}");
                if (found.Others.Count > 0)
                {
                    Console.WriteLine("  Not the right one? Run again with one of these links instead:");
                    foreach (var o in found.Others) Console.WriteLine($"    {o}");
                }
                job = new Job
                {
                    Kind = found.Kind, Id = found.Id, Name = found.Name, Url = found.Url, MinBitrate = minBr,
                    Formats = formats, NoRemixes = args.NoRemixes, NoCompilations = args.NoCompilations,
                    TheirRemixes = args.TheirRemixes, Years = years is null ? null : new() { years.Value.Item1, years.Value.Item2 },
                };
            }
            (tracks, tl, releasesRead) = Catalog.BuildTracks(dg, job);
        }
        catch (DiscogsError e) { throw new Exit($"Discogs problem: {e.Message}", 1); }

        var remixes = tl.All().Count(t => t.IsRemix);
        var outDir = Path.Combine(root, Catalog.FolderName(job));
        Directory.CreateDirectory(outDir);
        Catalog.WriteTracklist(tracks, Path.Combine(outDir, "tracklist.csv"));

        if (job.Kind == "tracks")
            Console.WriteLine($"'{Wanted}' list: {tracks.Count} tracks.");
        else if (job.Kind is "master" or "release")
            Console.WriteLine($"\nTrack list: {tracks.Count} tracks on {job.Name}.");
        else
            Console.WriteLine($"\nTrack list: {tracks.Count} unique tracks from {releasesRead} releases " +
                              $"({tl.Duplicates} duplicate copies removed" +
                              (args.NoRemixes && remixes > 0 ? $", {remixes} remixes/alt versions left out"
                               : remixes > 0 ? $", including {remixes} remixes/alt versions" : "") + ").");
        Console.WriteLine($"Saved to {Path.Combine(outDir, "tracklist.csv")}");

        if (args.Limit is > 0) tracks = tracks.Take(args.Limit.Value).ToList();
        if (args.DryRun)
        {
            Console.WriteLine("\nFirst tracks on the list:");
            foreach (var t in tracks.Take(25))
                Console.WriteLine($"  {(t.Year != 0 ? t.Year.ToString() : "----")}  {t.Display()}   [{t.Release}]");
            if (tracks.Count > 25) Console.WriteLine($"  ... and {tracks.Count - 25} more (see tracklist.csv)");
            Console.WriteLine("\nDry run: nothing downloaded. Remove --dry-run to start downloading.");
            return 0;
        }
        if (args.Later)
        {
            // A running download or check keeps this folder's list in memory and would overwrite
            // our additions, so wait for it first.
            int queued, alreadyHad;
            using (WaitQueue.WaitForLock(root, "Queueing"))
            {
                job.Save(outDir);
                (queued, alreadyHad) = Retry.QueueForLater(tracks, outDir);
            }
            Console.WriteLine($"\nQueued {queued} tracks for the timer" + (alreadyHad > 0 ? $" ({alreadyHad} you already have)" : "") + ".");
            var st = Schedule.Status();
            if (st is { Loaded: true })
                Console.WriteLine($"The next check (every {Retry.G(st.IntervalSeconds / 3600)} hours) will look for them. " +
                                  "See the list any time with: soulget --queue");
            else
                Console.WriteLine("The timer is off. Search now with soulget --retry-missing, " +
                                  "or make it automatic with soulget --schedule on");
            return 0;
        }

        var user = cfg.Str("soulseek", "username");
        var password = cfg.Str("soulseek", "password");
        if (user.Length == 0 || password.Length == 0)
            throw new Exit($"\nTo download, put your Soulseek username and password in {Config.ConfigPath}", 1);

        // Saved so a retry can later search again the same way.
        job.Save(outDir);
        var misses = Misses.Load(outDir);
        misses.Save();
        var (todo, outcomes) = Runner.SplitDone(tracks, outDir);
        if (todo.Count < tracks.Count)
            Console.WriteLine($"{tracks.Count - todo.Count} tracks were downloaded in an earlier run; skipping them.");
        Console.WriteLine($"\nSearching Soulseek: {string.Join(", ", formats)}; mp3 and other lossy files must be at least {minBr} kbps.");
        var work = new Work(outDir, todo, Opts.From(cfg, minBr, formats), outcomes, misses);
        if (todo.Count > 0)
        {
            Console.WriteLine("\n" + LegalNotice + "\n");
            using var stop = new CancelOnCtrlC();
            var engine = new SoulseekEngine(user, password, Path.Combine(root, ".incomplete"),
                                            cfg.Int("soulseek", "listen_port", 60000), args.Verbose);
            using (WaitQueue.WaitForLock(root, "This download"))
            {
                if (!Runner.RunWorkAsync(new List<Work> { work }, engine, user).GetAwaiter().GetResult()) return 1;
            }
            if (stop.Pressed)
            {
                Console.WriteLine("\nStopped. Finished files are kept; run the same command again to continue where it left off.");
                return 130;
            }
        }
        else Runner.WriteReport(work.Outcomes, Path.Combine(outDir, "report.csv"));

        Runner.PrintSummary(work.Outcomes, outDir);
        if (work.Outcomes.Any(o => o.Status is "not found" or "failed"))
            Console.WriteLine("Missing tracks can be searched for again later with: soulget --retry-missing\n" +
                              "(or automatically every few hours: soulget --schedule on)");
        return 0;
    }

    static string EmbeddedText(string name)
    {
        using var s = typeof(Program).Assembly.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Developer check: every track the saved job of a folder produces, as JSON lines,
    /// for comparing with the Python version.</summary>
    static int DumpTracks(Config cfg, string folder)
    {
        var job = Job.Load(folder) ?? throw new Exit($"no {Job.FileName} in {folder}", 1);
        var dg = new Discogs(cfg.DiscogsToken, Config.CacheDir, say: _ => { });
        var (tracks, _, _) = Catalog.BuildTracks(dg, job, quiet: true);
        var template = Catalog.FileNameTemplate(cfg);
        foreach (var t in tracks)
        {
            var o = new JsonObject
            {
                ["key"] = t.Key, ["artist"] = t.Artist, ["title"] = t.Title, ["base"] = t.Base, ["variant"] = t.Variant,
                ["is_remix"] = t.IsRemix, ["release"] = t.Release, ["year"] = t.Year, ["duration"] = t.Duration,
                ["generic"] = t.Generic, ["catno"] = t.Catno, ["position"] = t.Position, ["index"] = t.Index,
                ["code"] = t.Code, ["also_on"] = new JsonArray(t.AlsoOn.Select(x => (JsonNode)x).ToArray()),
                ["file_stem"] = Tracks.FileStem(t, template),
                ["queries"] = new JsonArray(Tracks.SearchQueries(t).Select(x => (JsonNode)x).ToArray()),
                ["retry_queries"] = new JsonArray(Tracks.SearchQueries(t, retry: true).Select(x => (JsonNode)x).ToArray()),
            };
            Console.WriteLine(o.ToJsonString());
        }
        return 0;
    }
}

/// <summary>Ctrl+C: finish cleanly (report, records and lock written) instead of dying mid-file.
/// A second Ctrl+C stops at once.</summary>
public sealed class CancelOnCtrlC : IDisposable
{
    public bool Pressed { get; private set; }

    public CancelOnCtrlC() => Console.CancelKeyPress += Handler;

    void Handler(object? sender, ConsoleCancelEventArgs e)
    {
        if (Pressed) return;   // second Ctrl+C: let it end the program
        Pressed = true;
        e.Cancel = true;
        Console.WriteLine("\nStopping after the downloads in progress... (Ctrl+C again to stop at once)");
        Runner.StopRequested = true;
    }

    public void Dispose() => Console.CancelKeyPress -= Handler;
}

/// <summary>Stop with a message and an exit code (Python's sys.exit).</summary>
public class Exit(string message, int code = 1) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Building track lists from saved jobs, and the files that describe them.</summary>
public static class Catalog
{
    public static (List<Track>, TrackList, int releasesRead) BuildTracks(Discogs dg, Job job, bool quiet = false)
    {
        var found = new Found(job.Kind, job.Id, job.Name, job.Url, new());
        (int, int)? years = job.Years is { Count: >= 2 } y ? (y[0], y[1]) : null;
        if (job.Kind == "tracks")
        {
            var tl0 = new TrackList();
            foreach (var pair in job.TrackPairs ?? new())
                tl0.Add(Tracks.MakeTrack(pair[0], pair[1], "", 0));
            return (tl0.All(), tl0, 0);
        }
        TrackList tl;
        int read;
        if (job.Kind is "master" or "release") (tl, _, read) = dg.ReleaseTracks(found);
        else if (job.Kind == "label") (tl, _, read) = dg.LabelTracks(found, years, compilations: !job.NoCompilations);
        else (tl, _, read) = dg.ArtistTracks(found, years, compilations: !job.NoCompilations, theirRemixes: job.TheirRemixes);
        var tracks = tl.All();
        if (job.NoRemixes) tracks = tracks.Where(t => !t.IsRemix).ToList();
        return (tracks, tl, read);
    }

    static readonly Regex Bad = Text.Re(@"[\\/:*?""<>|\x00-\x1f]");

    public static string SafeName(string name)
    {
        name = Bad.Replace(name, "_").Trim().TrimStart('.');
        var cut = Text.Take(name, 180);
        return cut.Length > 0 ? cut : "untitled";
    }

    public static string FolderName(Job job) =>
        job.Kind == "tracks" ? Program.Wanted : SafeName(job.Name + (job.Kind == "label" ? " (label)" : ""));

    public static void WriteTracklist(List<Track> tracks, string path) =>
        Files.WriteCsv(path, new[] { new object?[] { "artist", "title", "first_release", "catno", "position", "year", "length", "also_on" } }
            .Concat(tracks.Select(t => new object?[]
            {
                t.Artist, t.Title, t.Release, t.Catno, t.Position, t.Year != 0 ? t.Year : "",
                t.Duration is > 0 ? $"{t.Duration / 60}:{t.Duration % 60:00}" : "", string.Join(" | ", t.AlsoOn),
            })));

    static readonly Regex YearsRe = Text.Re(@"^(\d{4})(?:\s*-\s*(\d{4}))?\z");

    public static (int, int)? ParseYears(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = YearsRe.Match(text.Trim());
        if (!m.Success) throw new Exit("--years should look like 1995 or 1995-2005", 1);
        var a = int.Parse(m.Groups[1].Value);
        return (a, m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : a);
    }

    public static string FileNameTemplate(Config cfg)
    {
        var template = cfg.Str("download", "file_name", Tracks.DefaultFileName);
        try
        {
            Tracks.FormatTemplate(template, Tracks.FileNameFields.ToDictionary(f => f, f => "x"));
        }
        catch (FormatException e)
        {
            throw new Exit("file_name in config.toml can only use {year} {catno} {position} {artist} " +
                           $"{{title}} {{release}} (problem: {e.Message})", 1);
        }
        return template;
    }

    /// <summary>Add "Artist - Title" entries to the single-tracks job (kept in its own folder).</summary>
    public static Job WantedTracksJob(string root, List<string> wanted, int minBitrate, List<string> formats)
    {
        var job = Job.Load(Path.Combine(root, Program.Wanted))
                  ?? new Job { Kind = "tracks", Id = 0, Name = Program.Wanted, Url = "", TrackPairs = new() };
        job.MinBitrate = minBitrate;
        job.Formats = formats;
        job.TrackPairs ??= new();
        var known = job.TrackPairs.Select(p => (Text.Norm(p[0]), Text.Norm(p[1]))).ToHashSet();
        foreach (var entry in wanted)
        {
            var i = entry.IndexOf(" - ", StringComparison.Ordinal);
            if (i < 0) throw new Exit($"Write each track as \"Artist - Title\" (got: {entry})", 1);
            var (artist, title) = (entry[..i].Trim(), entry[(i + 3)..].Trim());
            if (known.Add((Text.Norm(artist), Text.Norm(title)))) job.TrackPairs.Add(new() { artist, title });
        }
        return job;
    }
}
