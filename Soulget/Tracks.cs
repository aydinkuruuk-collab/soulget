using System.Text;
using System.Text.RegularExpressions;
using static Soulget.Text;

namespace Soulget;

/// <summary>Track names: normalising, de-duplicating, and judging Soulseek search results.
/// Everything here is pure (no network), so it can be tested offline.</summary>
public static class Tracks
{
    // Words that describe an *edition* of the same recording rather than a different
    // piece of music. "Song (Remastered 2011)" and "Song (Radio Edit)" both collapse into "Song".
    public static readonly HashSet<string> EditionWords = new(StringComparer.Ordinal)
    {
        "remaster", "remastered", "remastering", "digital", "digitally", "original",
        "mix", "version", "album", "single", "lp", "radio", "edit", "edited",
        "extended", "short", "long", "full", "length", "clean", "explicit", "main",
        "mono", "stereo", "bonus", "track", "7", "12", "inch", "cut", "ep", "vinyl",
        "cd", "deluxe", "edition", "the", "and", "unreleased", "previously",
    };

    // Words that mark a genuinely different version (a remix, a live take...).
    public static readonly HashSet<string> RemixWords = new(StringComparer.Ordinal)
    {
        "remix", "rmx", "mix", "dub", "rework", "reworked", "edit", "vip", "bootleg",
        "live", "instrumental", "acapella", "cappella", "version", "remake",
        "reconstruction", "interpretation", "remodel", "refix", "flip", "mixed",
        "reprise", "demo", "karaoke", "cover",
    };

    // If a file name contains one of these but the wanted track doesn't, it is probably the wrong version.
    public static readonly HashSet<string> UnwantedInFile = new(StringComparer.Ordinal)
    {
        "remix", "rmx", "live", "instrumental", "acapella", "cappella", "karaoke",
        "cover", "rework", "bootleg", "vip", "remake", "refix", "demo", "rehearsal",
    };

    // Titles that mean nothing on their own; the release title is added to the search.
    public static readonly HashSet<string> GenericTitles = new(StringComparer.Ordinal)
    {
        "intro", "outro", "interlude", "untitled", "untitled track", "skit",
        "hidden track", "bonus track", "prelude", "coda", "reprise",
    };

    static readonly Regex SkipTitles = Re(@"^\W*(silence|data track|video|cd-?rom|multimedia|enhanced|no title)\W*$", ignoreCase: true);
    // Version tags that mean "not audio", e.g. "Dayvan Cowboy (Music Video)".
    static readonly HashSet<string> VideoWords = new(StringComparer.Ordinal) { "video", "videoclip", "clip", "dvd" };

    // Tokens that appear in file names but say nothing about the music.
    public static readonly HashSet<string> NoiseTokens = new(StringComparer.Ordinal)
    {
        "mp3", "flac", "wav", "aiff", "aif", "m4a", "ogg", "opus", "kbps", "kbs",
        "320", "256", "192", "vbr", "cbr", "v0", "v2", "web", "vinyl", "cd", "rip",
        "24bit", "16bit", "44", "48", "96", "khz", "feat", "ft", "featuring", "and",
        "the", "a", "of", "original", "mix", "version", "album", "remastered",
        "remaster", "edit", "radio", "extended", "single",
    };

    public static readonly HashSet<string> Lossless = new(StringComparer.Ordinal) { "flac", "wav", "aiff", "aif", "alac", "ape", "wv" };

    static readonly Regex DiscogsSuffix = Re(@"\s*\(\d+\)$");
    static readonly Regex ArtistSplit = Re(@"\s*(?:,|&|/|=| feat\.? | ft\.? | vs\.? )\s*", ignoreCase: true);

    /// <summary>Remove Discogs' disambiguation suffix: "Gator (4)" -> "Gator", "Mobil*" -> "Mobil".</summary>
    public static string CleanArtist(string name)
    {
        name = DiscogsSuffix.Replace(name.Trim(), "", 1);
        return name.TrimEnd('*').Trim();
    }

    public static List<string> ArtistTokens(string name)
    {
        var t = Tokens(CleanArtist(name));
        var w = t.Where(x => x != "the").ToList();
        return w.Count > 0 ? w : t;
    }

    /// <summary>"A &amp; B", "A feat. B", "A / B", "A = B" -> "A" (used for matching and searching).</summary>
    public static string FirstArtist(string artist) => BeforeFirst(ArtistSplit, artist);

