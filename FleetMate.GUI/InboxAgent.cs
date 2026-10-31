using System.Threading;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Serilog;

namespace FleetMate.GUI;

/// <summary>
/// Keeps the ticket inbox current while the app is closed. The same exe,
/// started at sign-in with <c>--inbox-agent</c> by the installer's Run entry.
///
/// It has no window or tray icon and never takes the single-instance lock, so
/// opening the app while it runs opens a normal window. It signs in the way
/// the app does, silently and never with a sign-in window, finds the signed-in
/// person, and runs the inbox scan on the app's schedule. It renews the
/// sign-in every 12 hours, which keeps the saved session fresh for the
/// window's next launch, and exits at once when TeamDynamix is not configured.
/// </summary>
internal sealed class InboxAgent
{
    public const string Argument = "--inbox-agent";

    private static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RenewInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan SignInRetry = TimeSpan.FromHours(1);

    private readonly App _app;
    private readonly TicketInboxStore _store = new();

    public InboxAgent(App app) => _app = app;

    public static bool IsRequested(IEnumerable<string> args) =>
        args.Contains(Argument, StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync()
    {
        if (_app.TdxService is not { } tdx || string.IsNullOrWhiteSpace(_app.Config?.Tdx?.BaseUrl))
        {
            Log.Information("[inbox-agent] Exiting: TeamDynamix is not configured");
            _app.Shutdown(0);
            return;
        }

        // One helper per person and edition; a second start (a repeated Run
        // entry, a manual launch) leaves at once.
        using var single = new Mutex(true, $@"Local\{AppEdition.Current.Name}.InboxAgent", out var first);
        if (!first)
        {
            Log.Information("[inbox-agent] Exiting: another helper is already running");
            _app.Shutdown(0);
            return;
        }

        Log.Information("[inbox-agent] Started (pid {Pid})", Environment.ProcessId);
        var renew = false;
        while (true)
        {
            try
            {
                // The first sign-in may reuse the window's saved session; each
                // later one is fresh, which renews the saved session.
                await _app.AttemptSilentTdxSsoAsync(renew);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[inbox-agent] Sign-in failed");
            }
            renew = true;

            var me = await ResolveMeAsync(tdx);
            if (me?.Uid is not { } uid || uid == Guid.Empty)
            {
                Log.Warning("[inbox-agent] Could not sign in or find the signed-in person; retrying in an hour");
                await Task.Delay(SignInRetry);
                continue;
            }

            var person = new TicketInboxPerson(uid, me.FullName ?? me.DisplayName, me.PrimaryEmail);
            var renewAt = DateTime.UtcNow + RenewInterval;
            while (DateTime.UtcNow < renewAt && tdx.IsSsoAuthenticated)
            {
                await RunPassAsync(tdx, person);
                await Task.Delay(PassInterval);
            }
        }
    }

    /// <summary>The signed-in person, retried for up to ten minutes while sign-in settles.</summary>
    private static async Task<TdxPerson?> ResolveMeAsync(TdxService tdx)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (tdx.IsSsoAuthenticated && await tdx.GetMeAsync() is { } me) return me;
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
        return null;
    }

    private async Task RunPassAsync(TdxService tdx, TicketInboxPerson person)
    {
        try
        {
            // Start from what is on disk: the window may have scanned last.
            var state = _store.Load(person.Uid);
            var scanner = new TicketInboxScanner(tdx, _store);
            if (!await scanner.TryRunPassAsync(person, state))
                Log.Debug("[inbox-agent] The app window is scanning; skipped this pass");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[inbox-agent] Inbox pass failed");
        }
    }
}
