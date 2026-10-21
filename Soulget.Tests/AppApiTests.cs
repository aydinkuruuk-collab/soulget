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
