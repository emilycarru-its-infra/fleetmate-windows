using System.Reflection;
using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Provider payload parsing for the PR viewer. Both providers are invoked
/// through their private parsers with real-shaped JSON, since the surrounding
/// methods need a live API.
/// </summary>
public class DevOpsPullRequestDetailTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static List<PullRequestCommit> ParseCommits(string json) =>
        (List<PullRequestCommit>)typeof(AzureDevOpsService)
            .GetMethod("ParseDevOpsCommits", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { Json(json) })!;

    private static List<PullRequestComment> ParseComments(string json) =>
        (List<PullRequestComment>)typeof(AzureDevOpsService)
            .GetMethod("ParseDevOpsComments", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { Json(json) })!;

    [Fact]
    public void ParsesCommits()
    {
        var commits = ParseCommits("""
            {
              "value": [
                {
                  "commitId": "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
                  "comment": "Fix the widget\n\nLonger explanation here.",
                  "author": { "name": "Ada Lovelace", "date": "2026-07-20T10:00:00Z" }
                }
              ]
            }
            """);

        var commit = Assert.Single(commits);
        Assert.Equal("Ada Lovelace", commit.AuthorName);
        Assert.Equal("a1b2c3d4", commit.ShortSha);
        // Only the first line belongs in a commit list.
        Assert.Equal("Fix the widget", commit.Subject);
        Assert.Equal(2026, commit.Date!.Value.Year);
    }

    [Fact]
    public void SurvivesCommitsMissingAnAuthor()
    {
        var commits = ParseCommits("""{ "value": [ { "commitId": "abc", "comment": "x" } ] }""");

        Assert.Single(commits);
        Assert.Null(commits[0].AuthorName);
        Assert.Null(commits[0].Date);
    }

    [Fact]
    public void FlattensCommentThreads()
    {
        var comments = ParseComments("""
            {
              "value": [
                {
                  "id": 7,
                  "comments": [
                    { "id": 1, "content": "Looks good", "commentType": "text",
                      "author": { "displayName": "Ada" }, "publishedDate": "2026-07-20T10:00:00Z" },
                    { "id": 2, "content": "Thanks", "commentType": "text",
                      "author": { "displayName": "Grace" }, "publishedDate": "2026-07-20T11:00:00Z" }
                  ]
                }
              ]
            }
            """);

        Assert.Equal(2, comments.Count);
        Assert.Equal("7-1", comments[0].Id);
        Assert.Equal("Ada", comments[0].AuthorName);
    }

    [Fact]
    public void MarksVoteNoiseAsSystem()
    {
        // Azure DevOps folds vote changes into the same thread list; mixing
        // them into the conversation buries what people actually said.
        var comments = ParseComments("""
            {
              "value": [
                { "id": 1, "comments": [
                  { "id": 1, "content": "Ada approved the pull request", "commentType": "system",
                    "author": { "displayName": "Ada" } },
                  { "id": 2, "content": "Real feedback", "commentType": "text",
                    "author": { "displayName": "Ada" } }
                ] }
              ]
            }
            """);

        Assert.True(comments.Single(c => c.Body.Contains("approved")).IsSystem);
        Assert.False(comments.Single(c => c.Body == "Real feedback").IsSystem);
    }

    [Fact]
    public void SkipsDeletedThreads()
    {
        // A deleted thread still comes back; rendering it would resurrect
        // something someone chose to remove.
        var comments = ParseComments("""
            {
              "value": [
                { "id": 1, "isDeleted": true, "comments": [ { "id": 1, "content": "oops" } ] },
                { "id": 2, "comments": [ { "id": 1, "content": "kept" } ] }
              ]
            }
            """);

        Assert.Equal("kept", Assert.Single(comments).Body);
    }

    [Fact]
    public void SkipsEmptyComments()
    {
        var comments = ParseComments("""
            {
              "value": [
                { "id": 1, "comments": [
                  { "id": 1, "content": "" },
                  { "id": 2 },
                  { "id": 3, "content": "real" }
                ] }
              ]
            }
            """);

        Assert.Equal("real", Assert.Single(comments).Body);
    }

    [Fact]
    public void OrdersCommentsOldestFirst()
    {
        var comments = ParseComments("""
            {
              "value": [
                { "id": 1, "comments": [
                  { "id": 1, "content": "second", "publishedDate": "2026-07-20T12:00:00Z" }
                ] },
                { "id": 2, "comments": [
                  { "id": 1, "content": "first", "publishedDate": "2026-07-20T09:00:00Z" }
                ] }
              ]
            }
            """);

        // A conversation reads forwards, and threads arrive in no useful order.
        Assert.Equal(new[] { "first", "second" }, comments.Select(c => c.Body));
    }

    [Theory]
    [InlineData("""{ "value": [] }""")]
    [InlineData("""{ }""")]
    public void EmptyPayloadsProduceEmptyLists(string json)
    {
        Assert.Empty(ParseCommits(json));
        Assert.Empty(ParseComments(json));
    }
}

public class GitHubPullRequestDetailTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static List<PullRequestCommit> ParseCommits(string json) =>
        (List<PullRequestCommit>)typeof(GitHubPullRequestService)
            .GetMethod("ParseCommits", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { Json(json) })!;

    private static List<PullRequestComment> ParseComments(string json) =>
        (List<PullRequestComment>)typeof(GitHubPullRequestService)
            .GetMethod("ParseComments", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { Json(json) })!;

    [Fact]
    public void ParsesCommits()
    {
        var commits = ParseCommits("""
            [
              {
                "sha": "abc1234567890",
                "commit": {
                  "message": "Add the widget\n\nDetail.",
                  "author": { "name": "Ada Lovelace", "date": "2026-07-20T10:00:00Z" }
                },
                "author": { "login": "ada" }
              }
            ]
            """);

        var commit = Assert.Single(commits);
        // The git signature is the truth about authorship; the GitHub account
        // is only a best-effort match on the commit email.
        Assert.Equal("Ada Lovelace", commit.AuthorName);
        Assert.Equal("Add the widget", commit.Subject);
        Assert.Equal("abc12345", commit.ShortSha);
    }

    [Fact]
    public void FallsBackToTheGitHubLoginWhenTheSignatureHasNoName()
    {
        var commits = ParseCommits("""
            [ { "sha": "abc", "commit": { "message": "x" }, "author": { "login": "ada" } } ]
            """);

        Assert.Equal("ada", commits[0].AuthorName);
    }

    [Fact]
    public void SurvivesACommitWithNoAuthorAtAll()
    {
        var commits = ParseCommits("""[ { "sha": "abc", "commit": { "message": "x" } } ]""");

        Assert.Single(commits);
        Assert.Null(commits[0].AuthorName);
    }

    [Fact]
    public void ParsesIssueComments()
    {
        var comments = ParseComments("""
            [
              { "id": 101, "user": { "login": "ada" }, "body": "Looks good",
                "created_at": "2026-07-20T10:00:00Z" }
            ]
            """);

        var comment = Assert.Single(comments);
        Assert.Equal("101", comment.Id);
        Assert.Equal("ada", comment.AuthorName);
        Assert.Equal("Looks good", comment.Body);
        Assert.False(comment.IsSystem);
    }

    [Fact]
    public void SurvivesACommentWithNoUser()
    {
        // A deleted account leaves the comment but drops the user.
        var comments = ParseComments("""[ { "id": 1, "body": "orphaned" } ]""");

        Assert.Equal("unknown", comments[0].AuthorName);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    public void EmptyPayloadsProduceEmptyLists(string json)
    {
        Assert.Empty(ParseCommits(json));
        Assert.Empty(ParseComments(json));
    }
}

public class PullRequestDetailModelTests
{
    [Fact]
    public void TotalsSumTheFiles()
    {
        var detail = new PullRequestDetail
        {
            Files =
            {
                DiffBuilder.Build("a.txt", "one", "ONE"),
                DiffBuilder.Build("b.txt", "two\nthree", "two\nTHREE"),
            },
        };

        Assert.Equal(2, detail.Insertions);
        Assert.Equal(2, detail.Deletions);
    }

    [Fact]
    public void ConversationExcludesSystemNoise()
    {
        var detail = new PullRequestDetail
        {
            Comments =
            {
                new() { Id = "1", Body = "approved the pull request", IsSystem = true },
                new() { Id = "2", Body = "Actually, one thought", IsSystem = false },
            },
        };

        var only = Assert.Single(detail.Conversation);
        Assert.Equal("Actually, one thought", only.Body);
    }

    [Theory]
    [InlineData("abcdef1234567890", "abcdef12")]
    // A short or absent SHA must not throw on the substring.
    [InlineData("abc", "abc")]
    [InlineData("", "")]
    public void ShortShaTruncatesSafely(string sha, string expected)
    {
        Assert.Equal(expected, new PullRequestCommit { Id = sha }.ShortSha);
    }

    [Theory]
    [InlineData("Subject only", "Subject only")]
    [InlineData("Subject\n\nBody text", "Subject")]
    [InlineData("Subject\r\nBody", "Subject")]
    [InlineData("", "")]
    public void SubjectIsTheFirstLine(string message, string expected)
    {
        Assert.Equal(expected, new PullRequestCommit { Message = message }.Subject);
    }
}
