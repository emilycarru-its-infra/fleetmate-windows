using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;

namespace FleetMate.Core.Providers.Tickets;

/// <summary>
/// Wraps TdxService to expose TeamDynamix tickets through the unified ITicketProvider interface.
/// </summary>
public class TdxTicketProvider : ITicketProvider
{
    private readonly TdxService _tdxService;

    public string ProviderId => "tdx";
    public string ProviderName => "TeamDynamix";
    public bool IsEnabled => true;

    public TdxTicketProvider(TdxService tdxService)
    {
        _tdxService = tdxService;
    }

    public Task<bool> AuthenticateAsync(CancellationToken ct = default) =>
        Task.FromResult(true);

    public async Task<List<UnifiedTicket>> ListTicketsAsync(TicketFilter? filter = null, CancellationToken ct = default)
    {
        var search = new TicketSearchRequest { MaxResults = filter?.Limit ?? 500 };
        var tickets = await _tdxService.SearchTicketsAsync(search, search.MaxResults ?? 500);
        return tickets.Select(ToUnified).ToList();
    }

    public async Task<UnifiedTicket?> GetTicketAsync(string ticketId, CancellationToken ct = default)
    {
        if (!int.TryParse(ticketId, out var id)) return null;
        var ticket = await _tdxService.GetTicketAsync(id);
        return ticket != null ? ToUnified(ticket) : null;
    }

    public async Task<List<UnifiedTicket>> SearchTicketsAsync(string query, CancellationToken ct = default)
    {
        var search = new TicketSearchRequest { SearchText = query, MaxResults = 100 };
        var tickets = await _tdxService.SearchTicketsAsync(search, 100);
        return tickets.Select(ToUnified).ToList();
    }

    private static UnifiedTicket ToUnified(TdxTicket t) => new()
    {
        Id = t.Id.ToString(),
        ProviderId = "tdx",
        Title = t.Title,
        Description = t.Description,
        Status = t.StatusName,
        Priority = t.PriorityName,
        Requestor = t.RequestorName,
        Responsible = t.ResponsibleFullName,
        GroupName = t.ResponsibleGroupName,
        CreatedDate = t.CreatedDate,
        ModifiedDate = t.ModifiedDate,
        DueDate = t.ResolveByDate,
        TicketType = t.TypeName,
        Source = t.SourceName,
    };
}
