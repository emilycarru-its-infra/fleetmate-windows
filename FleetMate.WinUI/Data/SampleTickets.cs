using FleetMate.Core.Models.Tickets;

namespace FleetMate.WinUI.Data;

/// <summary>
/// Made-up tickets for the prototype, built from Core's real <see cref="TdxTicket"/>
/// model so the stats strip runs through Core's <see cref="TicketStats"/>.
/// Every name, team and ticket here is fictional; no service is contacted.
/// </summary>
public static class SampleTickets
{
    private static readonly DateTime Now = DateTime.UtcNow;

    public static IReadOnlyList<TdxTicket> Tickets { get; } = Build();

    private static List<TdxTicket> Build()
    {
        var rows = new (int Id, string Title, string Status, string Priority, string? Responsible, string Group,
            string Requestor, int Days, bool Sla, string Type)[]
        {
            (204811, "Laptop drops Wi-Fi after waking from sleep", "In Process", "High", "Jordan Lee", "Endpoint Team", "Avery Kim", 2, false, "Hardware"),
            (204809, "Projector in studio B shows no signal over HDMI", "New", "High", null, "Classroom Technology", "Priya Shah", 0, false, "Classroom"),
            (204802, "Request access to the shared render queue", "Awaiting Input", "Medium", "Sam Patel", "Identity & Access", "Noah Fischer", 4, false, "Access"),
            (204797, "Password reset loop on the self-service portal", "In Process", "Emergency", "Riley Chen", "Identity & Access", "Mia Laurent", 1, true, "Account"),
            (204790, "New starter: workstation and accounts for Monday", "In Process", "High", "Jordan Lee", "Endpoint Team", "Ethan Moore", 6, false, "Onboarding"),
            (204781, "Plotter queue stuck with a 2 GB job", "On Hold", "Medium", "Casey Nguyen", "Print Services", "Lena Ortiz", 9, false, "Printing"),
            (204774, "Design suite licence for a visiting instructor", "Awaiting Input", "Low", "Sam Patel", "Software Licensing", "Omar Haddad", 12, false, "Software"),
            (204766, "Second monitor flickers on docking station", "New", "Low", null, "Endpoint Team", "Grace Ito", 3, false, "Hardware"),
            (204758, "VPN disconnects every 20 minutes off campus", "In Process", "High", "Morgan Diaz", "Network", "Lucas Brandt", 15, true, "Network"),
            (204750, "Shared drive permissions for the thesis cohort", "In Process", "Medium", "Riley Chen", "Identity & Access", "Hannah Weiss", 18, false, "Access"),
            (204733, "Audio interface not detected in edit suite 3", "On Hold", "Medium", "Taylor Brooks", "Classroom Technology", "Diego Ramos", 24, false, "Classroom"),
            (204712, "Replace swollen battery on loaner laptop", "New", "High", null, "Endpoint Team", "Zoe Martin", 33, true, "Hardware"),
            (204698, "Email signature template for the events team", "Awaiting Input", "Low", "Casey Nguyen", "Service Desk", "Isla Novak", 41, false, "Request"),
            (204677, "Lab machines slow to log in on Monday mornings", "In Process", "Medium", "Morgan Diaz", "Endpoint Team", "Felix Dubois", 47, false, "Performance"),
            (204650, "Wireless keyboard set for the reading room", "On Hold", "Low", "Taylor Brooks", "Service Desk", "Ruby Clarke", 58, false, "Hardware"),
            (204611, "Archive a departed instructor's mailbox", "Resolved", "Low", "Sam Patel", "Identity & Access", "Theo Bauer", 64, false, "Account"),
            (204598, "Colour calibration for the photo studio displays", "In Process", "Medium", "Jordan Lee", "Classroom Technology", "Ava Rossi", 72, false, "Classroom"),
            (204540, "Guest Wi-Fi sign-in page times out", "New", "Medium", null, "Network", "Leo Varga", 5, false, "Network"),
        };

        return rows.Select(r =>
        {
            var created = Now.AddDays(-r.Days).AddHours(-3);
            return new TdxTicket
            {
                Id = r.Id,
                Title = r.Title,
                TypeName = r.Type,
                ClassificationName = "Incident",
                StatusName = r.Status,
                PriorityName = r.Priority,
                ImpactName = r.Priority is "Emergency" or "High" ? "Affects Department" : "Affects User",
                UrgencyName = r.Priority is "Emergency" ? "High" : "Medium",
                ResponsibleFullName = r.Responsible,
                ResponsibleGroupName = r.Group,
                RequestorName = r.Requestor,
                RequestorEmail = r.Requestor.ToLowerInvariant().Replace(' ', '.') + "@example.edu",
                AccountName = "Academic Affairs",
                SourceName = "Self-Service Portal",
                ServiceName = r.Type + " Support",
                LocationName = "Main Building",
                SlaName = "Standard 3 business days",
                IsSlaViolated = r.Sla,
                IsOnHold = r.Status == TicketStats.OnHoldStatus,
                RespondByDate = created.AddHours(8),
                ResolveByDate = created.AddDays(3),
                CreatedDate = created,
                CreatedFullName = r.Requestor,
                ModifiedDate = Now.AddHours(-(r.Id % 30)),
                ModifiedFullName = r.Responsible ?? "System",
                DaysOld = r.Days,
                Description = DescriptionFor(r.Title, r.Requestor),
            };
        }).ToList();
    }

