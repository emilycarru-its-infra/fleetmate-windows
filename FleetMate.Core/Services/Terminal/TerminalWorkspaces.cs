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

    public void Remove(string name)
    {
        Workspaces.RemoveAll(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(OpenAtLaunch, name, StringComparison.OrdinalIgnoreCase)) OpenAtLaunch = null;
    }
}

public sealed class TerminalWorkspace
{
    public string Name { get; set; } = "";
    /// <summary>Show the terminal when the workspace opens. Default true.</summary>
    public bool? Show { get; set; }
    /// <summary>Open the terminal filling the window rather than split.</summary>
    public bool? FullWindow { get; set; }
    public List<TerminalWorkspaceSection> Sections { get; set; } = new();

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
    /// <summary>The session's name in the list; null takes the program's own title.</summary>
    public string? Name { get; set; }
    /// <summary>What it runs: "codex", "claude", any command; null or "" is the shell.</summary>
    public string? Command { get; set; }
    /// <summary>Where it starts; "~" is the home folder. Null is the default start folder.</summary>
    public string? Directory { get; set; }
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
        foreach (var workspace in file.Workspaces)
        {
            workspace.Sections ??= new();
            foreach (var section in workspace.Sections) section.Sessions ??= new();
        }
        return file;
    }

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
