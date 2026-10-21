// soulget - Copyright (C) 2026 the soulget contributors
// SPDX-License-Identifier: GPL-3.0-only (see LICENSE and NOTICE)

using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Soulget;

public class Outcome(Track track, string status, string file = "", string fmt = "", int? bitrate = null,
                     string user = "", string note = "")
{
    public Track Track = track;
    public string Status = status;   // downloaded / already had / not found / failed / gave up
    public string File = file, Fmt = fmt, User = user, Note = note;
    public int? Bitrate = bitrate;
}

/// <summary>How to search and download, from config.toml and the folder's saved settings.</summary>
public record Opts(int MinBitrate, List<string> Formats, int Parallel, double SearchWait, int MaxTries,
                   double StartTimeout, double StallTimeout, double ProgressEvery, double SlowBps,
                   bool FallbackQueries, string FileName, double MinFreeGb, bool Strict = false)
{
    public static Opts From(Config cfg, int minBitrate, List<string> formats, bool retry = false, bool strict = false) => new(
        minBitrate, formats,
        cfg.Int("download", "parallel_downloads", 3),
        cfg.Num("download", "search_wait_seconds", 8),
        cfg.Int("download", "max_tries_per_track", 5),
        cfg.Num("download", "start_timeout_seconds", 120),
        cfg.Num("download", "stall_timeout_seconds", 60),
        cfg.Num("download", "progress_every_seconds", 30),
        cfg.Num("download", "slow_speed_kb_per_s", 50) * 1000,
        // Retry rounds only send the main search per track: Soulseek allows about
        // 34 searches every 220 s, and a big label can have hundreds of misses.
        !retry,
        Catalog.FileNameTemplate(cfg),
        cfg.Num("download", "min_free_gb", 10),
        strict);

    public DownloadSettings Download => new(StartTimeout, StallTimeout, ProgressEvery, SlowBps);
}

/// <summary>One folder's job for this run: which tracks to look for, and how.</summary>
public class Work(string outDir, List<Track> todo, Opts opts, List<Outcome> outcomes, Misses misses, int gaveUp = 0)
{
    public string OutDir = outDir;
    public List<Track> Todo = todo;
    public Opts Opts = opts;
    public List<Outcome> Outcomes = outcomes;   // already-had / given-up rows, then this run's results
    public Misses Misses = misses;
    public int GaveUp = gaveUp;

    public int Found() { lock (Outcomes) return Outcomes.Count(o => o.Status == "downloaded"); }
}

public static class Runner
{
    static readonly object Sync = new();
    /// <summary>Set by Ctrl+C: no new tracks are started; the rest stay on the timer's list.</summary>
    public static volatile bool StopRequested;
    /// <summary>Free space where a folder lives, in GB (replaceable in tests).</summary>
    public static Func<string, double> FreeGb = path => new DriveInfo(path).AvailableFreeSpace / 1e9;

    static void Say(string line) { lock (Sync) Console.WriteLine(line); }

