using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Soulget.Text;

namespace Soulget;

public class DiscogsError(string message) : Exception(message);

/// <summary>An artist, label or record found on Discogs. Others: close matches, shown so a wrong guess can be corrected.</summary>
public record Found(string Kind, int Id, string Name, string Url, List<string> Others);

/// <summary>Reading JSON the way the Python version did (missing, null, 0, "" and [] all count as "nothing").</summary>
public static class J
{
    public static JsonNode? Get(JsonNode? n, string key) => n is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    /// <summary>A number, whether it was read from a file or put there by this program (the two are stored
    /// differently, and only reading via its JSON text handles both).</summary>
    static double? Number(JsonValue v) =>
        v.GetValueKind() == JsonValueKind.Number &&
        double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.Number => Number(v) != 0,
            _ => true,
        },
        _ => true,
    };

    public static string Str(JsonNode? n, string key, string dflt = "")
    {
        var v = Get(n, key);
        if (v is null) return dflt;
        return v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String ? jv.GetValue<string>() : v.ToJsonString();
    }

    public static int Int(JsonNode? n) => n switch
    {
        JsonValue v when Number(v) is { } d => (int)d,
        JsonValue v when v.GetValueKind() == JsonValueKind.String && int.TryParse(v.GetValue<string>(), out var i) => i,
        _ => 0,
    };

    public static int Int(JsonNode? n, string key) => Int(Get(n, key));

    public static JsonArray Arr(JsonNode? n, string key) => Get(n, key) as JsonArray ?? new JsonArray();

    public static IEnumerable<JsonNode> Items(JsonArray a) => a.Where(x => x is not null).Select(x => x!);
}

/// <summary>Discogs: find an artist or label, and build its de-duplicated track list. Works without an
/// account (25 requests a minute); a free personal token allows 60. Every answer is cached on disk.</summary>
public class Discogs
{
    const string Api = "https://api.discogs.com";
    const string UserAgent = "soulget/1.0 +https://github.com/";
    const double Day = 24 * 3600;

    static readonly Regex SkipFormats = Re(@"\b(DVD|VHS|Blu-ray|Betamax|Laserdisc)\b", ignoreCase: true);
    static readonly string[] FormatPreference = { "File", "CD", "Vinyl" }; // most complete track lists first

    readonly HttpClient http;
    double interval;
    double last;
    readonly string cache;
    readonly Action<string> say;

    public Discogs(string token, string cacheDir, Action<string>? say = null)
    {
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Discogs token={token}");
        interval = 60.0 / (string.IsNullOrEmpty(token) ? 25 : 60) + 0.1;
        cache = cacheDir;
        Directory.CreateDirectory(cache);
        this.say = say ?? Console.WriteLine;
    }

    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // -- low level -------------------------------------------------------------

