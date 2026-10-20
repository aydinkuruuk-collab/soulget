// soulget - Copyright (C) 2026 the soulget contributors
// SPDX-License-Identifier: GPL-3.0-only (see LICENSE and NOTICE)

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Soulget;

/// <summary>Per folder: when each missing track was first missed and how often it was checked
/// (.soulget-misses.json, same layout as the Python version).</summary>
public class Misses
{
    public const string FileName = ".soulget-misses.json";
    const string DateFormat = "yyyy-MM-ddTHH:mm:ss";

    readonly string folder;
    public JsonObject Data;

    Misses(string folder, JsonObject data) { this.folder = folder; Data = data; }

    public static Misses Load(string folder)
    {
        var p = Path.Combine(folder, FileName);
        return new Misses(folder, File.Exists(p) ? (JsonObject)JsonNode.Parse(File.ReadAllText(p))! : new JsonObject());
    }

    public void Save() => Files.WriteJson(Path.Combine(folder, FileName), Data);

    /// <summary>Now, to the second (like Python's datetime.now().replace(microsecond=0)).</summary>
    public static DateTime Now()
    {
        var n = DateTime.Now;
        return new DateTime(n.Ticks - n.Ticks % TimeSpan.TicksPerSecond, n.Kind);
    }
    static string Iso(DateTime d) => d.ToString(DateFormat, CultureInfo.InvariantCulture);

    public bool GaveUp(string key, double giveUpDays, DateTime? at = null)
    {
        if (Data[key] is not JsonObject e) return false;
        var first = DateTime.Parse(J.Str(e, "first_missed"), CultureInfo.InvariantCulture);
        return (at ?? Now()) - first > TimeSpan.FromDays(giveUpDays);
    }

    /// <summary>Put a track on the list for the next check without searching now.</summary>
    public void Queue(string key, string name = "", DateTime? at = null)
    {
        if (Data[key] is not JsonObject e)
        {
            e = new JsonObject { ["first_missed"] = Iso(at ?? Now()), ["checks"] = 0, ["note"] = "waiting for the first check" };
            Data[key] = e;
        }
        if (name.Length > 0) e["name"] = name;
    }

    public void Record(string key, bool found, string note = "", DateTime? at = null, string name = "")
    {
        var when = at ?? Now();
        if (found) { Data.Remove(key); return; }
        if (Data[key] is not JsonObject e)
        {
            e = new JsonObject { ["first_missed"] = Iso(when), ["checks"] = 0 };
            Data[key] = e;
        }
        e["checks"] = J.Int(e, "checks") + 1;
        e["last_checked"] = Iso(when);
        e["note"] = note;
        if (name.Length > 0) e["name"] = name;
    }
}

/// <summary>The record of what was downloaded into a folder: key -> {"file": ..., "from": ...}
/// (.soulget-downloaded.json).</summary>
public static class Manifest
{
    public const string FileName = ".soulget-downloaded.json";

    public static JsonObject Load(string folder)
    {
        var p = Path.Combine(folder, FileName);
        return File.Exists(p) ? (JsonObject)JsonNode.Parse(File.ReadAllText(p))! : new JsonObject();
    }

    public static void Save(string folder, JsonObject manifest) => Files.WriteJson(Path.Combine(folder, FileName), manifest);
}

public class Locked(string message) : Exception(message);

/// <summary>Only one soulget at a time (Python or this one): two would log in with the same name and
/// kick each other off. The lock file holds the running program's process number.</summary>
public sealed class RunLock(string root) : IDisposable
{
    public const string FileName = ".soulget.lock";
    readonly string path = Path.Combine(root, FileName);
    bool held;

    public RunLock Enter()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var pid)
            && Native.ProcessAlive(pid) && pid != Environment.ProcessId)
            throw new Locked($"another soulget is already running (process {pid})");
        File.WriteAllText(path, Environment.ProcessId.ToString());
        held = true;
        return this;
    }

    public void Dispose()
    {
        if (!held) return;
        try
        {
            if (File.ReadAllText(path).Trim() == Environment.ProcessId.ToString()) File.Delete(path);
        }
        catch (IOException) { }
        held = false;
    }
}

