using System.Windows;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Shared;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// The workflows TeamDynamix offers for a ticket, read from its own Reassign
/// Workflow page in a never-shown WebView2 (<see cref="TdxWorkflowPicker"/>).
///
/// The browser uses the sign-in profile, so the page opens in the operator's
/// own TeamDynamix web session, the one the hidden sign-in left behind. Nothing
/// about that session changes here: no sign-in page is answered, no window can
/// open, no cookie leaves the browser, and the form is never submitted. Only a
/// document that is exactly the asked-for ticket's picker, on the configured
/// origin, is read, and only its workflow drop-down. Anything else — a sign-in
/// page, an error page, a page without the drop-down, no answer in time — is a
/// failure the operator sees, never an empty list.
///
/// Every call reads the page afresh; nothing is cached.
/// </summary>
internal sealed class TdxWorkflowPickerCatalog : ITicketWorkflowCatalog
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Redirects the page may follow before it is given up on. Enough for a
    /// session renewal that completes by itself; a chain that needs an answer
    /// stops at its first page and times out instead.
    /// </summary>
    private const int MaxNavigations = 12;

    private readonly TdxConfig _config;
    private readonly SemaphoreSlim _one = new(1, 1);

    public TdxWorkflowPickerCatalog(TdxConfig config) => _config = config;

    public async Task<IReadOnlyList<TdxWorkflowChoice>> GetAssignableWorkflowsAsync(int ticketId, CancellationToken ct = default)
    {
        var page = TdxWorkflowPicker.PageUrl(_config, ticketId)
            ?? throw new TdxWorkflowException("No TeamDynamix web address is configured, so its workflow list cannot be read.");

        var dispatcher = Application.Current?.Dispatcher
            ?? throw new TdxWorkflowException("The workflow list can only be read while the app is running.");
        // The browser lives on the UI thread.
        if (!dispatcher.CheckAccess())
            return await dispatcher.InvokeAsync(() => GetAssignableWorkflowsAsync(ticketId, ct)).Task.Unwrap();

        await _one.WaitAsync(ct);
        try
        {
            return await ReadAsync(page, ticketId, ct);
        }
        finally
        {
            _one.Release();
        }
    }

    private static async Task<IReadOnlyList<TdxWorkflowChoice>> ReadAsync(Uri page, int ticketId, CancellationToken ct)
    {
        HiddenWebView browser;
        try
        {
            browser = await HiddenWebView.CreateAsync();
        }
        catch (Exception ex)
        {
            Log.Warning("[tdx] Workflow list for ticket {Id}: browser did not start ({Kind})", ticketId, ex.GetType().Name);
            throw new TdxWorkflowException("The workflow list could not be read: the browser it needs did not start.", null, ex);
        }

        var done = new TaskCompletionSource<IReadOnlyList<TdxWorkflowChoice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigations = 0;
        string? stoppedAt = null;

        using (browser)
        {
            var core = browser.Core;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, e) =>
            {
                // Web pages only, and not without end.
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var next) || next.Scheme != Uri.UriSchemeHttps
                    || ++navigations > MaxNavigations)
                {
                    e.Cancel = true;
                    done.TrySetException(new TdxWorkflowException(
                        "TeamDynamix did not open its workflow list. Your TeamDynamix web session may have expired; sign in to TeamDynamix again and retry."));
                }
            };
            core.NavigationCompleted += async (_, e) =>
            {
                if (done.Task.IsCompleted) return;
                var source = core.Source;
                if (!TdxWorkflowPicker.IsPickerPage(page, source))
                {
                    // Somewhere on the way, or a page that is not the picker:
                    // never read. Only its path is kept for the failure.
                    stoppedAt = Uri.TryCreate(source, UriKind.Absolute, out var at) ? at.AbsolutePath : null;
                    return;
                }
                if (!e.IsSuccess)
                {
                    done.TrySetException(new TdxWorkflowException(e.HttpStatusCode is 401 or 403
                        ? "You do not have permission to change the workflow on this ticket."
                        : $"TeamDynamix could not show its workflow list for ticket {ticketId}{(e.HttpStatusCode > 0 ? $" (HTTP {e.HttpStatusCode})" : "")}.",
                        e.HttpStatusCode > 0 ? e.HttpStatusCode : null));
                    return;
                }
                try
                {
                    var answer = await core.ExecuteScriptAsync(TdxWorkflowPicker.ReadOptionsScript);
                    // The page may have moved on while the script ran.
                    if (!TdxWorkflowPicker.IsPickerPage(page, core.Source)) return;
                    done.TrySetResult(TdxWorkflowPicker.ParseOptions(answer));
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex as TdxWorkflowException
                        ?? new TdxWorkflowException("The workflow list could not be read from TeamDynamix.", null, ex));
                }
            };

            core.Navigate(page.ToString());

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var finished = await Task.WhenAny(done.Task, Task.Delay(System.Threading.Timeout.Infinite, timeout.Token));
            if (finished == done.Task)
            {
                try
                {
                    var choices = await done.Task;
                    Log.Information("[tdx] Workflow list for ticket {Id}: {Count} choices", ticketId, choices.Count);
                    return choices;
                }
                catch (TdxWorkflowException)
                {
                    Log.Warning("[tdx] Workflow list for ticket {Id} not read", ticketId);
                    throw;
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        Log.Warning("[tdx] Workflow list for ticket {Id}: no picker page in time (stopped at {Path})", ticketId, stoppedAt ?? "start");
        throw new TdxWorkflowException(
            "TeamDynamix did not show its workflow list in time. Your TeamDynamix web session may have expired; sign in to TeamDynamix again and retry.");
    }
}
