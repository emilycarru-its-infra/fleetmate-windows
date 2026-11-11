using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Config;

namespace FleetMate.Core.Services.Teams;

public sealed record TeamsSavedThread(TeamsSearchHit Hit, DateTimeOffset SavedAt, string Collection = "Saved");

/// <summary>Local references, partitioned by API and verified tenant/principal. Full messages are fetched again.</summary>
public sealed class TeamsSavedThreads
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    };
    private sealed record State
    {
        public int Version { get; init; } = 1;
        public List<TeamsSavedThread> Saved { get; init; } = new();
        public List<string> Following { get; init; } = new();
    }

    public TeamsSavedThreads(string endpoint, TeamsStorageIdentity identity, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || identity == null || identity.TenantId == Guid.Empty || identity.ObjectId == Guid.Empty)
            throw new ArgumentException("A Teams connection and signed-in account are required to save threads.");
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var connection)
            || connection.Scheme is not ("https" or "http")) throw new ArgumentException("The Teams connection must be an HTTP or HTTPS URL.");
        var scope = connection.AbsoluteUri.TrimEnd('/') + "\n" + identity.TenantId.ToString("D") + "\n" + identity.ObjectId.ToString("D");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))).ToLowerInvariant();
        _path = Path.Combine(directory ?? Path.Combine(AppEdition.Current.LocalAppDataDirectory, "teams"), key + ".json");
    }

    public IReadOnlyList<TeamsSavedThread> Load() => Read().Saved;
    public IReadOnlyList<string> FollowedChannels() => Read().Following;

    public IReadOnlyList<string> SetFollowedChannels(IEnumerable<string> channels)
    {
        var ids = channels.ToList();
        if (ids.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("A followed channel must have an ID.");
        ids = ids.Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count > 500) throw new ArgumentException("Follow up to 500 channels.");
        var state = Read();
        Write(state with { Following = ids });
        return ids;
    }

    public IReadOnlyList<TeamsSavedThread> Save(TeamsSearchHit hit, string? collection = null)
    {
        var state = Read();
        var items = state.Saved.Where(item => !Same(item.Hit, hit)).ToList();
        items.Insert(0, new TeamsSavedThread(Reference(hit), DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(collection) ? "Saved" : TeamsText.OneLine(collection.Trim())));
        Write(state with { Saved = items });
        return items;
    }

    public IReadOnlyList<TeamsSavedThread> Remove(TeamsSearchHit hit)
    {
        var state = Read();
        var items = state.Saved.Where(item => !Same(item.Hit, hit)).ToList();
        Write(state with { Saved = items });
        return items;
    }

    public static bool Same(TeamsSearchHit left, TeamsSearchHit right) =>
        left.ChannelId == right.ChannelId && left.ThreadId == right.ThreadId;

    private State Read()
    {
        if (!File.Exists(_path)) return new State();
        using var json = JsonDocument.Parse(File.ReadAllText(_path));
        var state = json.RootElement.ValueKind == JsonValueKind.Array
            ? new State { Saved = json.RootElement.Deserialize<List<TeamsSavedThread>>(Json) ?? new() }
            : json.RootElement.Deserialize<State>(Json) ?? throw new InvalidDataException("The saved Teams state could not be read.");
        if (state.Version != 1 || state.Saved == null || state.Following == null
            || state.Saved.Any(item => item == null || item.Hit == null || string.IsNullOrWhiteSpace(item.Hit.ChannelId) || string.IsNullOrWhiteSpace(item.Hit.ThreadId))
            || state.Following.Any(string.IsNullOrWhiteSpace) || state.Following.Count > 500)
            throw new InvalidDataException("The saved Teams state is invalid or belongs to a newer version.");
        return state with { Saved = state.Saved.Select(item => item with { Hit = Reference(item.Hit) }).ToList() };
    }

    private static TeamsSearchHit Reference(TeamsSearchHit hit)
    {
        var title = TeamsText.OneLine(hit.Subject ?? "Conversation");
        return hit with
        {
            Subject = title[..Math.Min(160, title.Length)],
            Snippet = "", AuthorDisplayName = null, CreatedAt = "", LastActivityAt = "",
            ReplyCount = 0, Participants = Array.Empty<string>(), Score = 0,
        };
    }

    private void Write(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                state.Version, state.Following,
                Saved = state.Saved.Select(item => new
                {
                    Hit = new { item.Hit.ThreadId, item.Hit.TeamId, item.Hit.TeamName, item.Hit.ChannelId, item.Hit.ChannelName, item.Hit.Subject, item.Hit.WebUrl },
                    item.SavedAt, item.Collection,
                }),
            }, Json));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Copyable plain text and Markdown keep message content as data, with source context alongside it.</summary>
public static class TeamsThreadCopy
{
    public static string Message(TeamsMessage message) =>
        $"{TeamsText.OneLine(message.AuthorDisplayName ?? "Unknown author")} · {TeamsText.LocalTime(message.CreatedAt)}\n\n{TeamsText.TerminalSafe(message.Text)}";

    public static string Thread(TeamsThread thread, bool markdown, IReadOnlyList<string> allowedHosts)
    {
        var text = new StringBuilder();
        var title = TeamsText.OneLine(thread.Subject ?? "Conversation");
        text.AppendLine(markdown ? "# " + title : title);
        text.AppendLine(TeamsText.OneLine($"{thread.TeamName} › {thread.ChannelName}")).AppendLine();
        foreach (var message in thread.Messages)
        {
            var heading = $"{TeamsText.OneLine(message.AuthorDisplayName ?? "Unknown author")} · {TeamsText.LocalTime(message.CreatedAt)}";
            text.AppendLine(markdown ? "## " + heading : heading).AppendLine();
            text.AppendLine(TeamsText.TerminalSafe(message.Text)).AppendLine();
            foreach (var attachment in message.Attachments)
            {
                text.Append("Attachment: ").Append(TeamsText.OneLine(attachment.Name));
                if (TeamsLinkPolicy.SafeUri(attachment.Url, allowedHosts) is { } link) text.Append(" — ").Append(link.AbsoluteUri);
                text.AppendLine();
            }
            text.AppendLine();
        }
        if (TeamsLinkPolicy.SafeUri(thread.WebUrl, allowedHosts) is { } source) text.AppendLine("Source: " + source.AbsoluteUri);
        return text.ToString().TrimEnd();
    }
}