    /// <summary>Python's urllib.parse.quote_plus, so cached answers are shared with the Python version.</summary>
    static string QuotePlus(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            char c = (char)b;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or '-' or '~') sb.Append(c);
            else if (c == ' ') sb.Append('+');
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    public static string Url(string path, IDictionary<string, object>? query = null)
    {
        if (query is null || query.Count == 0) return Api + path;
        var parts = query.OrderBy(kv => kv.Key, PyStringComparer.Instance)
                         .Select(kv => QuotePlus(kv.Key) + "=" + QuotePlus(Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture) ?? ""));
        return Api + path + "?" + string.Join("&", parts);
    }

    string CacheFile(string url) =>
        Path.Combine(cache, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".json");

    public bool IsCached(string path, IDictionary<string, object>? query = null) => File.Exists(CacheFile(Url(path, query)));

    public virtual JsonNode? Get(string path, IDictionary<string, object>? query = null, double? maxAge = null)
    {
        var url = Url(path, query);
        var cf = CacheFile(url);
        if (File.Exists(cf) && (maxAge is null ||
                                (DateTime.UtcNow - File.GetLastWriteTimeUtc(cf)).TotalSeconds < maxAge))
            return JsonNode.Parse(File.ReadAllText(cf));

        for (int attempt = 0; attempt < 6; attempt++)
        {
            var wait = last + interval - Now();
            if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
            last = Now();
            HttpResponseMessage r;
            try
            {
                r = http.GetAsync(url).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                if (attempt == 5) throw new DiscogsError($"could not reach Discogs ({e.Message})");
                Thread.Sleep(5000);
                continue;
            }
            if (r.Headers.TryGetValues("X-Discogs-Ratelimit", out var vals) &&
                int.TryParse(vals.FirstOrDefault(), out var limit) && limit > 0)
                interval = 60.0 / limit + 0.1;
            var code = (int)r.StatusCode;
            if (code == 429)
            {
                say("  Discogs asked us to slow down; waiting a minute...");
                Thread.Sleep(60_000);
                continue;
            }
            if (r.StatusCode == HttpStatusCode.NotFound) return null;
            if (code is 401 or 403)
                throw new DiscogsError(
                    "Discogs refused the request without a token. Get a free one at " +
                    "https://www.discogs.com/settings/developers ('Generate new token') " +
                    "and paste it into config.toml, or paste the Discogs page URL " +
                    "of the artist/label instead of its name.");
            if (code >= 500) { Thread.Sleep(5000); continue; }
            if (!r.IsSuccessStatusCode) throw new DiscogsError($"Discogs answered {code} for {url}");
            var body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var data = JsonNode.Parse(body);
            File.WriteAllText(cf, body);
            return data;
        }
        throw new DiscogsError($"Discogs kept failing for {url}");
    }

    public List<JsonNode> Pages(string path, string key, Dictionary<string, object> query)
    {
        var outList = new List<JsonNode>();
        int page = 1;
        while (true)
        {
            var q = new Dictionary<string, object>(query) { ["page"] = page, ["per_page"] = 100 };
            var data = Get(path, q, maxAge: Day);
            if (!J.Truthy(data)) break;
            outList.AddRange(J.Items(J.Arr(data, key)));
            var pag = J.Get(data, "pagination");
            var pages = J.Get(pag, "pages") is null ? 1 : J.Int(pag, "pages");
            if (page >= pages) break;
            page++;
            say($"  listing releases... page {page} of {J.Str(pag, "pages", "None")}");
        }
        return outList;
    }

    // -- finding the artist / label / record -------------------------------------

    static readonly Regex EntityUrl = Re(@"discogs\.com/(?:[a-z]{2}/)?(artist|label)/(\d+)");
    static readonly Regex RecordUrl = Re(@"discogs\.com/(?:[a-z]{2}/)?(master|release)/(\d+)");

    public Found Find(string kind, string text)
    {
        var m = EntityUrl.Match(text);
        if (m.Success)
        {
            kind = m.Groups[1].Value;
            var id = int.Parse(m.Groups[2].Value);
            var data = Get($"/{kind}s/{id}", maxAge: 30 * Day);
            if (!J.Truthy(data)) throw new DiscogsError($"Discogs has no {kind} with id {id}");
            return new Found(kind, id, Tracks.CleanArtist(J.Str(data, "name")), $"https://www.discogs.com/{kind}/{id}", new());
        }
        var res = Get("/database/search", new Dictionary<string, object> { ["q"] = text, ["type"] = kind, ["per_page"] = 10 }, maxAge: 7 * Day);
        var results = J.Items(J.Arr(res, "results")).ToList();
        if (results.Count == 0) throw new DiscogsError($"Discogs found no {kind} called '{text}'");
        var want = Norm(text);
        var choice = results.FirstOrDefault(r => Norm(Tracks.CleanArtist(J.Str(r, "title"))) == want) ?? results[0];
        var others = results.Where(r => !ReferenceEquals(r, choice))
                            .Select(r => $"{J.Str(r, "title")}  https://www.discogs.com{J.Str(r, "uri")}").Take(4).ToList();
        return new Found(kind, J.Int(choice, "id"), Tracks.CleanArtist(J.Str(choice, "title")),
                         $"https://www.discogs.com{J.Str(choice, "uri")}", others);
    }

    /// <summary>One album, EP or single: a Discogs master/release link, or "Artist - Title".
    /// A "master" groups every edition of a record, so it is tried first.</summary>
    public Found FindRelease(string text)
    {
        var m = RecordUrl.Match(text);
        if (m.Success)
        {
            var kind = m.Groups[1].Value;
            var id = int.Parse(m.Groups[2].Value);
            var data = Get($"/{kind}s/{id}");
            if (!J.Truthy(data)) throw new DiscogsError($"Discogs has no {kind} with id {id}");
            var name = $"{ArtistString(J.Arr(data, "artists"))} - {J.Str(data, "title")}";
            return new Found(kind, id, name, $"https://www.discogs.com/{kind}/{id}", new());
        }
        Dictionary<string, object> query;
        var dash = text.IndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0)
            query = new() { ["artist"] = text[..dash].Trim(), ["release_title"] = text[(dash + 3)..].Trim() };
        else
            query = new() { ["q"] = text };
        var results = new List<JsonNode>();
        string k = "master";
        foreach (var kind in new[] { "master", "release" })
        {
            k = kind;
            var data = Get("/database/search", new Dictionary<string, object>(query) { ["type"] = kind, ["per_page"] = 10 }, maxAge: 7 * Day);
            results = J.Items(J.Arr(data, "results")).ToList();
            if (results.Count > 0) break;
        }
        if (results.Count == 0)
            throw new DiscogsError($"Discogs found no record called '{text}'. Write it as " +
                                   "'Artist - Title', or paste the record's Discogs link.");
        var want = Norm(text);
        var choice = results.FirstOrDefault(r => Norm(CleanTitle(J.Str(r, "title"))) == want) ?? results[0];
        var others = results.Where(r => !ReferenceEquals(r, choice))
                            .Select(r => $"{J.Str(r, "title")} ({J.Str(r, "year", "?")})  https://www.discogs.com{J.Str(r, "uri")}")
                            .Take(4).ToList();
        return new Found(k, J.Int(choice, "id"), CleanTitle(J.Str(choice, "title")),
                         $"https://www.discogs.com{J.Str(choice, "uri")}", others);
    }

    /// <summary>Every track on one record (a compilation keeps each track's own artist).</summary>
    public (TrackList, int releasesListed, int releasesRead) ReleaseTracks(Found found)
    {
        var data = Get($"/{found.Kind}s/{found.Id}");
        if (!J.Truthy(data)) throw new DiscogsError($"Discogs has no {found.Kind} with id {found.Id}");
        var tl = new TrackList();
        var relArtists = J.Arr(data, "artists");
        var cat = Catno(data!, found.Kind);
        int idx = 0;
        foreach (var t in FlatTracklist(J.Arr(data, "tracklist")))
        {
            idx++;
            var own = J.Arr(t, "artists");
            tl.Add(Tracks.MakeTrack(ArtistString(own.Count > 0 ? own : relArtists), J.Str(t, "title"),
                                    J.Str(data, "title"), J.Int(data, "year"), J.Str(t, "duration"), cat,
                                    J.Str(t, "position"), idx));
        }
        return (tl, 1, 1);
    }

    /// <summary>Catalogue number, e.g. "PRMT 010". A master has none of its own, so its main release
    /// (usually the first edition) is used. On a label run, that label's own number is preferred.</summary>
    public string Catno(JsonNode data, string kind, int? labelId = null)
    {
        JsonNode? d = data;
        if (kind == "master")
        {
            var main = J.Get(data, "main_release");
            d = J.Truthy(main) ? Get($"/releases/{J.Int(main)}") : null;
            if (!J.Truthy(d)) return "";
        }
        var labels = J.Items(J.Arr(d, "labels")).ToList();
        JsonNode? pick = labels.FirstOrDefault(lb => labelId is > 0 && J.Int(lb, "id") == labelId)
                         ?? labels.FirstOrDefault();
        var cat = (J.Truthy(J.Get(pick, "catno")) ? J.Str(pick, "catno") : "").Trim();
        return Lower(cat) is "none" or "n/a" or "-" ? "" : cat;
    }

    // -- track lists -------------------------------------------------------------

    List<(JsonNode data, JsonNode item)> FetchAll(List<(string path, JsonNode item)> jobs, string what)
    {
        var todo = jobs.Count(j => !IsCached(j.path));
        if (todo > 0)
        {
            var mins = todo * interval / 60;
            say($"Reading {jobs.Count} {what} pages from Discogs ({todo} not cached yet, about {Math.Max(1, Math.Round(mins))} min).");
            if (mins > 20 && !Console.IsInputRedirected)
            {
                var hint = interval < 1.5 ? "" : " A Discogs token in config.toml makes it about twice as fast.";
                Console.Write($"  That is a long wait.{hint} Continue? [y/N] ");
                var ans = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                if (ans is not ("y" or "yes")) throw new DiscogsError("stopped by you before reading the tracklists");
            }
        }
        var outList = new List<(JsonNode, JsonNode)>();
        int i = 0;
        foreach (var (path, item) in jobs)
        {
            i++;
            var data = Get(path);
            if (J.Truthy(data)) outList.Add((data!, item));
            if (!Console.IsOutputRedirected) Console.Write($"\r  pages read: {i}/{jobs.Count}");
        }
        if (!Console.IsOutputRedirected && jobs.Count > 0) Console.WriteLine();
        return outList;
    }

    static bool InYears((int, int)? years, JsonNode it)
    {
        var y = J.Int(it, "year");
        return years is null || y == 0 || (years.Value.Item1 <= y && y <= years.Value.Item2);
    }

    public (TrackList, int releasesListed, int releasesRead) ArtistTracks(Found found, (int, int)? years = null,
                                                                           bool compilations = true, bool theirRemixes = false)
    {
        var items = Pages($"/artists/{found.Id}/releases", "releases",
                          new() { ["sort"] = "year", ["sort_order"] = "asc" });
        var roles = new HashSet<string> { "Main" };
        if (compilations) roles.Add("Appearance");
        if (theirRemixes) roles.Add("Remix");
        var jobs = new List<(string, JsonNode)>();
        var seen = new HashSet<string>();
        foreach (var it in items)
        {
            if (!roles.Contains(J.Str(it, "role", "\0"))) continue;
            var fmt = J.Str(it, "format");
            if (SkipFormats.IsMatch(fmt) || fmt.Contains("Unofficial") || J.Str(it, "trackinfo").Contains("Unofficial")) continue;
            if (!InYears(years, it)) continue;
            var path = $"/{J.Str(it, "type")}s/{J.Int(it, "id")}";
            if (!seen.Add(path)) continue;
            jobs.Add((path, it));
        }
        var fetched = FetchAll(jobs, "release");
        // Masters carry no catalogue number; read each one's main release for it.
        var mains = fetched.Where(f => J.Str(f.item, "type") == "master" && J.Truthy(J.Get(f.data, "main_release")))
                           .Select(f => ($"/releases/{J.Int(f.data, "main_release")}", f.item)).ToList();
        FetchAll(mains, "first-edition (for catalogue numbers)");

        var tl = new TrackList();
        foreach (var (data, it) in fetched)
        {
            var relArtists = J.Arr(data, "artists");
            var year = J.Int(data, "year");
            if (year == 0) year = J.Int(it, "year");
            var cat = Catno(data, J.Str(it, "type"));
            var role = J.Str(it, "role");
            int idx = 0;
            foreach (var t in FlatTracklist(J.Arr(data, "tracklist")))
            {
                idx++;
                var own = J.Arr(t, "artists");
                var ids = J.Items(own.Count > 0 ? own : relArtists).Select(a => J.Int(a, "id")).ToHashSet();
                bool keep = role switch
                {
                    "Main" => ids.Contains(found.Id),
                    "Appearance" => J.Items(own).Any(a => J.Int(a, "id") == found.Id) || (own.Count == 0 && ids.Contains(found.Id)),
                    _ => J.Items(J.Arr(t, "extraartists")).Any(a => J.Int(a, "id") == found.Id &&
                                                                     Lower(J.Str(a, "role")).Contains("remix")),
                };
                if (keep)
                    tl.Add(Tracks.MakeTrack(ArtistString(own.Count > 0 ? own : relArtists), J.Str(t, "title"),
                                            J.Str(data, "title"), year, J.Str(t, "duration"), cat, J.Str(t, "position"), idx));
            }
        }
        return (tl, items.Count, jobs.Count);
    }

    public (TrackList, int releasesListed, int releasesRead) LabelTracks(Found found, (int, int)? years = null, bool compilations = true)
    {
        var items = Pages($"/labels/{found.Id}/releases", "releases", new());
        // The same record on CD, vinyl and download is listed several times; keep one (first-seen order).
        var groupOrder = new List<(string, string)>();
        var groups = new Dictionary<(string, string), JsonNode>();
        foreach (var it in items)
        {
            var fmt = J.Str(it, "format");
            if (SkipFormats.IsMatch(fmt) && !Re(@"\b(CD|Vinyl|File)\b").IsMatch(fmt)) continue;
            if (!compilations && IsCompilation(fmt, J.Str(it, "artist"))) continue;
            if (!InYears(years, it)) continue;
            var k = (Norm(Tracks.CleanArtist(J.Str(it, "artist"))), Norm(J.Str(it, "title")));
            if (!groups.TryGetValue(k, out var have)) { groups[k] = it; groupOrder.Add(k); }
            else if (FormatRank(fmt) < FormatRank(J.Str(have, "format"))) groups[k] = it;
        }
        var jobs = groupOrder.Select(k => groups[k])
                             .OrderBy(it => J.Int(it, "year") is var y && y != 0 ? y : 9999)
                             .Select(it => ($"/releases/{J.Int(it, "id")}", it)).ToList();
        var tl = new TrackList();
        foreach (var (data, it) in FetchAll(jobs, "release"))
        {
            var relArtists = J.Arr(data, "artists");
            var year = J.Int(data, "year");
            if (year == 0) year = J.Int(it, "year");
            var cat = Catno(data, "release", labelId: found.Id);
            int idx = 0;
            foreach (var t in FlatTracklist(J.Arr(data, "tracklist")))
            {
                idx++;
                var own = J.Arr(t, "artists");
                tl.Add(Tracks.MakeTrack(ArtistString(own.Count > 0 ? own : relArtists), J.Str(t, "title"),
                                        J.Str(data, "title"), year, J.Str(t, "duration"), cat, J.Str(t, "position"), idx));
            }
        }
        return (tl, items.Count, jobs.Count);
    }

    // -- helpers -----------------------------------------------------------------

    static readonly Regex Vinyl = Re(@"\b(Vinyl|LP)\b|\b\d*x?(7|10|12)""", ignoreCase: true);
    static readonly Regex Mixed = Re(@"\bMixed\b");
    static readonly Regex Comp = Re(@"\bComp\b");

    /// <summary>What --no-compilations leaves out on a label: DJ-mix CDs, and CD/download compilations
    /// (mostly other labels' tracks licensed in). Vinyl is always kept.</summary>
    public static bool IsCompilation(string fmt, string artist)
    {
        if (Mixed.IsMatch(fmt)) return true;
        if (Vinyl.IsMatch(fmt)) return false;
        return Comp.IsMatch(fmt) || Norm(Tracks.CleanArtist(artist)) == "various";
    }

    static int FormatRank(string fmt)
    {
        for (int i = 0; i < FormatPreference.Length; i++)
            if (Re($@"\b{FormatPreference[i]}\b").IsMatch(fmt)) return i;
        return FormatPreference.Length;
    }

    static readonly Regex NumSuffix = Re(@"\s*\(\d+\)");

    /// <summary>"Gator (4) - Self Titled" -> "Gator - Self Titled" (suffixes only in the artist part).</summary>
    public static string CleanTitle(string title)
    {
        var i = title.IndexOf(" - ", StringComparison.Ordinal);
        if (i < 0) return title;
        var artist = NumSuffix.Replace(title[..i], "").Replace("*", "").Trim();
        return $"{artist} - {title[(i + 3)..]}";
    }

    /// <summary>Drop headings; an "index" track (a suite with sub-parts) counts as one track.</summary>
    static IEnumerable<JsonNode> FlatTracklist(JsonArray tracklist) =>
        J.Items(tracklist).Where(t => J.Get(t, "type_") is null
            ? true
            : J.Str(t, "type_") is "track" or "index");

    public static string ArtistString(JsonArray artists)
    {
        var sb = new StringBuilder();
        bool skip = false;
        foreach (var a in J.Items(artists))
        {
            var j = J.Str(a, "join").Trim();
            if (!skip)
            {
                sb.Append(Tracks.CleanArtist(J.Str(a, "name")));
                if (j.Length > 0 && j != "=") sb.Append(j == "," ? ", " : $" {j} ");
            }
            skip = j == "="; // "A = B": B is A's name in another script (Japanese editions etc.)
        }
        return sb.ToString().Trim(' ', ',');
    }
}
