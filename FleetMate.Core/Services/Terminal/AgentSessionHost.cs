using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Services.Repos;
using Serilog;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// What the terminal needs from the rest of the app before a session starts:
/// where it should open, the brief that tells its agent what FleetMate is and
/// where it is, and agent CLIs kept current so the session opens straight on
/// the agent rather than on its updater. Both desktop apps build one at launch.
/// </summary>
public sealed class AgentSessionHost
{
    private readonly Func<TerminalSettings> _settings;
    private readonly Func<AuthManager?> _auth;
    private readonly SynchronizationContext? _ui;
    private bool _started;

    /// <param name="context">The FLEETMATE_CONTEXT file every session points at.</param>
    /// <param name="settings">Settings ▸ Terminal as it is now.</param>
    /// <param name="auth">The sign-ins as they are now; read for the context file's backends.</param>
    /// <param name="ui">The UI thread, where environment changes are published. Defaults to the caller's.</param>
    public AgentSessionHost(AppContextFile context, Func<TerminalSettings> settings, Func<AuthManager?> auth,
        SynchronizationContext? ui = null)
    {
        Context = context;
        _settings = settings;
        _auth = auth;
        _ui = ui ?? SynchronizationContext.Current;
        Brief = new AgentBriefStore(AgentBriefStore.DefaultDirectory);
        Updater = new AgentCliUpdateModel(() => Settings.KeepClisCurrent, _ui);
    }

    public AppContextFile Context { get; }
    public AgentBriefStore Brief { get; }
    public AgentCliUpdateModel Updater { get; }

    private TerminalSettings Settings => _settings();

    /// <summary>
    /// Generate the brief ahead of the first session, clear session briefs a
    /// previous run left, start the update schedule, and keep the context
    /// file's tracked repositories and sign-ins current. Once, at launch.
    /// </summary>
    public void Start()
    {
        if (_started || AppEdition.Current.IsTicketsOnly) return;
        _started = true;
        // Owner-only before the context file is first written into it.
        try { PrivateFile.EnsureDirectory(Brief.Directory); }
        catch (Exception ex) { Log.Warning(ex, "Agent brief: could not restrict {Folder}", Brief.Directory); }
        _ = Task.Run(() =>
        {
            try
            {
                Brief.ClearSessions();
                Brief.Refresh(CliPath());
            }
            catch (Exception ex) { Log.Warning(ex, "Agent brief: could not prepare"); }
        });
        Updater.Start();
        PublishEnvironment();
        if (_auth() is { } auth)
            auth.PropertyChanged += (_, _) =>
            {
                if (_ui == null) PublishEnvironment();
                else _ui.Post(_ => PublishEnvironment(), null);
            };
    }

    /// <summary>The installed fleetmate.exe, or one built beside the app.</summary>
    private static string? CliPath() =>
        TerminalEnvironment.FindCliDirectory(AppContext.BaseDirectory) is { } dir ? Path.Combine(dir, "fleetmate.exe") : null;

    // ── Where a session opens ────────────────────────────────────────────

