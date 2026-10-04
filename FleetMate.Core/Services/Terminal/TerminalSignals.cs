using System.Text;

namespace FleetMate.Core.Services.Terminal;

/// <summary>What a session's output says about it, beyond the text itself.</summary>
public abstract record TerminalSignal;

/// <summary>OSC 0 or OSC 2: the program set the window title (an agent's /rename does this).</summary>
public sealed record TitleSignal(string Title) : TerminalSignal;

/// <summary>OSC 7 or OSC 9;9: the shell reported its current directory.</summary>
public sealed record DirectorySignal(string Path) : TerminalSignal;

/// <summary>BEL outside an escape sequence: the program wants attention.</summary>
public sealed record BellSignal : TerminalSignal;

/// <summary>
/// Watches a session's output for OSC title and directory reports and for
/// BEL. Output arrives in chunks, and a sequence can be split across two of
/// them, so the scanner keeps its place between calls.
/// </summary>
public sealed class TerminalSignalScanner
{
    private enum State { Text, Escape, Osc, OscEscape }

    private const int MaxOscLength = 4096;
    private State _state = State.Text;
    private readonly StringBuilder _osc = new();

    public List<TerminalSignal> Scan(string chunk)
    {
        var signals = new List<TerminalSignal>();
        foreach (var c in chunk)
        {
            switch (_state)
            {
                case State.Text:
                    if (c == '\x1b') _state = State.Escape;
                    else if (c == '\a') signals.Add(new BellSignal());
                    break;
                case State.Escape:
                    if (c == ']') { _state = State.Osc; _osc.Clear(); }
                    else _state = c == '\x1b' ? State.Escape : State.Text;
                    break;
                case State.Osc:
                    if (c == '\a') { Finish(signals); }
                    else if (c == '\x1b') _state = State.OscEscape;
                    else if (_osc.Length < MaxOscLength) _osc.Append(c);
                    else { _state = State.Text; _osc.Clear(); }
                    break;
                case State.OscEscape:
                    // ESC \ is the string terminator; anything else abandons the sequence.
                    if (c == '\\') Finish(signals);
                    else { _state = c == ']' ? State.Osc : State.Text; _osc.Clear(); }
                    break;
            }
        }
        return signals;
    }

    private void Finish(List<TerminalSignal> signals)
    {
        _state = State.Text;
        if (Parse(_osc.ToString()) is { } signal) signals.Add(signal);
        _osc.Clear();
    }

    /// <summary>One OSC payload (without ESC ] and the terminator) as a signal, or null.</summary>
    public static TerminalSignal? Parse(string payload)
    {
        var semi = payload.IndexOf(';');
        if (semi < 0) return null;
        var code = payload[..semi];
        var body = payload[(semi + 1)..];
        switch (code)
        {
            case "0":
            case "2":
                return new TitleSignal(body);
            case "7":
                return FileUriPath(body) is { } path ? new DirectorySignal(path) : null;
            case "9":
                // ConEmu / Windows Terminal: OSC 9;9;"path"
                if (!body.StartsWith("9;")) return null;
                var dir = body[2..].Trim().Trim('"');
                return dir.Length > 0 ? new DirectorySignal(dir) : null;
            default:
                return null;
        }
    }

    /// <summary>file://host/C:/path (or /path) → a local path.</summary>
    private static string? FileUriPath(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = uri["file://".Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0) return null;
        var path = Uri.UnescapeDataString(rest[slash..]);
        // /C:/Users/x → C:\Users\x
        if (path.Length >= 3 && path[0] == '/' && path[2] == ':') path = path[1..];
        return path.Replace('/', Path.DirectorySeparatorChar);
    }
}

/// <summary>How a session's directory is shown in the list.</summary>
public static class TerminalPathDisplay
{
    /// <summary>
    /// The path relative to home ("~\src\repo") and, when still too long,
    /// trimmed from the front so the end stays readable ("…\repo\sub").
    /// </summary>
    public static string Compact(string? path, string home, int maxLength = 32)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.TrimEnd('\\', '/');
        if (p.Length == 2 && p[1] == ':') p += "\\";
        var h = home.TrimEnd('\\', '/');
        if (h.Length > 0 && p.Equals(h, StringComparison.OrdinalIgnoreCase)) p = "~";
        else if (h.Length > 0 && p.StartsWith(h + "\\", StringComparison.OrdinalIgnoreCase)) p = "~" + p[h.Length..];

        if (p.Length <= maxLength) return p;

        // Keep whole trailing segments that fit after "…\".
        var segments = p.Split('\\');
        var kept = segments[^1];
        for (var i = segments.Length - 2; i >= 0; i--)
        {
            var next = segments[i] + "\\" + kept;
            if (next.Length + 2 > maxLength) break;
            kept = next;
        }
        if (kept.Length + 2 > maxLength) kept = kept[^(maxLength - 1)..];
        return "…\\" + kept;
    }
}