    public static async Task<Outcome> FetchTrackAsync(IEngine sl, Track t, string tag, string outDir, Opts opts, HashSet<string> taken)
    {
        int seen = 0, attempts = 0;
        var reasons = new Dictionary<string, int>();
        var reasonOrder = new List<string>();
        var tried = new HashSet<string>();
        var tries = new int[2];          // per quality tier: lossless / lossy
        var slow = new List<Candidate>(); // users dropped for being slow, kept as a last resort
        var lastFail = "";
        var fmts = opts.Formats;
        var folder = Path.GetFileName(outDir.TrimEnd(Path.DirectorySeparatorChar));
        void Ev(string state, params (string, object?)[] more) =>
            Events.Emit("track", new (string, object?)[] { ("folder", folder), ("key", t.Key), ("title", t.Display()), ("state", state) }
                                 .Concat(more).ToArray());

        async Task<(bool, string, Outcome?)> Attempt(Candidate c, bool maySwitch)
        {
            var quality = Tracks.Lossless.Contains(c.Ext) ? c.Ext : $"{c.Ext} {c.Bitrate} kbps";
            Say($"{tag} {t.Display()}: downloading {quality} from {c.Username}");
            Ev("downloading", ("quality", quality), ("from", c.Username));
            var name = Tracks.FileStem(t, opts.FileName);
            string final;
            lock (taken)
            {
                final = Path.Combine(outDir, $"{name}.{c.Ext}");
                for (int n = 2; taken.Contains(Path.GetFileName(final)) || System.IO.File.Exists(final); n++)  // two tracks, same name
                    final = Path.Combine(outDir, $"{name} ({n}).{c.Ext}");
                taken.Add(Path.GetFileName(final));   // reserved while downloading
            }
            var (ok, why) = await sl.DownloadAsync(c, final, opts.Download, msg => { Say($"{tag}      {t.Display()}: {msg}"); Ev("progress", ("message", msg)); }, maySwitch);
            if (!ok)
            {
                lock (taken) taken.Remove(Path.GetFileName(final));
                Say($"{tag}      {c.Username}: {why}");
                Ev("retrying", ("message", $"{c.Username}: {why}"));
                return (false, why, null);
            }
            var mb = new FileInfo(final).Length / 1e6;
            Say($"{tag} OK   {t.Display()}  ({mb:0.0} MB)");
            Ev("done", ("quality", quality), ("mb", Math.Round(mb, 1)));
            return (true, "", new Outcome(t, "downloaded", Path.GetFileName(final), c.Ext, c.Bitrate, c.Username));
        }

        foreach (var q in Tracks.SearchQueries(t, retry: !opts.FallbackQueries))
        {
            var cands = await sl.SearchAsync(q, opts.SearchWait);
            seen += cands.Count;
            var good = new List<Candidate>();
            foreach (var c in cands)
            {
                var why = Tracks.Judge(t, c, opts.MinBitrate, fmts, opts.Strict);
                if (why is not null)
                {
                    if (!reasons.ContainsKey(why)) { reasons[why] = 0; reasonOrder.Add(why); }
                    reasons[why]++;
                }
                else
                {
                    c.Score = Tracks.Score(t, c, opts.MinBitrate, fmts);
                    good.Add(c);
                }
            }
            good = good.OrderBy(c => Tracks.RankKey(c, fmts)).ToList();   // stable, like Python's sort
            for (int i = 0; i < good.Count; i++)
            {
                var c = good[i];
                if (tried.Contains(c.Username)) continue;   // one try per user per track
                var tier = Tracks.QualityTier(c);
                if (tries[tier] >= opts.MaxTries) continue;  // this quality level had its turns
                // Before settling for a lower-quality format, give a slow user with the preferred format the chance to finish.
                if (slow.Count > 0 && Tracks.QualityTier(slow[0]) != tier && fmts.IndexOf(slow[0].Ext) < fmts.IndexOf(c.Ext))
                {
                    var s0 = slow[0];
                    slow.RemoveAt(0);
                    Say($"{tag} {t.Display()}: going back to {s0.Username} rather than a lower quality");
                    var (ok0, why0, done0) = await Attempt(s0, false);
                    if (ok0) return done0!;
                    lastFail = why0;
                }
                tried.Add(c.Username);
                tries[tier]++;
                attempts++;
                // Only drop a slow user if someone with the same quality is still left to try.
                var maySwitch = good.Skip(i + 1).Any(x => !tried.Contains(x.Username) && Tracks.QualityTier(x) == tier
                                                          && tries[tier] < opts.MaxTries);
                var (ok, why, done) = await Attempt(c, maySwitch);
                if (ok) return done!;
                lastFail = why;
                if (why.StartsWith("too slow")) slow.Add(c);
            }
            if (attempts >= 2 * opts.MaxTries) break;
        }

        // Every faster user failed: go back to a slow one and let it finish this time.
        foreach (var c in slow.Take(1))
        {
            Say($"{tag} {t.Display()}: no faster user worked, going back to {c.Username}");
            var (ok, why, done) = await Attempt(c, false);
            if (ok) return done!;
            lastFail = why;
        }

        string note;
        if (seen == 0) note = "no results on Soulseek";
        else if (attempts == 0)
        {
            // Counter.most_common(3): highest counts first, ties in first-seen order.
            var top = reasonOrder.Select((w, i) => (w, n: reasons[w], i)).OrderByDescending(x => x.n).ThenBy(x => x.i).Take(3)
                                 .Select(x => $"{x.w} ({x.n})");
            note = $"{seen} files seen, none suitable: {string.Join(", ", top)}";
        }
        else note = $"{attempts} users tried, none delivered (last: {lastFail})";
        Say($"{tag} MISS {t.Display()}: {note}");
        Ev("missing", ("message", note));
        return new Outcome(t, attempts == 0 ? "not found" : "failed", note: note);
    }

    /// <summary>Every track downloaded into any folder: key -> (folder name, file name).</summary>
    public static Dictionary<string, (string Folder, string File)> AllDownloads(string root)
    {
        var have = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return have;
        foreach (var d in Directory.GetDirectories(root).OrderBy(x => x, PyStringComparer.Instance))
        {
            if (!System.IO.File.Exists(Path.Combine(d, Manifest.FileName))) continue;
            foreach (var (key, e) in Manifest.Load(d))
            {
                var file = J.Str(e, "file");
                if (System.IO.File.Exists(Path.Combine(d, file))) have.TryAdd(key, (Path.GetFileName(d), file));
            }
        }
        return have;
    }

