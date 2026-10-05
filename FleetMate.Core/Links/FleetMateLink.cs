namespace FleetMate.Core.Links;

/// <summary>
/// Something a <c>fleetmate:</c> link opens. One parser for the app's URL
/// handler and anything that prints links (macOS parity: FleetMateLink).
/// </summary>
/// <remarks>
/// Routes:
/// <code>
/// fleetmate://pull/&lt;project&gt;/&lt;repo&gt;/&lt;id&gt;              Azure DevOps PR
/// fleetmate://pull/github/&lt;owner&gt;/&lt;repo&gt;/&lt;number&gt;     GitHub PR
/// fleetmate://commit/&lt;project&gt;/&lt;repo&gt;/&lt;sha&gt;
/// fleetmate://commit/github/&lt;owner&gt;/&lt;repo&gt;/&lt;sha&gt;
/// fleetmate://pipeline/&lt;project&gt;/&lt;runId&gt;              Azure DevOps run
/// fleetmate://pipeline/&lt;project&gt;/definition/&lt;id&gt;      Azure DevOps pipeline
/// fleetmate://pipeline/github/&lt;owner&gt;/&lt;repo&gt;/&lt;runId&gt;  Actions run
/// fleetmate://workitem/&lt;id&gt;
/// fleetmate://issue/github/&lt;owner&gt;/&lt;repo&gt;/&lt;number&gt;
/// fleetmate://open?url=&lt;web URL of any of the above&gt;
/// </code>
/// </remarks>
public abstract record FleetMateLink
{
    public const string Scheme = "fleetmate";

    /// <summary>Where a pull request or commit lives.</summary>
    public abstract record LinkSource;
    public sealed record AzureDevOps(string Project, string Repo) : LinkSource;
    public sealed record GitHub(string Owner, string Repo) : LinkSource;

    public sealed record PullRequest(LinkSource Source, int Number) : FleetMateLink;
    public sealed record Commit(LinkSource Source, string Sha) : FleetMateLink;
    public sealed record AzureDevOpsRun(string Project, int RunId) : FleetMateLink;
    public sealed record AzureDevOpsPipeline(string Project, int DefinitionId) : FleetMateLink;
    public sealed record GitHubRun(string Owner, string Repo, int RunId) : FleetMateLink;
    public sealed record WorkItem(int Id) : FleetMateLink;
    public sealed record GitHubIssue(string Owner, string Repo, int Number) : FleetMateLink;
    /// <summary>An Intune managed device, by its Intune ID.</summary>
    public sealed record Device(string IntuneId) : FleetMateLink;
    /// <summary>A Snipe-IT asset, by its asset ID.</summary>
    public sealed record Asset(int Id) : FleetMateLink;
    /// <summary>A TeamDynamix ticket, by its ID.</summary>
    public sealed record Ticket(int Id) : FleetMateLink;
    /// <summary>An Entra user, by object ID.</summary>
    public sealed record User(string Id) : FleetMateLink;
    /// <summary>An Entra group, by object ID.</summary>
    public sealed record Group(string Id) : FleetMateLink;

