using System.Diagnostics;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Projects;
using Spectre.Console;

namespace FleetMate.Commands.Devices;

/// <summary>
/// Bridges a QA/test outcome to the Azure DevOps package-readiness board.
/// Shared by <c>fleetmate test</c> and <c>fleetmate qa</c> so both report a single
/// upserted work item per package. Best-effort: a board failure never fails the run.
/// </summary>
public static class BoardSyncHelper
{
    /// <summary>
    /// Sync one package's readiness to the board and print a one-line result.
    /// Silently does nothing when DevOps isn't configured, sync is disabled,
    /// <paramref name="noBoard"/> is set, or this is a dry run.
    /// </summary>
    public static async Task ReportAsync(
        FleetMateConfig config, string package, string? version,
        ChecklistStatus status, bool dryRun, bool noBoard)
    {
        var ado = config.AzureDevOps;
        if (noBoard || dryRun) return;
        if (ado?.PackageReadiness is not { Enabled: true }) return;
        if (string.IsNullOrEmpty(ado.Organization) || string.IsNullOrEmpty(ado.Project)) return;

        try
        {
            using var svc = new AzureDevOpsService(ado);
            var readiness = new PackageReadinessService(svc, ado.PackageReadiness, ado.Project);

            ReadinessSyncResult result = null!;
            await AnsiConsole.Status().Spinner(Spinner.Known.Dots)
                .StartAsync("Syncing to DevOps board...", async _ =>
                {
                    result = await readiness.SyncAsync(package, version, status, GetGitUser());
                });

            switch (result.Action)
            {
                case ReadinessSyncAction.Created:
                    AnsiConsole.MarkupLine($"[cyan]Board:[/] created [white]#{result.WorkItemId}[/] → [white]{Markup.Escape(result.State ?? "")}[/]");
                    break;
                case ReadinessSyncAction.Updated:
                    AnsiConsole.MarkupLine($"[cyan]Board:[/] updated [white]#{result.WorkItemId}[/] → [white]{Markup.Escape(result.State ?? "")}[/]");
                    break;
                case ReadinessSyncAction.Failed:
                    AnsiConsole.MarkupLine($"[yellow]Board sync failed:[/] {Markup.Escape(result.Error ?? "unknown error")}");
                    break;
                // Disabled / NotConfigured / Skipped: stay quiet.
            }
        }
        catch (Exception ex)
        {
            // Board sync is best-effort; never fail the test/qa run over it.
            AnsiConsole.MarkupLine($"[yellow]Board sync skipped:[/] {Markup.Escape(ex.Message)}");
        }
    }

    private static string? GetGitUser()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "config user.name")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var name = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return string.IsNullOrEmpty(name) ? null : name.Split(' ')[0];
        }
        catch { return null; }
    }
}
