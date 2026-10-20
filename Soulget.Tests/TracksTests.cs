using Soulget;
using Xunit;
using static Soulget.Tracks;

namespace Soulget.Tests;

/// <summary>Offline checks of the de-duplication and file-matching rules (ported from tests/test_tracks.py).</summary>
public class TracksTests
{
    static Track T(string artist, string title, string release, int year, string duration = "",
                   string catno = "", string position = "", int index = 0) =>
        MakeTrack(artist, title, release, year, duration, catno, position, index)!;

    static Candidate Cand(string path, int? bitrate = 320, int? duration = 300, string? ext = null, long size = 10_000_000) =>
        new() { Username = "u", Path = path, Size = size, Ext = ext ?? path[(path.LastIndexOf('.') + 1)..].ToLowerInvariant(),
                Bitrate = bitrate, Duration = duration, FreeSlot = true, Speed = 100000, Queue = 0 };

    [Fact]
    public void Editions_collapse_but_remixes_stay()
    {
        Assert.Equal("", SplitVersion("Roygbiv (Remastered 2013)").Variant);
        Assert.Equal("", SplitVersion("Roygbiv - 2013 Remaster").Variant);
        Assert.Equal("", SplitVersion("Song (Radio Edit)").Variant);
        Assert.Equal("", SplitVersion("Song (Original Mix)").Variant);
        Assert.Equal("", SplitVersion("Song (Extended Version) [Bonus Track]").Variant);
        var v = SplitVersion("Song (Bibio Remix)");
        Assert.True(v.Variant == "Bibio Remix" && v.IsRemix);
        v = SplitVersion("Sunshine Recorder (Part 2)");
        Assert.True(v.Variant == "Part 2" && !v.IsRemix);
        Assert.Equal("(Untitled)", SplitVersion("(Untitled)").Base);
    }

    [Fact]
    public void Tracklist_keeps_one_copy()
    {
        var tl = new TrackList();
        tl.Add(MakeTrack("Boards Of Canada", "Roygbiv", "Music Has The Right To Children", 1998));
        tl.Add(MakeTrack("Boards Of Canada", "Roygbiv (Remastered)", "Reissue", 2013));
        tl.Add(MakeTrack("Boards Of Canada", "Roygbiv", "Peel Session", 1999));
        tl.Add(MakeTrack("Boards Of Canada (2)", "ROYGBIV", "Some Comp", 2004));
        tl.Add(MakeTrack("Boards Of Canada", "Roygbiv (Bibio Remix)", "Remix EP", 2005));
        tl.Add(MakeTrack("Boards Of Canada", "Intro", "Album A", 2000));
        tl.Add(MakeTrack("Boards Of Canada", "Intro", "Album B", 2002));
        tl.Add(MakeTrack("Boards Of Canada", "Silence", "Album B", 2002));
        Assert.Equal(new[] { "Intro", "Intro", "Roygbiv", "Roygbiv (Bibio Remix)" },
                     tl.All().Select(t => t.Title).OrderBy(x => x, StringComparer.Ordinal));
        var roy = tl.All().First(t => t.Title == "Roygbiv");
        Assert.Equal(("Music Has The Right To Children", 1998), (roy.Release, roy.Year));
        Assert.Equal(3, tl.Duplicates);
    }

    [Fact]
    public void Earliest_release_wins_even_if_seen_later()
    {
        var tl = new TrackList();
        tl.Add(MakeTrack("A", "Song", "Best Of", 2010));
        tl.Add(MakeTrack("A", "Song", "Debut Single", 1999));
        var t = tl.All()[0];
        Assert.Equal(("Debut Single", 1999), (t.Release, t.Year));
        Assert.Equal(new[] { "Best Of" }, t.AlsoOn);
    }

