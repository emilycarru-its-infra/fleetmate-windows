using System.Text;

namespace FleetMate.Core.Services.Manage;

/// <summary>
/// Wraps a PowerShell script for execution over SSH on a Windows host whose
/// default sshd shell is not PowerShell. <c>-EncodedCommand</c> carries the
/// script as base64 UTF-16LE, so quoting never depends on the remote shell.
/// The whole command line has to fit the remote shell's limit (cmd.exe: 8191
/// characters), which bounds the script to roughly 2,700 characters.
/// </summary>
public static class RemoteScriptEncoder
{
    /// <summary>Command-line ceiling of the remote shell, minus a margin for the prefix.</summary>
    public const int MaxCommandLineLength = 8000;

    public const string Prefix = "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ";

    public static string Encode(string script)
    {
        var normalized = NormalizeNewlines(script);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(normalized));
    }

    /// <summary>The full remote command line; throws when the script cannot fit.</summary>
    public static string Wrap(string script)
    {
        var line = Prefix + Encode(script);
        if (line.Length > MaxCommandLineLength)
        {
            throw new ArgumentException(
                $"Script is too long to send as an encoded command ({line.Length} characters after encoding, limit {MaxCommandLineLength}). Shorten it or run it as a file.");
        }
        return line;
    }

    public static bool Fits(string script) => (Prefix + Encode(script)).Length <= MaxCommandLineLength;

    public static string Decode(string base64) => Encoding.Unicode.GetString(Convert.FromBase64String(base64));

    /// <summary>
    /// Windows PowerShell 5.1 tolerates LF-only scripts, but an error message
    /// quoting a line is easier to read with the operator's own newline
    /// removed; also strips a BOM if the script came from a file.
    /// </summary>
    internal static string NormalizeNewlines(string script) =>
        (script ?? "").TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n');
}
