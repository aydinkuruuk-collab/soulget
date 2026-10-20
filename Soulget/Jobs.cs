using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soulget;

/// <summary>Everything needed to rebuild a folder's track list and search the same way again
/// (.soulget-job.json, same layout as the Python version).</summary>
public class Job
{
    public const string FileName = ".soulget-job.json";

    public string Kind = "";                 // "artist", "label", "master"/"release" (one record) or "tracks"
    public int Id;                           // Discogs id (0 for "tracks")
    public string Name = "";
    public string Url = "";
    public int MinBitrate = 320;
    public List<string> Formats = new();
    public bool NoRemixes;
    public bool NoCompilations;
    public bool TheirRemixes;
    public bool Strict;                      // artist name shared with others: files must prove they're this artist's
    public List<int>? Years;
    public List<List<string>>? TrackPairs;   // kind "tracks": [artist, title] pairs you asked for

    public static Job? Load(string folder)
    {
        var p = Path.Combine(folder, FileName);
        if (!File.Exists(p)) return null;
        var o = JsonNode.Parse(File.ReadAllText(p))!;
        return new Job
        {
            Kind = J.Str(o, "kind"), Id = J.Int(o, "id"), Name = J.Str(o, "name"), Url = J.Str(o, "url"),
            MinBitrate = J.Int(o, "min_bitrate"),
            Formats = J.Items(J.Arr(o, "formats")).Select(x => x.GetValue<string>()).ToList(),
            NoRemixes = J.Truthy(J.Get(o, "no_remixes")),
            NoCompilations = J.Truthy(J.Get(o, "no_compilations")),
            TheirRemixes = J.Truthy(J.Get(o, "their_remixes")),
            Strict = J.Truthy(J.Get(o, "strict")),   // older files without it: false
            Years = J.Get(o, "years") is JsonArray y ? J.Items(y).Select(J.Int).ToList() : null,
            TrackPairs = J.Get(o, "tracks") is JsonArray tr
                ? J.Items(tr).Select(p => J.Items((JsonArray)p).Select(x => x.GetValue<string>()).ToList()).ToList()
                : null,
        };
    }

    public void Save(string folder)
    {
        var o = new JsonObject
        {
            ["kind"] = Kind, ["id"] = Id, ["name"] = Name, ["url"] = Url, ["min_bitrate"] = MinBitrate,
            ["formats"] = new JsonArray(Formats.Select(f => (JsonNode)f).ToArray()),
            ["no_remixes"] = NoRemixes, ["no_compilations"] = NoCompilations, ["their_remixes"] = TheirRemixes,
            ["strict"] = Strict,
            ["years"] = Years is null ? null : new JsonArray(Years.Select(y => (JsonNode)y).ToArray()),
            ["tracks"] = TrackPairs is null ? null
                : new JsonArray(TrackPairs.Select(p => (JsonNode)new JsonArray(p.Select(x => (JsonNode)x).ToArray())).ToArray()),
        };
        Files.WriteJson(Path.Combine(folder, FileName), o);
    }
}

public static class Files
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, IndentSize = 1 };

    /// <summary>Write JSON via a temporary file, so a crash never leaves half a file behind.</summary>
    public static void WriteJson(string path, JsonNode node)
    {
        var tmp = Path.ChangeExtension(path, ".tmp");
        File.WriteAllText(tmp, node.ToJsonString(Pretty));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>A CSV writer that produces exactly what Python's csv module writes (\r\n, minimal quoting).</summary>
    public static void WriteCsv(string path, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(string.Join(",", row.Select(Field)));
            sb.Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Add rows to the end of a CSV file (Python's csv writer in append mode).</summary>
    public static void AppendCsv(string path, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows) sb.Append(string.Join(",", row.Select(Field))).Append("\r\n");
        File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    static string Field(object? v)
    {
        var s = v switch { null => "", bool b => b ? "True" : "False", _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" };
        return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