    // ── Parse ────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a <c>fleetmate:</c> link. Accepts both <c>fleetmate://route/…</c>
    /// and <c>fleetmate:route/…</c>, since Windows hands over whichever was
    /// typed.
    /// </summary>
    /// <exception cref="FleetMateLinkException">The link is not one FleetMate can open.</exception>
    public static FleetMateLink Parse(string link)
    {
        var text = link.Trim().Trim('"');
        var prefix = Scheme + ":";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new FleetMateLinkException(FleetMateLinkError.NotFleetMate, text);

        var rest = text[prefix.Length..].TrimStart('/');
        var query = "";
        var q = rest.IndexOf('?');
        if (q >= 0) { query = rest[(q + 1)..]; rest = rest[..q]; }

        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var route = segments.Length > 0 ? segments[0].ToLowerInvariant() : "";
        var parts = segments.Skip(1).ToArray();
        bool IsGitHub() => parts.Length > 0 && parts[0].Equals("github", StringComparison.OrdinalIgnoreCase);

        switch (route)
        {
            case "pull":
                if (IsGitHub())
                    return parts.Length == 4 && ParseNumber(parts[3]) is { } gh
                        ? new PullRequest(new GitHub(parts[1], parts[2]), gh)
                        : throw Bad(text, "fleetmate://pull/github/<owner>/<repo>/<number>");
                return parts.Length == 3 && ParseNumber(parts[2]) is { } pr
                    ? new PullRequest(new AzureDevOps(parts[0], parts[1]), pr)
                    : throw Bad(text, "fleetmate://pull/<project>/<repo>/<id>");

            case "commit":
                if (IsGitHub())
                    return parts.Length == 4 && IsSha(parts[3])
                        ? new Commit(new GitHub(parts[1], parts[2]), parts[3])
                        : throw Bad(text, "fleetmate://commit/github/<owner>/<repo>/<sha>");
                return parts.Length == 3 && IsSha(parts[2])
                    ? new Commit(new AzureDevOps(parts[0], parts[1]), parts[2])
                    : throw Bad(text, "fleetmate://commit/<project>/<repo>/<sha>");

            case "pipeline":
                if (IsGitHub())
                    return parts.Length == 4 && ParseNumber(parts[3]) is { } run
                        ? new GitHubRun(parts[1], parts[2], run)
                        : throw Bad(text, "fleetmate://pipeline/github/<owner>/<repo>/<runId>");
                if (parts.Length == 3 && parts[1].Equals("definition", StringComparison.OrdinalIgnoreCase) && ParseNumber(parts[2]) is { } def)
                    return new AzureDevOpsPipeline(parts[0], def);
                return parts.Length == 2 && ParseNumber(parts[1]) is { } build
                    ? new AzureDevOpsRun(parts[0], build)
                    : throw Bad(text, "fleetmate://pipeline/<project>/<runId>");

            case "workitem":
                return parts.Length == 1 && ParseNumber(parts[0]) is { } id
                    ? new WorkItem(id)
                    : throw Bad(text, "fleetmate://workitem/<id>");

            case "issue":
                return parts.Length == 4 && parts[0].Equals("github", StringComparison.OrdinalIgnoreCase) && ParseNumber(parts[3]) is { } issue
                    ? new GitHubIssue(parts[1], parts[2], issue)
                    : throw Bad(text, "fleetmate://issue/github/<owner>/<repo>/<number>");

            case "device":
                return parts.Length == 1 && parts[0].Length > 0
                    ? new Device(parts[0])
                    : throw Bad(text, "fleetmate://device/<intuneId>");

            case "asset":
                return parts.Length == 1 && ParseNumber(parts[0]) is { } asset
                    ? new Asset(asset)
                    : throw Bad(text, "fleetmate://asset/<id>");

            case "ticket":
                return parts.Length == 1 && ParseNumber(parts[0]) is { } ticket
                    ? new Ticket(ticket)
                    : throw Bad(text, "fleetmate://ticket/<id>");

            case "user":
                return parts.Length == 1 && parts[0].Length > 0
                    ? new User(parts[0])
                    : throw Bad(text, "fleetmate://user/<id or UPN>");

            case "group":
                return parts.Length == 1 && parts[0].Length > 0
                    ? new Group(parts[0])
                    : throw Bad(text, "fleetmate://group/<id>");

            case "open":
                var target = QueryValue(query, "url");
                if (target == null || !Uri.TryCreate(target, UriKind.Absolute, out var web))
                    throw Bad(text, "fleetmate://open?url=<web URL>");
                return ParseWeb(web);

            default:
                throw new FleetMateLinkException(FleetMateLinkError.UnknownRoute, route.Length == 0 ? text : route);
        }
    }

    /// <summary>Parse, returning null and the reason instead of throwing.</summary>
    public static FleetMateLink? TryParse(string link, out FleetMateLinkException? error)
    {
        try { error = null; return Parse(link); }
        catch (FleetMateLinkException ex) { error = ex; return null; }
    }