    /// <summary>Discogs durations look like "5:40" or "1:02:03".</summary>
    public static int? ParseDuration(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        int secs = 0;
        foreach (var p in text.Trim().Split(':'))
        {
            if (!int.TryParse(p.Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var n)) return null;
            secs = secs * 60 + n;
        }
        return secs == 0 ? null : secs;
    }

    public record Version(string Base, string Variant, bool IsRemix);

    static readonly Regex TrailingGroup = Re(@"\s*[\(\[]([^\(\)\[\]]+)[\)\]]\s*$");
    static readonly Regex TrailingDash = Re(@"\s+[-–]\s+([^-–]+)$");
    static readonly Regex FourDigits = Re(@"^\d{4}\z");

    /// <summary>Separate a track title into the song name and its version tag. Edition tags
    /// (remastered, radio edit...) are dropped; anything else, like "(Bibio Remix)", is kept.</summary>
    public static Version SplitVersion(string title)
    {
        var bas = title.Trim();
        var variants = new List<string>();
        while (true)
        {
            var m = TrailingGroup.Match(bas);
            if (!m.Success || m.Index == 0) break;
            variants.Insert(0, m.Groups[1].Value.Trim());
            bas = bas[..m.Index].Trim();
        }
        var d = TrailingDash.Match(bas);
        if (d.Success)
        {
            var tail = Tokens(d.Groups[1].Value);
            var tailSet = tail.ToHashSet();
            if (tail.Count > 0 && (tailSet.All(w => EditionWords.Contains(w) || IsDigits(w))
                                   || tailSet.Overlaps(RemixWords)))
            {
                variants.Insert(0, d.Groups[1].Value.Trim());
                bas = bas[..d.Index].Trim();
            }
        }
        var kept = new List<string>();
        foreach (var v in variants)
        {
            var vt = Tokens(v);
            if (vt.Count == 0) continue;
            if (vt.All(w => EditionWords.Contains(w) || FourDigits.IsMatch(w))) continue; // edition only
            kept.Add(v);
        }
        var variant = string.Join(" ", kept);
        var remix = TokenSet(variant).Overlaps(RemixWords);
        return new Version(bas.Length > 0 ? bas : title.Trim(), variant, remix);
    }

    static readonly Regex CodeWord = Re(@"^(untitled|track|part|side|cut)\s*[#(]?\s*[\w.\-]{0,6}\)?\z", ignoreCase: true);
    static readonly Regex AnyDigit = Re(@"\d");

    /// <summary>Titles that say nothing a file name would repeat: "Untitled 1", "Track 3", "005.3", "A1".</summary>
    public static bool IsCodeTitle(string bas)
    {
        var b = bas.Trim();
        if (Tokens(b).Count == 0 || GenericTitles.Contains(Norm(b))) return true;
        if (CodeWord.IsMatch(b)) return true;
        var words = Tokens(b).Where(w => IsAlpha(w) && Len(w) >= 4).ToList();
        return (words.Count == 0 && AnyDigit.IsMatch(b)) || Len(Norm(b)) <= 3;
    }

    /// <summary>Does a file name show this track's place on the record ("A1 - Untitled", "01 ...")?</summary>
    public static bool PositionInName(Track t, string name)
    {
        var toks = Tokens(name);
        if (toks.Count == 0) return false;
        var pos = Norm(t.Position).Replace(" ", "");
        if (Len(pos) >= 2 && !IsDigits(pos) && toks.Contains(pos)) return true;   // "a1", "b2", "aa"
        if (pos.Length > 0 && toks[0] == pos) return true;                        // "A - Untitled", "3 ..."
        var first = toks[0];
        if (IsDigits(first))
        {
            var n = ToNumber(first);
            if (n is null) return false;
            if (t.Index != 0 && n == t.Index) return true;
            return IsDigits(pos) && ToNumber(pos) == n;
        }
        return false;
    }

    /// <summary>Strict check that a folder is this record: every number in the release name must
    /// be there ("Vault Series 26.0" is not "Vault Series 2.0"), and at most one word missing.</summary>
    public static bool ReleaseInPath(string release, HashSet<string> pathTok)
    {
        var rt = TokenSet(release);
        rt.ExceptWith(NoiseTokens);
        if (rt.Count == 0) return false;
        var digits = rt.Where(w => w.EnumerateRunes().Any(Rune.IsDigit)).ToHashSet();
        var words = rt.Except(digits).ToHashSet();
        return digits.IsSubsetOf(pathTok) && words.Count(w => !pathTok.Contains(w)) <= (words.Count >= 3 ? 1 : 0);
    }

