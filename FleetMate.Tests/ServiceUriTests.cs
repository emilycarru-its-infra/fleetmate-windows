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
}