/// <summary>Runs waiting for the lock take turns in the order they started waiting. Tickets are files
/// named "{nanoseconds}-{process}" (same as the Python version, so both share one line).</summary>
public class WaitQueue(string root)
{
    public const string DirName = ".soulget-waiting";
    readonly string dir = Path.Combine(root, DirName);

    public List<string> Live()
    {
        if (!Directory.Exists(dir)) return new();
        var live = new List<string>();
        foreach (var ticket in Directory.GetFiles(dir).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var parts = Path.GetFileName(ticket).Split('-');
            if (parts.Length >= 2 && int.TryParse(parts[1], out var pid) && Native.ProcessAlive(pid)) live.Add(ticket);
            else try { File.Delete(ticket); } catch (IOException) { }   // its run is gone
        }
        return live;
    }

    public string Join()
    {
        Directory.CreateDirectory(dir);
        var ns = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;
        var ticket = Path.Combine(dir, $"{ns}-{Environment.ProcessId}");
        File.WriteAllText(ticket, "");
        return ticket;
    }

    public bool First(string ticket) => Live() is { Count: > 0 } live && live[0] == ticket;

    public void Leave(string ticket) { try { File.Delete(ticket); } catch (IOException) { } }

    /// <summary>Take the run lock, waiting in line (first come, first served) while another download or
    /// timer check holds it or other runs are already waiting.</summary>
    public static RunLock WaitForLock(string root, string what)
    {
        var line = new WaitQueue(root);
        var reason = "other runs are waiting first";
        if (line.Live().Count == 0)
        {
            try { return new RunLock(root).Enter(); }
            catch (Locked e) { reason = e.Message; }
        }
        var ticket = line.Join();
        Console.WriteLine($"\nWaiting: {reason}. {what} starts when it's its turn (Ctrl+C to cancel).");
        try
        {
            while (true)
            {
                if (line.First(ticket))
                {
                    try { return new RunLock(root).Enter(); }
                    catch (Locked) { }
                }
                Thread.Sleep(5000);
            }
        }
        finally { line.Leave(ticket); }
    }
}

/// <summary>Small operating-system calls: is a process alive, and how many files may be open.</summary>
public static class Native
{
    [DllImport("libc", SetLastError = true)] static extern int kill(int pid, int sig);
    [DllImport("libc", SetLastError = true)] static extern int getrlimit(int resource, out RLimit rlim);
    [DllImport("libc", SetLastError = true)] static extern int setrlimit(int resource, ref RLimit rlim);

    [StructLayout(LayoutKind.Sequential)]
    struct RLimit { public ulong Cur; public ulong Max; }

    static int RlimitNofile => OperatingSystem.IsMacOS() ? 8 : 7;
    static ulong RlimInfinity => OperatingSystem.IsMacOS() ? (1UL << 63) - 1 : ulong.MaxValue;

    public static bool ProcessAlive(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
            catch (ArgumentException) { return false; }
        }
        if (kill(pid, 0) == 0) return true;
        return Marshal.GetLastPInvokeError() == 1;   // EPERM: it exists, but belongs to someone else
    }

    /// <summary>Programs started by the macOS timer may only keep 256 files and network connections
    /// open; a long session with many Soulseek peers needs more.</summary>
    public static long RaiseOpenFileLimit(ulong target = 10240)
    {
        if (OperatingSystem.IsWindows()) return -1;
        try
        {
            if (getrlimit(RlimitNofile, out var r) != 0) return -1;
            var want = r.Max == RlimInfinity ? target : Math.Min(target, r.Max);
            if (r.Cur < want)
            {
                var n = new RLimit { Cur = want, Max = r.Max };
                setrlimit(RlimitNofile, ref n);
            }
            return OpenFileLimit();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return -1; }
    }

    public static long OpenFileLimit()
    {
        if (OperatingSystem.IsWindows()) return -1;
        try { return getrlimit(RlimitNofile, out var r) == 0 ? (long)Math.Min(r.Cur, long.MaxValue) : -1; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return -1; }
    }

    public static int OpenFiles()
    {
        try { return Directory.GetFileSystemEntries(OperatingSystem.IsLinux() ? "/proc/self/fd" : "/dev/fd").Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    public static bool OpenFilesNearlyUsedUp(double share = 0.8)
    {
        var limit = OpenFileLimit();
        return limit > 0 && OpenFiles() > share * limit;
    }
}
