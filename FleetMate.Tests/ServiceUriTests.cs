using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

public class ServiceUriTests
{
    [Theory]
    [InlineData("inventory.example.edu", "https://inventory.example.edu")]
    [InlineData("https://inventory.example.edu/", "https://inventory.example.edu")]
    [InlineData("http://localhost:8080/", "http://localhost:8080")]
    public void Normalize_AcceptsHostsAndAbsoluteUris(string input, string expected)
    {
        Assert.Equal(expected, ServiceUri.Normalize(input));
    }

    /// <summary>
    /// An unconfigured host is a normal state on a machine that has never run
    /// `fleetmate configure`. Normalize used to dereference it, which turned a
    /// missing setting into a startup crash.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_TreatsAnUnsetHostAsEmptyRatherThanThrowing(string? input)
    {
        Assert.Equal(string.Empty, ServiceUri.Normalize(input));
    }
}
