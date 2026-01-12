using System.CommandLine;
using FleetMate.Config;
using FleetMate.Models;
using FleetMate.Services;
using Spectre.Console;

namespace FleetMate.Commands;

public static class LintCommand
{
    public static Command Create(FleetMateConfig config, PkgInfoService pkgInfo)
    {
        var command = new Command("lint", "Lint pkginfo files for issues");
        
        var packageArg = new Argument<string?>(
            name: "package",
            getDefaultValue: () => null,
            description: "Package name to lint (optional - lints all if not specified)");
        
        var fixOption = new Option<bool>(
            aliases: new[] { "--fix", "-f" },
            description: "Apply auto-fixes where possible");
        
        var errorsOnlyOption = new Option<bool>(
            aliases: new[] { "--errors-only", "-e" },
            description: "Show only errors, not warnings");
        
        command.AddArgument(packageArg);
        command.AddOption(fixOption);
        command.AddOption(errorsOnlyOption);
        
        command.SetHandler((package, fix, errorsOnly) =>
        {
            Execute(config, pkgInfo, package, fix, errorsOnly);
        }, packageArg, fixOption, errorsOnlyOption);
        
        return command;
    }
    
    private static void Execute(
        FleetMateConfig config,
        PkgInfoService pkgInfo,
        string? package,
        bool fix,
        bool errorsOnly)
    {
        IEnumerable<string> filesToLint;
        
        if (!string.IsNullOrEmpty(package))
        {
            var path = pkgInfo.FindPkgInfo(package);
            if (path == null)
            {
                AnsiConsole.MarkupLine($"[yellow]No pkginfo found for:[/] {package}");
                return;
            }
            filesToLint = new[] { path };
        }
        else
        {
            filesToLint = pkgInfo.GetAllPkgInfoPaths();
        }
        
        var results = new List<PkgInfoValidation>();
        var totalIssues = 0;
        var totalErrors = 0;
        var totalWarnings = 0;
        
        AnsiConsole.Status()
            .Start("Linting pkginfo files...", ctx =>
            {
                foreach (var file in filesToLint)
                {
                    var result = pkgInfo.ValidatePkgInfo(file);
                    
                    if (result.Issues.Any())
                    {
                        results.Add(result);
                        totalIssues += result.Issues.Count;
                        totalErrors += result.Issues.Count(i => i.Severity == ValidationSeverity.Error);
                        totalWarnings += result.Issues.Count(i => i.Severity == ValidationSeverity.Warning);
                    }
                }
            });
        
        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]✓ No issues found![/]");
            return;
        }
        
        // Display results
        AnsiConsole.MarkupLine($"\n[cyan]Lint Results:[/] {totalErrors} errors, {totalWarnings} warnings\n");
        
        foreach (var result in results.OrderByDescending(r => r.Issues.Count(i => i.Severity == ValidationSeverity.Error)))
        {
            var issues = errorsOnly 
                ? result.Issues.Where(i => i.Severity == ValidationSeverity.Error).ToList()
                : result.Issues;
            
            if (!issues.Any())
                continue;
            
            var relativePath = GetRelativePath(config, result.FilePath);
            AnsiConsole.MarkupLine($"[yellow]{relativePath}[/]");
            
            foreach (var issue in issues)
            {
                var icon = issue.Severity switch
                {
                    ValidationSeverity.Error => "[red]✗[/]",
                    ValidationSeverity.Warning => "[yellow]⚠[/]",
                    _ => "[dim]ℹ[/]"
                };
                
                var field = !string.IsNullOrEmpty(issue.Field) ? $"[dim]({issue.Field})[/] " : "";
                AnsiConsole.MarkupLine($"  {icon} {field}{Markup.Escape(issue.Message)}");
                
                if (!string.IsNullOrEmpty(issue.Suggestion))
                {
                    AnsiConsole.MarkupLine($"      [dim]→ {Markup.Escape(issue.Suggestion)}[/]");
                }
            }
            
            Console.WriteLine();
        }
        
        // Summary
        if (totalErrors > 0)
        {
            AnsiConsole.MarkupLine($"[red]✗ {totalErrors} error(s) must be fixed[/]");
        }
        
        if (fix)
        {
            AnsiConsole.MarkupLine("[dim]Auto-fix not yet implemented for lint issues[/]");
        }
    }
    
    private static string GetRelativePath(FleetMateConfig config, string fullPath)
    {
        if (config.RepoRoot != null && fullPath.StartsWith(config.RepoRoot))
        {
            return fullPath[(config.RepoRoot.Length + 1)..];
        }
        return fullPath;
    }
}