    /// <summary>
    /// A web URL from Azure DevOps or GitHub. Azure DevOps is recognised by its
    /// path shape (/_git/, /_build, /_workitems/), never its host, so any
    /// server or organization works.
    /// </summary>
    public static FleetMateLink ParseWeb(Uri url)
    {
        var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var query = url.Query.TrimStart('?');

        if (url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
        {
            var (owner, repo, kind) = (parts[0], parts[1], parts[2]);
            switch (kind)
            {
                case "pull" when ParseNumber(parts[3]) is { } n: return new PullRequest(new GitHub(owner, repo), n);
                case "commit" when IsSha(parts[3]): return new Commit(new GitHub(owner, repo), parts[3]);
                case "issues" when ParseNumber(parts[3]) is { } n: return new GitHubIssue(owner, repo, n);
                case "actions" when parts.Length >= 5 && parts[3] == "runs" && ParseNumber(parts[4]) is { } id:
                    return new GitHubRun(owner, repo, id);
            }
            throw new FleetMateLinkException(FleetMateLinkError.UnsupportedWebUrl, url.ToString());
        }

        // …/<org>/<project>/_git/<repo>/pullrequest/<id>, and the same with the
        // org as a subdomain (no org segment).
        var git = Array.IndexOf(parts, "_git");
        if (git >= 1 && parts.Length > git + 3)
        {
            var (project, repo, kind, value) = (parts[git - 1], parts[git + 1], parts[git + 2].ToLowerInvariant(), parts[git + 3]);
            if (kind == "pullrequest" && ParseNumber(value) is { } n) return new PullRequest(new AzureDevOps(project, repo), n);
            if (kind == "commit" && IsSha(value)) return new Commit(new AzureDevOps(project, repo), value);
        }

        var build = Array.FindIndex(parts, p => p.StartsWith("_build", StringComparison.OrdinalIgnoreCase));
        if (build >= 1)
        {
            if (ParseNumber(QueryValue(query, "buildId")) is { } run) return new AzureDevOpsRun(parts[build - 1], run);
            if (ParseNumber(QueryValue(query, "definitionId")) is { } def) return new AzureDevOpsPipeline(parts[build - 1], def);
        }

        var edit = Array.IndexOf(parts, "edit");
        if (edit >= 1 && parts[edit - 1].Equals("_workitems", StringComparison.OrdinalIgnoreCase)
            && parts.Length > edit + 1 && ParseNumber(parts[edit + 1]) is { } workItem)
            return new WorkItem(workItem);

        throw new FleetMateLinkException(FleetMateLinkError.UnsupportedWebUrl, url.ToString());
    }

    // ── Build ────────────────────────────────────────────────────────────

    /// <summary>The link's canonical <c>fleetmate://</c> form.</summary>
    public string ToLink()
    {
        static string E(string s) => Uri.EscapeDataString(s);
        var path = this switch
        {
            PullRequest { Source: AzureDevOps a } p => $"pull/{E(a.Project)}/{E(a.Repo)}/{p.Number}",
            PullRequest { Source: GitHub g } p => $"pull/github/{E(g.Owner)}/{E(g.Repo)}/{p.Number}",
            Commit { Source: AzureDevOps a } c => $"commit/{E(a.Project)}/{E(a.Repo)}/{c.Sha}",
            Commit { Source: GitHub g } c => $"commit/github/{E(g.Owner)}/{E(g.Repo)}/{c.Sha}",
            AzureDevOpsRun r => $"pipeline/{E(r.Project)}/{r.RunId}",
            AzureDevOpsPipeline d => $"pipeline/{E(d.Project)}/definition/{d.DefinitionId}",
            GitHubRun r => $"pipeline/github/{E(r.Owner)}/{E(r.Repo)}/{r.RunId}",
            WorkItem w => $"workitem/{w.Id}",
            GitHubIssue i => $"issue/github/{E(i.Owner)}/{E(i.Repo)}/{i.Number}",
            Device d => $"device/{E(d.IntuneId)}",
            Asset a => $"asset/{a.Id}",
            Ticket t => $"ticket/{t.Id}",
            User u => $"user/{E(u.Id)}",
            Group g => $"group/{E(g.Id)}",
            _ => throw new InvalidOperationException("Unknown link kind."),
        };
        return $"{Scheme}://{path}";
    }

    public static bool IsSha(string s) => s.Length is >= 7 and <= 40 && s.All(Uri.IsHexDigit);

    private static int? ParseNumber(string? s) => int.TryParse(s, out var n) && n > 0 ? n : null;

    private static string? QueryValue(string query, string name)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return null;
    }

    private static FleetMateLinkException Bad(string link, string expected) =>
        new(FleetMateLinkError.Malformed, link, expected);
}

public enum FleetMateLinkError { NotFleetMate, UnknownRoute, Malformed, UnsupportedWebUrl }

/// <summary>Why a link can't be opened, worded for the banner that shows it.</summary>
public sealed class FleetMateLinkException : Exception
{
    public FleetMateLinkError Kind { get; }
    public string Link { get; }
    public string? Expected { get; }

    public FleetMateLinkException(FleetMateLinkError kind, string link, string? expected = null)
        : base(Describe(kind, link, expected))
    {
        Kind = kind;
        Link = link;
        Expected = expected;
    }

    private static string Describe(FleetMateLinkError kind, string link, string? expected) => kind switch
    {
        FleetMateLinkError.NotFleetMate => $"Not a fleetmate: link: {link}",
        FleetMateLinkError.UnknownRoute => $"FleetMate doesn't know the link \"{link}\". Links open pull, commit, pipeline, workitem, issue, device, asset, ticket, user, group or open?url=.",
        FleetMateLinkError.Malformed => $"The link {link} is incomplete. Expected {expected}.",
        _ => $"FleetMate can't open {link}. It takes Azure DevOps or GitHub pull request, commit, pipeline, work item and issue URLs.",
    };
}
