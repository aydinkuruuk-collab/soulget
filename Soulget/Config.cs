using System.Reflection;
using Tomlyn;
using Tomlyn.Model;

namespace Soulget;

public class DriveMissing(string message) : Exception(message);

/// <summary>config.toml, read the same way as the Python version (same file, same defaults).</summary>
public class Config
{
    /// <summary>Where config.toml, the Discogs cache and the timer log live: next to the program,
    /// or SOULGET_HOME if set (handy while developing).</summary>
    /// <summary>Set by --home (the Windows and Linux timers pass it).</summary>
    public static string? HomeOverride;

    public static string Home
    {
        get
        {
            if (!string.IsNullOrEmpty(HomeOverride)) return Expand(HomeOverride);
            var env = Environment.GetEnvironmentVariable("SOULGET_HOME");
            if (!string.IsNullOrEmpty(env)) return Expand(env);
            return Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        }
    }

    public static string ConfigPath => Path.Combine(Home, "config.toml");
    public static string CacheDir => Path.Combine(Home, ".cache", "discogs");

    readonly TomlTable root;
    Config(TomlTable root) => this.root = root;

    public static Config Load()
    {
        if (!File.Exists(ConfigPath))
        {
            File.WriteAllText(ConfigPath, ExampleConfig());
            Console.WriteLine($"Created {ConfigPath} - put your Soulseek username and password in it.\n");
        }
        return Parse(File.ReadAllText(ConfigPath));
    }

    public static Config Parse(string toml) => new(TomlSerializer.Deserialize<TomlTable>(toml, (TomlSerializerOptions?)null) ?? new TomlTable());

    /// <summary>The example settings file is built into the program, so a single file is all you need.</summary>
    public static string ExampleConfig()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Soulget.config.example.toml")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    TomlTable Section(string name) => root.TryGetValue(name, out var v) && v is TomlTable t ? t : new TomlTable();

    public object? Get(string section, string key) => Section(section).TryGetValue(key, out var v) ? v : null;

    public string Str(string section, string key, string dflt = "") =>
        Get(section, key) is { } v ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? dflt : dflt;

    public double Num(string section, string key, double dflt) => Get(section, key) switch
    {
        long l => l,
        double d => d,
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ => dflt,
    };

    public int Int(string section, string key, int dflt) => (int)Num(section, key, dflt);

    public List<string> List(string section, string key, List<string> dflt) =>
        Get(section, key) is TomlArray a ? a.Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToList() : dflt;

    public string DiscogsToken => Str("discogs", "token");

    /// <summary>The music folder. If it is on an external drive that isn't connected, stop instead
    /// of writing anywhere else.</summary>
    public string DownloadRoot()
    {
        var root = Expand(Str("soulseek", "download_folder", "~/Music/soulget"));
        var parts = root.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (OperatingSystem.IsMacOS() && root.StartsWith("/Volumes/") && parts.Length >= 2)
        {
            var mount = "/Volumes/" + parts[1];
            if (!DriveInfo.GetDrives().Any(d => d.RootDirectory.FullName.TrimEnd('/') == mount))
                throw new DriveMissing($"the drive '{parts[1]}' isn't connected");
        }
        else if (OperatingSystem.IsWindows())
        {
            var drive = Path.GetPathRoot(root);
            if (!string.IsNullOrEmpty(drive) && !Directory.Exists(drive))
                throw new DriveMissing($"the drive '{drive}' isn't connected");
        }
        return root;
    }

    public static string Expand(string path) =>
        path == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : path.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
        : path;
}
