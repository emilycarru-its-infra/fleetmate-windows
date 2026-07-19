using System.CommandLine;
using System.Diagnostics;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services.Devices;
using Spectre.Console;

namespace FleetMate.Commands.Devices;

/// <summary>
/// Native quality testing — dispatches to QaService / pkgsinfo validation and
/// records results in checklist.md via ChecklistService. Fully native: no
/// PowerShell / control.ps1 shell-out (the sole remaining quality/ dependency).
/// </summary>
public static class TestCommand
{
    public static Command Create(FleetMateConfig config)
    {
        var command = new Command("test", "Run native quality tests on a package (QA/lint; writes checklist.md)");

        var packageArg = new Argument<string?>(
            name: "package",
            getDefaultValue: () => null,
            description: "Package to test (optional — lints all packages if omitted)");

        var categoryOption = new Option<string>(
            aliases: new[] { "--category", "-c" },
            getDefaultValue: () => "all",
            description: "Test category: lint (validate pkgsinfo only), systems, deployment, all");

        var fixOption = new Option<bool>(new[] { "--fix", "-f" }, "Apply auto-fixes where possible");
        var dryRunOption = new Option<bool>(new[] { "--dry-run", "-n" }, "Show what would be done without making changes or writing the checklist");
        var installOnlyOption = new Option<bool>(new[] { "--install-only" }, "Skip build, test installation only");
        var uninstallFirstOption = new Option<bool>(new[] { "--uninstall-first" }, "Uninstall before testing installation");
        var noChecklistOption = new Option<bool>(new[] { "--no-checklist" }, "Do not record results in checklist.md");

        command.AddArgument(packageArg);
        command.AddOption(categoryOption);
        command.AddOption(fixOption);
        command.AddOption(dryRunOption);
        command.AddOption(installOnlyOption);
        command.AddOption(uninstallFirstOption);
        command.AddOption(noChecklistOption);

        command.SetHandler(async (context) =>
        {
            var package = context.ParseResult.GetValueForArgument(packageArg);
            var category = context.ParseResult.GetValueForOption(categoryOption) ?? "all";
            var fix = context.ParseResult.GetValueForOption(fixOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var installOnly = context.ParseResult.GetValueForOption(installOnlyOption);
            var uninstallFirst = context.ParseResult.GetValueForOption(uninstallFirstOption);
            var noChecklist = context.ParseResult.GetValueForOption(noChecklistOption);
            await ExecuteAsync(config, package, category, fix, dryRun, installOnly, uninstallFirst, noChecklist);
        });

        return command;
    }

    private static async Task ExecuteAsync(
        FleetMateConfig config, string? package, string category,
        bool fix, bool dryRun, bool installOnly, bool uninstallFirst, bool noChecklist)
    {
        var qa = new QaService(config);
        var checklistPath = Path.Combine(config.ResolvePath(config.QualityPath), "checklist.md");
        var checklist = new ChecklistService(config.ResolvePath(config.PkgsinfoPath));
        var user = GetGitUser();
        var lintOnly = category.Equals("lint", StringComparison.OrdinalIgnoreCase);

        // No package -> lint every package (safe: validates pkgsinfo, never mass-installs).
        if (string.IsNullOrEmpty(package))
        {
            AnsiConsole.Write(new Rule("[cyan]Linting all packages[/]").LeftJustified());
            AnsiConsole.WriteLine();
            var packages = qa.GetAllPackages();
            int pass = 0, warn = 0, fail = 0;
            foreach (var pkg in packages.OrderBy(p => p.BasePackageName ?? p.Name))
            {
                var name = pkg.BasePackageName ?? pkg.Name;
                var yaml = pkg.YamlPath ?? pkg.PkgInfoPath;
                if (string.IsNullOrEmpty(yaml) || !File.Exists(yaml)) continue;

                var status = StatusOf(qa.ValidatePkgInfo(yaml));
                Report(name, status);
                if (status == ChecklistStatus.Passed) pass++; else if (status == ChecklistStatus.Warning) warn++; else fail++;
                if (!noChecklist && !dryRun) TryUpdateChecklist(checklist, checklistPath, name, status, user);
            }
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]{pass} passed[/], [yellow]{warn} warnings[/], [red]{fail} failed[/] of {packages.Count}");
            ShowChecklistSummary(checklist, checklistPath);
            return;
        }

        // Package + lint category -> validate pkgsinfo only.
        if (lintOnly)
        {
            var loc = qa.FindPackageLocation(package);
            var yaml = loc.YamlPath ?? loc.PkgInfoPath;
            if (string.IsNullOrEmpty(yaml) || !File.Exists(yaml))
            {
                AnsiConsole.MarkupLine($"[yellow]No pkgsinfo found for {Markup.Escape(package)}[/]");
                return;
            }
            var result = qa.ValidatePkgInfo(yaml);
            DisplayValidation(package, result);
            var status = StatusOf(result);
            if (!noChecklist && !dryRun) TryUpdateChecklist(checklist, checklistPath, package, status, user);
            return;
        }

        // Package -> full native QA (same engine as `fleetmate qa`).
        var options = new QaOptions
        {
            DryRun = dryRun,
            Fix = fix,
            InstallOnly = installOnly,
            UninstallFirst = uninstallFirst,
            Category = ParseCategory(category),
        };

        AnsiConsole.Write(new Rule($"[cyan]Testing {Markup.Escape(package)}[/]").LeftJustified());
        AnsiConsole.WriteLine();

        QaResult qaResult = null!;
        await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Running quality control...", async _ =>
        {
            qaResult = await qa.RunPackageQaAsync(package, options);
        });

