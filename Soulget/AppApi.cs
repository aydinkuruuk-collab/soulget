// soulget - Copyright (C) 2026 the soulget contributors
// SPDX-License-Identifier: GPL-3.0-only (see LICENSE and NOTICE)

using System.Text;
using System.Text.Json.Nodes;
using static Soulget.Text;

namespace Soulget;

/// <summary>Progress for the Mac app. With --events, every important step is also printed as one line
/// "@@ {json}" (the normal text lines stay, for the app's Details view and for people).</summary>
public static class Events
{
    public const string Prefix = "@@ ";
    public static bool On;
    static readonly object Sync = new();

    public static void Emit(string ev, params (string Key, object? Value)[] fields)
    {
        if (!On) return;
        lock (Sync) Console.Out.WriteLine(Prefix + Json(ev, fields).ToJsonString());
    }

    public static JsonObject Json(string ev, params (string Key, object? Value)[] fields)
    {
        var o = new JsonObject { ["ev"] = ev };
        foreach (var (k, v) in fields) o[k] = Node(v);
        return o;
    }

    public static JsonNode? Node(object? v) => v switch
    {
        null => null,
        JsonNode n => n,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        IEnumerable<string> list => new JsonArray(list.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        _ => JsonValue.Create(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)),
    };
}

/// <summary>The commands only the Mac app uses: --app-find, --app-status and --app-set.
/// Each prints a single JSON object.</summary>
public static class AppApi
{
    static void Print(JsonObject o) => Console.WriteLine(o.ToJsonString());

    // -- --app-find: what the search box shows ------------------------------------

    /// <summary>Artists, labels and records matching the text (or the one a Discogs link points to).
    /// Pictures come with a Discogs token only; without one Discogs leaves them out.</summary>
    public static int Find(Config cfg, string text)
    {
        var dg = new Discogs(cfg.DiscogsToken, Config.CacheDir, say: _ => { });
        JsonObject result;
        try { result = FindJson(dg, text.Trim()); }
        catch (DiscogsError e) { result = new JsonObject { ["error"] = e.Message, ["results"] = new JsonArray() }; }
        result["query"] = text;
        result["has_token"] = cfg.DiscogsToken.Length > 0;
        Print(result);
        return 0;
    }

    static readonly System.Text.RegularExpressions.Regex Link =
        Re(@"discogs\.com/(?:[a-z]{2}/)?(artist|label|master|release)/(\d+)");

