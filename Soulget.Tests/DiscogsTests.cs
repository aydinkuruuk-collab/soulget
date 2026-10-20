using System.Text.Json.Nodes;
using Soulget;
using Xunit;

namespace Soulget.Tests;

/// <summary>Discogs answers are faked: albums by link and by name, compilations, catalogue numbers
/// (ported from tests/test_queue.py).</summary>
public class DiscogsTests
{
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

    const string Geogaddi = """
        {"title": "Geogaddi", "year": 2002, "artists": [{"name": "Boards Of Canada", "join": "", "id": 307}],
         "tracklist": [{"type_": "track", "title": "Music Is Math", "duration": "5:21"},
                       {"type_": "heading", "title": "Side B"},
                       {"type_": "track", "title": "Gyroscope", "duration": "3:34"}]}
        """;

    [Fact]
    public void Album_by_link()
    {
        var dg = new FakeDiscogs(new() { ["/masters/2129"] = Geogaddi });
        var f = dg.FindRelease("https://www.discogs.com/master/2129-Boards-Of-Canada-Geogaddi");
        Assert.Equal(("master", 2129, "Boards Of Canada - Geogaddi"), (f.Kind, f.Id, f.Name));
        var (tl, _, _) = dg.ReleaseTracks(f);
        Assert.Equal(new[] { "Gyroscope", "Music Is Math" }, tl.All().Select(t => t.Title));   // heading skipped
    }

    [Fact]
    public void Album_by_name_prefers_master_then_release()
    {
        var dg = new FakeDiscogs(new()
        {
            ["/database/search?master"] = """
                {"results": [{"id": 1, "title": "Boards Of Canada - Music Has The Right To Children / Geogaddi", "uri": "/master/1"},
                             {"id": 2129, "title": "Boards Of Canada - Geogaddi", "uri": "/master/2129"}]}
                """,
        });
        var f = dg.FindRelease("Boards of Canada - Geogaddi");
        Assert.Equal(("master", 2129), (f.Kind, f.Id));                     // exact title wins over first result
        Assert.Equal("Boards of Canada", dg.Calls[0].query!["artist"]);
        Assert.Equal("Geogaddi", dg.Calls[0].query!["release_title"]);
        dg = new FakeDiscogs(new()
        {
            ["/database/search?release"] = """{"results": [{"id": 2519, "title": "Mould Impression (2) - 6 Hours", "uri": "/release/2519"}]}""",
        });
        f = dg.FindRelease("Mould Impression - 6 Hours");
        Assert.Equal(("release", 2519, "Mould Impression - 6 Hours"), (f.Kind, f.Id, f.Name));
    }

    [Fact]
    public void Compilation_keeps_each_tracks_artist()
    {
        var dg = new FakeDiscogs(new()
        {
            ["/releases/9"] = """
                {"title": "Prime Cuts", "year": 1995, "artists": [{"name": "Various", "join": ""}],
                 "tracklist": [{"type_": "track", "title": "Smak", "artists": [{"name": "Plastikman", "join": ""}]}]}
                """,
        });
        var f = dg.FindRelease("https://www.discogs.com/release/9");
        Assert.Equal(new[] { "Plastikman - Smak" }, dg.ReleaseTracks(f).Item1.All().Select(t => t.Display()));
    }

    [Fact]
    public void Catalogue_number_prefers_the_labels_own()
    {
        var rel = JsonNode.Parse("""{"labels": [{"name": "Other", "id": 1, "catno": "OTH 1"}, {"name": "Primate", "id": 363, "catno": "PRMT 010"}]}""")!;
        var dg = new FakeDiscogs(new() { ["/releases/24432"] = """{"labels": [{"id": 9, "catno": "none"}]}""" });
        Assert.Equal("PRMT 010", dg.Catno(rel, "release", labelId: 363));
        Assert.Equal("OTH 1", dg.Catno(rel, "release"));
        Assert.Equal("", dg.Catno(JsonNode.Parse("""{"main_release": 24432}""")!, "master"));   // "none" means no number
    }

    [Fact]
    public void What_counts_as_a_compilation_on_a_label()
    {
        Assert.False(Discogs.IsCompilation("12\", EP", "Various"));          // the label's own V/A record
        Assert.False(Discogs.IsCompilation("2x12\", Comp", "Various"));      // vinyl sampler: kept
        Assert.True(Discogs.IsCompilation("CD, Comp, Enh", "Various"));
        Assert.True(Discogs.IsCompilation("CD, Mixed", "Jeff Mills"));       // DJ-mix CD
        Assert.True(Discogs.IsCompilation("2x12\", Comp, Mixed", "Various"));
        Assert.True(Discogs.IsCompilation("File, MP3, Comp", "Various"));
        Assert.False(Discogs.IsCompilation("12\"", "Mould Impression"));
        Assert.False(Discogs.IsCompilation("CD, Album", "Auto Union"));
    }

    [Fact]
    public void Cache_names_match_the_python_version()
    {
        // Same URL text as Python's urlencode(sorted(...)), so both versions share one cache.
        Assert.Equal("https://api.discogs.com/database/search?per_page=10&q=Sigur+R%C3%B3s+%26+Co&type=artist",
                     Discogs.Url("/database/search", new Dictionary<string, object> { ["q"] = "Sigur Rós & Co", ["type"] = "artist", ["per_page"] = 10 }));
    }
}