    /// <summary>
    /// Prefer the selected pull request's local checkout; otherwise use the
    /// shared per-user workspace with generated guide and operator instructions.
    /// </summary>
    public string StartDirectory()
    {
        var context = Context.Current;
        if (context.Tab == "Development"
            && context.Selection.TryGetValue("pullRequest", out var pulls) && pulls.FirstOrDefault() is { } pr
            && pr.Fields.TryGetValue("repository", out var name) && name != null)
        {
            var repoName = name.Split('/').Last();
            var registered = (Registry()?.Entries ?? Array.Empty<RepoRegistryEntry>())
                .Where(e => string.Equals(e.Key.Name, repoName, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Path);
            var listed = TrackedRepositories()
                .Where(r => string.Equals(r.Name.Split('/').Last(), repoName, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Path);
            if (registered.Concat(listed).FirstOrDefault(Directory.Exists) is { } checkout) return checkout;
        }
        return WorkspaceDirectory();
    }

    /// <summary>The registry of checkouts the app and `fleetmate repos` share, or null when it cannot be read.</summary>
    private static RepoRegistryDocument? Registry()
    {
        try { return new RepoRegistryStore().Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// The stable per-user workspace; repository sessions choose their own directory.
    /// </summary>
    public static string WorkspaceDirectory()
    {
        var workspace = AgentWorkspace.DefaultDirectory;
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    // ── What a session is told ───────────────────────────────────────────

    /// <summary>
    /// The session's command, with the brief handed to its agent, and the
    /// variables it starts with. Blocking (a stat, or a short CLI run after
    /// the CLI changed), so call it off the UI thread.
    /// </summary>
    public (TerminalCommand Command, Dictionary<string, string> Environment) Prepare(
        string sessionId, TerminalCommand command, string workingDirectory, AgentWhereabouts place)
    {
        Brief.Refresh(CliPath());
        var brief = Brief.WriteSessionBrief(sessionId, place with { WorkingDirectory = workingDirectory });
        var managed = Settings.KeepClisCurrent;
        if (managed && AgentBrief.AgentOf(command.FileName) != null) Updater.UpdateIfStale();
        var workspace = WorkspaceDirectory();
        var prepared = AgentSessionLaunch.Prepare(command, brief, managed, false, AgentCommands.FindOnPath,
            new[] { workspace, Brief.Directory });
        var instructions = AgentWorkspace.Prepare(workspace, Brief.BriefPath, Context.Path);
        PrivateFile.EnsureDirectory(AgentSessionMetadata.Directory);
        var metadata = AgentSessionMetadata.FilePath(sessionId);
        PrivateFile.Write(metadata, "{}");
        prepared.Environment["FLEETMATE_SESSION"] = metadata;
        prepared.Environment["FLEETMATE_WORKSPACE"] = workspace;
        prepared.Environment["FLEETMATE_INSTRUCTIONS"] = instructions;
        return prepared;
    }

    public void Release(string sessionId)
    {
        Brief.RemoveSessionBrief(sessionId);
        try { File.Delete(AgentSessionMetadata.FilePath(sessionId)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Tab → the selection kind it publishes.</summary>
    private static readonly Dictionary<string, string> SelectionKinds = new()
    {
        ["Devices"] = "device", ["Inventory"] = "asset", ["Tickets"] = "ticket",
        ["Projects"] = "workItem", ["Development"] = "pullRequest",
    };

    /// <summary>The app as it is now. Call on the UI thread.</summary>
    public AgentWhereabouts Whereabouts()
    {
        var context = Context.Current;
        AgentWhereabouts.Selected? selected = null;
        if (SelectionKinds.TryGetValue(context.Tab, out var kind)
            && context.Selection.TryGetValue(kind, out var items) && items.Count > 0)
            selected = new AgentWhereabouts.Selected(kind, items.Count == 1 ? items[0].Id : $"{items[0].Id} and {items.Count - 1} more");
        return new AgentWhereabouts
        {
            Module = context.Tab,
            Segment = context.Segment,
            Selection = selected,
            TrackedRepositories = TrackedRepositories(),
            Backends = Backends(),
        };
    }

    /// <summary>Rewrite the context file's tracked repositories and sign-ins.</summary>
    public void PublishEnvironment()
    {
        try { Context.SetEnvironment(TrackedRepositories(), Backends()); }
        catch (Exception ex) { Log.Debug(ex, "Agent context: could not publish the environment"); }
    }

    /// <summary>
    /// The tracked checkouts in the registry `fleetmate repos` shares, then
    /// the repos Settings › Terminal lists that are not among them, each with
    /// its origin where it has one.
    /// </summary>
    private List<AgentRepository> TrackedRepositories()
    {
        var tracked = (Registry()?.TrackedEntries ?? Array.Empty<RepoRegistryEntry>())
            .Select(e => new AgentRepository(e.Key.DisplayName, e.Path, e.RemoteUrl, e.DefaultBranch))
            .ToList();
        foreach (var entry in Settings.EffectiveRepos)
        {
            var r = RepoLocator.Resolve(entry, RepoLocator.DefaultRoot);
            if (tracked.Any(t => string.Equals(t.Path.TrimEnd('\\'), r.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) continue;
            tracked.Add(new AgentRepository(r.Name, r.Path, r.CloneUrl ?? GitOrigin.Read(r.Path)));
        }
        return tracked;
    }

    /// <summary>
    /// Every configured system and whether it is signed in. Account names,
    /// failure detail and anything token-like stay out: an agent needs to
    /// know a sign-in is missing, not whose it is or why.
    /// </summary>
    private List<AgentBackend> Backends() => Backends(_auth());

    internal static List<AgentBackend> Backends(AuthManager? auth) =>
        (auth?.Systems.Values.ToList() ?? new())
            .Where(s => s.State.Kind != AuthStateKind.NotConfigured)
            .OrderBy(s => s.SystemId.DisplayName())
            .Select(s => new AgentBackend(s.SystemId.DisplayName(), s.State.Kind switch
            {
                AuthStateKind.Valid => "signed in",
                AuthStateKind.Configured => "configured, not yet checked",
                AuthStateKind.Authenticating => "signing in",
                AuthStateKind.Expired => "sign-in expired",
                AuthStateKind.ServicePrincipal => "signed in as a service principal",
                _ => "not signed in",
            }))
            .ToList();
}