    public static JsonObject FindJson(Discogs dg, string text)
    {
        var results = new JsonArray();
        var m = Link.Match(text);
        if (m.Success)
        {
            var kind = m.Groups[1].Value;
            var id = int.Parse(m.Groups[2].Value);
            var data = dg.Get($"/{kind}s/{id}", maxAge: 30 * 24 * 3600.0);
            if (!J.Truthy(data)) throw new DiscogsError($"Discogs has no {kind} with id {id}");
            var thumb = J.Items(J.Arr(data, "images")).Select(i => J.Str(i, "uri150")).FirstOrDefault(u => u.Length > 0) ?? "";
            if (kind is "artist" or "label")
                results.Add(Item(kind, id, Tracks.CleanArtist(J.Str(data, "name")), J.Str(data, "name"), "", thumb,
                                 $"https://www.discogs.com/{kind}/{id}"));
            else
            {
                var year = J.Int(data, "year");
                var title = $"{Discogs.ArtistString(J.Arr(data, "artists"))} - {J.Str(data, "title")}";
                results.Add(Item(kind, id, title, title, year > 0 ? year.ToString() : "", thumb,
                                 $"https://www.discogs.com/{kind}/{id}"));
            }
            return new JsonObject { ["results"] = results };
        }
        if (text.Length == 0) return new JsonObject { ["results"] = results };

        var want = Norm(text);
        foreach (var kind in new[] { "artist", "label", "master" })
        {
            // Same request as the download uses (per_page 10), so the answer is cached for it.
            var data = dg.Get("/database/search", new Dictionary<string, object> { ["q"] = text, ["type"] = kind, ["per_page"] = 10 },
                              maxAge: 7 * 24 * 3600.0);
            var found = J.Items(J.Arr(data, "results")).ToList();
            string Name(JsonNode r) => kind == "master" ? Discogs.CleanTitle(J.Str(r, "title")) : Tracks.CleanArtist(J.Str(r, "title"));
            // Exact name matches first, otherwise Discogs' own order.
            var ordered = found.Select((r, i) => (r, i))
                               .OrderBy(x => kind == "master" ? (Norm(Name(x.r)).Contains(want) ? 0 : 1)
                                             : Norm(J.Str(x.r, "title")) == want ? 0 : Norm(Name(x.r)) == want ? 1 : 2)
                               .ThenBy(x => x.i).Select(x => x.r).Take(kind == "master" ? 6 : 4);
            foreach (var r in ordered)
            {
                var extra = new List<string>();
                if (kind == "master")
                {
                    if (J.Int(r, "year") > 0) extra.Add(J.Int(r, "year").ToString());
                    var labels = J.Items(J.Arr(r, "label")).Select(x => x.GetValue<string>()).Distinct().Take(1);
                    extra.AddRange(labels);
                    var formats = J.Items(J.Arr(r, "format")).Select(x => x.GetValue<string>())
                                   .Where(f => f is "Album" or "EP" or "Single" or "Compilation" or "LP" or "12\"").Distinct().Take(1);
                    extra.AddRange(formats);
                }
                else if (J.Str(r, "title") != Name(r))
                    extra.Add($"listed on Discogs as \"{J.Str(r, "title")}\"");   // e.g. "(3)": another one with the same name
                results.Add(Item(kind, J.Int(r, "id"), Name(r), J.Str(r, "title"), string.Join(" · ", extra),
                                 J.Str(r, "thumb"), $"https://www.discogs.com{J.Str(r, "uri")}"));
            }
        }
        return new JsonObject { ["results"] = results };
    }

    static JsonObject Item(string kind, int id, string name, string discogsName, string detail, string thumb, string url) => new()
    {
        ["kind"] = kind, ["id"] = id, ["name"] = name, ["discogs_name"] = discogsName,
        ["detail"] = detail, ["thumb"] = thumb, ["url"] = url,
    };

    // -- --app-status: settings, folders and timer --------------------------------

    public static int Status(Config cfg) { Print(StatusJson(cfg)); return 0; }

    public static JsonObject StatusJson(Config cfg)
    {
        var o = new JsonObject
        {
            ["version"] = Program.AppVersion,
            ["home"] = Config.Home,
            ["config_path"] = Config.ConfigPath,
            ["music_folder"] = Config.Expand(cfg.Str("soulseek", "download_folder", "~/Music/soulget")),
            ["username"] = cfg.Str("soulseek", "username"),
            ["has_password"] = cfg.Str("soulseek", "password").Length > 0,
            ["has_token"] = cfg.DiscogsToken.Length > 0,
            ["formats"] = Events.Node(cfg.List("download", "formats", new() { "mp3", "flac" })),
            ["min_bitrate"] = cfg.Int("download", "default_min_bitrate", 320),
            ["every_hours"] = cfg.Num("retry", "every_hours", 7),
        };
        var st = Schedule.Status();
        o["timer"] = new JsonObject
        {
            ["on"] = st is { Loaded: true }, ["installed"] = st is not null, ["program"] = st?.Program ?? "",
            ["this_home"] = st is not null && Schedule.ServesThisHome(st),
        };

        string root;
        try { root = cfg.DownloadRoot(); }
        catch (DriveMissing e)
        {
            o["drive_missing"] = e.Message;
            o["folders"] = new JsonArray();
            return o;
        }
        o["drive_missing"] = null;
        o["busy"] = BusyPid(root) is int pid ? pid : null;
        o["waiting"] = new WaitQueue(root).Live().Count;

        var days = cfg.Num("retry", "give_up_after_days", 14);
        var folders = new List<(DateTime When, JsonObject Row)>();
        if (Directory.Exists(root))
            foreach (var d in Directory.GetDirectories(root))
            {
                var job = Job.Load(d);
                if (job is null) continue;
                var row = new JsonObject
                {
                    ["name"] = Path.GetFileName(d), ["path"] = d, ["kind"] = job.Kind, ["url"] = job.Url,
                    ["total"] = CountRows(Path.Combine(d, "tracklist.csv")),
                    ["have"] = Manifest.Load(d).Count,
                };
                if (File.Exists(Path.Combine(d, Misses.FileName)))
                {
                    var misses = Misses.Load(d);
                    var looking = misses.Data.Count(kv => !misses.GaveUp(kv.Key, days));
                    row["looking"] = looking;
                    row["gave_up"] = misses.Data.Count - looking;
                }
                else { row["looking"] = 0; row["gave_up"] = 0; }
                var when = new[] { "report.csv", Manifest.FileName, Job.FileName }
                    .Select(f => Path.Combine(d, f)).Where(File.Exists).Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(d)).Max();
                row["updated"] = when.ToString("yyyy-MM-ddTHH:mm:ssZ");
                folders.Add((when, row));
            }
        o["folders"] = new JsonArray(folders.OrderByDescending(f => f.When).Select(f => (JsonNode)f.Row).ToArray());

