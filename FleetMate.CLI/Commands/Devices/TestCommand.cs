using System.CommandLine;
using System.Diagnostics;
using FleetMate.Core.Config;
using Spectre.Console;

namespace FleetMate.Commands.Devices;

public static class TestCommand
{
    public static Command Create(FleetMateConfig config)
    {
        var command = new Command("test", "Run quality tests on a package");
        
        var packageArg = new Argument<string?>(
            name: "package",
            getDefaultValue: () => null,
            description: "Package name to test (optional - runs all if not specified)");
        
        var categoryOption = new Option<string>(
            aliases: new[] { "--category", "-c" },
            getDefaultValue: () => "all",
            description: "Test category: unit, systems, lint, deployment, all");
        
        var fixOption = new Option<bool>(
            aliases: new[] { "--fix", "-f" },
            description: "Apply auto-fixes where possible");
        
        var dryRunOption = new Option<bool>(
            aliases: new[] { "--dry-run", "-n" },
            description: "Show what would be done without making changes");
        
        var installOnlyOption = new Option<bool>(
            aliases: new[] { "--install-only" },
            description: "Skip build, test installation only");
        
        var uninstallFirstOption = new Option<bool>(
            aliases: new[] { "--uninstall-first" },
            description: "Uninstall before testing installation");
        
        command.AddArgument(packageArg);
        command.AddOption(categoryOption);
        command.AddOption(fixOption);
        command.AddOption(dryRunOption);
        command.AddOption(installOnlyOption);
        command.AddOption(uninstallFirstOption);
        
        command.SetHandler((package, category, fix, dryRun, installOnly, uninstallFirst) =>
        {
            Execute(config, package, category, fix, dryRun, installOnly, uninstallFirst);
        }, packageArg, categoryOption, fixOption, dryRunOption, installOnlyOption, uninstallFirstOption);
        
        return command;
    }
    
    private static void Execute(
        FleetMateConfig config,
        string? package,
        string category,
        bool fix,
        bool dryRun,
        bool installOnly,
        bool uninstallFirst)
    {
        var qualityPath = config.ResolvePath(config.QualityPath);
        var controlScript = Path.Combine(qualityPath, "control.ps1");
        
        if (!File.Exists(controlScript))
        {
            AnsiConsole.MarkupLine($"[red]Quality control script not found:[/] {controlScript}");
            return;
        }
        
        // Build PowerShell arguments
        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", controlScript };
        
        if (!string.IsNullOrEmpty(package))
        {
            args.Add("-Package");
            args.Add(package);
        }
        
        args.Add("-Category");
        args.Add(category);
        
        if (fix) args.Add("-Fix");
        if (dryRun) args.Add("-DryRun");
        if (installOnly) args.Add("-InstallOnly");
        if (uninstallFirst) args.Add("-UninstallFirst");
        
        // Show what we're running
        var packageDisplay = package ?? "(all packages)";
        AnsiConsole.Write(new Rule($"[cyan]Testing {packageDisplay}[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[dim]Category: {category}[/]");
        if (fix) AnsiConsole.MarkupLine("[dim]Auto-fix enabled[/]");
        if (dryRun) AnsiConsole.MarkupLine("[dim]Dry run mode[/]");
        Console.WriteLine();
        
        // Run PowerShell
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            UseShellExecute = false,
            WorkingDirectory = config.RepoRoot ?? Directory.GetCurrentDirectory()
        };
        
        try
        {
            using var process = Process.Start(psi);
            process?.WaitForExit();
            
            var exitCode = process?.ExitCode ?? -1;
            Console.WriteLine();
            
            if (exitCode == 0)
            {
                AnsiConsole.MarkupLine("[green]✓ Tests completed successfully[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]✗ Tests failed with exit code {exitCode}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to run tests:[/] {ex.Message}");
            AnsiConsole.MarkupLine("[dim]Make sure PowerShell 7 (pwsh) is installed[/]");
        }
    }
}