    public static Track? MakeTrack(string? artist, string? title, string? release, int year,
                                   string? duration = "", string catno = "", string? position = "", int index = 0)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0 || SkipTitles.IsMatch(title)) return null;
        artist = string.IsNullOrEmpty(artist) ? "" : CleanArtist(artist);
        release ??= "";
        var v = SplitVersion(title);
        if (TokenSet(v.Variant).Overlaps(VideoWords)) return null;
        var generic = GenericTitles.Contains(Norm(v.Base)) || Tokens(v.Base).Count == 0;
        var shown = v.Base + (v.Variant.Length > 0 ? $" ({v.Variant})" : "");
        var t = new Track
        {
            Artist = artist, Title = shown, Base = v.Base, Variant = v.Variant, IsRemix = v.IsRemix,
            Release = release, Year = year, Duration = ParseDuration(duration), Generic = generic,
            Catno = catno, Position = (position ?? "").Trim(), Index = index,
            Code = generic || IsCodeTitle(v.Base),
        };
        // Spaces are ignored so "Hi Scores" and "Hiscores" count as the same song.
        var key = new List<string>
        {
            artist.Length > 0 ? string.Join(" ", ArtistTokens(FirstArtist(artist))) : "",
            Norm(v.Base).Replace(" ", ""), Norm(v.Variant),
        };
        if (generic) key.Add(Norm(release));
        t.Key = string.Join("|", key);
        return t;
    }

    public const string DefaultFileName = "{year} [{catno}] {position} {artist} - {title} ({release})";
    public static readonly string[] FileNameFields = { "artist", "title", "release", "catno", "year", "position" };

    static readonly Regex DigitsDotsDashes = Re(@"^[\d.\-]+\z");
    static readonly Regex DigitRun = Re(@"\d+");

    /// <summary>"3" -> "03" and "1-5" -> "01-05", so track 10 sorts after track 9; "A1" stays "A1".</summary>
    public static string SortablePosition(string pos)
    {
        pos = pos.Trim();
        return DigitsDotsDashes.IsMatch(pos) ? DigitRun.Replace(pos, m => ZFill(m.Value, 2)) : pos;
    }

    /// <summary>Python's str.format with named fields only ({{ and }} for literal braces).</summary>
    public static string FormatTemplate(string template, IReadOnlyDictionary<string, string> values)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{') { sb.Append('{'); i++; continue; }
                int end = template.IndexOf('}', i + 1);
                if (end < 0) throw new FormatException("Single '{' encountered in format string");
                var name = template[(i + 1)..end];
                if (!values.TryGetValue(name, out var v)) throw new FormatException($"'{name}'");
                sb.Append(v);
                i = end;
            }
            else if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}') { sb.Append('}'); i++; continue; }
                throw new FormatException("Single '}' encountered in format string");
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static readonly Regex EmptyBrackets = Re(@"\(\s*\)|\[\s*\]");
    static readonly Regex EdgeDash = Re(@"^\s*-\s+|\s+-\s*$");
    static readonly Regex MultiSpace = Re(@"\s{2,}");
    static readonly Regex BadChars = Re(@"[\\/:*?""<>|\x00-\x1f]");

    /// <summary>File name without extension, e.g. "1996 [PRMT 010] A2 Mould Impression - Broken Monitor (6 Hours)".
    /// Empty parts disappear with their brackets; the release is left out when it is named like the track.</summary>
    public static string FileStem(Track t, string template = DefaultFileName)
    {
        var nr = Norm(t.Release);
        var release = nr == Norm(t.Title) || nr == Norm(t.Base) ? "" : t.Release;
        var name = FormatTemplate(template, new Dictionary<string, string>
        {
            ["artist"] = t.Artist, ["title"] = t.Title, ["release"] = release, ["catno"] = t.Catno,
            ["year"] = t.Year != 0 ? t.Year.ToString() : "", ["position"] = SortablePosition(t.Position),
        });
        name = EmptyBrackets.Replace(name, "");
        name = EdgeDash.Replace(name.Trim(), "");
        name = MultiSpace.Replace(name, " ").Trim();
        name = BadChars.Replace(name, "_").TrimStart('.');
        var cut = Take(name, 180).Trim();
        return cut.Length > 0 ? cut : "untitled";
    }

    static readonly Regex Apostrophes = Re(@"['’`]");

    /// <summary>Search strings to try, most specific first. Retry rounds skip the title-only fallback.</summary>
    public static List<string> SearchQueries(Track t, bool retry = false)
    {
        static string Q(params string[] parts)
        {
            var words = new List<string>();
            foreach (var p in parts)
                // Soulseek clients split file names at apostrophes ("Lenk's" -> lenk, s).
                foreach (var w in Tokens(Apostrophes.Replace(p, " ")))
                    if ((Len(w) > 1 || IsDigits(w)) && !words.Contains(w)) words.Add(w);
            return string.Join(" ", words);
        }

        var fa = FirstArtist(t.Artist);
        var artist = fa.Length > 0 && Norm(fa) != "various" ? string.Join(" ", ArtistTokens(fa)) : "";
        var queries = new List<string>();
        if (t.Generic) queries.Add(Q(artist, t.Release, t.Base, t.Variant));
        queries.Add(Q(artist, t.Base, t.Variant));
        if (!t.Code && !retry && (Tokens(t.Base).Count >= 2 || Len(Norm(t.Base)) >= 6))
            queries.Add(Q(t.Base, t.Variant));          // song name alone (artist still checked in the path)
        if (t.Release.Length > 0)
            queries.Add(Q(artist, t.Release));          // last resort: the whole record
        var outList = new List<string>();
        foreach (var x in queries)
            if (x.Length > 0 && !outList.Contains(x)) outList.Add(x);
        return outList;
    }

    static readonly Regex PathSplit = Re(@"[\\/]");

    public static string FileExt(string path)
    {
        var name = PathSplit.Split(path)[^1];
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? Lower(name[(dot + 1)..]) : "";
    }

    static string NameWithoutExt(string path)
    {
        var name = PathSplit.Split(path)[^1];
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[..dot] : name;
    }

    /// <summary>Null if the file is acceptable, otherwise a short reason it was rejected.</summary>
    public static string? Judge(Track t, Candidate c, int minBitrate, IList<string> formats)
    {
        if (!formats.Contains(c.Ext)) return "format";
        if (!Lossless.Contains(c.Ext))
        {
            var br = c.Bitrate;
            if ((br is null or 0) && c.Duration is > 0 && c.Size > 0)
                br = (int)(c.Size * 8.0 / c.Duration.Value / 1000);
            if (br is null or 0) return "unknown bitrate";
            if (br < minBitrate) return "bitrate too low";
            c.Bitrate = br;
        }
        var parts = PathSplit.Split(c.Path);
        var name = NameWithoutExt(c.Path);
        var nameTok = TokenSet(name);
        var pathTok = TokenSet(string.Join(" ", parts));

        if (!TokenSet(t.Base).IsSubsetOf(nameTok))
        {
            // A code-like title may be missing from the name; accept the file if it sits in
            // this record's folder at this track's position.
            if (!(t.Code && t.Release.Length > 0 && ReleaseInPath(t.Release, pathTok) && PositionInName(t, name)))
                return "title not in file name";
        }
        if (t.Variant.Length > 0)
        {
            var need = TokenSet(t.Variant);
            need.ExceptWith(EditionWords);
            if (!need.IsSubsetOf(nameTok)) return "different version";
        }
        var unwanted = nameTok.Where(UnwantedInFile.Contains).ToHashSet();
        unwanted.ExceptWith(TokenSet(t.Title));
        if (unwanted.Count > 0) return "different version";
        if (t.Artist.Length > 0 && Norm(t.Artist) != "various")
        {
            var at = ArtistTokens(FirstArtist(t.Artist)).ToHashSet();
            var missing = at.Count(w => !pathTok.Contains(w));
            if (missing > 0 && !(at.Count >= 3 && missing == 1)) return "artist not in path";
        }
        if (t.Generic && t.Release.Length > 0)
        {
            var rt = TokenSet(t.Release);
            rt.ExceptWith(NoiseTokens);
            if (rt.Count > 0 && rt.Count(pathTok.Contains) < Math.Max(1, rt.Count / 2)) return "different release";
        }
        if (t.Duration is > 0 && c.Duration is > 0)
        {
            if (c.Duration > t.Duration * 1.6 + 30 || c.Duration < t.Duration * 0.5) return "length doesn't match";
        }
        return null;
    }

    /// <summary>0 = lossless, 1 = lossy. Each tier gets its own number of tries per track.</summary>
    public static int QualityTier(Candidate c) => Lossless.Contains(c.Ext) ? 0 : 1;

    /// <summary>Try order: your format order always wins, then higher bitrate, then the overall score.</summary>
    public static (int, int, double) RankKey(Candidate c, IList<string> formats)
    {
        var lossyBitrate = Lossless.Contains(c.Ext) ? 0 : -((c.Bitrate ?? 0) / 32);
        return (formats.IndexOf(c.Ext), lossyBitrate, -c.Score);
    }

    /// <summary>Higher is better: format order, free upload slots, fast users, short queues,
    /// clean file names and a matching length.</summary>
    public static double Score(Track t, Candidate c, int minBitrate, IList<string> formats)
    {
        double s = 0;
        s -= 10 * formats.IndexOf(c.Ext);
        s += c.FreeSlot ? 20 : 0;
        s += Math.Min(c.Speed / 100_000.0, 10);
        s -= Math.Min(c.Queue, 20) * 0.5;
        var extra = TokenSet(NameWithoutExt(c.Path));
        extra.ExceptWith(TokenSet(t.Title));
        extra.ExceptWith(TokenSet(t.Artist));
        extra.ExceptWith(NoiseTokens);
        s -= 2 * extra.Count(w => !IsDigits(w));
        if (c.Bitrate is > 0 && !Lossless.Contains(c.Ext))
            s += Math.Min((c.Bitrate.Value - minBitrate) / 64.0, 3);
        if (t.Duration is > 0 && c.Duration is > 0)
            s -= Math.Min(Math.Abs(c.Duration.Value - t.Duration.Value) / 10.0, 10);
        return s;
    }
}