        var log = Path.Combine(root, Retry.RetryLog);
        var checks = new JsonArray();
        if (File.Exists(log))
            foreach (var line in File.ReadAllLines(log).Skip(1).TakeLast(30))
            {
                var p = line.Split(',');
                if (p.Length < 9) continue;
                checks.Add(new JsonObject
                {
                    ["when"] = p[0], ["folder"] = string.Join(",", p[3..^5]).Trim('"'),
                    ["searched"] = int.TryParse(p[^5], out var a) ? a : 0, ["found"] = int.TryParse(p[^4], out var b) ? b : 0,
                    ["still_missing"] = int.TryParse(p[^3], out var c) ? c : 0, ["trigger"] = p[^1],
                });
            }
        o["last_checks"] = checks;
        return o;
    }

    static int CountRows(string csv)
    {
        if (!File.Exists(csv)) return 0;
        // Fields may hold line breaks inside quotes, so count records rather than lines.
        int rows = 0;
        bool quoted = false, any = false;
        foreach (var ch in File.ReadAllText(csv))
        {
            if (ch == '"') quoted = !quoted;
            if (ch == '\n' && !quoted) { if (any) rows++; any = false; }
            else if (ch != '\r') any = true;
        }
        if (any) rows++;
        return Math.Max(0, rows - 1);   // minus the header
    }

    /// <summary>The process holding the run lock, if it's still running.</summary>
    public static int? BusyPid(string root)
    {
        var path = Path.Combine(root, RunLock.FileName);
        try
        {
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var pid) && Native.ProcessAlive(pid))
                return pid;
        }
        catch (IOException) { }
        return null;
    }

    // -- --app-folder: one folder's tracks, and why the missing ones are missing --------

    public static int Folder(Config cfg, string folder) { Print(FolderJson(cfg, folder)); return 0; }

    public static JsonObject FolderJson(Config cfg, string folder)
    {
        folder = Path.GetFullPath(Config.Expand(folder)).TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(folder);
        var o = new JsonObject { ["name"] = name, ["path"] = folder };
        if (Job.Load(folder) is { } job)
            o["job"] = new JsonObject
            {
                ["kind"] = job.Kind, ["url"] = job.Url, ["formats"] = Events.Node(job.Formats), ["min_bitrate"] = job.MinBitrate,
                ["strict"] = job.Strict, ["no_remixes"] = job.NoRemixes, ["no_compilations"] = job.NoCompilations,
                ["their_remixes"] = job.TheirRemixes,
                ["years"] = job.Years is { Count: >= 2 } y ? $"{y[0]}-{y[1]}" : null,
            };
        var manifest = Manifest.Load(folder);
        var misses = File.Exists(Path.Combine(folder, Misses.FileName)) ? Misses.Load(folder) : null;
        var days = cfg.Num("retry", "give_up_after_days", 14);
        var elsewhere = Runner.AllDownloads(Path.GetDirectoryName(folder)!);
        var counts = new Dictionary<string, int> { ["have"] = 0, ["elsewhere"] = 0, ["missing"] = 0, ["gave_up"] = 0, ["not_tried"] = 0 };
        var tracks = new JsonArray();
        foreach (var row in Files.ReadCsv(Path.Combine(folder, "tracklist.csv")))
        {
            string F(string k) => row.TryGetValue(k, out var v) ? v : "";
            var year = int.TryParse(F("year"), out var yr) ? yr : 0;
            var t = Tracks.MakeTrack(F("artist"), F("title"), F("first_release"), year);
            if (t is null) continue;
            var item = new JsonObject
            {
                ["artist"] = t.Artist, ["title"] = t.Title, ["release"] = F("first_release"), ["year"] = year,
                ["catno"] = F("catno"), ["position"] = F("position"), ["length"] = F("length"), ["remix"] = t.IsRemix,
            };
            string status;
            var file = manifest[t.Key] is JsonObject m ? J.Str(m, "file") : "";
            if (file.Length > 0 && File.Exists(Path.Combine(folder, file)))
            {
                status = "have";
                item["file"] = file;
                item["format"] = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
            }
            else if (elsewhere.TryGetValue(t.Key, out var h) && h.Folder != name)
            {
                status = "elsewhere";
                item["note"] = $"in {h.Folder}";
                item["file"] = $"../{h.Folder}/{h.File}";
            }
            else if (misses?.Data[t.Key] is JsonObject e)
            {
                status = misses.GaveUp(t.Key, days) ? "gave_up" : "missing";
                item["note"] = J.Str(e, "note");
                item["checks"] = J.Int(e, "checks");
                item["first_missed"] = J.Str(e, "first_missed");
                item["last_checked"] = J.Str(e, "last_checked");
            }
            else status = "not_tried";
            item["status"] = status;
            counts[status]++;
            tracks.Add(item);
        }
        o["counts"] = new JsonObject(counts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        o["tracks"] = tracks;
        return o;
    }

    // -- --app-checks: what each check for missing tracks did ---------------------

    public static int Checks(Config cfg) { Print(ChecksJson(cfg)); return 0; }

    static readonly System.Text.RegularExpressions.Regex Header =
        Re(@"^=== soulget retry check, \w{3} (\d{1,2} \w{3} \d{4} \d{2}:\d{2})(?: \((\w+)\))? ===");

    class Run
    {
        public DateTime Started;
        public string Trigger = "scheduled", Result = "unfinished", Reason = "";
        public bool StoppedEarly;
        public List<Dictionary<string, string>> Rows = new();
    }

    public static JsonObject ChecksJson(Config cfg, int max = 30)
    {
        var runs = new List<Run>();
        // The timer's own log: every timed check, including skipped and failed ones.
        if (File.Exists(Schedule.LogPath))
        {
            Run? cur = null;
            foreach (var line in Tail(Schedule.LogPath, 4_000_000))
            {
                var m = Header.Match(line);
                if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                                                        System.Globalization.DateTimeStyles.None, out var started))
                {
                    cur = new Run { Started = started, Trigger = m.Groups[2].Success ? m.Groups[2].Value : "scheduled" };
                    runs.Add(cur);
                    continue;
                }
                if (cur is null) continue;
                if (line.StartsWith("Unhandled exception"))
                {
                    cur.Result = "crashed";
                    cur.Reason = line.Contains("DirectoryNotFound") || line.Contains("/Volumes/")
                        ? "the music drive went away during the check" : "an unexpected error";
                }
                else if (cur.Result == "crashed") { }
                else if (line.StartsWith("Skipped: ")) { cur.Result = "skipped"; cur.Reason = line["Skipped: ".Length..].TrimEnd('.'); }
                else if (line.StartsWith("Could not log in")) { cur.Result = "login_failed"; cur.Reason = line.Split(':', 2).Last().Trim(); }
                else if (line.StartsWith("Nothing left to look for") || line.StartsWith("Nothing to retry yet")) cur.Result = "nothing";
                else if (line.StartsWith("Retry check done")) cur.Result = "done";
                else if (line.StartsWith("Stopping") || line.StartsWith("\nStopping")) cur.StoppedEarly = true;
            }
        }
        // What each check found, per folder (also checks started by hand, which aren't in the timer's log).
        try
        {
            var root = cfg.DownloadRoot();
            var rows = Files.ReadCsv(Path.Combine(root, Retry.RetryLog));
            var scheduled = runs.OrderBy(r => r.Started).ToList();
            foreach (var row in rows)
            {
                if (!DateTime.TryParseExact(row.GetValueOrDefault("when", ""), "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                                            System.Globalization.DateTimeStyles.None, out var when)) continue;
                if (row.GetValueOrDefault("trigger", "") == "manual")
                {
                    var r = runs.FirstOrDefault(x => x.Trigger == "manual" && x.Started == when);
                    if (r is null) runs.Add(r = new Run { Started = when, Trigger = "manual", Result = "done" });
                    r.Rows.Add(row);
                    continue;
                }
                // A timed check logs its folders when it finishes: give them to the last check that started before.
                var owner = scheduled.LastOrDefault(x => x.Started <= when);
                if (owner is not null) owner.Rows.Add(row);
            }
        }
        catch (DriveMissing) { }
        bool Busy() { try { return BusyPid(cfg.DownloadRoot()) is not null; } catch (DriveMissing) { return false; } }
        if (runs.OrderByDescending(r => r.Started).FirstOrDefault() is { Result: "unfinished" } newest && Busy()) newest.Result = "running";

        int N(Dictionary<string, string> r, string k) => int.TryParse(r.GetValueOrDefault(k, ""), out var n) ? n : 0;
        var list = new JsonArray();
        foreach (var r in runs.OrderByDescending(r => r.Started).Take(max))
        {
            list.Add(new JsonObject
            {
                ["started"] = r.Started.ToString("yyyy-MM-ddTHH:mm:ss"), ["trigger"] = r.Trigger, ["result"] = r.Result,
                ["reason"] = r.Reason, ["stopped_early"] = r.StoppedEarly,
                ["searched"] = r.Rows.Sum(x => N(x, "searched")), ["found"] = r.Rows.Sum(x => N(x, "found")),
                ["still_missing"] = r.Rows.Sum(x => N(x, "still_missing")),
                ["folders"] = new JsonArray(r.Rows.Select(x => (JsonNode)new JsonObject
                {
                    ["folder"] = x.GetValueOrDefault("folder", ""), ["searched"] = N(x, "searched"), ["found"] = N(x, "found"),
                    ["still_missing"] = N(x, "still_missing"),
                }).ToArray()),
            });
        }
        return new JsonObject { ["runs"] = list };
    }

    /// <summary>The last lines of a file (only its end is read, so a big log stays quick).</summary>
    static IEnumerable<string> Tail(string path, long bytes)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var start = Math.Max(0, f.Length - bytes);
        f.Seek(start, SeekOrigin.Begin);
        using var r = new StreamReader(f, Encoding.UTF8);
        if (start > 0) r.ReadLine();   // a partial line
        while (r.ReadLine() is { } line) yield return line;
    }

    // -- --app-set: change config.toml, keeping its comments ---------------------

    /// <summary>Reads {"section.key": value, ...} from stdin. Values: text, whole numbers, true/false,
    /// or a list of texts. The file keeps its comments and layout; only those lines change.</summary>
    public static int Set(string json)
    {
        JsonObject changes;
        try { changes = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("expected a JSON object"); }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException)
        {
            Print(new JsonObject { ["ok"] = false, ["error"] = $"bad input: {e.Message}" });
            return 1;
        }
        var text = File.Exists(Config.ConfigPath) ? File.ReadAllText(Config.ConfigPath) : Config.ExampleConfig();
        try
        {
            foreach (var (name, value) in changes)
            {
                var dot = name.IndexOf('.');
                if (dot <= 0) throw new FormatException($"'{name}' should look like section.key");
                text = SetValue(text, name[..dot], name[(dot + 1)..], TomlValue(value));
            }
            Config.Parse(text);   // never write a file the program can't read back
        }
        catch (Exception e)
        {
            Print(new JsonObject { ["ok"] = false, ["error"] = e.Message });
            return 1;
        }
        var tmp = Config.ConfigPath + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);  // it holds a password
        File.Move(tmp, Config.ConfigPath, overwrite: true);
        Print(new JsonObject { ["ok"] = true });
        return 0;
    }

    public static string TomlValue(JsonNode? v) => v switch
    {
        null => "\"\"",
        JsonArray a => "[" + string.Join(", ", a.Select(TomlValue)) + "]",
        JsonValue jv when jv.GetValueKind() == System.Text.Json.JsonValueKind.True => "true",
        JsonValue jv when jv.GetValueKind() == System.Text.Json.JsonValueKind.False => "false",
        JsonValue jv when jv.GetValueKind() == System.Text.Json.JsonValueKind.Number =>
            jv.ToJsonString(),
        JsonValue jv when jv.GetValueKind() == System.Text.Json.JsonValueKind.String => TomlString(jv.GetValue<string>()),
        _ => throw new FormatException($"can't store {v.ToJsonString()} in config.toml"),
    };

    static string TomlString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in s)
            sb.Append(ch switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' or '\x7f' => $"\\u{(int)ch:X4}",
                _ => ch.ToString(),
            });
        return sb.Append('"').ToString();
    }

    /// <summary>Replace (or add) one "key = value" line inside a [section], leaving everything else as it is.</summary>
    public static string SetValue(string text, string section, string key, string value)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        bool IsHeader(string l) { var t = l.Trim(); return t.StartsWith('[') && !t.StartsWith("[[") && t.Contains(']'); }
        string HeaderName(string l) { var t = l.Trim(); return t[1..t.IndexOf(']')].Trim(); }

        var start = lines.FindIndex(l => IsHeader(l) && HeaderName(l) == section);
        var newLine = $"{key} = {value}";
        if (start < 0)
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.AddRange(new[] { "", $"[{section}]", newLine, "" });
            return string.Join(nl, lines);
        }
        var end = lines.FindIndex(start + 1, l => IsHeader(l));
        if (end < 0) end = lines.Count;
        var keyRe = Re(@"^\s*" + System.Text.RegularExpressions.Regex.Escape(key) + @"\s*=");
        for (int i = start + 1; i < end; i++)
        {
            if (!keyRe.IsMatch(lines[i])) continue;
            // A list written over several lines: drop its continuation lines too.
            var rest = lines[i][(lines[i].IndexOf('=') + 1)..].Trim();
            int last = i;
            if (rest.StartsWith('[') && Depth(rest) > 0)
            {
                int depth = Depth(rest);
                while (depth > 0 && last + 1 < end) { last++; depth += Depth(lines[last]); }
            }
            lines.RemoveRange(i, last - i + 1);
            lines.Insert(i, newLine);
            return string.Join(nl, lines);
        }
        lines.Insert(start + 1, newLine);
        return string.Join(nl, lines);
    }

    static int Depth(string s)
    {
        int d = 0;
        bool quoted = false;
        foreach (var ch in s)
        {
            if (ch == '"') quoted = !quoted;
            else if (!quoted && ch == '#') break;
            else if (!quoted && ch == '[') d++;
            else if (!quoted && ch == ']') d--;
        }
        return d;
    }
}