    [Fact]
    public void Judge_checks_format_bitrate_version_artist_and_length()
    {
        var t = T("Boards Of Canada", "Roygbiv", "MHTRTC", 1998, "2:31");
        var ok = @"@@music\Boards of Canada\1998 - Music Has the Right to Children\04 - Roygbiv.mp3";
        var f = new[] { "mp3", "flac" };
        Assert.Null(Judge(t, Cand(ok, duration: 151), 320, f));
        Assert.Equal("bitrate too low", Judge(t, Cand(ok, bitrate: 256, duration: 151), 320, f));
        Assert.Null(Judge(t, Cand(ok.Replace(".mp3", ".flac"), bitrate: null, duration: 151), 320, f));
        Assert.Equal("format", Judge(t, Cand(ok.Replace(".mp3", ".ogg"), duration: 151), 320, f));
        Assert.Equal("different version", Judge(t, Cand(@"x\Boards of Canada\Roygbiv (Bibio Remix).mp3", duration: 151), 320, new[] { "mp3" }));
        Assert.Equal("artist not in path", Judge(t, Cand(@"x\Other Artist\Roygbiv.mp3", duration: 151), 320, new[] { "mp3" }));
        Assert.Equal("length doesn't match", Judge(t, Cand(ok, duration: 3600), 320, new[] { "mp3" }));
        // bitrate missing: estimated from size and length (10 MB over 151 s = ~530 kbps)
        Assert.Null(Judge(t, Cand(ok, bitrate: null, duration: 151), 320, new[] { "mp3" }));
        Assert.Equal("unknown bitrate", Judge(t, Cand(ok, bitrate: null, duration: null), 320, new[] { "mp3" }));
    }

    [Fact]
    public void Queries()
    {
        Assert.Equal("black dog raxmus plaid remix", SearchQueries(T("The Black Dog", "Raxmus (Plaid Remix)", "X", 1995))[0]);
        Assert.Equal("boards of canada geogaddi intro", SearchQueries(T("Boards Of Canada", "Intro", "Geogaddi", 2002))[0]);
    }

    [Fact]
    public void Spelling_and_video()
    {
        var tl = new TrackList();
        tl.Add(MakeTrack("Boards Of Canada", "Hi Scores", "Hi Scores", 1996));
        tl.Add(MakeTrack("Boards Of Canada", "Hiscores", "Old Tunes Vol. 2", 1996));
        tl.Add(MakeTrack("Boards Of Canada", "Dayvan Cowboy (Music Video)", "Dayvan Cowboy", 2006));
        Assert.Equal(new[] { "Hi Scores" }, tl.All().Select(t => t.Title));
    }

    [Fact]
    public void Apostrophes_split_in_searches()
    {
        Assert.Equal("mould impression lenk 808", SearchQueries(T("Mould Impression", "Lenk's 808", "X", 1997))[0]);
        Assert.Equal("boards of canada house abin adab",   // repeated words are sent once
                     SearchQueries(T("Boards Of Canada", "House Of Abin'adab", "X", 1996))[0]);
    }

    [Fact]
    public void File_names_sort_by_year_catno_and_position()
    {
        var names = new[] { ("Shimmer", "10"), ("Defrag", "1"), ("Forever", "2") }
            .Select(x => FileStem(T("Auto Union", x.Item1, "Real World Texture", 1995, catno: "ECOMCD1", position: x.Item2))).ToList();
        Assert.Equal(new[] { "1995 [ECOMCD1] 10 Auto Union - Shimmer (Real World Texture)",
                             "1995 [ECOMCD1] 01 Auto Union - Defrag (Real World Texture)",
                             "1995 [ECOMCD1] 02 Auto Union - Forever (Real World Texture)" }, names);
        Assert.EndsWith("Defrag (Real World Texture)", names.OrderBy(x => x, StringComparer.Ordinal).First());
        Assert.Equal("1996 [PRMT 010] A1 Mould Impression - 6 Hours",       // release = title: left out
                     FileStem(T("Mould Impression", "6 Hours", "6 Hours", 1996, catno: "PRMT 010", position: "A1")));
        Assert.Equal("X - Y", FileStem(T("X", "Y", "", 0)));                 // nothing known: just the basics
    }

