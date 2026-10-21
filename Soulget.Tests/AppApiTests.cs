using System.Text.Json.Nodes;
using Soulget;
using Xunit;

namespace Soulget.Tests;

/// <summary>The commands the Mac app uses: editing config.toml, search results, status and progress lines.</summary>
public class AppApiTests : IDisposable
{
    readonly string tmp = Path.Combine(Path.GetTempPath(), "soulget-appapi-" + Guid.NewGuid());

    public AppApiTests() => Directory.CreateDirectory(tmp);

    public void Dispose()
    {
        Config.HomeOverride = null;
        Events.On = false;
        try { Directory.Delete(tmp, recursive: true); } catch (IOException) { }
    }

    class FakeDiscogs(Dictionary<string, string> answers) : Discogs("", Path.Combine(Path.GetTempPath(), "soulget-test-cache-" + Guid.NewGuid()), _ => { })
    {
        public readonly List<(string path, IDictionary<string, object>? query)> Calls = new();

        public override JsonNode? Get(string path, IDictionary<string, object>? query = null, double? maxAge = null)
        {
            Calls.Add((path, query));
            var key = path + (query is not null && query.TryGetValue("type", out var t) ? "?" + t : "");
            return answers.TryGetValue(key, out var json) ? JsonNode.Parse(json) : null;
        }
    }

    // -- config.toml editing ------------------------------------------------------

    [Fact]
    public void Set_replaces_only_that_line_and_keeps_comments()
    {
        var text = Config.ExampleConfig();
        var changed = AppApi.SetValue(text, "soulseek", "username", AppApi.TomlValue(JsonValue.Create("soulget-ab12")));
        var cfg = Config.Parse(changed);
        Assert.Equal("soulget-ab12", cfg.Str("soulseek", "username"));
        Assert.Equal("", cfg.Str("retry", "username"));   // the [retry] username is a different key
        Assert.Contains("# Your Soulseek login", changed);
        Assert.Equal(text.Split('\n').Length, changed.Split('\n').Length);
    }

    [Fact]
    public void Set_handles_lists_numbers_quotes_and_new_sections()
    {
        var text = "[download]\nformats = [\n  \"mp3\",\n  \"flac\",\n]\n# keep me\nparallel_downloads = 3\n";
        text = AppApi.SetValue(text, "download", "formats", AppApi.TomlValue(JsonNode.Parse("""["flac","wav","mp3"]""")));
        text = AppApi.SetValue(text, "download", "default_min_bitrate", AppApi.TomlValue(JsonValue.Create(256)));
        text = AppApi.SetValue(text, "soulseek", "password", AppApi.TomlValue(JsonValue.Create("a\"b\\c")));
        var cfg = Config.Parse(text);
        Assert.Equal(new List<string> { "flac", "wav", "mp3" }, cfg.List("download", "formats", new()));
        Assert.Equal(256, cfg.Int("download", "default_min_bitrate", 0));
        Assert.Equal(3, cfg.Int("download", "parallel_downloads", 0));
        Assert.Equal("a\"b\\c", cfg.Str("soulseek", "password"));
        Assert.Contains("# keep me", text);
    }