    private static string DescriptionFor(string title, string requestor) =>
        $"{title}.\n\nThis started earlier in the week and happens every time. I have restarted twice and checked the cables. " +
        $"It is blocking work for the rest of the team, so anything that gets us going again today would help.\n\n— {requestor.Split(' ')[0]}";

    /// <summary>The activity feed for a ticket: comments plus the edits and status changes TDX logs as entries.</summary>
    public static IReadOnlyList<TdxFeedEntry> FeedFor(TdxTicket t)
    {
        var who = t.ResponsibleFullName ?? "Service Desk";
        var start = t.CreatedDate;
        var entries = new List<TdxFeedEntry>
        {
            new() { Id = 1, CreatedDate = start, CreatedFullName = t.RequestorName, Body = "Ticket created from the self-service portal." },
            new() { Id = 2, CreatedDate = start.AddMinutes(40), CreatedFullName = "System", Body = $"Changed Responsible Group from Nothing to {t.ResponsibleGroupName}." },
            new() { Id = 3, CreatedDate = start.AddHours(2), CreatedFullName = who, Body = "Changed Status from New to In Process." },
            new() { Id = 4, CreatedDate = start.AddHours(3), CreatedFullName = who, Body = "Thanks — I can reproduce this. Could you tell me roughly when it last worked, and whether anyone nearby sees the same thing?" },
            new() { Id = 5, CreatedDate = start.AddHours(5), CreatedFullName = t.RequestorName, Body = "It was fine until Monday. The person at the next desk has it too." },
            new() { Id = 6, CreatedDate = start.AddHours(6), CreatedFullName = who, Body = $"Changed Priority from Medium to {t.PriorityName}.", IsPrivate = true },
            new() { Id = 7, CreatedDate = start.AddHours(7), CreatedFullName = who, Body = "Internal note: likely the driver update from the weekend. Rolling one machine back to confirm.", IsPrivate = true },
        };
        if (t.StatusName is not ("New" or "In Process"))
        {
            entries.Add(new() { Id = 8, CreatedDate = start.AddHours(9), CreatedFullName = who, Body = $"Changed Status from In Process to {t.StatusName}." });
        }
        return entries.OrderByDescending(e => e.CreatedDate).ToList();
    }

    public static IReadOnlyList<SampleTask> TasksFor(TdxTicket t) =>
    [
        new("Confirm scope with requester", t.ResponsibleFullName ?? "Unassigned", true, t.CreatedDate.AddDays(1).ToLocalTime().ToString("MMM d")),
        new("Apply fix and test", t.ResponsibleFullName ?? "Unassigned", false, t.CreatedDate.AddDays(2).ToLocalTime().ToString("MMM d")),
        new("Follow up after 48 hours", "Service Desk", false, t.CreatedDate.AddDays(4).ToLocalTime().ToString("MMM d")),
    ];
}

public sealed record SampleTask(string Title, string Responsible, bool Done, string Due);
