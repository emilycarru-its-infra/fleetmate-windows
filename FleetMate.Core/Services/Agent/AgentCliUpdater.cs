using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FleetMate.Core.Services.Terminal;
using Serilog;

namespace FleetMate.Core.Services.Agent;

/// <summary>The agent command-line tools FleetMate keeps current.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentCli>))]
public enum AgentCli { Codex, Claude }

/// <summary>How an agent CLI got onto this PC, read from where its program really is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentCliInstallMethod>))]
public enum AgentCliInstallMethod { Npm, Winget, ClaudeNative, Unknown }

public static class AgentCliNames
{
    public static string Command(this AgentCli cli) => cli == AgentCli.Codex ? "codex" : "claude";
    public static string DisplayName(this AgentCli cli) => cli == AgentCli.Codex ? "Codex" : "Claude Code";
    /// <summary>The npm package a global npm install comes from.</summary>
    public static string NpmPackage(this AgentCli cli) => cli == AgentCli.Codex ? "@openai/codex" : "@anthropic-ai/claude-code";

    public static string DisplayName(this AgentCliInstallMethod method) => method switch
    {
        AgentCliInstallMethod.Npm => "npm global",
        AgentCliInstallMethod.Winget => "winget",
        AgentCliInstallMethod.ClaudeNative => "Claude native installer",
        _ => "Unknown",
    };
}

/// <summary>Where one installed CLI is and how to update it.</summary>
public sealed record AgentCliInstall(AgentCli Cli, string Path, string ResolvedPath, AgentCliInstallMethod Method,
    string? Package = null, string? Prefix = null);

/// <summary>One process to run as part of an update.</summary>
public sealed record AgentCliCommand(string Executable, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    public string Display => string.Join(" ", new[] { Executable }.Concat(Arguments));
}