    /// <summary>Tracks still to get, and an "already had" row for each one downloaded before, in this
    /// folder or any other (an album you add won't re-download what a label run already got).</summary>
    public static (List<Track> Todo, List<Outcome> Had) SplitDone(List<Track> tracks, string outDir,
                                                                    Dictionary<string, (string Folder, string File)>? have = null)
    {
        var manifest = Manifest.Load(outDir);
        have ??= AllDownloads(Path.GetDirectoryName(outDir.TrimEnd(Path.DirectorySeparatorChar))!);
        var name = Path.GetFileName(outDir.TrimEnd(Path.DirectorySeparatorChar));
        var todo = new List<Track>();
        var had = new List<Outcome>();
        foreach (var t in tracks)
        {
            var file = manifest[t.Key] is JsonObject e ? J.Str(e, "file") : null;
            if (file is not null && System.IO.File.Exists(Path.Combine(outDir, file)))
                had.Add(new Outcome(t, "already had", file, note: "from an earlier run"));
            else if (have.TryGetValue(t.Key, out var h) && h.Folder != name)
                had.Add(new Outcome(t, "already had", $"../{h.Folder}/{h.File}", note: $"already in {h.Folder}"));
            else todo.Add(t);
        }
        return (todo, had);
    }

    /// <summary>Log in once, then work through each folder. False if the login failed.</summary>
    public static async Task<bool> RunWorkAsync(List<Work> works, IEngine sl, string user)
    {
        var incomplete = Path.Combine(Path.GetDirectoryName(works[0].OutDir.TrimEnd(Path.DirectorySeparatorChar))!, ".incomplete");
        try { Directory.Delete(incomplete, recursive: true); } catch (DirectoryNotFoundException) { }
        Directory.CreateDirectory(incomplete);
        Say($"Logging in to Soulseek as {user}...");
        Events.Emit("login", ("state", "start"), ("user", user));
        try
        {
            await sl.StartAsync();
        }
        catch (LoginFailed e)
        {
            Say($"Could not log in: {e.Message}");
            Events.Emit("login", ("state", "failed"), ("message", e.Message));
            await sl.StopAsync();
            return false;
        }
        Say("Logged in.");
        Events.Emit("login", ("state", "ok"));
        var have = AllDownloads(Path.GetDirectoryName(works[0].OutDir.TrimEnd(Path.DirectorySeparatorChar))!);
        try
        {
            foreach (var w in works)
            {
                if (w.Todo.Count == 0) continue;
                Say($"\n{Path.GetFileName(w.OutDir)}: looking for {w.Todo.Count} tracks, {w.Opts.Parallel} at a time.\n");
                Events.Emit("folder", ("folder", Path.GetFileName(w.OutDir)), ("path", w.OutDir), ("todo", w.Todo.Count));
                await WorkFolderAsync(sl, w, have);
            }
        }
        finally
        {
            try { await sl.StopAsync().WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception) { }
            try { Directory.Delete(incomplete, recursive: true); } catch (IOException) { }
            foreach (var w in works)
            {
                try { WriteReport(w.Outcomes, Path.Combine(w.OutDir, "report.csv")); }
                catch (IOException) { Say($"Couldn't write report.csv in {w.OutDir}: the folder can't be reached."); }
            }
        }
        return true;
    }

