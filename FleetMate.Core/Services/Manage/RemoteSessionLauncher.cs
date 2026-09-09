using System.Diagnostics;
using System.Text;
using FleetMate.Core.Config;
using Serilog;

namespace FleetMate.Core.Services.Manage;

/// <summary>One SSH session to open: address, display title.</summary>
public record SshSession(string Ip, string Title);

/// <summary>Starts processes; the real one in the app, a recorder in tests.</summary>
public interface IProcessLauncher
{
    void Start(string fileName, string arguments, bool hidden = false);
}

public class ProcessLauncher : IProcessLauncher
{
    public void Start(string fileName, string arguments, bool hidden = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = !hidden,
            CreateNoWindow = hidden,
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
        using var process = Process.Start(psi);
        if (hidden && process != null) process.WaitForExit(5000);
    }
}

/// <summary>
/// Opens interactive sessions to machines: SSH in Windows Terminal (one
/// window, one tab per host) with a console fallback, and Remote Desktop
/// by address, optionally with a credential stored for the target so no
/// prompt appears. mstsc is given the address on its command line rather
/// than a generated .rdp file: an unsigned file that asks for resource
/// redirection triggers the "unknown publisher" warning on every launch.
/// Everything that builds a command line is a pure function so it can be
/// tested without launching anything.
/// </summary>
public class RemoteSessionLauncher
{
    private readonly ManageConfig _config;
    private readonly IProcessLauncher _launcher;
    private readonly RdpCredentialStore _credentials;

    public RemoteSessionLauncher(ManageConfig config, RdpCredentialStore credentials, IProcessLauncher? launcher = null)
    {
        _config = config;
        _credentials = credentials;
        _launcher = launcher ?? new ProcessLauncher();
    }

    /// <summary>The OpenSSH client that ships with Windows, before any other ssh on PATH.</summary>
    public static string SshExecutable
    {
        get
        {
            var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
            return File.Exists(system) ? system : "ssh.exe";
        }
    }

    public static bool WindowsTerminalAvailable
    {
        get
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
            if (File.Exists(local)) return true;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            return path.Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, "wt.exe")));
        }
    }

    // ── SSH ─────────────────────────────────────────────────────────────

    /// <summary>The ssh command line for one host, as it would be typed.</summary>
    public string SshCommandLine(string ip) => SshCommandLine(ip, _config.ResolvedSshUser, _config.ResolvedSshKeyPath);

    public static string SshCommandLine(string ip, string user, string keyPath) =>
        $"{Quote(SshExecutable)} -i {Quote(keyPath)} -o StrictHostKeyChecking=accept-new -o ConnectTimeout=8 -o ServerAliveInterval=15 {user}@{ip}";

    /// <summary>
    /// Windows Terminal arguments opening one tab per session in a new window.
    /// Tabs are separated by ";" and each carries its own title and profile;
    /// the ssh command line is passed through verbatim after the options.
    /// </summary>
    public string WindowsTerminalArguments(IReadOnlyList<SshSession> sessions) =>
        WindowsTerminalArguments(sessions, _config.ResolvedSshUser, _config.ResolvedSshKeyPath, _config.TerminalProfile);

    public static string WindowsTerminalArguments(IReadOnlyList<SshSession> sessions, string user, string keyPath, string? profile)
    {
        var sb = new StringBuilder("-w new");
        var first = true;
        foreach (var s in sessions)
        {
            sb.Append(first ? " " : " ; ");
            first = false;
            sb.Append("new-tab");
            if (!string.IsNullOrWhiteSpace(profile)) sb.Append(" -p ").Append(Quote(profile.Trim()));
            sb.Append(" --title ").Append(Quote(EscapeForTerminal(s.Title)));
            sb.Append(' ').Append(SshCommandLine(s.Ip, user, keyPath));
        }
        return sb.ToString();
    }

    /// <summary>Console fallback when Windows Terminal is missing: one console window per session.</summary>
    public static string ConsoleFallbackArguments(SshSession session, string user, string keyPath) =>
        $"/c start \"{EscapeForCmd(session.Title)}\" {SshCommandLine(session.Ip, user, keyPath)}";

    public void OpenSsh(string ip, string title) => OpenSshTabs(new[] { new SshSession(ip, title) });

    public void OpenSshTabs(IReadOnlyList<SshSession> sessions)
    {
        if (sessions.Count == 0) return;
        if (WindowsTerminalAvailable)
        {
            var args = WindowsTerminalArguments(sessions);
            Log.Information("Opening {Count} SSH tab(s) in Windows Terminal", sessions.Count);
            _launcher.Start("wt.exe", args);
            return;
        }

        Log.Information("Windows Terminal not found; opening {Count} console window(s)", sessions.Count);
        foreach (var s in sessions)
            _launcher.Start("cmd.exe", ConsoleFallbackArguments(s, _config.ResolvedSshUser, _config.ResolvedSshKeyPath));
    }

    // ── RDP ─────────────────────────────────────────────────────────────

    /// <summary>
    /// mstsc arguments: the target and a windowed size. Credentials come from
    /// the TERMSRV entry when one is stored, otherwise mstsc prompts.
    /// </summary>
    public static string RdpArguments(string ip, int width = 1600, int height = 1000) =>
        $"/v:{ip} /w:{width} /h:{height}";

    /// <summary>cmdkey arguments that store the credential for one target.</summary>
    public static string CmdKeyStoreArguments(string ip, string user, string password) =>
        $"/generic:TERMSRV/{ip} /user:{Quote(user)} /pass:{Quote(password)}";

    public static string CmdKeyDeleteArguments(string ip) => $"/delete:TERMSRV/{ip}";

    /// <summary>
    /// Launch Remote Desktop to the address. When a credential is stored it
    /// is registered for this target first; otherwise mstsc prompts.
    /// </summary>
    public void OpenRdp(string ip)
    {
        var user = _config.ResolvedRdpUser;
        var password = _credentials.Load();
        var hasCredential = !string.IsNullOrEmpty(password);

        if (hasCredential)
        {
            _launcher.Start("cmdkey.exe", CmdKeyStoreArguments(ip, user, password!), hidden: true);
        }

        Log.Information("Opening Remote Desktop to {Ip} as {User} ({Cred})", ip, user, hasCredential ? "stored credential" : "prompt");
        _launcher.Start("mstsc.exe", RdpArguments(ip));
    }

    public void OpenSshAndRdp(string ip, string title)
    {
        OpenSsh(ip, title);
        OpenRdp(ip);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    internal static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (value.IndexOfAny(new[] { ' ', '\t', '"', ';' }) < 0) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    /// <summary>Terminal titles cannot carry a semicolon (it splits commands) or quotes.</summary>
    internal static string EscapeForTerminal(string title) => title.Replace(";", ",").Replace("\"", "'");

    internal static string EscapeForCmd(string title) => title.Replace("\"", "'").Replace("&", "^&").Replace("|", "^|");
}