        if (!qaResult.Location.Found)
        {
            AnsiConsole.MarkupLine($"[yellow]Package not found: {Markup.Escape(package)}[/]");
            return;
        }

        DisplayQaResult(qaResult);
        var qaStatus = !qaResult.Success ? ChecklistStatus.Failed
            : qaResult.Steps.Any(s => s.Warnings.Any()) ? ChecklistStatus.Warning
            : ChecklistStatus.Passed;
        if (!noChecklist && !dryRun) TryUpdateChecklist(checklist, checklistPath, package, qaStatus, user);
    }

    private static ChecklistStatus StatusOf(YamlValidationResult v)
        => !v.IsValid ? ChecklistStatus.Failed : v.Warnings.Any() ? ChecklistStatus.Warning : ChecklistStatus.Passed;

    private static QaCategory ParseCategory(string category) => category.ToLowerInvariant() switch
    {
        "unit" => QaCategory.Unit,
        "systems" => QaCategory.Systems,
        "lint" => QaCategory.Lint,
        "autofix" => QaCategory.Autofix,
        "deployment" => QaCategory.Deployment,
        _ => QaCategory.All,
    };

    private static void Report(string name, ChecklistStatus status)
    {
        var (color, glyph) = status switch
        {
            ChecklistStatus.Failed => ("red", "✗"),
            ChecklistStatus.Warning => ("yellow", "⚠"),
            _ => ("green", "✓"),
        };
        AnsiConsole.MarkupLine($"  [{color}]{glyph}[/] {Markup.Escape(name)}");
    }

    private static void DisplayValidation(string package, YamlValidationResult v)
    {
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(package)}[/] — {(v.IsValid ? "[green]valid[/]" : "[red]invalid[/]")}");
        foreach (var e in v.Errors) AnsiConsole.MarkupLine($"  [red]✗ {Markup.Escape(e)}[/]");
        foreach (var w in v.Warnings) AnsiConsole.MarkupLine($"  [yellow]⚠ {Markup.Escape(w)}[/]");
    }

    private static void DisplayQaResult(QaResult result)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Step").AddColumn("Status").AddColumn("Details");
        foreach (var step in result.Steps)
        {
            var status = step.Success ? "[green]✓ PASS[/]"
                : step.Severity == QaSeverity.Warning ? "[yellow]⚠ WARN[/]" : "[red]✗ FAIL[/]";
            var detail = step.Errors.Concat(step.Warnings).FirstOrDefault() ?? step.Messages.FirstOrDefault() ?? "";
            table.AddRow(Markup.Escape(step.StepName), status, Markup.Escape(detail));
        }
        AnsiConsole.Write(table);
        var overall = result.Success ? "[green]PASSED[/]" : "[red]FAILED[/]";
        AnsiConsole.MarkupLine($"{overall}  {result.Passed}/{result.Total} steps ({result.SuccessRate}%)");
    }

    private static void ShowChecklistSummary(ChecklistService checklist, string checklistPath)
    {
        if (!File.Exists(checklistPath)) return;
        var s = checklist.GetSummary(checklistPath);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]Checklist:[/] {s.TotalTested}/{s.TotalItems} tested" +
            (s.NextUntested != null ? $" — next: [white]{Markup.Escape(s.NextUntested.Name)}[/]" : " — [green]all tested[/]"));
    }

    private static void TryUpdateChecklist(ChecklistService checklist, string checklistPath, string name, ChecklistStatus status, string? user)
    {
        if (!File.Exists(checklistPath)) return;
        try { checklist.UpdateItem(checklistPath, name, status, note: null, timestamp: null, user: user); }
        catch { /* checklist is best-effort; a missing item shouldn't fail the test run */ }
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
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch { return null; }
    }
}
