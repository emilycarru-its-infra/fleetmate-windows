using FleetMate.Core.Models.Tickets;

namespace FleetMate.Core.Services.Tickets;

public static class TicketFeedRefresh
{
    public static bool Same(IReadOnlyList<TdxFeedEntry> first, IReadOnlyList<TdxFeedEntry> second) =>
        first.Count == second.Count && first.Zip(second).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Body == pair.Second.Body
            && pair.First.CreatedDate == pair.Second.CreatedDate && pair.First.CreatedUid == pair.Second.CreatedUid
            && pair.First.CreatedFullName == pair.Second.CreatedFullName && pair.First.IsPrivate == pair.Second.IsPrivate
            && pair.First.IsRichHtml == pair.Second.IsRichHtml && pair.First.NotifiedList == pair.Second.NotifiedList
            && pair.First.RepliesCount == pair.Second.RepliesCount && Same(pair.First.ReplyList, pair.Second.ReplyList));

    /// <summary>Keep known replies while bounded new-reply reads are pending; remove deleted reply bodies.</summary>
    public static List<TdxFeedEntry> Merge(IEnumerable<TdxFeedEntry> fresh, IEnumerable<TdxFeedEntry> previous,
        IReadOnlyDictionary<int, List<TdxFeedEntry>> hydrated)
    {
        var old = previous.ToDictionary(entry => entry.Id);
        return fresh.Select(entry => hydrated.TryGetValue(entry.Id, out var replies) ? entry.WithReplies(replies)
            : old.TryGetValue(entry.Id, out var prior) && prior.ReplyList.Count <= (entry.RepliesCount ?? 0)
                ? entry.WithReplies(prior.ReplyList.ToList()) : entry).ToList();
    }
}