public sealed record AgentCliProcessOutput(int ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>What the updater knows about one CLI, kept between runs.</summary>
public sealed record AgentCliStatus
{
    [JsonPropertyName("cli")] public AgentCli Cli { get; init; }
    [JsonPropertyName("installed")] public bool Installed { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("method")] public AgentCliInstallMethod? Method { get; init; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("latestVersion")] public string? LatestVersion { get; set; }
    [JsonPropertyName("lastChecked")] public DateTimeOffset? LastChecked { get; init; }
    [JsonPropertyName("lastUpdated")] public DateTimeOffset? LastUpdated { get; set; }
    /// <summary>A neutral one-line note on the last attempt: "Up to date", "Updated 0.161.0 → 0.162.1", "Not installed".</summary>
    [JsonPropertyName("message")] public string? Message { get; set; }
}

/// <summary>The saved state the app and `fleetmate agent update` share.</summary>
public sealed record AgentCliUpdateState
{
    [JsonPropertyName("lastChecked")] public DateTimeOffset? LastChecked { get; init; }
    [JsonPropertyName("statuses")] public IReadOnlyList<AgentCliStatus> Statuses { get; init; } = Array.Empty<AgentCliStatus>();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "FleetMate", "agent-cli-updates.json");

    public static AgentCliUpdateState Load(string? path = null)
    {
        try { return JsonSerializer.Deserialize<AgentCliUpdateState>(File.ReadAllText(path ?? DefaultPath), Json) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, ToJson());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>True when no update run has happened within <paramref name="interval"/>.</summary>
    public bool IsStale(DateTimeOffset? now = null, TimeSpan? interval = null) =>
        LastChecked is not { } last || (now ?? DateTimeOffset.UtcNow) - last >= (interval ?? AgentCliUpdater.CheckInterval);
}

/// <summary>
/// Keeps codex and claude at their latest versions without anyone watching:
/// finds each CLI, works out how it was installed from where its program
/// really lives, and updates it with that same tool, non-interactively.
///
/// It never installs a CLI that is not there, never elevates, and never
/// waits on input (every child gets an empty stdin). An npm or winget install
/// that a running session is using is left until the next run, since Windows
/// will not replace a program that is running. The process runner and file checks are injected
/// so the decisions are testable.
/// </summary>
public sealed class AgentCliUpdater
{
    /// <summary>How often the app checks on its own.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// <summary>winget's exit code when the package has no newer version.</summary>
    public const int WingetNoUpdate = unchecked((int)0x8A15002B);

    public delegate Task<AgentCliProcessOutput> Runner(AgentCliCommand command);

    private readonly Runner _run;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, string> _resolveLinks;
    private readonly Func<string, string?> _findOnPath;
    private readonly Func<AgentCli, bool> _inUse;
    private readonly Action<string> _log;

    public string Home { get; }
    public string AppData { get; }
    public string LocalAppData { get; }

    public AgentCliUpdater(
        string? home = null, string? appData = null, string? localAppData = null,
        Runner? run = null, Func<string, bool>? exists = null, Func<string, string>? resolveLinks = null,
        Func<string, string?>? findOnPath = null, Func<AgentCli, bool>? inUse = null, Action<string>? log = null)
    {
        Home = home ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        AppData = appData ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        LocalAppData = localAppData ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        _run = run ?? DefaultRunner;
        _exists = exists ?? File.Exists;
        _resolveLinks = resolveLinks ?? ResolveLinks;
        _findOnPath = findOnPath ?? AgentCommands.FindOnPath;
        _inUse = inUse ?? IsRunning;
        _log = log ?? (m => Log.Information("agent-update: {Message}", m));
    }

    // ── Finding the CLIs ─────────────────────────────────────────────────

    /// <summary>Folders the CLIs install to, which the app's PATH can lack when one was installed after sign-in.</summary>
    public IReadOnlyList<string> SearchFolders() => new[]
    {
        Path.Combine(Home, ".local", "bin"),
        Path.Combine(AppData, "npm"),
        Path.Combine(LocalAppData, "Microsoft", "WinGet", "Links"),
    };

    /// <summary>The CLI on PATH, else in the usual install folders.</summary>
    public string? Locate(AgentCli cli)
    {
        var name = cli.Command();
        if (_findOnPath(name) is { } onPath) return onPath;
        foreach (var folder in SearchFolders())
            foreach (var ext in new[] { ".exe", ".cmd" })
            {
                var candidate = Path.Combine(folder, name + ext);
                if (_exists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>
    /// The install method for a program at <paramref name="resolvedPath"/>,
    /// from its location: winget keeps portable packages under
    /// WinGet\Packages\&lt;Id&gt;_&lt;source&gt;\; npm's shim sits in its
    /// global prefix beside node_modules\&lt;package&gt;; Claude Code's native
    /// installer under %USERPROFILE%\.local.
    /// </summary>
    public AgentCliInstall Detect(AgentCli cli, string path, string resolvedPath)
    {
        var parts = resolvedPath.Split('\\', '/');
        var packages = Array.FindIndex(parts, p => p.Equals("Packages", StringComparison.OrdinalIgnoreCase));
        if (packages > 0 && parts[packages - 1].Equals("WinGet", StringComparison.OrdinalIgnoreCase) && packages + 1 < parts.Length)
        {
            var folder = parts[packages + 1];
            var cut = folder.IndexOf("_Microsoft.Winget.Source", StringComparison.OrdinalIgnoreCase);
            if (cut < 0) cut = folder.LastIndexOf('_');
            var id = cut > 0 ? folder[..cut] : folder;
            return new AgentCliInstall(cli, path, resolvedPath, AgentCliInstallMethod.Winget, id);
        }

        var modules = Array.FindIndex(parts, p => p.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
        if (modules > 0)
            return new AgentCliInstall(cli, path, resolvedPath, AgentCliInstallMethod.Npm, cli.NpmPackage(),
                string.Join('\\', parts[..modules]));
        var dir = Path.GetDirectoryName(path);
        if (dir != null && _exists(Path.Combine(dir, "node_modules", cli.NpmPackage().Replace('/', '\\'), "package.json")))
            return new AgentCliInstall(cli, path, resolvedPath, AgentCliInstallMethod.Npm, cli.NpmPackage(), dir);

        if (cli == AgentCli.Claude && new[] { Path.Combine(Home, ".local") + "\\", Path.Combine(Home, ".claude") + "\\" }
                .Any(p => resolvedPath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return new AgentCliInstall(cli, path, resolvedPath, AgentCliInstallMethod.ClaudeNative);

        return new AgentCliInstall(cli, path, resolvedPath, AgentCliInstallMethod.Unknown);
    }

    // ── What to run ──────────────────────────────────────────────────────

    /// <summary>Settings that keep winget from asking anything.</summary>
    private static readonly string[] WingetQuiet =
        { "--silent", "--disable-interactivity", "--accept-source-agreements", "--accept-package-agreements" };

    private string Npm(AgentCliInstall install) =>
        install.Prefix is { } prefix && _exists(Path.Combine(prefix, "npm.cmd")) ? Path.Combine(prefix, "npm.cmd")
        : _findOnPath("npm.cmd") ?? "npm.cmd";

    /// <summary>The command that updates <paramref name="install"/> in place, or null when FleetMate cannot update it safely.</summary>
    public AgentCliCommand? UpdateCommand(AgentCliInstall install) => install.Method switch
    {
        AgentCliInstallMethod.Npm => new AgentCliCommand(Npm(install),
            new[] { "install", "--global" }
                .Concat(install.Prefix != null ? new[] { "--prefix", install.Prefix } : Array.Empty<string>())
                .Concat(new[] { "--no-fund", "--no-audit", $"{install.Package ?? install.Cli.NpmPackage()}@latest" }).ToList(),
            new Dictionary<string, string> { ["npm_config_yes"] = "true", ["npm_config_update_notifier"] = "false" }),
        AgentCliInstallMethod.Winget when install.Package != null => new AgentCliCommand(_findOnPath("winget.exe") ?? "winget.exe",
            new[] { "upgrade", "--id", install.Package, "--exact" }.Concat(WingetQuiet).ToList()),
        AgentCliInstallMethod.ClaudeNative => new AgentCliCommand(install.Path, new[] { "update" },
            new Dictionary<string, string> { ["CI"] = "1" }),
        _ => null,
    };

    /// <summary>The command that reports the newest version, or null where that cannot be asked cheaply.</summary>
    public AgentCliCommand? LatestVersionCommand(AgentCliInstall install) => install.Method switch
    {
        AgentCliInstallMethod.Npm => new AgentCliCommand(Npm(install),
            new[] { "view", install.Package ?? install.Cli.NpmPackage(), "version" }),
        _ => null,
    };

    /// <summary>
    /// The first dotted version number in --version output: "codex-cli
    /// 0.162.1" is 0.162.1, "2.1.296 (Claude Code)" is 2.1.296.
    /// </summary>
    public static string? ParseVersion(string output)
    {
        foreach (var word in output.Split(new[] { ' ', '\t', '\r', '\n', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word.StartsWith('v') ? word[1..] : word;
            var pieces = w.Split('.');
            if (pieces.Length >= 2 && pieces.All(p => p.Length > 0) && pieces[0].All(char.IsAsciiDigit) && pieces[1].All(char.IsAsciiDigit))
                return w;
        }
        return null;
    }

    // ── Running ──────────────────────────────────────────────────────────

    /// <summary>Find each CLI and how it is installed.</summary>
    public Dictionary<AgentCli, AgentCliInstall> Installs()
    {
        var found = new Dictionary<AgentCli, AgentCliInstall>();
        foreach (var cli in Enum.GetValues<AgentCli>())
            if (Locate(cli) is { } path) found[cli] = Detect(cli, path, _resolveLinks(path));
        return found;
    }

    private async Task<string?> VersionOf(AgentCliInstall install)
    {
        var output = await _run(new AgentCliCommand(install.Path, new[] { "--version" }));
        return output.Succeeded ? ParseVersion(output.Stdout) : null;
    }

    /// <summary>
    /// Check every CLI, and update the installed ones unless
    /// <paramref name="checkOnly"/>. Returns the new state; the caller saves
    /// it. Its LastChecked is when an update run last completed, the clock the
    /// schedule goes by.
    /// </summary>
    public async Task<AgentCliUpdateState> RunAsync(bool checkOnly, AgentCliUpdateState? previous = null, Func<DateTimeOffset>? now = null)
    {
        previous ??= new AgentCliUpdateState();
        now ??= () => DateTimeOffset.UtcNow;
        var installs = Installs();
        var statuses = new List<AgentCliStatus>();
        var deferred = false;

        foreach (var cli in Enum.GetValues<AgentCli>())
        {
            var old = previous.Statuses.FirstOrDefault(s => s.Cli == cli);
            if (!installs.TryGetValue(cli, out var install))
            {
                statuses.Add(new AgentCliStatus { Cli = cli, Installed = false, LastChecked = now(), Message = "Not installed" });
                continue;
            }
            var status = new AgentCliStatus
            {
                Cli = cli, Installed = true, Path = install.Path, Method = install.Method,
                Version = await VersionOf(install), LastChecked = now(), LastUpdated = old?.LastUpdated,
            };
            if (LatestVersionCommand(install) is { } latest)
            {
                var output = await _run(latest);
                if (output.Succeeded && output.Stdout.Trim() is { Length: > 0 } v) status.LatestVersion = v;
            }

            if (checkOnly)
            {
                status.Message = Describe(status.Version, status.LatestVersion);
                statuses.Add(status);
                continue;
            }
            if (status.LatestVersion != null && status.LatestVersion == status.Version)
            {
                status.Message = "Up to date";
                statuses.Add(status);
                continue;
            }
            if (UpdateCommand(install) is not { } command)
            {
                status.Message = "Installed by an unrecognised method; FleetMate leaves it alone";
                statuses.Add(status);
                continue;
            }
            if (install.Method != AgentCliInstallMethod.ClaudeNative && _inUse(cli))
            {
                // Windows will not replace a running program, and npm and winget
                // replace it in place; try again on the next run. Claude Code's
                // own updater is built to run beside open sessions.
                status.Message = "In use by a running session; updated later";
                deferred = true;
                statuses.Add(status);
                continue;
            }

            _log($"Updating {cli.Command()} ({install.Method}): {command.Display}");
            var result = await _run(command);
            var before = status.Version;
            status.Version = await VersionOf(install) ?? before;
            if (result.Succeeded || (install.Method == AgentCliInstallMethod.Winget && result.ExitCode == WingetNoUpdate))
            {
                if (before != null && status.Version != null && status.Version != before)
                {
                    status.Message = $"Updated {before} → {status.Version}";
                    status.LastUpdated = now();
                }
                else status.Message = "Up to date";
                _log($"{cli.Command()}: {status.Message}");
            }
            else
            {
                var reason = Tail(result.Stderr.Trim().Length > 0 ? result.Stderr : result.Stdout);
                status.Message = $"Update did not complete: {reason}";
                _log($"{cli.Command()} update failed ({result.ExitCode}): {reason}");
            }
            statuses.Add(status);
        }
        // A check alone, or a run that had to leave a CLI for later, does not reset the schedule.
        return new AgentCliUpdateState { LastChecked = checkOnly || deferred ? previous.LastChecked : now(), Statuses = statuses };
    }

    internal static string Describe(string? version, string? latest) => (version, latest) switch
    {
        ({ } v, { } l) when v == l => "Up to date",
        (_, { } l) => $"{l} available",
        _ => "Checked",
    };

    /// <summary>The last line of a tool's output, trimmed for a status line.</summary>
    internal static string Tail(string text)
    {
        var line = text.Split('\n', '\r').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "no output";
        return line.Length > 160 ? line[..157] + "…" : line;
    }

    // ── Defaults ─────────────────────────────────────────────────────────

    /// <summary>Whether a process of that CLI (codex.exe, claude.exe) is running.</summary>
    public static bool IsRunning(AgentCli cli)
    {
        var processes = Process.GetProcessesByName(cli.Command());
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    public static string ResolveLinks(string path)
    {
        try { return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return path; }
    }

    /// <summary>
    /// Run a command with no window and an empty stdin, for at most ten
    /// minutes. A .cmd (npm) runs through cmd.exe; the arguments are this
    /// class's own constants and paths, never text from elsewhere.
    /// </summary>
    public static async Task<AgentCliProcessOutput> DefaultRunner(AgentCliCommand command)
    {
        try
        {
            var ext = Path.GetExtension(command.Executable);
            var viaCmd = ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(viaCmd ? "cmd.exe" : command.Executable)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                CreateNoWindow = true, UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            if (viaCmd)
                psi.Arguments = "/d /s /c \"" + CommandLineBuilder.Build(command.Executable, command.Arguments) + "\"";
            else
                foreach (var a in command.Arguments) psi.ArgumentList.Add(a);
            foreach (var (k, v) in command.Environment ?? new Dictionary<string, string>()) psi.Environment[k] = v;

            using var process = Process.Start(psi)!;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new AgentCliProcessOutput(-1, "", "timed out");
            }
            return new AgentCliProcessOutput(process.ExitCode, await stdout, await stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new AgentCliProcessOutput(-1, "", ex.Message);
        }
    }
}
