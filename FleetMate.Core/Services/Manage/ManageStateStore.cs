using System.Text.Json;
using FleetMate.Core.Models.Manage;
using Serilog;

namespace FleetMate.Core.Services.Manage;

/// <summary>
/// Persists the Manage tab's operator state as JSON files under one folder:
/// command history and custom groups. Default root is
/// <c>%LOCALAPPDATA%\FleetMate\manage</c>; tests pass a temp folder.
/// No secrets live here; the RDP credential has its own DPAPI store.
/// </summary>
public class ManageStateStore
{
    public const int HistoryLimit = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string Root { get; }
    public string HistoryPath => Path.Combine(Root, "history.json");
    public string CustomGroupsPath => Path.Combine(Root, "custom-groups.json");
    public string CommandsPath => Path.Combine(Root, "commands.yaml");

    public ManageStateStore(string? root = null)
    {
        Root = root ?? DefaultRoot;
    }

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleetMate", "manage");

    public List<CommandHistoryEntry> LoadHistory() => Load<List<CommandHistoryEntry>>(HistoryPath) ?? new();

    public void SaveHistory(IEnumerable<CommandHistoryEntry> entries) =>
        Save(HistoryPath, entries.Take(HistoryLimit).ToList());

    /// <summary>Insert at the front, trim to the limit, save, and return the new list.</summary>
    public List<CommandHistoryEntry> AddHistory(List<CommandHistoryEntry> history, string label, string command)
    {
        history.Insert(0, new CommandHistoryEntry(label, command));
        if (history.Count > HistoryLimit) history.RemoveRange(HistoryLimit, history.Count - HistoryLimit);
        SaveHistory(history);
        return history;
    }

    public void ClearHistory()
    {
        try { if (File.Exists(HistoryPath)) File.Delete(HistoryPath); }
        catch (Exception ex) { Log.Warning(ex, "Could not clear history at {Path}", HistoryPath); }
    }

    public List<CustomGroup> LoadCustomGroups() => Load<List<CustomGroup>>(CustomGroupsPath) ?? new();

    public void SaveCustomGroups(IEnumerable<CustomGroup> groups) => Save(CustomGroupsPath, groups.ToList());

    private T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read {Path}; starting empty", path);
            return null;
        }
    }

    private void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not write {Path}", path);
        }
    }
}
