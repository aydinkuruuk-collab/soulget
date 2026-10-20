// soulget - Copyright (C) 2026 the soulget contributors
// SPDX-License-Identifier: GPL-3.0-only (see LICENSE and NOTICE)

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Soulget;

/// <summary>Retrying missing tracks later, on a schedule. Who has a file on Soulseek depends on who is
/// online, which changes with time zones and weekdays. Each run saves its settings next to the music
/// (.soulget-job.json) and remembers which tracks were missing (.soulget-misses.json); --retry-missing
/// searches again for just those. A timer can run that every N hours; with N = 7 the check time drifts
/// around the clock and across the week by itself.</summary>
public static class Retry
{
    public const string RetryLog = "retry-log.csv";

    /// <summary>One line per folder per check, so you can later see which times find the most.</summary>
    public static void LogCheck(string root, string folder, int tried, int found, int stillMissing, int gaveUp,
                                string trigger, DateTime? at = null)
    {
        var when = at ?? Misses.Now();
        var p = Path.Combine(root, RetryLog);
        var rows = new List<object?[]>();
        if (!File.Exists(p))
            rows.Add(new object?[] { "when", "weekday", "hour", "folder", "searched", "found", "still_missing", "given_up", "trigger" });
        rows.Add(new object?[] { when.ToString("yyyy-MM-dd HH:mm"), when.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture),
                                 when.Hour, folder, tried, found, stillMissing, gaveUp, trigger });
        Files.AppendCsv(p, rows);
    }

    public static bool SoulseekQtRunning()
    {
        if (OperatingSystem.IsWindows())
            return Process.GetProcessesByName("SoulseekQt").Length > 0;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pgrep", "-i -x soulseekqt") { RedirectStandardOutput = true, UseShellExecute = false })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>A notification (top-right corner on a Mac). Silently does nothing where unsupported.</summary>
    public static Action<string, string> Notify = (title, text) =>
    {
        static string Q(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        ProcessStartInfo psi;
        if (OperatingSystem.IsMacOS())
            psi = new ProcessStartInfo("osascript") { ArgumentList = { "-e", $"display notification \"{Q(text)}\" with title \"{Q(title)}\"" } };
        else if (OperatingSystem.IsLinux())
            psi = new ProcessStartInfo("notify-send") { ArgumentList = { title, text } };
        else return;
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        try { using var p = Process.Start(psi); p?.WaitForExit(10_000); }
        catch (Exception) { }   // no notifier installed: nothing to show
    };

    /// <summary>Replaceable in tests; true when SoulseekQt is open.</summary>
    public static Func<bool> QtRunning = SoulseekQtRunning;

    /// <summary>Replaceable in tests: how soulget logs in to Soulseek.</summary>
    public static Func<string, string, string, int, bool, IEngine> MakeEngine =
        (user, pass, incomplete, port, verbose) => new SoulseekEngine(user, pass, incomplete, port, verbose);

    /// <summary>Replaceable in tests: the de-duplicated track list for a saved job.</summary>
    public static Func<Discogs, Job, List<Track>> BuildTracks = (dg, job) => Catalog.BuildTracks(dg, job).Item1;

    static List<string> JobFolders(string root) =>
        Directory.Exists(root)
            ? Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, Job.FileName)))
                       .OrderBy(d => d, PyStringComparer.Instance).ToList()
            : new();

    /// <summary>Search again for the tracks earlier runs couldn't get, in every folder.</summary>
    public static int Check(Config cfg, bool scheduled, bool verbose)
    {
        var root = cfg.DownloadRoot();
        var trigger = scheduled ? "scheduled" : "manual";
        Console.WriteLine($"\n=== soulget retry check, {En(Misses.Now(), "ddd dd MMM yyyy HH:mm")} ({trigger}) ===");
        var fileLimit = Native.OpenFileLimit();
        Console.WriteLine($"(open-file limit: {(fileLimit > 0 ? fileLimit.ToString() : "n/a")})");

        (string user, string pass) login;
        if (cfg.Str("retry", "username").Length > 0 && cfg.Str("retry", "password").Length > 0)
            login = (cfg.Str("retry", "username"), cfg.Str("retry", "password"));
        else
        {
            login = (cfg.Str("soulseek", "username"), cfg.Str("soulseek", "password"));
            if (scheduled && QtRunning())
            {
                Console.WriteLine("Skipped: SoulseekQt is open with the same login, and two logins would kick " +
                                  "each other off. (A second login under [retry] in config.toml avoids this.)");
                return 0;
            }
        }
        if (login.user.Length == 0 || login.pass.Length == 0)
            throw new Exit($"Put your Soulseek username and password in {Config.ConfigPath}", 1);

        var giveUpDays = cfg.Num("retry", "give_up_after_days", 14);
        var folders = JobFolders(root);
        if (folders.Count == 0)
        {
            Console.WriteLine("Nothing to retry yet: no download folders with saved settings.");
            return 0;
        }

        var dg = new Discogs(cfg.DiscogsToken, Config.CacheDir);
        var works = new List<Work>();
        foreach (var folder in folders)
        {
            var job = Job.Load(folder)!;
            List<Track> tracks;
            try { tracks = BuildTracks(dg, job); }
            catch (DiscogsError e)
            {
                Console.WriteLine($"{Path.GetFileName(folder)}: skipped this time, Discogs problem: {e.Message}");
                continue;
            }
            var legacy = !File.Exists(Path.Combine(folder, Misses.FileName));  // folders from before retries existed
            var misses = Misses.Load(folder);
            var (todo, outcomes) = Runner.SplitDone(tracks, folder);
            var keep = new List<Track>();
            int gave = 0;
            foreach (var t in todo)
            {
                if (!legacy && misses.Data[t.Key] is null) continue;   // never tried (e.g. a --limit run); not a "miss"
                if (misses.GaveUp(t.Key, giveUpDays))
                {
                    var e = misses.Data[t.Key]!;
                    outcomes.Add(new Outcome(t, "gave up", note: $"not found in {J.Int(e, "checks")} checks since {J.Str(e, "first_missed")[..10]}"));
                    gave++;
                    continue;
                }
                keep.Add(t);
            }
            if (legacy)
                foreach (var t in keep) misses.Queue(t.Key, t.Display());   // so a check stopped halfway doesn't forget the rest
            misses.Save();
            works.Add(new Work(folder, keep, Opts.From(cfg, job.MinBitrate, job.Formats, retry: true), outcomes, misses, gave));
            Console.WriteLine($"{Path.GetFileName(folder)}: {keep.Count} still missing" + (gave > 0 ? $", {gave} given up on" : ""));
        }

        if (!works.Any(w => w.Todo.Count > 0))
        {
            Console.WriteLine("Nothing left to look for.");
            foreach (var w in works) LogCheck(root, Path.GetFileName(w.OutDir), 0, 0, 0, w.GaveUp, trigger);
            return 0;
        }

        RunLock theLock;
        try
        {
            if (scheduled)
            {
                // A timed check never queues up: it just skips.
                if (new WaitQueue(root).Live().Count > 0) throw new Locked("other runs are waiting their turn");
                theLock = new RunLock(root).Enter();
            }
            else theLock = WaitQueue.WaitForLock(root, "The retry check");
        }
        catch (Locked e)
        {
            Console.WriteLine($"Skipped: {e.Message}.");
            return 0;
        }
        bool ok;
        using (theLock)
        using (var stop = new CancelOnCtrlC())
        {
            var engine = MakeEngine(login.user, login.pass, Path.Combine(root, ".incomplete"),
                                    cfg.Int("soulseek", "listen_port", 60000), verbose);
            ok = Runner.RunWorkAsync(works, engine, login.user).GetAwaiter().GetResult();
            if (stop.Pressed) Console.WriteLine("\nStopped. Whatever was found so far is kept.");
        }
        if (!ok) return 1;

        int total = 0;
        Console.WriteLine("\nRetry check done.");
        foreach (var w in works)
        {
            var found = w.Found();
            total += found;
            var still = w.Todo.Count - found;
            LogCheck(root, Path.GetFileName(w.OutDir), w.Todo.Count, found, still, w.GaveUp, trigger);
            Console.WriteLine($"  {Path.GetFileName(w.OutDir)}: found {found} of {w.Todo.Count}; {still} still missing");
        }
        Console.WriteLine($"When each check ran and what it found: {Path.Combine(root, RetryLog)}");
        if (scheduled && total > 0)
        {
            var names = string.Join(", ", works.Where(w => w.Found() > 0).Select(w => Path.GetFileName(w.OutDir)));
            Notify("soulget", $"Found {total} more track{(total != 1 ? "s" : "")}: {names}");
        }
        return 0;
    }

    /// <summary>Put every track not downloaded yet on the timer's list. Returns (queued, already had).</summary>
    public static (int Queued, int Had) QueueForLater(List<Track> tracks, string outDir)
    {
        var misses = Misses.Load(outDir);
        var (todo, had) = Runner.SplitDone(tracks, outDir);
        foreach (var t in todo) misses.Queue(t.Key, t.Display());
        misses.Save();
        return (todo.Count, had.Count);
    }

    /// <summary>What the timer is still looking for, per folder.</summary>
    public static int Queue(Config cfg)
    {
        var root = cfg.DownloadRoot();
        var days = cfg.Num("retry", "give_up_after_days", 14);
        var folders = JobFolders(root);
        if (folders.Count == 0) { Console.WriteLine("Nothing on the list yet."); return 0; }
        Console.WriteLine($"{"folder",-42}{"have",6}{"looking for",13}{"given up",10}");
        foreach (var d in folders)
        {
            var name = Path.GetFileName(d);
            var shortName = Text.Take(name, 41);
            var have = Manifest.Load(d).Count;
            if (!File.Exists(Path.Combine(d, Misses.FileName)))
            {
                Console.WriteLine($"{shortName,-42}{have,6}{"not checked yet",23}");
                continue;
            }
            var misses = Misses.Load(d);
            var waiting = misses.Data.Where(kv => !misses.GaveUp(kv.Key, days)).ToList();
            Console.WriteLine($"{shortName,-42}{have,6}{waiting.Count,13}{misses.Data.Count - waiting.Count,10}");
            if (waiting.Count is > 0 and <= 8)
                foreach (var (k, e) in waiting)
                {
                    var checks = J.Int(e, "checks");
                    var nm = J.Str(e, "name");
                    Console.WriteLine($"    - {(nm.Length > 0 ? nm : k.Replace('|', ' ').Trim())}  ({(checks > 0 ? $"checked {checks}x" : "not checked yet")})");
                }
        }
        var st = Schedule.Status();
        Console.WriteLine("\nTimer: " + (st is { Loaded: true } ? $"on, every {Hours(st.IntervalSeconds)} hours."
                                                                  : "off (turn on with soulget --schedule on)."));
        Console.WriteLine("have = files in that folder; looking for = searched again at each check; " +
                          $"given up = still missing {G(days)} days after first missed.");
        return 0;
    }

    /// <summary>A date in English, whatever language the computer uses (like the Python version).</summary>
    public static string En(DateTime d, string format) => d.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Python's "{:g}" for whole and simple numbers: 7.0 -> "7", 7.5 -> "7.5".</summary>
    public static string G(double x) => x.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
    static string Hours(double seconds) => G(seconds / 3600);

    // -- renaming -----------------------------------------------------------------

    /// <summary>(key, old file, new file) for every downloaded track whose name doesn't follow the pattern.</summary>
    public static List<(string Key, string Old, string New)> PlanRenames(string folder, List<Track> tracks, string template)
    {
        var manifest = Manifest.Load(folder);
        var byKey = new Dictionary<string, Track>(StringComparer.Ordinal);
        foreach (var t in tracks) byKey[t.Key] = t;   // like Python's {t.key: t}: the last one wins
        var taken = Directory.GetFiles(folder).Select(f => Path.GetFileName(f)).ToHashSet(StringComparer.Ordinal);
        var plan = new List<(string, string, string)>();
        foreach (var (key, e) in manifest)
        {
            var old = J.Str(e, "file");
            if (!byKey.TryGetValue(key, out var t) || !File.Exists(Path.Combine(folder, old))) continue;
            var stem = Tracks.FileStem(t, template);
            var ext = Path.GetExtension(old);
            var nw = stem + ext;
            if (nw == old) continue;
            for (int n = 2; taken.Contains(nw); n++) nw = $"{stem} ({n}){ext}";
            taken.Add(nw);
            plan.Add((key, old, nw));
        }
        return plan;
    }

    /// <summary>Rename files from earlier runs to the current pattern (preview unless apply).</summary>
    public static int Rename(Config cfg, bool apply)
    {
        var root = cfg.DownloadRoot();
        var template = Catalog.FileNameTemplate(cfg);
        var dg = new Discogs(cfg.DiscogsToken, Config.CacheDir);
        var plans = new List<(string Folder, List<Track> Tracks, List<(string Key, string Old, string New)> Plan)>();
        foreach (var folder in JobFolders(root))
        {
            List<Track> tracks;
            try { tracks = BuildTracks(dg, Job.Load(folder)!); }
            catch (DiscogsError e)
            {
                Console.WriteLine($"{Path.GetFileName(folder)}: skipped, Discogs problem: {e.Message}");
                continue;
            }
            var plan = PlanRenames(folder, tracks, template);
            if (plan.Count > 0) plans.Add((folder, tracks, plan));
            Console.WriteLine($"{Path.GetFileName(folder)}: {plan.Count} files to rename");
        }
        var total = plans.Sum(p => p.Plan.Count);
        if (total == 0) { Console.WriteLine("All file names already follow the pattern."); return 0; }
        Console.WriteLine($"\nPattern: {template}\nExamples:");
        foreach (var (_, _, plan) in plans)
            foreach (var (_, old, nw) in plan.Take(3))
                Console.WriteLine($"  {old}\n   -> {nw}");
        if (!apply)
        {
            Console.WriteLine($"\nNothing changed yet. To rename these {total} files, run: soulget --rename --yes");
            return 0;
        }
        int renamed = 0;
        using (WaitQueue.WaitForLock(root, "Renaming"))
        {
            foreach (var (folder, tracks, _) in plans)
            {
                var plan = PlanRenames(folder, tracks, template);   // again, inside the lock
                var manifest = Manifest.Load(folder);
                foreach (var (key, old, nw) in plan)
                {
                    File.Move(Path.Combine(folder, old), Path.Combine(folder, nw));
                    ((JsonObject)manifest[key]!)["file"] = nw;
                    renamed++;
                }
                Manifest.Save(folder, manifest);
            }
        }
        Console.WriteLine($"\nRenamed {renamed} files.");
        return 0;
    }
}

