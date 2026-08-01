using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

public class ServiceUriTests
{
    [Theory]
    [InlineData("inventory.its.ecuad.ca", "https://inventory.its.ecuad.ca")]
    [InlineData("https://inventory.its.ecuad.ca/", "https://inventory.its.ecuad.ca")]
    [InlineData("http://localhost:8080/", "http://localhost:8080")]
    public void Normalize_AcceptsHostsAndAbsoluteUris(string input, string expected)
    {
        Assert.Equal(expected, ServiceUri.Normalize(input));
    }
}
