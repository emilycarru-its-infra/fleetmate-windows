using System.Text;
using FleetMate.Core.Services.Terminal;
using Xunit;

namespace FleetMate.Tests;

public class PseudoConsoleSessionTests
{
    /// <summary>
    /// The child must read the pseudoconsole, not this process's own standard
    /// handles: a test host's stdin is a pipe, so a child that inherited it
    /// would hit end-of-input and exit at once.
    /// </summary>
    [Fact]
    public void ChildReadsInputFromThePseudoConsole()
    {
        var output = new StringBuilder();
        using var session = new PseudoConsoleSession();
        session.Output += text => { lock (output) output.Append(text); };
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");

        session.Start("cmd.exe", null, environment, 80, 25);
        session.Write("echo CONPTY_PROBE_%COMSPEC:~0,1%\r");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        string text;
        do
        {
            Thread.Sleep(100);
            lock (output) text = output.ToString();
        } while (!text.Contains("CONPTY_PROBE_C", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline);

        Assert.Contains("CONPTY_PROBE_C", text, StringComparison.OrdinalIgnoreCase);
    }
}