    static async Task WorkFolderAsync(IEngine sl, Work w, Dictionary<string, (string Folder, string File)> have)
    {
        var manifest = Manifest.Load(w.OutDir);
        var taken = Directory.GetFiles(w.OutDir).Select(f => Path.GetFileName(f)).ToHashSet();
        var queue = new ConcurrentQueue<(int, Track)>(w.Todo.Select((t, i) => (i + 1, t)));
        var width = w.Todo.Count.ToString().Length;
        var folderName = Path.GetFileName(w.OutDir);
        string? stopReason = null;
        var state = new object();
        const string Gone = "Stopping: the music folder can't be reached any more (was the drive disconnected?).";
        bool FolderGone() => !Directory.Exists(w.OutDir);

        void StopFor(string why)   // call inside lock (state)
        {
            if (stopReason is not null) return;
            stopReason = why;
            Say($"\n{why} The rest stay on the timer's list.\n");
            Events.Emit("stopping", ("folder", folderName), ("reason", why));
        }

        void Record(Track t, Outcome o)
        {
            lock (state)
            {
                lock (w.Outcomes) w.Outcomes.Add(o);
                try
                {
                    if (o.Status == "downloaded")
                    {
                        manifest[t.Key] = new JsonObject { ["file"] = o.File, ["from"] = o.User };
                        lock (have) have[t.Key] = (folderName, o.File);
                        Manifest.Save(w.OutDir, manifest);
                    }
                    // Saved after every track, so stopping halfway loses nothing.
                    w.Misses.Record(t.Key, o.Status is "downloaded" or "already had", o.Note, name: t.Display());
                    w.Misses.Save();
                }
                catch (IOException e) { StopFor(FolderGone() ? Gone : $"Stopping: couldn't write to the music folder ({e.Message})."); }
                Events.Emit("result", ("folder", folderName), ("key", t.Key), ("title", t.Display()), ("status", o.Status),
                            ("file", o.File), ("note", o.Note));
            }
        }

        void Postpone(Track t, string why)
        {
            lock (state)
            {
                StopFor(why);
                w.Misses.Queue(t.Key, t.Display());   // not searched, but not forgotten
                try { w.Misses.Save(); } catch (IOException) when (FolderGone()) { }
            }
        }

        async Task Worker()
        {
            while (queue.TryDequeue(out var item))
            {
                var (i, t) = item;
                if (stopReason is not null) { Postpone(t, stopReason); continue; }
                if (StopRequested) { Postpone(t, "Stopped by you."); continue; }
                if (sl.Problem is { } problem) { Postpone(t, $"Stopping: {problem}."); continue; }
                if (FolderGone()) { Postpone(t, Gone); continue; }
                if (Native.OpenFilesNearlyUsedUp())
                {
                    Postpone(t, $"Stopping this round early: too many network connections are open " +
                                $"({Native.OpenFiles()} of {Native.OpenFileLimit()}).");
                    continue;
                }
                var freeGb = FreeGb(w.OutDir);
                if (freeGb < w.Opts.MinFreeGb)
                {
                    Postpone(t, $"Stopping downloads: only {freeGb:0.0} GB free on the disk (limit: min_free_gb in config.toml).");
                    continue;
                }
                Outcome o;
                (string Folder, string File) h;
                bool elsewhere;
                lock (have) elsewhere = have.TryGetValue(t.Key, out h) && h.Folder != folderName;
                if (elsewhere)   // another folder got it this run
                    o = new Outcome(t, "already had", $"../{h.Folder}/{h.File}", note: $"already in {h.Folder}");
                else
                {
                    var tag = $"[{i.ToString().PadLeft(width)}/{w.Todo.Count}]";
                    try
                    {
                        o = await FetchTrackAsync(sl, t, tag, w.OutDir, w.Opts, taken);
                    }
                    catch (Exception e)   // one odd track must never stop the whole run
                    {
                        Say($"{tag} MISS {t.Display()}: unexpected error ({e.GetType().Name}: {e.Message})");
                        o = new Outcome(t, "failed", note: $"unexpected error: {e.GetType().Name}");
                    }
                }
                Record(t, o);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Max(1, w.Opts.Parallel)).Select(_ => Task.Run(Worker)));
    }

    public static void WriteReport(List<Outcome> outcomes, string path)
    {
        var order = new Dictionary<string, int> { ["downloaded"] = 0, ["already had"] = 1, ["failed"] = 2, ["not found"] = 3, ["gave up"] = 4 };
        List<Outcome> rows;
        lock (outcomes) rows = outcomes.OrderBy(o => order.GetValueOrDefault(o.Status, 9))
                                       .ThenBy(o => o.Track.Year != 0 ? o.Track.Year : 9999).ToList();
        Files.WriteCsv(path, new[] { new object?[] { "status", "artist", "title", "first_release", "catno", "year", "file",
                                                     "format", "bitrate_kbps", "from_user", "note" } }
            .Concat(rows.Select(o => new object?[]
            {
                o.Status, o.Track.Artist, o.Track.Title, o.Track.Release, o.Track.Catno,
                o.Track.Year != 0 ? o.Track.Year : "", o.File, o.Fmt, o.Bitrate is > 0 ? o.Bitrate : "", o.User, o.Note,
            })));
    }

    public static void PrintSummary(List<Outcome> outcomes, string outDir, bool quiet = false)
    {
        int N(string s) => outcomes.Count(o => o.Status == s);
        Events.Emit("summary", ("folder", Path.GetFileName(outDir.TrimEnd(Path.DirectorySeparatorChar))), ("path", outDir),
                    ("downloaded", N("downloaded")), ("had", N("already had")), ("not_found", N("not found")),
                    ("failed", N("failed")), ("gave_up", N("gave up")));
        if (quiet) return;
        Console.WriteLine($"\nDone.\n  downloaded now:   {N("downloaded")}\n  already had:      {N("already had")}\n" +
                          $"  not found:        {N("not found")}\n  failed downloads: {N("failed")}" +
                          (N("gave up") > 0 ? $"\n  given up on:      {N("gave up")}" : ""));
        Console.WriteLine($"Files: {outDir}\nDetails per track: {Path.Combine(outDir, "report.csv")}");
    }
}
