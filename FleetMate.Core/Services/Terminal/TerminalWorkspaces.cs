using System.Text.Json;
using System.Text.Json.Serialization;
using FleetMate.Core.Config;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// Saved terminal workspaces: named sets of sessions in sections, kept in
/// <c>%USERPROFILE%\.fleetmate\terminal-workspaces.json</c> (the same file and
/// shape as <c>~/.fleetmate</c> on the Mac), so they can be edited by hand.
/// One may be named to open when FleetMate starts.
/// </summary>
public sealed class TerminalWorkspaceFile
{
    /// <summary>The workspace opened at launch, by name (case-insensitive); null opens none.</summary>
    public string? OpenAtLaunch { get; set; }
    public List<TerminalWorkspace> Workspaces { get; set; } = new();
    /// <summary>False once the person deleted the workspace generated from Repos, so it is not written again behind their back. Null or true keeps it.</summary>
    public bool? GenerateFromRepos { get; set; }
    /// <summary>The sessions open when FleetMate last quit, put back in the session list at the next launch. Written by FleetMate; null when none.</summary>
    public TerminalWorkspace? Restore { get; set; }

    public TerminalWorkspace? Workspace(string name) =>
        Workspaces.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The workspace to open at launch, when it names one that exists.</summary>
    [JsonIgnore]
    public TerminalWorkspace? LaunchWorkspace => OpenAtLaunch is { } name ? Workspace(name) : null;

    /// <summary>Add <paramref name="workspace"/>, or replace the one with its name in place.</summary>
    public void Upsert(TerminalWorkspace workspace)
    {
        var index = Workspaces.FindIndex(w => string.Equals(w.Name, workspace.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) Workspaces[index] = workspace;
        else Workspaces.Add(workspace);
    }

    /// <summary>The workspace generated from Repos, by its mark or else its name; null when there is none.</summary>
    [JsonIgnore]
    public TerminalWorkspace? Baseline => BaselineIndex is { } i ? Workspaces[i] : null;

    /// <summary>Where the workspace generated from Repos sits, by its mark or else its name.</summary>
    [JsonIgnore]
    public int? BaselineIndex
    {
        get
        {
            var index = Workspaces.FindIndex(w => w.Source == TerminalWorkspaceBaseline.Source);
            if (index < 0) index = Workspaces.FindIndex(w => string.Equals(w.Name, TerminalWorkspaceBaseline.Name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : null;
        }
    }

    /// <summary>Remove a workspace. Removing the Repos workspace also stops FleetMate writing it again.</summary>
    public void Remove(string name)
    {
        if (Baseline is { } baseline && string.Equals(baseline.Name, name, StringComparison.OrdinalIgnoreCase))
            GenerateFromRepos = false;
        Workspaces.RemoveAll(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(OpenAtLaunch, name, StringComparison.OrdinalIgnoreCase)) OpenAtLaunch = null;
    }
}

public sealed class TerminalWorkspace
{
    public string Name { get; set; } = "";
    /// <summary>"repos" for the workspace FleetMate generated from the Repos tab, so Add missing repos finds it after a rename; null for the person's own.</summary>
    public string? Source { get; set; }
    /// <summary>Show the terminal when the workspace opens. Default true.</summary>
    public bool? Show { get; set; }
    /// <summary>Open the terminal filling the window rather than split.</summary>
    public bool? FullWindow { get; set; }
    public List<TerminalWorkspaceSection> Sections { get; set; } = new();
    /// <summary>
    /// For the Repos workspace: every folder FleetMate has put in it. A folder
    /// listed here and missing from the sessions was removed by the person, so
    /// it is not added again; one no longer tracked is taken out.
    /// </summary>
    public List<string>? Generated { get; set; }
    /// <summary>
    /// For the Repos workspace: a session FleetMate placed with no command
    /// runs the default launch as it is when the row starts. True once the
    /// commands older versions wrote in have been cleared.
    /// </summary>
    public bool? CommandsFollowDefault { get; set; }

    [JsonIgnore]
    public int SessionCount => Sections.Sum(s => s.Sessions?.Count ?? 0);
}

public sealed class TerminalWorkspaceSection
{
    public string Name { get; set; } = "";
    public bool? Collapsed { get; set; }
    public List<TerminalWorkspaceSession> Sessions { get; set; } = new();
}

public sealed class TerminalWorkspaceSession
{
    /// <summary>The name the person gave the session. It wins over every automatic name; null follows the program's own title.</summary>
    public string? Name { get; set; }
    /// <summary>The row's name until the program sets a title, such as the repository's name for a session generated from Repos.</summary>
    public string? Label { get; set; }
    /// <summary>What it runs: "codex", "claude", any command; "" is the shell. Null is the shell, except in the Repos workspace, where it is the default launch.</summary>
    public string? Command { get; set; }
    /// <summary>Where it starts; "~" is the home folder. Null is the default start folder.</summary>
    public string? Directory { get; set; }
    /// <summary>Listed under Pinned at the top of the session list.</summary>
    public bool? Pinned { get; set; }
    /// <summary>Resume the agent's last conversation in this folder when the session next starts (<c>claude --continue</c>, <c>codex resume --last</c>).</summary>
    public bool? Resume { get; set; }
}

public static class TerminalWorkspaceStore
{
    public static string DefaultPath => Path.Combine(AppEdition.Current.UserDirectory, "terminal-workspaces.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The file's workspaces; an empty file when it does not exist yet.</summary>
    public static TerminalWorkspaceFile Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new TerminalWorkspaceFile();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new TerminalWorkspaceFile();
        var file = JsonSerializer.Deserialize<TerminalWorkspaceFile>(text, Options) ?? new TerminalWorkspaceFile();
        // Hand-written files may leave lists out; read them as empty.
        file.Workspaces ??= new();
        foreach (var workspace in file.Restore is { } restore ? file.Workspaces.Append(restore) : file.Workspaces)
        {
            workspace.Sections ??= new();
            foreach (var section in workspace.Sections) section.Sessions ??= new();
        }
        return file;
    }

    /// <summary>The JSON a workspace saves as, to tell whether a change altered it.</summary>
    public static string Serialize(TerminalWorkspace workspace) => JsonSerializer.Serialize(workspace, Options);

    public static void Save(TerminalWorkspaceFile file, string? path = null)
    {
        path ??= DefaultPath;
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary><paramref name="path"/> with the home folder written as <c>~</c>, so a saved workspace reads the same on another account.</summary>
    public static string Abbreviate(string path, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return path;
        var trimmed = home.TrimEnd('\\', '/');
        if (string.Equals(path.TrimEnd('\\', '/'), trimmed, StringComparison.OrdinalIgnoreCase)) return "~";
        foreach (var separator in new[] { '\\', '/' })
            if (path.StartsWith(trimmed + separator, StringComparison.OrdinalIgnoreCase))
                return "~" + path[trimmed.Length..];
        return path;
    }

    public static string Expand(string path, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~") return home;
        return path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? home.TrimEnd('\\', '/') + path[1..]
            : path;
    }
}
