using System.Text.RegularExpressions;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// The program behind an npm global shim. npm installs a CLI on Windows as a
/// .cmd file that runs node on the package's script, and a .cmd only runs
/// through cmd.exe, which caps a command line at 8,191 characters and reads
/// %, &amp; and quotes in its arguments its own way. Starting node on the
/// script directly passes arguments through untouched, which is what lets a
/// Codex session be handed the brief on its command line.
/// </summary>
public static partial class NpmShim
{
    [GeneratedRegex(@"""%dp0%\\(?<script>[^""]+\.(?:js|cjs|mjs))""\s+%\*", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptPattern();

    /// <summary>
    /// The command that runs <paramref name="command"/> without cmd.exe, or
    /// null when it is not an npm shim this can read.
    /// </summary>
    public static TerminalCommand? Unwrap(TerminalCommand command, Func<string, string?> findOnPath)
    {
        if (!Path.GetExtension(command.FileName).Equals(".cmd", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var shim = File.ReadAllText(command.FileName);
            var match = ScriptPattern().Match(shim);
            if (!match.Success) return null;
            var dir = Path.GetDirectoryName(command.FileName)!;
            var script = Path.GetFullPath(Path.Combine(dir, match.Groups["script"].Value));
            if (!File.Exists(script)) return null;
            var bundled = Path.Combine(dir, "node.exe");
            var node = File.Exists(bundled) ? bundled : findOnPath("node.exe");
            if (node == null) return null;
            return new TerminalCommand(node, new[] { script }.Concat(command.Arguments).ToList());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
