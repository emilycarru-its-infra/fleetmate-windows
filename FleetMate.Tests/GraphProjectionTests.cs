using System.Reflection;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Guards the Graph query projections.
///
/// Graph's default projection for /users omits most fields, so a missing
/// $select does not fail — it returns a user object with nulls where the data
/// should be. That is the worst shape of bug: everything works, and every user
/// renders as Disabled.
/// </summary>
public class GraphUserProjectionTests
{
    private static string UserSelect =>
        (string)typeof(GraphService)
            .GetField("UserSelect", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

    [Fact]
    public void UserProjectionRequestsAccountEnabled()
    {
        // The whole reason the projection is explicit: without this field the
        // badge said Disabled for everyone in the tenant.
        Assert.Contains("accountEnabled", UserSelect);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("displayName")]
    [InlineData("userPrincipalName")]
    [InlineData("mail")]
    [InlineData("jobTitle")]
    [InlineData("department")]
    [InlineData("officeLocation")]
    [InlineData("employeeId")]
    [InlineData("companyName")]
    [InlineData("usageLocation")]
    [InlineData("onPremisesSamAccountName")]
    [InlineData("onPremisesSyncEnabled")]
    public void UserProjectionCoversTheInspectorFields(string field)
    {
        Assert.Contains(field, UserSelect);
    }

    [Fact]
    public void EveryProjectedFieldHasAModelProperty()
    {
        // A $select naming a field the model cannot hold is wasted bandwidth and
        // a sign the two drifted apart.
        var properties = typeof(EntraUser)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = UserSelect
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(field => !properties.Contains(field))
            .ToList();

        Assert.True(missing.Count == 0,
            $"EntraUser has no property for: {string.Join(", ", missing)}");
    }

    [Fact]
    public void ProjectionIsAFlatCommaSeparatedList()
    {
        // Graph rejects whitespace in $select, and it is easy to introduce when
        // the constant is wrapped across lines.
        Assert.DoesNotContain(" ", UserSelect);
        Assert.DoesNotContain("\n", UserSelect);
        Assert.False(UserSelect.StartsWith(","));
        Assert.False(UserSelect.EndsWith(","));
    }

    [Fact]
    public void AccountEnabledDecodesFromGraphJson()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        var enabled = JsonSerializer.Deserialize<EntraUser>(
            """{ "id": "1", "displayName": "Ada", "accountEnabled": true }""", options);
        var disabled = JsonSerializer.Deserialize<EntraUser>(
            """{ "id": "2", "displayName": "Bob", "accountEnabled": false }""", options);
        var omitted = JsonSerializer.Deserialize<EntraUser>(
            """{ "id": "3", "displayName": "Cy" }""", options);

        Assert.True(enabled!.AccountEnabled);
        Assert.False(disabled!.AccountEnabled);

        // Null, not false. The distinction matters: "we did not ask" must be
        // distinguishable from "the account is disabled", or the UI cannot tell
        // a projection bug from a real disabled account.
        Assert.Null(omitted!.AccountEnabled);
    }
}

public class GraphPagingContractTests
{
    [Fact]
    public void GroupListResponseCarriesTheNextLink()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var page = JsonSerializer.Deserialize<EntraGroupListResponse>("""
            {
              "value": [ { "id": "g1", "displayName": "Devices-Lab" } ],
              "@odata.nextLink": "https://graph.microsoft.com/v1.0/groups?$skiptoken=abc"
            }
            """, options);

        Assert.NotNull(page);
        Assert.Single(page!.Value);
        Assert.Contains("skiptoken", page.NextLink);
    }

    [Fact]
    public void ALastPageHasNoNextLink()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var page = JsonSerializer.Deserialize<EntraGroupListResponse>("""
            { "value": [ { "id": "g1", "displayName": "Devices-Lab" } ] }
            """, options);

        // The paging loop terminates on this being null; if it decoded to empty
        // string instead the loop would spin on the same URL.
        Assert.Null(page!.NextLink);
    }

    [Fact]
    public void SearchGroupsFollowsNextLink()
    {
        // Structural check that the paging loop exists — the earlier page-size
        // clamp made a single-page fetch look correct at 999 results, so this
        // pins the behaviour the comment claims.
        var source = ServiceSource();
        var method = ExtractMethod(source, "public async Task<List<EntraGroup>> SearchGroupsAsync");

        Assert.Contains("NextLink", method);
        Assert.Contains("while", method);
    }

    private static string ServiceSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FleetMate.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(
            dir!.FullName, "FleetMate.Core", "Services", "GraphService.cs"));
    }

    /// <summary>Crude brace-matched slice of one method, enough for a structural assertion.</summary>
    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {signature}");

        var depth = 0;
        var seenOpen = false;
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == '{') { depth++; seenOpen = true; }
            else if (source[i] == '}')
            {
                depth--;
                if (seenOpen && depth == 0) return source[start..(i + 1)];
            }
        }

        return source[start..];
    }
}