public class Track
{
    public string Artist = "";          // main artist, Discogs suffix removed
    public string Title = "";           // full display title incl. variant, e.g. "Song (X Remix)"
    public string Base = "";            // song name without version tag
    public string Variant = "";         // "" or e.g. "X Remix"
    public bool IsRemix;
    public string Release = "";         // release title it was first found on
    public int Year;
    public int? Duration;               // seconds, if Discogs knows it
    public bool Generic;                // title like "Intro" -> search with release title
    public string Key = "";
    public string Catno = "";           // catalogue number of that release, e.g. "PRMT 010"
    public string Position = "";        // where it sits on that release, e.g. "A1" or "3"
    public int Index;                   // its place in that release's track list: 1, 2, 3...
    public bool Code;                   // title is a code ("Untitled 1", "005.3"): also search by release
    public List<string> AlsoOn = new();

    public string Display() => $"{Artist} - {Title}";
}

/// <summary>Keeps the first edition of each track and remembers where else it appeared.</summary>
public class TrackList
{
    readonly Dictionary<string, Track> byKey = new(StringComparer.Ordinal);
    readonly List<Track> order = new();   // first-seen order, like a Python dict
    public int Duplicates;

    public void Add(Track? t)
    {
        if (t is null) return;
        if (!byKey.TryGetValue(t.Key, out var old))
        {
            byKey[t.Key] = t;
            order.Add(t);
            return;
        }
        Duplicates++;
        if (old.Duration is null && t.Duration is > 0) old.Duration = t.Duration;
        if (t.Year != 0 && (old.Year == 0 || t.Year < old.Year))
        {
            // Keep the earliest release as "where it is from".
            old.AlsoOn.Remove(t.Release);
            old.AlsoOn.Add(old.Release);
            (old.Release, old.Year, old.Catno, old.Position) = (t.Release, t.Year, t.Catno, t.Position);
            old.Index = t.Index;
        }
        else if (t.Release != old.Release && !old.AlsoOn.Contains(t.Release))
            old.AlsoOn.Add(t.Release);
    }

