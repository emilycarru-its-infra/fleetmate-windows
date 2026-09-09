using FleetMate.Core.Models.Manage;
using Xunit;

namespace FleetMate.Tests;

public class PlaceholderTemplateTests
{
    [Fact]
    public void Detect_FindsEachPlaceholderOnce()
    {
        var t = PlaceholderTemplate.Detect("Create user", "New-LocalUser '<USERNAME>' -Password '<PASSWORD>'; Add-LocalGroupMember -Member '<USERNAME>'");
        Assert.NotNull(t);
        Assert.Equal(new[] { "<USERNAME>", "<PASSWORD>" }, t!.Placeholders);
        Assert.True(t.HasSensitive);
    }

    [Fact]
    public void Detect_ReturnsNullWithoutPlaceholders()
    {
        Assert.Null(PlaceholderTemplate.Detect("Hostname", "hostname"));
        Assert.Null(PlaceholderTemplate.Detect("Compare", "if ($a -lt $b) { 'less' }"));
        Assert.Null(PlaceholderTemplate.Detect("Lower", "<username>"));
    }

    [Fact]
    public void Resolve_QuotesBareAndKeepsAuthorQuotes()
    {
        var t = PlaceholderTemplate.Detect("x", "a <NAME> b '<NAME>' c \"<NAME>\"")!;
        var values = new Dictionary<string, string> { ["<NAME>"] = "it's $x" };

        Assert.Equal("a 'it''s $x' b 'it''s $x' c \"it's `$x\"", t.Resolve(values));
    }

    [Fact]
    public void Resolve_RedactsSensitiveForPreviewOnly()
    {
        var t = PlaceholderTemplate.Detect("x", "-Password '<PASSWORD>' -User <USERNAME>")!;
        var values = new Dictionary<string, string> { ["<PASSWORD>"] = "hunter2", ["<USERNAME>"] = "jdoe" };

        Assert.Equal("-Password '••••••••' -User 'jdoe'", t.Resolve(values, redactSensitive: true));
        Assert.Equal("-Password 'hunter2' -User 'jdoe'", t.Resolve(values));
    }

    [Fact]
    public void IsComplete_RequiresEveryValue()
    {
        var t = PlaceholderTemplate.Detect("x", "<A> <B>")!;
        Assert.False(t.IsComplete(new Dictionary<string, string> { ["<A>"] = "1" }));
        Assert.False(t.IsComplete(new Dictionary<string, string> { ["<A>"] = "1", ["<B>"] = " " }));
        Assert.True(t.IsComplete(new Dictionary<string, string> { ["<A>"] = "1", ["<B>"] = "2" }));
    }

    [Theory]
    [InlineData("<USERNAME>", "Username")]
    [InlineData("<PACKAGE_NAME>", "Package name")]
    [InlineData("<API_TOKEN>", "Api token")]
    public void FieldLabel_ReadsAsAPromptLabel(string placeholder, string expected) =>
        Assert.Equal(expected, PlaceholderTemplate.FieldLabel(placeholder));

    [Theory]
    [InlineData("<PASSWORD>", true)]
    [InlineData("<ADMIN_PASSWORD>", true)]
    [InlineData("<CLIENT_SECRET>", true)]
    [InlineData("<USERNAME>", false)]
    public void IsSensitive_MatchesSecretLikeNames(string placeholder, bool expected) =>
        Assert.Equal(expected, PlaceholderTemplate.IsSensitive(placeholder));
}