    [Fact]
    public void Set_command_writes_file_readable_only_by_you()
    {
        Config.HomeOverride = tmp;
        var old = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            Assert.Equal(0, AppApi.Set("""{"soulseek.username": "x1", "soulseek.password": "p w", "discogs.token": ""}"""));
            Assert.Equal(1, AppApi.Set("""{"nosection": 1}"""));
        }
        finally { Console.SetOut(old); }
        var cfg = Config.Parse(File.ReadAllText(Config.ConfigPath));
        Assert.Equal(("x1", "p w"), (cfg.Str("soulseek", "username"), cfg.Str("soulseek", "password")));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Config.ConfigPath));
        Assert.Contains("\"ok\":true", sw.ToString());
        Assert.Contains("\"ok\":false", sw.ToString());
    }

    // -- search results -----------------------------------------------------------

    [Fact]
    public void Find_by_link_needs_no_search()
    {
        var dg = new FakeDiscogs(new() { ["/labels/1234"] = """{"name": "Primate (2)", "images": [{"uri150": "https://i.example/p.jpg"}]}""" });
        var r = AppApi.FindJson(dg, "https://www.discogs.com/label/1234-Primate-2");
        var item = r["results"]!.AsArray().Single()!;
        Assert.Equal(("label", 1234, "Primate", "https://i.example/p.jpg"),
                     (J.Str(item, "kind"), J.Int(item, "id"), J.Str(item, "name"), J.Str(item, "thumb")));
        Assert.Single(dg.Calls);
    }

    [Fact]
    public void Find_by_name_lists_artists_labels_and_records_exact_matches_first()
    {
        var dg = new FakeDiscogs(new()
        {
            ["/database/search?artist"] = """{"results": [{"id": 9, "title": "Boards Of Canada (3)", "uri": "/artist/9", "thumb": ""}, {"id": 307, "title": "Boards Of Canada", "uri": "/artist/307-Boards-Of-Canada", "thumb": ""}]}""",
            ["/database/search?label"] = """{"results": []}""",
            ["/database/search?master"] = """{"results": [{"id": 2129, "title": "Boards Of Canada - Geogaddi", "uri": "/master/2129", "year": 2002, "label": ["Warp Records", "Music70"], "format": ["Vinyl", "LP", "Album"], "thumb": ""}]}""",
        });
        var res = AppApi.FindJson(dg, "boards of canada")["results"]!.AsArray();
        Assert.Equal(new[] { "artist:307", "artist:9", "master:2129" }, res.Select(x => $"{J.Str(x, "kind")}:{J.Int(x, "id")}"));
        Assert.Equal("2002 · Warp Records · LP", J.Str(res[2], "detail"));
        Assert.Equal("Geogaddi", J.Str(res[2], "name").Split(" - ").Last());
        // Same per_page as the download's own search, so its answer is reused from the cache.
        Assert.All(dg.Calls, c => Assert.Equal(10, c.query!["per_page"]));
    }

    // -- status ---------------------------------------------------------------------

    [Fact]
    public void Status_lists_folders_with_counts()
    {
        Config.HomeOverride = tmp;
        var music = Path.Combine(tmp, "music");
        var folder = Path.Combine(music, "Some Label (label)");
        Directory.CreateDirectory(folder);
        new Job { Kind = "label", Id = 5, Name = "Some Label", Url = "https://www.discogs.com/label/5", Formats = new() { "flac" } }.Save(folder);
        File.WriteAllText(Path.Combine(folder, "tracklist.csv"), "artist,title\r\nA,\"Two\r\nlines\"\r\nB,Three\r\nC,Four\r\n");
        File.WriteAllText(Path.Combine(folder, "Three.flac"), "x");
        Manifest.Save(folder, new JsonObject { ["b|three|"] = new JsonObject { ["file"] = "Three.flac" } });
        var misses = Misses.Load(folder);
        misses.Queue("c|four|", "C - Four");
        misses.Save();

        var cfg = Config.Parse($"[soulseek]\nusername = \"me\"\ndownload_folder = \"{music.Replace("\\", "\\\\")}\"\n[discogs]\ntoken = \"\"\n");
        var s = AppApi.StatusJson(cfg);
        Assert.Equal(("me", false, false), (J.Str(s, "username"), J.Truthy(J.Get(s, "has_password")), J.Truthy(J.Get(s, "has_token"))));
        var f = s["folders"]!.AsArray().Single()!;
        Assert.Equal(("Some Label (label)", "label", 3, 1, 1, 0),
                     (J.Str(f, "name"), J.Str(f, "kind"), J.Int(f, "total"), J.Int(f, "have"), J.Int(f, "looking"), J.Int(f, "gave_up")));
        Assert.Null(J.Get(s, "busy"));
    }

    [Fact]
    public void Status_says_when_the_drive_is_missing()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var cfg = Config.Parse("[soulseek]\ndownload_folder = \"/Volumes/NoSuchDrive-soulget-test/Music\"\n");
        var s = AppApi.StatusJson(cfg);
        Assert.Contains("NoSuchDrive-soulget-test", J.Str(s, "drive_missing"));
        Assert.Empty(s["folders"]!.AsArray());
    }

    [Fact]
    public void Timer_counts_only_for_the_folder_it_checks()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Config.HomeOverride = tmp;
        Assert.True(Schedule.ServesThisHome(new Schedule.State(true, 25200, Path.Combine(tmp, "retry-output.log"), "x")));
        Assert.False(Schedule.ServesThisHome(new Schedule.State(true, 25200, "/Users/someone/soulget/retry-output.log", "x")));
        Assert.False(Schedule.ServesThisHome(new Schedule.State(true, 25200, "", "x")));
    }

    // -- one folder's tracks ---------------------------------------------------------

    [Fact]
    public void Csv_reader_handles_quotes_commas_and_line_breaks()
    {
        var p = Path.Combine(tmp, "x.csv");
        Files.WriteCsv(p, new[] { new object?[] { "artist", "title" }, new object?[] { "A, B", "Say \"hi\"\nagain" }, new object?[] { "C", "" } });
        var rows = Files.ReadCsv(p);
        Assert.Equal(2, rows.Count);
        Assert.Equal(("A, B", "Say \"hi\"\nagain"), (rows[0]["artist"], rows[0]["title"]));
        Assert.Equal(("C", ""), (rows[1]["artist"], rows[1]["title"]));
    }

    [Fact]
    public void Folder_lists_every_track_with_its_status_and_reason()
    {
        Config.HomeOverride = tmp;
        var music = Path.Combine(tmp, "music");
        var folder = Path.Combine(music, "Raime");
        var other = Path.Combine(music, "Blackest Ever Black (label)");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(other);
        var have = Tracks.MakeTrack("Raime", "This Foundry", "Raime EP", 2010)!;
        var elsewhere = Tracks.MakeTrack("Raime", "Passed Over (Live)", "Raime EP", 2010)!;
        var missing = Tracks.MakeTrack("Raime", "Exist", "Quarter Turns", 2012)!;
        var gaveUp = Tracks.MakeTrack("Raime", "Soil", "Quarter Turns", 2012)!;
        var untried = Tracks.MakeTrack("Raime", "Untitled", "Hennail", 2011)!;   // a generic title: its key includes the record
        Catalog.WriteTracklist(new() { have, elsewhere, missing, gaveUp, untried }, Path.Combine(folder, "tracklist.csv"));
        File.WriteAllText(Path.Combine(folder, "This Foundry.flac"), "x");
        Manifest.Save(folder, new JsonObject { [have.Key] = new JsonObject { ["file"] = "This Foundry.flac" } });
        File.WriteAllText(Path.Combine(other, "Passed Over.mp3"), "x");
        Manifest.Save(other, new JsonObject { [elsewhere.Key] = new JsonObject { ["file"] = "Passed Over.mp3" } });
        var misses = Misses.Load(folder);
        misses.Record(missing.Key, false, "34 files seen, none suitable: bitrate too low (34)", name: missing.Display());
        misses.Record(gaveUp.Key, false, "no results on Soulseek", at: DateTime.Now.AddDays(-30), name: gaveUp.Display());
        misses.Save();

        var cfg = Config.Parse("[retry]\ngive_up_after_days = 14\n");
        var o = AppApi.FolderJson(cfg, folder);
        var byTitle = o["tracks"]!.AsArray().ToDictionary(t => J.Str(t, "title"), t => t!);
        Assert.Equal(("have", "flac"), (J.Str(byTitle["This Foundry"], "status"), J.Str(byTitle["This Foundry"], "format")));
        Assert.Equal(("elsewhere", "in Blackest Ever Black (label)"), (J.Str(byTitle["Passed Over (Live)"], "status"), J.Str(byTitle["Passed Over (Live)"], "note")));
        Assert.True(J.Truthy(J.Get(byTitle["Passed Over (Live)"], "remix")));
        Assert.Equal(("missing", 1), (J.Str(byTitle["Exist"], "status"), J.Int(byTitle["Exist"], "checks")));
        Assert.Contains("bitrate too low", J.Str(byTitle["Exist"], "note"));
        Assert.Equal("gave_up", J.Str(byTitle["Soil"], "status"));
        Assert.Equal("not_tried", J.Str(byTitle["Untitled"], "status"));
        Assert.Equal(1, J.Int(o["counts"], "not_tried"));
    }

    // -- what each check did ------------------------------------------------------

    [Fact]
    public void Check_history_reads_the_timer_log_and_what_each_check_found()
    {
        Config.HomeOverride = tmp;
        var music = Path.Combine(tmp, "music");
        Directory.CreateDirectory(music);
        File.WriteAllText(Path.Combine(tmp, "retry-output.log"), string.Join("\n",
            "=== soulget retry check, Mon 19 Oct 2026 07:59 ===",
            "Skipped: the drive 'MusicDrive' isn't connected.",
            "=== soulget retry check, Tue 20 Oct 2026 14:07 (scheduled) ===",
            "(open-file limit: 10240)",
            "Retry check done.",
            "=== soulget retry check, Tue 20 Oct 2026 23:08 (scheduled) ===",
            "[ 1/9] MISS x",
            "Unhandled exception. System.IO.DirectoryNotFoundException: Could not find a part of the path '/Volumes/MusicDrive/x/report.csv'.",
            "   at Soulget.Files.WriteCsv(String path, IEnumerable`1 rows)", ""));
        var at = new DateTime(2026, 10, 20, 16, 8, 0);
        Retry.LogCheck(music, "Primate Recordings (label)", 138, 1, 137, 0, "scheduled", at);
        Retry.LogCheck(music, "Vault Series (label)", 1, 0, 1, 0, "scheduled", at);
        Retry.LogCheck(music, "Katran", 4, 2, 2, 0, "manual", new DateTime(2026, 10, 21, 9, 30, 0));

        var cfg = Config.Parse($"[soulseek]\ndownload_folder = \"{music.Replace("\\", "\\\\")}\"\n");
        var runs = AppApi.ChecksJson(cfg)["runs"]!.AsArray();
        Assert.Equal(new[] { "manual:done", "scheduled:crashed", "scheduled:done", "scheduled:skipped" },
                     runs.Select(r => $"{J.Str(r, "trigger")}:{J.Str(r, "result")}"));
        Assert.Equal((139, 1, 2), (J.Int(runs[2], "searched"), J.Int(runs[2], "found"), runs[2]!["folders"]!.AsArray().Count));
        Assert.Equal("the music drive went away during the check", J.Str(runs[1], "reason"));
        Assert.Equal("the drive 'MusicDrive' isn't connected", J.Str(runs[3], "reason"));
        Assert.Equal((4, 2), (J.Int(runs[0], "searched"), J.Int(runs[0], "found")));
        Assert.Equal("2026-10-20T14:07:00", J.Str(runs[2], "started"));
    }

    [Fact]
    public void Min_bitrate_can_be_given_by_name_for_albums_too()
    {
        var a = Args.Parse(new[] { "--album", "https://www.discogs.com/master/2129", "--min-bitrate", "192" });
        Assert.Equal((192, (string?)null), (a.MinBitrate, a.Name));
        Assert.Equal(256, Args.Parse(new[] { "Raime", "256" }).MinBitrate);
        Assert.Equal(128, Args.Parse(new[] { "Raime", "--min-bitrate=128" }).MinBitrate);
        Assert.Throws<UsageError>(() => Args.Parse(new[] { "Raime", "--min-bitrate", "lots" }));
    }

    // -- progress lines -----------------------------------------------------------

    [Fact]
    public void Events_are_single_json_lines_only_when_asked()
    {
        var old = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            Events.Emit("track", ("title", "x"));   // off: nothing
            Events.On = true;
            Events.Emit("track", ("title", "Ünïcode \"quoted\"\nline"), ("n", 3), ("ok", true), ("missing", null));
        }
        finally { Console.SetOut(old); Events.On = false; }
        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines);
        Assert.StartsWith(Events.Prefix, line);
        var o = JsonNode.Parse(line[Events.Prefix.Length..])!;
        Assert.Equal(("track", "Ünïcode \"quoted\"\nline", 3, true), (J.Str(o, "ev"), J.Str(o, "title"), J.Int(o, "n"), J.Truthy(J.Get(o, "ok"))));
    }
}
