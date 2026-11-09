using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Config;

namespace FleetMate.Core.Services.Teams;

public sealed record TeamsSavedThread(TeamsSearchHit Hit, DateTimeOffset SavedAt, string Collection = "Saved");

/// <summary>Local references and search summaries, partitioned by API and signed-in account. Full messages are fetched again.</summary>
public sealed class TeamsSavedThreads
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public TeamsSavedThreads(string endpoint, string account, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(account))
            throw new ArgumentException("A Teams connection and signed-in account are required to save threads.");
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var connection)
            || connection.Scheme is not ("https" or "http")) throw new ArgumentException("The Teams connection must be an HTTP or HTTPS URL.");
        var scope = connection.AbsoluteUri.TrimEnd('/') + "\n" + account.Trim().ToLowerInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))).ToLowerInvariant();
        _path = Path.Combine(directory ?? Path.Combine(AppEdition.Current.LocalAppDataDirectory, "teams"), key + ".json");
    }

    public IReadOnlyList<TeamsSavedThread> Load()
    {
        if (!File.Exists(_path)) return Array.Empty<TeamsSavedThread>();
        return JsonSerializer.Deserialize<List<TeamsSavedThread>>(File.ReadAllText(_path), Json)
            ?? throw new InvalidDataException("The saved Teams thread list could not be read.");
    }

    public IReadOnlyList<TeamsSavedThread> Save(TeamsSearchHit hit, string? collection = null)
    {
        var items = Load().Where(item => !Same(item.Hit, hit)).ToList();
        items.Insert(0, new TeamsSavedThread(hit, DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(collection) ? "Saved" : TeamsText.OneLine(collection.Trim())));
        Write(items);
        return items;
    }

    public IReadOnlyList<TeamsSavedThread> Remove(TeamsSearchHit hit)
    {
        var items = Load().Where(item => !Same(item.Hit, hit)).ToList();
        Write(items);
        return items;
    }

    public static bool Same(TeamsSearchHit left, TeamsSearchHit right) =>
        left.ChannelId == right.ChannelId && left.ThreadId == right.ThreadId;

    private void Write(IReadOnlyList<TeamsSavedThread> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(items, Json));
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
