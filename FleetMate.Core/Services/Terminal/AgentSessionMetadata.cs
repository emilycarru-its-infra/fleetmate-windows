using System.Text.Json;

namespace FleetMate.Core.Services.Terminal;

/// <summary>Optional sidebar hints written by an agent to its own session file.</summary>
public sealed record AgentSessionMetadata(string? Title, string? Section)
{
    public static string Directory => Path.Combine(AgentBriefStore.DefaultDirectory, "agent-sessions");
    public static string FilePath(string id) => Path.Combine(Directory, id + ".json");

    public static AgentSessionMetadata? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            string? Field(string name)
            {
                if (!doc.RootElement.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
                var text = value.GetString()!.Trim();
                return text.Length <= 120 && !text.Any(char.IsControl) ? text : null;
            }
            return new AgentSessionMetadata(Field("title"), Field("section"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
