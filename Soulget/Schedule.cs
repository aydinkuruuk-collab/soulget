// soulget - Copyright (C) 2026 the soulget contributors
// SPDX-License-Identifier: GPL-3.0-only (see LICENSE and NOTICE)

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Soulget;

/// <summary>The timer that runs --retry-missing every few hours:
/// on a Mac a launchd agent (same name as the Python version used, com.soulget.retry, so there is only
/// ever one timer); on Windows a Task Scheduler task; on Linux a systemd user timer.</summary>
public static class Schedule
{
    public const string Label = "com.soulget.retry";
    const string WindowsTask = "soulget retry";
    const string LinuxUnit = "soulget-retry";

    static string HomeDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string PlistPath = Path.Combine(HomeDir, "Library", "LaunchAgents", Label + ".plist");
    public static string SystemdDir = Path.Combine(HomeDir, ".config", "systemd", "user");

    public record State(bool Loaded, double IntervalSeconds, string LogPath, string Program);

    static (int Code, string Out) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, o.Trim());
        }
        catch (System.ComponentModel.Win32Exception e) { return (-1, e.Message); }   // program not installed
    }

    static string E(string s) => SecurityElement.Escape(s) ?? "";

    /// <summary>Where a timed check writes what it did.</summary>
    public static string LogPath => Path.Combine(Config.Home, "retry-output.log");

    // -- Mac: launchd ---------------------------------------------------------------

    [DllImport("libc")] static extern uint getuid();
    static string Domain => $"gui/{getuid()}";

    /// <summary>The launchd job description. SOULGET_HOME tells the timed run where config.toml is.</summary>
    public static string PlistXml(string program, double everyHours, string logPath, string home) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
        	<key>EnvironmentVariables</key>
        	<dict>
        		<key>SOULGET_HOME</key>
        		<string>{E(home)}</string>
        	</dict>
        	<key>Label</key>
        	<string>{Label}</string>
        	<key>LowPriorityIO</key>
        	<true/>
        	<key>ProcessType</key>
        	<string>Background</string>
        	<key>ProgramArguments</key>
        	<array>
        		<string>{E(program)}</string>
        		<string>--retry-missing</string>
        		<string>--scheduled</string>
        	</array>
        	<key>RunAtLoad</key>
        	<false/>
        	<key>StandardErrorPath</key>
        	<string>{E(logPath)}</string>
        	<key>StandardOutPath</key>
        	<string>{E(logPath)}</string>
        	<key>StartInterval</key>
        	<integer>{(int)(everyHours * 3600)}</integer>
        </dict>
        </plist>

        """;

    static State? MacStatus()
    {
        if (!File.Exists(PlistPath)) return null;
        var els = XDocument.Load(PlistPath).Root!.Element("dict")!.Elements().ToList();
        XElement? Value(string key) => els.SkipWhile(e => !(e.Name == "key" && e.Value == key)).Skip(1).FirstOrDefault();
        double.TryParse(Value("StartInterval")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var interval);
        var program = Value("ProgramArguments")?.Elements("string").FirstOrDefault()?.Value ?? "";
        return new State(Run("launchctl", "print", $"{Domain}/{Label}").Code == 0, interval, Value("StandardOutPath")?.Value ?? "", program);
    }

    // -- Windows: Task Scheduler ----------------------------------------------------

    /// <summary>The scheduled task, as Task Scheduler XML. "conhost --headless" runs it without a window;
    /// a check missed while the PC was off runs when it can; a check is stopped after 6 hours so a stuck
    /// one never blocks the next.</summary>
    public static string WindowsTaskXml(string program, double everyHours, string home, DateTime firstRun)
    {
        var minutes = (int)Math.Round(everyHours * 60);
        var interval = minutes % 60 == 0 ? $"PT{minutes / 60}H" : $"PT{minutes}M";
        var args = $"--headless \"{program}\" --retry-missing --scheduled --home \"{home}\"";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>soulget: search again for missing tracks (soulget --schedule off removes this)</Description>
              </RegistrationInfo>
              <Triggers>
                <TimeTrigger>
                  <Repetition>
                    <Interval>{interval}</Interval>
                    <StopAtDurationEnd>false</StopAtDurationEnd>
                  </Repetition>
                  <StartBoundary>{firstRun.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}</StartBoundary>
                  <Enabled>true</Enabled>
                </TimeTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT6H</ExecutionTimeLimit>
                <Priority>7</Priority>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>conhost.exe</Command>
                  <Arguments>{E(args)}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    static State? WindowsStatus()
    {
        var (code, xml) = Run("schtasks", "/Query", "/TN", WindowsTask, "/XML");
        if (code != 0 || !xml.Contains("<Task")) return null;
        var doc = XDocument.Parse(xml[xml.IndexOf('<')..]);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var iv = doc.Descendants(ns + "Interval").FirstOrDefault()?.Value ?? "";
        var seconds = iv.StartsWith("PT") ? System.Xml.XmlConvert.ToTimeSpan(iv).TotalSeconds : 0;
        var enabled = doc.Descendants(ns + "Settings").Elements(ns + "Enabled").FirstOrDefault()?.Value != "false";
        var arguments = doc.Descendants(ns + "Arguments").FirstOrDefault()?.Value ?? "";
        var program = arguments.Split('"').Skip(1).FirstOrDefault() ?? "";
        return new State(enabled, seconds, LogPath, program);
    }

    // -- Linux: systemd user timer --------------------------------------------------

    public static (string Service, string Timer) SystemdUnits(string program, double everyHours, string home)
    {
        var minutes = (int)Math.Round(everyHours * 60);
        var service = $"""
            [Unit]
            Description=soulget: search again for missing tracks

            [Service]
            Type=oneshot
            ExecStart="{program}" --retry-missing --scheduled --home "{home}"
            Nice=10

            """;
        var timer = $"""
            [Unit]
            Description=soulget retry timer (every {Retry.G(everyHours)} hours)

            [Timer]
            OnActiveSec={minutes}min
            OnUnitActiveSec={minutes}min
            Unit={LinuxUnit}.service

            [Install]
            WantedBy=timers.target

            """;
        return (service, timer);
    }

    static State? LinuxStatus()
    {
        var timerFile = Path.Combine(SystemdDir, LinuxUnit + ".timer");
        if (!File.Exists(timerFile)) return null;
        var line = File.ReadAllLines(timerFile).FirstOrDefault(l => l.StartsWith("OnUnitActiveSec=")) ?? "";
        double.TryParse(line.Replace("OnUnitActiveSec=", "").Replace("min", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes);
        var serviceFile = Path.Combine(SystemdDir, LinuxUnit + ".service");
        var exec = File.Exists(serviceFile) ? File.ReadAllLines(serviceFile).FirstOrDefault(l => l.StartsWith("ExecStart=")) ?? "" : "";
        var program = exec.Split('"').Skip(1).FirstOrDefault() ?? "";
        var active = Run("systemctl", "--user", "is-active", LinuxUnit + ".timer").Out == "active";
        return new State(active, minutes * 60, LogPath, program);
    }

    // -- the same three actions everywhere ------------------------------------------

    public static void On(string program, double everyHours)
    {
        if (OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            Run("launchctl", "bootout", $"{Domain}/{Label}");   // replace an older one (also the Python version's), if any
            File.WriteAllText(PlistPath, PlistXml(program, everyHours, LogPath, Config.Home));
            var (code, output) = Run("launchctl", "bootstrap", Domain, PlistPath);
            if (code != 0) throw new Exit($"macOS refused the timer: {output}", 1);
        }
        else if (OperatingSystem.IsWindows())
        {
            var xmlFile = Path.Combine(Path.GetTempPath(), "soulget-task.xml");
            File.WriteAllText(xmlFile, WindowsTaskXml(program, everyHours, Config.Home, Misses.Now().AddHours(everyHours)), Encoding.Unicode);
            var (code, output) = Run("schtasks", "/Create", "/TN", WindowsTask, "/XML", xmlFile, "/F");
            File.Delete(xmlFile);
            if (code != 0) throw new Exit($"Windows refused the timer: {output}", 1);
        }
        else
        {
            if (Run("systemctl", "--user", "--version").Code != 0)
                throw new Exit("No systemd here. Add this line with `crontab -e` instead:\n" +
                               $"  0 */{(int)everyHours} * * * \"{program}\" --retry-missing --scheduled --home \"{Config.Home}\"", 1);
            Directory.CreateDirectory(SystemdDir);
            var (service, timer) = SystemdUnits(program, everyHours, Config.Home);
            File.WriteAllText(Path.Combine(SystemdDir, LinuxUnit + ".service"), service);
            File.WriteAllText(Path.Combine(SystemdDir, LinuxUnit + ".timer"), timer);
            Run("systemctl", "--user", "daemon-reload");
            var (code, output) = Run("systemctl", "--user", "enable", "--now", LinuxUnit + ".timer");
            if (code != 0) throw new Exit($"systemd refused the timer: {output}", 1);
        }
    }

    public static bool Off()
    {
        if (OperatingSystem.IsMacOS())
        {
            Run("launchctl", "bootout", $"{Domain}/{Label}");
            if (!File.Exists(PlistPath)) return false;
            File.Delete(PlistPath);
            return true;
        }
        if (OperatingSystem.IsWindows())
            return Run("schtasks", "/Delete", "/TN", WindowsTask, "/F").Code == 0;
        var timerFile = Path.Combine(SystemdDir, LinuxUnit + ".timer");
        if (!File.Exists(timerFile)) return false;
        Run("systemctl", "--user", "disable", "--now", LinuxUnit + ".timer");
        File.Delete(timerFile);
        File.Delete(Path.Combine(SystemdDir, LinuxUnit + ".service"));
        Run("systemctl", "--user", "daemon-reload");
        return true;
    }

    /// <summary>True when the timer checks this soulget folder (the one this config.toml is in). A Mac has one
    /// soulget timer per user; another copy of soulget may have set it up for its own folder.</summary>
    public static bool ServesThisHome(State st)
    {
        if (!OperatingSystem.IsMacOS()) return true;
        static string Full(string p) => Path.GetFullPath(p).TrimEnd('/');
        return st.LogPath.Length > 0 && Full(Path.GetDirectoryName(st.LogPath)!) == Full(Config.Home);
    }

    public static State? Status() =>
        OperatingSystem.IsMacOS() ? MacStatus() : OperatingSystem.IsWindows() ? WindowsStatus() : LinuxStatus();

    static string WhenItRuns =>
        OperatingSystem.IsMacOS() ? "It runs while the Mac is awake; checks missed during sleep happen on waking."
        : OperatingSystem.IsWindows() ? "It runs while you're signed in to Windows; a check missed while the PC was off runs as soon as it can."
        : "It runs while you're logged in (for always: `loginctl enable-linger`).";

    public static int Command(string action, Config cfg)
    {
        string? root = null;
        try { root = cfg.DownloadRoot(); }
        catch (DriveMissing e)
        {
            if (action == "on") throw new Exit($"Not now: {e.Message}. Connect it and try again.", 1);
            Console.WriteLine($"(Note: {e.Message}.)");
        }
        var every = cfg.Num("retry", "every_hours", 7);
        if (action == "on")
        {
            if (!((cfg.Str("soulseek", "username").Length > 0 && cfg.Str("soulseek", "password").Length > 0) ||
                  (cfg.Str("retry", "username").Length > 0 && cfg.Str("retry", "password").Length > 0)))
                throw new Exit($"Put your Soulseek username and password in {Config.ConfigPath} first.", 1);
            On(Environment.ProcessPath!, every);
            var first = Misses.Now().AddHours(every);
            Console.WriteLine($"Timer on: every {Retry.G(every)} hours your computer searches again for missing tracks, " +
                              $"first around {Retry.En(first, "ddd HH:mm")}.");
            Console.WriteLine(WhenItRuns);
            Console.WriteLine($"Log of each check: {LogPath}\nTurn it off with: soulget --schedule off");
        }
        else if (action == "off")
            Console.WriteLine(Off() ? "Timer turned off." : "The timer wasn't on.");
        else
        {
            var st = Status();
            if (st is null) Console.WriteLine("The timer is off. Turn it on with: soulget --schedule on");
            else
            {
                Console.WriteLine($"Timer {(st.Loaded ? "on" : "installed but not active (try --schedule on again)")}: " +
                                  $"every {Retry.G(st.IntervalSeconds / 3600)} hours.\nLog: {st.LogPath}");
                Console.WriteLine($"It runs: {st.Program}");
            }
            var rl = root is null ? null : Path.Combine(root, Retry.RetryLog);
            if (rl is not null && File.Exists(rl))
            {
                var lines = File.ReadAllText(rl).Trim().Split('\n').Skip(1).Select(l => l.TrimEnd('\r')).ToList();
                if (lines.Count > 0)
                {
                    Console.WriteLine("Last checks (when, weekday, hour, folder, searched, found, still missing, given up, trigger):");
                    foreach (var line in lines.TakeLast(6)) Console.WriteLine("  " + line);
                }
            }
        }
        return 0;
    }
}