    [Fact]
    public void Earliest_release_brings_its_catno_and_position()
    {
        var tl = new TrackList();
        tl.Add(MakeTrack("A", "Song", "Best Of", 2010, catno: "BEST 1", position: "7"));
        tl.Add(MakeTrack("A", "Song", "Debut", 1999, catno: "DEB 001", position: "A1"));
        var t = tl.All()[0];
        Assert.Equal(("Debut", "DEB 001", "A1"), (t.Release, t.Catno, t.Position));
    }

    [Fact]
    public void Code_like_titles()
    {
        foreach (var title in new[] { "Untitled 1", "Untitled", "Track 3", "005.3", "006.CE", "006.SB1", "A1", "010*1", "Intro" })
            Assert.True(IsCodeTitle(title), title);
        foreach (var title in new[] { "Last Riot", "Chamber 1", "The Raven", "Twoism", "Broken Monitor", "Lick" })
            Assert.False(IsCodeTitle(title), title);
    }

    [Fact]
    public void Code_title_found_by_position_in_the_right_record_only()
    {
        var t = T("Sawlin & Subjected", "Untitled 1", "Vault Series 2.0", 2011, position: "A1", index: 1);
        Assert.True(t.Code);
        Assert.Equal("sawlin vault series 2 0", SearchQueries(t)[^1]);   // the record search
        var f = new[] { "flac" };
        Assert.Null(Judge(t, Cand(@"m\Sawlin & Subjected - Vault Series 2.0 (VAULT002)\A1 - Untitled.flac", bitrate: null, ext: "flac"), 320, f));
        Assert.Null(Judge(t, Cand(@"m\Sawlin & Subjected - Vault Series 2.0\01 Sawlin & Subjected.flac", bitrate: null, ext: "flac"), 320, f));
        Assert.Equal("title not in file name", Judge(t, Cand(@"m\Sawlin & Subjected - Vault Series 26.0\A1 - Untitled.flac", bitrate: null, ext: "flac"), 320, f));
        Assert.Equal("title not in file name", Judge(t, Cand(@"m\Sawlin & Subjected - Vault Series 2.0\B1 - Untitled.flac", bitrate: null, ext: "flac"), 320, f));
    }

    [Fact]
    public void Normal_titles_still_need_the_title_in_the_file_name()
    {
        var t = T("Subjected", "Last Riot", "Vault Series 7.0", 2012, position: "A", index: 1);
        Assert.False(t.Code);
        Assert.Equal(new[] { "subjected last riot", "subjected vault series 7 0" }, SearchQueries(t, retry: true));
        Assert.Equal("title not in file name", Judge(t, Cand(@"m\Subjected - Vault Series 7.0\A - Nervous City.mp3"), 320, new[] { "mp3" }));
        Assert.Null(Judge(t, Cand(@"m\Subjected - Vault Series 7.0\A - Last Riot.mp3"), 320, new[] { "mp3" }));
    }

    [Fact]
    public void Text_rules_match_python_for_tricky_characters()
    {
        Assert.Equal(new[] { "sigur", "ros" }, Text.Tokens("Sigur Rós"));
        Assert.Equal("σας", Text.Norm("ΣΑΣ"));                // Greek final sigma, like Python's lower()
        Assert.Equal(new[] { "dont", "stop", "remix" }, Text.Tokens("Don't Stop (Remix)"));
        Assert.Equal(new[] { "a", "and", "b" }, Text.Tokens("A&B"));
        Assert.Equal("Gator", CleanArtist("Gator (4)"));
        Assert.Equal("Mobil", CleanArtist("Mobil*"));
        Assert.Equal("A", FirstArtist("A feat. B"));
    }
}
