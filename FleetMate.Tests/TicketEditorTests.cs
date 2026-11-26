using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The detail pane's shared edit rules: parent input and quoting.</summary>
public class TicketEditorTests
{
    [Theory]
    [InlineData("#123", 123)]
    [InlineData(" 123 ", 123)]
    public void ParentAcceptsANumberWithOrWithoutHash(string text, int expected)
    {
        var (parent, error) = TicketEditor.ParseParent(text, childId: 7);

        Assert.Equal(expected, parent);
        Assert.Null(error);
    }

    [Fact]
    public void EmptyParentClears()
    {
        var (parent, error) = TicketEditor.ParseParent("  ", childId: 7);

        Assert.Null(parent);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("abc", "Enter a ticket number.")]
    [InlineData("0", "Enter a ticket number.")]
    [InlineData("#7", "A ticket can't be its own parent.")]
    public void ParentRefusesWhatCannotBeAParent(string text, string message)
    {
        var (parent, error) = TicketEditor.ParseParent(text, childId: 7);

        Assert.Null(parent);
        Assert.Equal(message, error);
    }

    [Fact]
    public void QuoteMarksEveryLineAndNamesTheAuthor() =>
        Assert.Equal("Ada wrote:\n> First\n> Second", TicketFeedView.Quote("Ada", "First  \nSecond"));

    [Fact]
    public void QuoteWithoutAnAuthorSaysSomeone() =>
        Assert.StartsWith("someone wrote:", TicketFeedView.Quote(null, "x"));
}
