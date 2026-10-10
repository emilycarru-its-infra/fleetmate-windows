using System.IO;
using System.Windows;
using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Services.Agent;
using FleetMate.Core.Services.Terminal;
using Serilog;

namespace FleetMate.GUI.Views.Terminal;

/// <summary>
/// What the terminal needs from the rest of the app before a session starts:
/// where it should open, the brief that tells its agent what FleetMate is and
/// where it is, and agent CLIs kept current so the session opens straight on
/// the agent rather than on its updater.
/// </summary>
public sealed class AgentSessions
{
    private readonly App _app;
    private bool _started;
    private volatile bool _codexNoDaemon;

    public AgentSessions(App app)
    {
        _app = app;
        Brief = new AgentBriefStore(AgentBriefStore.DefaultDirectory);
        Updater = new AgentCliUpdateModel(() => Settings.KeepClisCurrent);
    }

    public AgentBriefStore Brief { get; }
    public AgentCliUpdateModel Updater { get; }

    private TerminalSettings Settings => _app.Config.Terminal;

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
                _codexNoDaemon = CodexHasNoDaemon();
            }
            catch (Exception ex) { Log.Warning(ex, "Agent brief: could not prepare"); }
        });
        Updater.Start();
        PublishEnvironment();
        _app.AuthManager.PropertyChanged += (_, _) => _app.Dispatcher.BeginInvoke(PublishEnvironment);
    }

    /// <summary>The installed fleetmate.exe, or one built beside the app.</summary>
    private static string? CliPath() =>
        TerminalEnvironment.FindCliDirectory(AppContext.BaseDirectory) is { } dir ? Path.Combine(dir, "fleetmate.exe") : null;

    /// <summary>
    /// Whether this Codex has --no-daemon. Any -c override makes Codex warn
    /// at start about running without its background server; saying
    /// --no-daemon outright avoids that, where the flag exists.
    /// </summary>
    private static bool CodexHasNoDaemon()
    {
        if (AgentCommands.FindOnPath("codex") is not { } codex) return false;
        var output = AgentCliUpdater.DefaultRunner(new AgentCliCommand(codex, new[] { "--help" })).GetAwaiter().GetResult();
        return output.Succeeded && output.Stdout.Contains("--no-daemon", StringComparison.Ordinal);
    }

    // ── Where a session opens ────────────────────────────────────────────

    /// <summary>
    /// Where a session opens when nothing chose a folder: the checkout of the
    /// selected pull request's repository while Development is on screen,
    /// else the folder repositories are cloned into, else FleetMate's own
    /// folder. Never the bare home folder, which tells an agent nothing.
    /// </summary>
    public string StartDirectory()
    {
        var context = _app.Context.Current;
        if (context.Tab == "Development"
            && context.Selection.TryGetValue("pullRequest", out var pulls) && pulls.FirstOrDefault() is { } pr
            && pr.Fields.TryGetValue("repository", out var name) && name != null)
        {
            var repo = TrackedRepositories().FirstOrDefault(r =>
                string.Equals(r.Name, name.Split('/').Last(), StringComparison.OrdinalIgnoreCase));
            if (repo != null && Directory.Exists(repo.Path)) return repo.Path;
        }
        return WorkspaceDirectory();
    }

    /// <summary>The repos folder when it exists, else FleetMate's per-user folder.</summary>
    public static string WorkspaceDirectory()
    {
        if (Directory.Exists(RepoLocator.DefaultRoot)) return RepoLocator.DefaultRoot;
        var own = AppEdition.Current.UserDirectory;
        try { Directory.CreateDirectory(own); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return own;
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
        return AgentSessionLaunch.Prepare(command, brief, managed, _codexNoDaemon, AgentCommands.FindOnPath);
    }

    public void Release(string sessionId) => Brief.RemoveSessionBrief(sessionId);

    /// <summary>Tab → the selection kind it publishes.</summary>
    private static readonly Dictionary<string, string> SelectionKinds = new()
    {
        ["Devices"] = "device", ["Inventory"] = "asset", ["Tickets"] = "ticket",
        ["Projects"] = "workItem", ["Development"] = "pullRequest",
    };

    /// <summary>The app as it is now. Call on the UI thread.</summary>
    public AgentWhereabouts Whereabouts()
    {
        var context = _app.Context.Current;
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
        try { _app.Context.SetEnvironment(TrackedRepositories(), Backends()); }
        catch (Exception ex) { Log.Debug(ex, "Agent context: could not publish the environment"); }
    }

    /// <summary>The repos Settings › Terminal lists, with each checkout's origin where it has one.</summary>
    private List<AgentRepository> TrackedRepositories() =>
        Settings.EffectiveRepos
            .Select(entry => RepoLocator.Resolve(entry, RepoLocator.DefaultRoot))
            .Select(r => new AgentRepository(r.Name, r.Path, r.CloneUrl ?? GitOrigin.Read(r.Path)))
            .ToList();

    /// <summary>
    /// Every configured system and whether it is signed in. Account names,
    /// failure detail and anything token-like stay out: an agent needs to
    /// know a sign-in is missing, not whose it is or why.
    /// </summary>
    private List<AgentBackend> Backends() =>
        _app.AuthManager.Systems.Values.ToList()
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