    /// <summary>Sorted like Python: (year or 9999, release.lower(), title.lower()), stable.</summary>
    public List<Track> All() => order
        .OrderBy(t => t.Year != 0 ? t.Year : 9999)
        .ThenBy(t => Text.Lower(t.Release), PyStringComparer.Instance)
        .ThenBy(t => Text.Lower(t.Title), PyStringComparer.Instance)
        .ToList();
}

/// <summary>Python compares strings by code point; .NET's ordinal compare uses UTF-16 units.</summary>
public sealed class PyStringComparer : IComparer<string>
{
    public static readonly PyStringComparer Instance = new();
    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var ea = a.EnumerateRunes().GetEnumerator();
        var eb = b.EnumerateRunes().GetEnumerator();
        while (true)
        {
            bool ha = ea.MoveNext(), hb = eb.MoveNext();
            if (!ha || !hb) return ha == hb ? 0 : (ha ? 1 : -1);
            int d = ea.Current.Value.CompareTo(eb.Current.Value);
            if (d != 0) return d;
        }
    }
}

public class Candidate
{
    public string Username = "";
    public string Path = "";
    public long Size;
    public string Ext = "";
    public int? Bitrate;
    public int? Duration;
    public bool FreeSlot;
    public int Speed;
    public int Queue;
    public double Score;

    public Candidate Copy() => (Candidate)MemberwiseClone();
}
