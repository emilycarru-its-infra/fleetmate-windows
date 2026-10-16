using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Shared;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// Reads the task templates TeamDynamix offers for a ticket from its own Add
/// Task Template page, in a never-shown WebView2 that uses the signed-in web
/// session's profile. The page is only read — the script reads the
/// <c>#ddlTaskTemplate</c> options and nothing else — and never submitted.
/// A sign-in page, an error page or a page without the list is a failure,
/// never an empty catalog. Call on the UI thread.
/// </summary>
internal static class TaskTemplateCatalog
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    public static async Task<IReadOnlyList<TaskTemplateChoice>> LoadAsync(TdxConfig config, int ticketId, CancellationToken ct)
    {
        var page = TdxTaskTemplatePicker.PageUrl(config, ticketId)
                   ?? throw new TdxTicketTaskException("No HTTPS TeamDynamix web address is configured, so the task template list cannot be read.");

        HiddenWebView browser;
        try
        {
            browser = await HiddenWebView.CreateAsync();
        }
        catch (Exception ex)
        {
            Log.Warning("[tdx] Tasks: could not start the hidden browser for the template list ({Kind})", ex.GetType().Name);
            throw new TdxTicketTaskException("FleetMate could not start its browser to read the task template list.", null, ex);
        }

        using (browser)
        {
            var core = browser.Core;
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) =>
            {
                if (done.Task.IsCompleted) return;
                // Only the page asked for is read. Anything else — a step of
                // the single sign-on chain, a sign-in or error page — is never
                // read; if the page is not reached in time the read fails.
                if (!e.IsSuccess || !TdxTaskTemplatePicker.IsPickerPage(page, core.Source)) return;
                _ = ReadAsync();

                async Task ReadAsync()
                {
                    try
                    {
                        done.TrySetResult(await core.ExecuteScriptAsync(TdxTaskTemplatePicker.ReadOptionsScript));
                    }
                    catch (Exception ex)
                    {
                        done.TrySetException(ex);
                    }
                }
            };

            core.Navigate(page.ToString());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var finished = await Task.WhenAny(done.Task, Task.Delay(System.Threading.Timeout.Infinite, timeout.Token));
            ct.ThrowIfCancellationRequested();
            if (finished != done.Task)
            {
                Log.Information("[tdx] Tasks: the template page for ticket {Ticket} was not reached signed in", ticketId);
                throw new TdxTicketTaskException(
                    "TeamDynamix's task template page did not open signed in. Sign in to TeamDynamix again, then retry.");
            }

            string answer;
            try
            {
                answer = await done.Task;
            }
            catch (Exception ex)
            {
                Log.Warning("[tdx] Tasks: reading the template list failed ({Kind})", ex.GetType().Name);
                throw new TdxTicketTaskException("FleetMate could not read TeamDynamix's task template list.", null, ex);
            }
            return TdxTaskTemplatePicker.ParseOptions(answer);
        }
    }
}
