using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The aze exec stream carries command output between AZE_BEGIN/AZE_END markers,
/// wrapped in terminal echo and ANSI escapes. ParseExecOutput must recover the
/// exact payload and exit code; these tests pin that contract.
/// </summary>
public class ElevationParseTests
{
    private const string Esc = "";

    private static string Stream(string body, int code) =>
        $"\n<<<AZE_BEGIN>>>\n{body}\n<<<AZE_END:{code}>>>\n";

    [Fact]
    public void ExtractsPayloadAndZeroExitCode()
    {
        var (output, exit) = ElevationSession.ParseExecOutput(Stream("hello world", 0));

        Assert.Equal("hello world", output);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void PreservesNonZeroExitCode()
    {
        var (_, exit) = ElevationSession.ParseExecOutput(Stream("boom", 1));

        Assert.Equal(1, exit);
    }

    [Fact]
    public void StripsAnsiEscapesAndCarriageReturns()
    {
        // Simulate a terminal painting color codes (real ESC sequences) and CRLF.
        var raw = $"{Esc}[32m\r\n<<<AZE_BEGIN>>>\r\n{Esc}[0m{{\"id\":\"abc\"}}\r\n<<<AZE_END:0>>>\r\n";

        var (output, exit) = ElevationSession.ParseExecOutput(raw);

        Assert.Equal("{\"id\":\"abc\"}", output);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void PreservesMultiLineJsonPayload()
    {
        var json = "{\n  \"value\": [\n    { \"id\": 1 }\n  ]\n}";

        var (output, exit) = ElevationSession.ParseExecOutput(Stream(json, 0));

        Assert.Equal(json, output);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void ThrowsWhenEndMarkerMissing()
    {
        var raw = "\n<<<AZE_BEGIN>>>\npartial output with no end marker\n";

        Assert.Throws<ElevationException>(() => ElevationSession.ParseExecOutput(raw));
    }
}
