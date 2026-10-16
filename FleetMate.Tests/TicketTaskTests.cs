using System.Net;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Ticket tasks: the routes against the documented TDWebApi contract, the
/// task-template page reading, and the controller that guards every change
/// for the UI. Every fixture is synthetic, and nothing here reaches a server.
/// </summary>
public class TicketTaskTests
{
    private const string Token = "synthetic-bearer-token-value";
    private static readonly Guid Person = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    // ── Wire contract ──────────────────────────────────────────────────

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return _respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static TdxConfig Config() => new()
    {
        BaseUrl = "https://service.example.com",
        AppId = 1,
        TicketingAppId = 7,
        AssetsAppId = 9,
    };

    private static TdxService Service(StubHandler handler, bool signedIn = true)
    {
        var service = new TdxService(Config(), handler);
        if (signedIn) service.SetSsoToken(Token, DateTime.UtcNow.AddHours(1), "operator@example.com", "Operator");
        return service;
    }

    private const string TaskJson = """
        {"ID": 11, "TicketID": 42, "TypeID": 1, "Title": "Image the laptop", "Description": "Use the standard image",
         "IsActive": true, "PercentComplete": 40, "StartDate": "2026-10-01T09:00:00-07:00", "EndDate": null,
         "CompleteWithinMinutes": null, "EstimatedMinutes": 90, "ActualMinutes": 30, "Order": 1,
         "PredecessorID": 0, "ResponsibleUid": "6f9619ff-8b86-d011-b42d-00c04fc964ff", "ResponsibleFullName": "Sample Person",
         "ResponsibleGroupID": 5, "ResponsibleGroupName": "Sample Group", "NotifyResponsible": true,
         "CreatedDate": "2026-09-30T10:00:00Z", "ModifiedDate": "2026-10-02T10:00:00Z", "CompletedDate": "0001-01-01T00:00:00",
         "Uri": "api/7/tickets/42/tasks/11", "WebUiUrl": "https://service.example.com/x"}
        """;

    [Fact]
    public async Task ReadsTasksFromTheTicketingApplicationAsTheSignedInUserInOrder()
    {
        var handler = new StubHandler(_ => Json("""
            [{"ID": 2, "TypeID": 3, "Title": "Approve", "Order": 2, "PercentComplete": 0, "EstimatedMinutes": 0},
             {"ID": 1, "TypeID": 1, "Title": "Prepare", "Order": 1, "PercentComplete": 100, "EstimatedMinutes": 30}]
            """));
        using var service = Service(handler);

        var tasks = await service.Tasks.GetTasksAsync(42);

        var (request, _) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://service.example.com/TDWebApi/api/7/tickets/42/tasks", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
        Assert.Equal(new[] { 1, 2 }, tasks.Select(t => t.Id));
        Assert.Equal(TicketTaskType.Workflow, tasks[1].Type);
        Assert.True(tasks[0].IsComplete);
    }

    [Fact]
    public async Task AsksOnlyForEligiblePredecessorsWhenPicking()
    {
        var handler = new StubHandler(_ => Json("[]"));
        using var service = Service(handler);

        await service.Tasks.GetTasksAsync(42, eligiblePredecessorsOnly: true);

        Assert.Equal("https://service.example.com/TDWebApi/api/7/tickets/42/tasks?isEligiblePredecessor=true",
            Assert.Single(handler.Requests).Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task SendsNothingWithoutTheOperatorsOwnSignIn()
    {
        var handler = new StubHandler(_ => Json("[]"));
        using var service = Service(handler, signedIn: false);

        var ex = await Assert.ThrowsAsync<TdxTicketTaskException>(() => service.Tasks.GetTasksAsync(42));

        Assert.Equal(401, ex.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReadsOneTaskWithItsRawJsonAndUnsetDates()
    {
        using var service = Service(new StubHandler(_ => Json(TaskJson)));

        var (task, raw) = await service.Tasks.GetTaskAsync(42, 11);

        Assert.Equal("Image the laptop", task.Title);
        Assert.Equal(Person, task.ResponsibleUid);
        Assert.False(TicketTaskFormat.HasDate(task.CompletedDate));
        Assert.False(task.IsComplete);
        Assert.Equal(JsonValueKind.Object, raw.ValueKind);
        Assert.Equal("api/7/tickets/42/tasks/11", raw.GetProperty("Uri").GetString());
    }

    [Fact]
    public async Task ProgressGoesThroughTheTaskFeedWithTheDocumentedBody()
    {
        var handler = new StubHandler(_ => Json("""{"ID": 900, "Body": "Done"}"""));
        using var service = Service(handler);

        await service.Tasks.PostTaskFeedAsync(42, 11, new TdxTicketTaskFeedRequest { PercentComplete = 100, Comments = "Done", IsPrivate = true });

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://service.example.com/TDWebApi/api/7/tickets/42/tasks/11/feed", request.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal(100, doc.RootElement.GetProperty("PercentComplete").GetInt32());
        Assert.Equal("Done", doc.RootElement.GetProperty("Comments").GetString());
        Assert.True(doc.RootElement.GetProperty("IsPrivate").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("IsRichHtml").GetBoolean());
    }

    [Fact]
    public async Task ACommentOnlyUpdateLeavesProgressOut()
    {
        var handler = new StubHandler(_ => Json("{}"));
        using var service = Service(handler);

        await service.Tasks.PostTaskFeedAsync(42, 11, new TdxTicketTaskFeedRequest { Comments = "Waiting on parts" });

        using var doc = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.False(doc.RootElement.TryGetProperty("PercentComplete", out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task ProgressOutsideZeroToOneHundredIsNeverSent(int percent)
    {
        var handler = new StubHandler(_ => Json("{}"));
        using var service = Service(handler);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            service.Tasks.PostTaskFeedAsync(42, 11, new TdxTicketTaskFeedRequest { PercentComplete = percent }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnEmptyUpdateIsNeverSent()
    {
        var handler = new StubHandler(_ => Json("{}"));
        using var service = Service(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.Tasks.PostTaskFeedAsync(42, 11, new TdxTicketTaskFeedRequest { Comments = "  " }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TaskFeedHydratesReplyThreads()
    {
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/feed/500")
            ? Json("""{"ID": 500, "Body": "Parent", "Replies": [{"ID": 501, "Body": "A reply", "CreatedFullName": "Someone"}]}""")
            : Json("""
                [{"ID": 500, "Body": "Parent", "RepliesCount": 1, "Replies": [], "CreatedDate": "2026-10-02T10:00:00Z"},
                 {"ID": 499, "Body": "Older", "RepliesCount": 0, "CreatedDate": "2026-10-01T10:00:00Z"}]
                """));
        using var service = Service(handler);

        var feed = await service.Tasks.GetTaskFeedAsync(42, 11);

        Assert.Equal("https://service.example.com/TDWebApi/api/7/tickets/42/tasks/11/feed", handler.Requests[0].Request.RequestUri!.ToString());
        Assert.Equal(500, feed[0].Id);
        Assert.Equal("A reply", Assert.Single(feed[0].ReplyList).Body);
        Assert.All(handler.Requests, r => Assert.Equal(Token, r.Request.Headers.Authorization!.Parameter));
    }

    [Fact]
    public async Task CreatesAndDeletesOnTheDocumentedRoutes()
    {
        var handler = new StubHandler(r => r.Method == HttpMethod.Post
            ? Json("""{"ID": 12, "Title": "New one", "TypeID": 1}""", HttpStatusCode.Created)
            : Json("\"Removed\""));
        using var service = Service(handler);

        var created = await service.Tasks.CreateTaskAsync(42, TicketTaskBody.ForCreate(new TicketTaskWrite { Title = "New one", EstimatedMinutes = 15 }));
        await service.Tasks.DeleteTaskAsync(42, 12);

        Assert.Equal(12, created!.Id);
        Assert.Equal("https://service.example.com/TDWebApi/api/7/tickets/42/tasks", handler.Requests[0].Request.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("New one", doc.RootElement.GetProperty("Title").GetString());
        Assert.Equal(15, doc.RootElement.GetProperty("EstimatedMinutes").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("PercentComplete", out _));
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Request.Method);
        Assert.Equal("https://service.example.com/TDWebApi/api/7/tickets/42/tasks/12", handler.Requests[1].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task AppliesATemplateWithTheDocumentedParameters()
    {
        var handler = new StubHandler(_ => Json("""[{"ID": 20, "TypeID": 1, "Title": "From template"}]"""));
        using var service = Service(handler);

        var tasks = await service.Tasks.ApplyTaskTemplateAsync(42, new TdxApplyTaskTemplateRequest
        {
            TaskTemplateId = 3, RemoveExistingTasks = false, StartDateSource = TaskTemplateStartDate.TicketStart,
        });

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://service.example.com/TDWebApi/api/7/tickets/42/tasks/applytemplate", request.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal(3, doc.RootElement.GetProperty("TaskTemplateId").GetInt32());
        Assert.False(doc.RootElement.GetProperty("RemoveExistingTasks").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("StartDateSource").GetInt32());
        Assert.Equal(20, Assert.Single(tasks).Id);
    }

    [Fact]
    public async Task SearchesActiveGroupsByName()
    {
        var handler = new StubHandler(_ => Json("""[{"ID": 5, "Name": "Sample Group", "IsActive": true}, {"ID": 0, "Name": "bad"}]"""));
        using var service = Service(handler);

        var groups = await service.Tasks.SearchGroupsAsync("samp");

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://service.example.com/TDWebApi/api/groups/search", request.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("samp", doc.RootElement.GetProperty("NameLike").GetString());
        Assert.True(doc.RootElement.GetProperty("IsActive").GetBoolean());
        Assert.Equal(5, Assert.Single(groups).Id);
    }

    [Fact]
    public async Task AssignToMeResolvesTheSignedInOperatorByExactEmail()
    {
        var handler = new StubHandler(_ => Json($$"""
            [{"UID": "11111111-1111-1111-1111-111111111111", "FullName": "Other", "PrimaryEmail": "operator.other@example.com"},
             {"UID": "{{Person}}", "FullName": "Operator", "PrimaryEmail": "OPERATOR@example.com"}]
            """));
        using var service = Service(handler);

        var me = await service.Tasks.GetMeAsync();

        Assert.Equal(Person, me!.Uid);
        Assert.Contains("searchText=operator%40example.com", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "permission")]
    [InlineData(HttpStatusCode.NotFound, "could not find")]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task FailuresAreFleetMatesOwnWordsNeverTheBody(HttpStatusCode status, string expected)
    {
        using var service = Service(new StubHandler(_ => Json("""{"Message": "secret ticket content"}""", status)));

        var ex = await Assert.ThrowsAsync<TdxTicketTaskException>(() => service.Tasks.GetTasksAsync(42));

        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
        Assert.Equal((int)status, ex.StatusCode);
    }

    [Fact]
    public async Task AValidationReasonIsShownWhenItIsPlainText()
    {
        using var service = Service(new StubHandler(_ => Json("""{"Message": "Title is required."}""", HttpStatusCode.BadRequest)));

        var ex = await Assert.ThrowsAsync<TdxTicketTaskException>(() =>
            service.Tasks.CreateTaskAsync(42, new Dictionary<string, object?> { ["Title"] = "" }));

        Assert.Contains("Title is required.", ex.Message);
    }

    [Theory]
    [InlineData("<html>proxy</html>")]
    [InlineData("""{"Message": "<b>bad</b>"}""")]
    [InlineData("""{"Message": "see https://service.example.com/internal"}""")]
    [InlineData("""["not", "an", "object"]""")]
    public void AValidationReasonWithMarkupOrAddressesIsDropped(string body) =>
        Assert.Null(TdxTicketTaskService.SanitizeValidationMessage(body));

    [Fact]
    public async Task ATransportFailureNamesNoAddress()
    {
        using var service = Service(new StubHandler(_ => throw new HttpRequestException("https://service.example.com/secret-path")));

        var ex = await Assert.ThrowsAsync<TdxTicketTaskException>(() => service.Tasks.GetTasksAsync(42));

        Assert.DoesNotContain("secret-path", ex.Message);
        Assert.Null(ex.StatusCode);
    }

    // ── The PUT body ───────────────────────────────────────────────────

    [Fact]
    public void AnEditEchoesOnlyWritableFieldsAndReplacesOnlyWhatChanged()
    {
        using var doc = JsonDocument.Parse(TaskJson);
        var task = doc.RootElement.Deserialize<TdxTicketTask>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var original = TicketTaskWrite.From(task);
        var edited = original with { Title = "Image the laptop again", ResponsibleUid = null };

        var body = TicketTaskBody.ForUpdate(doc.RootElement, original, edited);

        // Writable, untouched: echoed exactly as TeamDynamix sent it.
        Assert.Equal(90, ((JsonElement)body["EstimatedMinutes"]!).GetInt32());
        Assert.Equal(5, ((JsonElement)body["ResponsibleGroupID"]!).GetInt32());
        Assert.Equal("2026-10-01T09:00:00-07:00", ((JsonElement)body["StartDate"]!).GetString());
        Assert.Null(body["EndDate"]);
        // Changed: replaced, and a cleared field is an explicit null.
        Assert.Equal("Image the laptop again", body["Title"]);
        Assert.True(body.ContainsKey("ResponsibleUid"));
        Assert.Null(body["ResponsibleUid"]);
        // Read-only: never sent.
        foreach (var readOnly in new[] { "ID", "TicketID", "TypeID", "PercentComplete", "IsActive", "Order", "ActualMinutes",
                     "CreatedDate", "ModifiedDate", "CompletedDate", "Uri", "WebUiUrl", "ResponsibleFullName", "ResponsibleGroupName" })
            Assert.False(body.ContainsKey(readOnly), readOnly);
    }

    [Fact]
    public void AnExplicitNullSurvivesSerialization()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?> { ["ResponsibleUid"] = null, ["EndDate"] = null });
        Assert.Equal("""{"ResponsibleUid":null,"EndDate":null}""", json);
    }

    [Fact]
    public void ChangesListsOnlyDifferingFields()
    {
        var a = new TicketTaskWrite { Title = "T", EstimatedMinutes = 10, Description = " " };
        Assert.Empty(TicketTaskBody.Changes(a, a with { Title = " T ", Description = null }));
        Assert.Equal(new[] { "PredecessorID" }, TicketTaskBody.Changes(a, a with { PredecessorId = 3 }).Keys);
    }

    [Theory]
    [InlineData("", 0, null, "title")]
    [InlineData("T", -1, null, "negative")]
    [InlineData("T", 0, 7, "own predecessor")]
    public void InvalidValuesAreRefusedBeforeSending(string title, int estimate, int? predecessor, string expected)
    {
        var values = new TicketTaskWrite { Title = title, EstimatedMinutes = estimate, PredecessorId = predecessor ?? 0 };
        Assert.Contains(expected, values.Validate(7));
    }

    [Fact]
    public void ATitleOverTheDocumentedMaximumIsRefused() =>
        Assert.Contains("300", new TicketTaskWrite { Title = new string('x', 301) }.Validate());

    [Fact]
    public void AnEndBeforeTheStartIsRefused()
    {
        var start = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        Assert.Contains("before", new TicketTaskWrite { Title = "T", StartDate = start, EndDate = start.AddHours(-1) }.Validate());
    }

    // ── Template page ──────────────────────────────────────────────────

    [Fact]
    public void TheTemplatePageIsTheTicketsOwnAddTaskTemplatePage()
    {
        var url = TdxTaskTemplatePicker.PageUrl(Config(), 42);
        Assert.Equal("https://service.example.com/TDNext/Apps/7/Tickets/TicketAddTaskTemplate?TicketID=42", url!.ToString());
        Assert.Null(TdxTaskTemplatePicker.PageUrl(new TdxConfig { BaseUrl = "http://plain.example.com", AppId = 1 }, 42));
        Assert.Null(TdxTaskTemplatePicker.PageUrl(Config(), 0));
    }

    [Theory]
    [InlineData("https://service.example.com/TDNext/Apps/7/Tickets/TicketAddTaskTemplate?TicketID=42", true)]
    [InlineData("https://service.example.com/TDNext/Apps/7/Tickets/ticketaddtasktemplate?ticketid=42", true)]
    [InlineData("https://service.example.com/TDNext/Apps/7/Tickets/TicketAddTaskTemplate?TicketID=43", false)]
    [InlineData("https://service.example.com/TDNext/Apps/7/Tickets/TicketAddTaskTemplate?TicketID=42&TicketID=43", false)]
    [InlineData("https://login.example.com/TDNext/Apps/7/Tickets/TicketAddTaskTemplate?TicketID=42", false)]
    [InlineData("https://service.example.com/TDNext/Login.aspx?ReturnUrl=TicketAddTaskTemplate", false)]
    [InlineData("not a url", false)]
    public void OnlyThatExactPageIsRead(string actual, bool expected)
    {
        var page = TdxTaskTemplatePicker.PageUrl(Config(), 42)!;
        Assert.Equal(expected, TdxTaskTemplatePicker.IsPickerPage(page, actual));
    }

    private static string ScriptAnswer(string inner) => JsonSerializer.Serialize(inner);

    [Fact]
    public void ReadsTemplatesSkippingBlankDisabledAndRepeatedOptions()
    {
        var answer = ScriptAnswer("""
            {"found": true, "options": [
              {"value": "", "text": "-- Select --", "disabled": false},
              {"value": "3", "text": "  New   hire  ", "disabled": false},
              {"value": "4", "text": "Retired", "disabled": true},
              {"value": "3", "text": "Duplicate", "disabled": false},
              {"value": "-2", "text": "Negative", "disabled": false},
              {"value": "x", "text": "Not a number", "disabled": false},
              {"value": "9", "text": "Laptop return", "disabled": false}]}
            """);

        var choices = TdxTaskTemplatePicker.ParseOptions(answer);

        Assert.Equal(new[] { (3, "New hire"), (9, "Laptop return") }, choices.Select(c => (c.Id, c.Name)));
    }

    [Fact]
    public void ATicketWithNoTemplatesIsAnEmptyListNotAFailure() =>
        Assert.Empty(TdxTaskTemplatePicker.ParseOptions(ScriptAnswer("""{"found": true, "options": [{"value": "", "text": "None"}]}""")));

    [Theory]
    [InlineData("""{"found": false}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void APageWithoutTheListIsAFailure(string inner) =>
        Assert.Throws<TdxTicketTaskException>(() => TdxTaskTemplatePicker.ParseOptions(inner.Length == 0 ? "null" : ScriptAnswer(inner)));

    [Fact]
    public void TheScriptOnlyReadsTheTemplateSelect()
    {
        Assert.Contains("getElementById('ddlTaskTemplate')", TdxTaskTemplatePicker.ReadOptionsScript);
        Assert.DoesNotContain("submit", TdxTaskTemplatePicker.ReadOptionsScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("click", TdxTaskTemplatePicker.ReadOptionsScript, StringComparison.OrdinalIgnoreCase);
    }

    // ── Controller ─────────────────────────────────────────────────────

    private sealed class FakeApi : ITicketTaskApi
    {
        public List<string> Calls { get; } = new();
        public Dictionary<int, TdxTicketTask> Tasks { get; } = new();
        public Func<int, TdxTicketTask, TdxTicketTask>? OnGetTask;
        public Func<Task>? BeforeGetTasks;
        public Exception? PostFails;
        public Exception? DeleteFails;
        public bool DeleteIsIgnored;
        public TdxTicketTaskFeedRequest? LastFeed;
        public IDictionary<string, object?>? LastBody;
        public TaskCompletionSource? HoldPost;
        public List<TdxTicketTask>? ApplyResult;
        public TdxApplyTaskTemplateRequest? LastApply;

        public async Task<IReadOnlyList<TdxTicketTask>> GetTasksAsync(int ticketId, bool eligiblePredecessorsOnly = false, CancellationToken ct = default)
        {
            Calls.Add($"list {ticketId}");
            if (BeforeGetTasks != null) await BeforeGetTasks();
            ct.ThrowIfCancellationRequested();
            return Tasks.Values.OrderBy(t => t.Id).ToList();
        }

        public Task<(TdxTicketTask Task, JsonElement Raw)> GetTaskAsync(int ticketId, int taskId, CancellationToken ct = default)
        {
            Calls.Add($"get {taskId}");
            if (!Tasks.TryGetValue(taskId, out var task)) throw new TdxTicketTaskException("gone", 404);
            var current = OnGetTask?.Invoke(Calls.Count(c => c == $"get {taskId}"), task) ?? task;
            return Task.FromResult((current, JsonSerializer.SerializeToElement(current)));
        }

        public Task<IReadOnlyList<TdxFeedEntry>> GetTaskFeedAsync(int ticketId, int taskId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TdxFeedEntry>>(new List<TdxFeedEntry> { new() { Id = 1, Body = "Entry" } });

        public async Task PostTaskFeedAsync(int ticketId, int taskId, TdxTicketTaskFeedRequest request, CancellationToken ct = default)
        {
            Calls.Add($"feed {taskId}");
            LastFeed = request;
            if (HoldPost != null) await HoldPost.Task;
            if (PostFails != null) throw PostFails;
            if (request.PercentComplete is int p) Tasks[taskId].PercentComplete = p;
        }

        public Task<TdxTicketTask?> CreateTaskAsync(int ticketId, IDictionary<string, object?> body, CancellationToken ct = default)
        {
            Calls.Add("create");
            LastBody = body;
            var task = new TdxTicketTask { Id = 99, TypeId = 1, Title = (string?)body["Title"] };
            Tasks[99] = task;
            return Task.FromResult<TdxTicketTask?>(task);
        }

        public Task<TdxTicketTask?> UpdateTaskAsync(int ticketId, int taskId, IDictionary<string, object?> body, CancellationToken ct = default)
        {
            Calls.Add($"put {taskId}");
            LastBody = body;
            if (body.TryGetValue("Title", out var t) && t is string title) Tasks[taskId].Title = title;
            return Task.FromResult<TdxTicketTask?>(Tasks[taskId]);
        }

        public Task DeleteTaskAsync(int ticketId, int taskId, CancellationToken ct = default)
        {
            Calls.Add($"delete {taskId}");
            if (DeleteFails != null) throw DeleteFails;
            if (!DeleteIsIgnored) Tasks.Remove(taskId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TdxTicketTask>> ApplyTaskTemplateAsync(int ticketId, TdxApplyTaskTemplateRequest request, CancellationToken ct = default)
        {
            Calls.Add("apply");
            LastApply = request;
            return Task.FromResult<IReadOnlyList<TdxTicketTask>>(ApplyResult ?? Tasks.Values.ToList());
        }

        public Task<IReadOnlyList<TdxPerson>> SearchPeopleAsync(string text, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TdxPerson>>(Array.Empty<TdxPerson>());

        public Task<IReadOnlyList<TdxGroup>> SearchGroupsAsync(string text, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TdxGroup>>(Array.Empty<TdxGroup>());

        public Task<TdxPerson?> GetMeAsync(CancellationToken ct = default) => Task.FromResult<TdxPerson?>(null);
    }

    private static readonly DateTimeOffset Modified = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    private static TdxTicketTask Standard(int id = 11, int percent = 40) => new()
    {
        Id = id, TicketId = 42, TypeId = 1, Title = $"Task {id}", IsActive = true, PercentComplete = percent,
        EstimatedMinutes = 30, ModifiedDate = Modified,
    };

    private static async Task<(FakeApi Api, TicketTaskController Controller)> LoadedAsync(params TdxTicketTask[] tasks)
    {
        var api = new FakeApi();
        foreach (var task in tasks.DefaultIfEmpty(Standard())) api.Tasks[task.Id] = task;
        var controller = new TicketTaskController(api);
        Assert.Equal(TicketTasksPhase.Loaded, (await controller.LoadAsync(42))!.Phase);
        api.Calls.Clear();
        return (api, controller);
    }

    [Fact]
    public async Task ASlowLoadForThePreviousTicketIsDropped()
    {
        var api = new FakeApi();
        api.Tasks[11] = Standard();
        var gate = new TaskCompletionSource();
        api.BeforeGetTasks = () => gate.Task;
        var controller = new TicketTaskController(api);

        var first = controller.LoadAsync(41);
        api.BeforeGetTasks = null;
        var second = await controller.LoadAsync(42);
        gate.SetResult();

        Assert.Null(await first);
        Assert.Equal(42, second!.TicketId);
        Assert.Equal(42, controller.Current!.TicketId);
    }

    [Fact]
    public async Task AFailedLoadSaysWhyAndOffersNoChanges()
    {
        var api = new FakeApi { BeforeGetTasks = () => throw new TdxTicketTaskException("You do not have permission to read the tasks of ticket 42.", 403) };
        var controller = new TicketTaskController(api);

        var snapshot = await controller.LoadAsync(42);

        Assert.Equal(TicketTasksPhase.Failed, snapshot!.Phase);
        Assert.Contains("permission", snapshot.Error);
        var outcome = await controller.UpdateProgressAsync(42, 11, 50, null, false);
        Assert.Equal(TicketTaskSubmitStatus.NotSent, outcome.Status);
    }

    [Fact]
    public async Task ProgressReadsTheTaskFirstThenPostsThenReadsItBack()
    {
        var (api, controller) = await LoadedAsync();

        var outcome = await controller.UpdateProgressAsync(42, 11, 75, " Halfway there ", isPrivate: true);

        Assert.Equal(TicketTaskSubmitStatus.Succeeded, outcome.Status);
        Assert.Equal(new[] { "get 11", "feed 11", "get 11" }, api.Calls);
        Assert.Equal(75, api.LastFeed!.PercentComplete);
        Assert.Equal("Halfway there", api.LastFeed.Comments);
        Assert.True(api.LastFeed.IsPrivate);
        // Spent: nothing on screen can be sent again without a fresh load.
        Assert.Null(controller.Current);
        Assert.Equal(TicketTaskSubmitStatus.NotSent, (await controller.UpdateProgressAsync(42, 11, 80, null, false)).Status);
    }

    [Fact]
    public async Task CompleteIsProgressOneHundredThroughTheFeed()
    {
        var (api, controller) = await LoadedAsync();

        var outcome = await controller.CompleteAsync(42, 11, null, false);

        Assert.True(outcome.Succeeded);
        Assert.Equal(100, api.LastFeed!.PercentComplete);
        Assert.Equal("", api.LastFeed.Comments);
        Assert.StartsWith("Completed", outcome.Message);
    }

    [Fact]
    public async Task ACommentOnTheSameProgressSendsNoProgress()
    {
        var (api, controller) = await LoadedAsync();

        var outcome = await controller.UpdateProgressAsync(42, 11, 40, "Note", false);

        Assert.True(outcome.Succeeded);
        Assert.Null(api.LastFeed!.PercentComplete);
    }

    [Fact]
    public async Task NothingToSaveSendsNothing()
    {
        var (api, controller) = await LoadedAsync();

        var outcome = await controller.UpdateProgressAsync(42, 11, 40, "  ", false);

        Assert.Equal(TicketTaskSubmitStatus.NotSent, outcome.Status);
        Assert.Empty(api.Calls);
        Assert.NotNull(controller.Current);
    }

    [Fact]
    public async Task ATaskChangedSinceItWasShownIsNotOverwritten()
    {
        var (api, controller) = await LoadedAsync();
        api.OnGetTask = (_, t) => new TdxTicketTask { Id = t.Id, TypeId = 1, PercentComplete = t.PercentComplete, ModifiedDate = Modified.AddMinutes(5) };

        var outcome = await controller.UpdateProgressAsync(42, 11, 100, null, false);

        Assert.Equal(TicketTaskSubmitStatus.NotSent, outcome.Status);
        Assert.Contains("changed", outcome.Message);
        Assert.Equal(new[] { "get 11" }, api.Calls);
        // The stale tasks are dropped so the page reloads them.
        Assert.Null(controller.Current);
    }

    [Fact]
    public async Task AProgressTheServerDoesNotShowIsAFailureNotASuccess()
    {
        var (api, controller) = await LoadedAsync();
        api.OnGetTask = (n, t) => n == 2 ? new TdxTicketTask { Id = 11, TypeId = 1, PercentComplete = 40 } : t;
        api.PostFails = null;

        var outcome = await controller.UpdateProgressAsync(42, 11, 90, null, false);

        Assert.Equal(TicketTaskSubmitStatus.Failed, outcome.Status);
        Assert.Contains("40%", outcome.Message);
    }

    [Fact]
    public async Task AServerErrorAfterSendingIsUncertainAndSpent()
    {
        var (api, controller) = await LoadedAsync();
        api.PostFails = new TdxTicketTaskException("TeamDynamix did not answer in time.");

        var outcome = await controller.UpdateProgressAsync(42, 11, 90, null, false);

        Assert.Equal(TicketTaskSubmitStatus.Uncertain, outcome.Status);
        Assert.True(outcome.WasSent);
        Assert.Contains("may still have been applied", outcome.Message);
        Assert.Null(controller.Current);
    }

    [Fact]
    public async Task ARefusalIsAFailure()
    {
        var (api, controller) = await LoadedAsync();
        api.PostFails = new TdxTicketTaskException("You do not have permission to update task 11 on ticket 42.", 403);

        var outcome = await controller.UpdateProgressAsync(42, 11, 90, null, false);

        Assert.Equal(TicketTaskSubmitStatus.Failed, outcome.Status);
        Assert.Contains("permission", outcome.Message);
    }

    [Fact]
    public async Task OnlyOneChangeIsInFlight()
    {
        var (api, controller) = await LoadedAsync();
        api.HoldPost = new TaskCompletionSource();

        var first = controller.UpdateProgressAsync(42, 11, 60, null, false);
        var second = await controller.UpdateProgressAsync(42, 11, 70, null, false);
        api.HoldPost.SetResult();

        Assert.Equal(TicketTaskSubmitStatus.NotSent, second.Status);
        Assert.True((await first).Succeeded);
        Assert.Single(api.Calls, c => c == "feed 11");
    }

    [Fact]
    public async Task AFailedPreReadSendsNothing()
    {
        var (api, controller) = await LoadedAsync();
        api.Tasks.Remove(11);

        var outcome = await controller.UpdateProgressAsync(42, 11, 60, null, false);

        Assert.Equal(TicketTaskSubmitStatus.NotSent, outcome.Status);
        Assert.Contains("Nothing was sent", outcome.Message);
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("feed"));
        Assert.Null(controller.Current);
    }

    [Fact]
    public async Task AChangeForAnotherTicketIsRefused()
    {
        var (api, controller) = await LoadedAsync();

        Assert.Equal(TicketTaskSubmitStatus.NotSent, (await controller.UpdateProgressAsync(41, 11, 60, null, false)).Status);
        Assert.Equal(TicketTaskSubmitStatus.NotSent, (await controller.UpdateProgressAsync(42, 12, 60, null, false)).Status);
        Assert.Empty(api.Calls);
    }

    [Theory]
    [InlineData(TicketTaskType.Workflow)]
    [InlineData(TicketTaskType.MaintenanceActivity)]
    public async Task WorkflowTasksAndMaintenanceActivitiesAreNeverEditedOrDeleted(TicketTaskType type)
    {
        var task = Standard();
        task.TypeId = (int)type;
        var (api, controller) = await LoadedAsync(task);
        var original = TicketTaskWrite.From(task);

        var edit = await controller.EditAsync(42, 11, original, original with { Title = "Renamed" });
        var delete = await controller.DeleteAsync(42, 11);

        Assert.Equal(TicketTaskSubmitStatus.NotSent, edit.Status);
        Assert.Equal(TicketTaskSubmitStatus.NotSent, delete.Status);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task AWorkflowTasksProgressCanStillBeUpdated()
    {
        var task = Standard();
        task.TypeId = (int)TicketTaskType.Workflow;
        var (api, controller) = await LoadedAsync(task);

        Assert.True((await controller.CompleteAsync(42, 11, null, false)).Succeeded);
        Assert.Equal(100, api.LastFeed!.PercentComplete);
    }

    [Fact]
    public async Task AnEditReadsTheLatestTaskAndPutsItsWritableFields()
    {
        var (api, controller) = await LoadedAsync();
        var original = TicketTaskWrite.From(api.Tasks[11]);

        var outcome = await controller.EditAsync(42, 11, original, original with { Title = "Renamed" });

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(new[] { "get 11", "put 11", "get 11" }, api.Calls);
        Assert.Equal("Renamed", api.LastBody!["Title"]);
        Assert.False(api.LastBody.ContainsKey("PercentComplete"));
        Assert.False(api.LastBody.ContainsKey("ID"));
    }

    [Fact]
    public async Task AnEditWithNoChangesSendsNothing()
    {
        var (api, controller) = await LoadedAsync();
        var original = TicketTaskWrite.From(api.Tasks[11]);

        Assert.Equal(TicketTaskSubmitStatus.NotSent, (await controller.EditAsync(42, 11, original, original)).Status);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task AddingValidatesThenCreates()
    {
        var (api, controller) = await LoadedAsync();

        Assert.Equal(TicketTaskSubmitStatus.NotSent, (await controller.CreateAsync(42, new TicketTaskWrite { Title = " " })).Status);
        Assert.Empty(api.Calls);

        var outcome = await controller.CreateAsync(42, new TicketTaskWrite { Title = "Order a charger", EstimatedMinutes = 10 });
        Assert.True(outcome.Succeeded);
        Assert.Equal(99, outcome.TaskId);
        Assert.Equal("Order a charger", api.LastBody!["Title"]);
    }

    [Fact]
    public async Task ADeleteIsConfirmedGoneByReadingTheTasksAgain()
    {
        var (api, controller) = await LoadedAsync(Standard(11), Standard(12));

        var outcome = await controller.DeleteAsync(42, 12);

        Assert.True(outcome.Succeeded);
        Assert.Equal(new[] { "get 12", "delete 12", "list 42" }, api.Calls);
    }

    [Fact]
    public async Task ADeleteThatLeavesTheTaskIsAFailure()
    {
        var (api, controller) = await LoadedAsync();
        api.DeleteIsIgnored = true;

        var outcome = await controller.DeleteAsync(42, 11);

        Assert.Equal(TicketTaskSubmitStatus.Failed, outcome.Status);
        Assert.Contains("still on the ticket", outcome.Message);
    }

    [Theory]
    [InlineData(403, TicketTaskSubmitStatus.Failed)]
    [InlineData(500, TicketTaskSubmitStatus.Uncertain)]
    [InlineData(null, TicketTaskSubmitStatus.Uncertain)]
    public async Task ADeleteThatFailsIsReportedAsRefusedOrUncertain(int? status, TicketTaskSubmitStatus expected)
    {
        var (api, controller) = await LoadedAsync();
        api.DeleteFails = new TdxTicketTaskException("Refused or unanswered.", status);

        var outcome = await controller.DeleteAsync(42, 11);

        Assert.Equal(expected, outcome.Status);
        Assert.True(outcome.WasSent);
        Assert.Null(controller.Current);
    }

    [Fact]
    public async Task ATemplateNoLongerOfferedIsNotApplied()
    {
        var (api, controller) = await LoadedAsync();
        var template = new TaskTemplateChoice(3, "New hire");

        var outcome = await controller.ApplyTemplateAsync(42, template, new[] { new TaskTemplateChoice(4, "Other") }, false, TaskTemplateStartDate.Current);

        Assert.Equal(TicketTaskSubmitStatus.NotSent, outcome.Status);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task ApplyingReportsWhatTheServerDidWithExistingTasks()
    {
        var (api, controller) = await LoadedAsync(Standard(11));
        // Removal was asked for, but TeamDynamix kept task 11 (no delete permission).
        api.ApplyResult = new List<TdxTicketTask> { Standard(11), Standard(20), Standard(21) };
        var template = new TaskTemplateChoice(3, "New hire");

        var outcome = await controller.ApplyTemplateAsync(42, template, new[] { template }, true, TaskTemplateStartDate.TicketCreated);

        Assert.True(outcome.Succeeded);
        Assert.Equal(3, api.LastApply!.TaskTemplateId);
        Assert.True(api.LastApply.RemoveExistingTasks);
        Assert.Equal(TaskTemplateStartDate.TicketCreated, api.LastApply.StartDateSource);
        Assert.Contains("2 tasks added", outcome.Message);
        Assert.Contains("1 existing task was kept", outcome.Message);
        Assert.Null(controller.Current);
    }

    [Fact]
    public async Task TheFeedOfATaskNotInTheCurrentLoadIsNotRead()
    {
        var (_, controller) = await LoadedAsync();

        Assert.Null(await controller.LoadFeedAsync(42, 12));
        var feed = await controller.LoadFeedAsync(42, 11);
        Assert.Equal("Entry", Assert.Single(feed!.Value.Feed).Body);
    }

    // ── The panel, built and driven without a window ───────────────────

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void Pump(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void ThePanelShowsTasksAndOffersEditAndDeleteOnlyForStandardTasks() => OnSta(() =>
    {
        var workflow = Standard(12, 0);
        workflow.TypeId = (int)TicketTaskType.Workflow;
        workflow.Title = "Manager approval";
        var api = new FakeApi();
        api.Tasks[11] = Standard(11, 100);
        api.Tasks[12] = workflow;
        var catalogReads = 0;
        var panel = new TicketTasksPanel();
        panel.Attach(api, (_, _) =>
        {
            catalogReads++;
            return Task.FromResult<IReadOnlyList<TaskTemplateChoice>>(Array.Empty<TaskTemplateChoice>());
        });
        var changes = new List<TicketTaskSubmitOutcome>();
        panel.TasksChanged += changes.Add;

        Pump(panel.LoadAsync(42));

        Assert.Equal(System.Windows.Visibility.Visible, panel.Visibility);
        Assert.Equal("Tasks · 2 · 1 of 2 complete", panel.TasksHeaderText.Text);
        var rows = Assert.IsAssignableFrom<IEnumerable<TicketTaskRow>>(panel.TasksList.ItemsSource).ToList();
        Assert.Equal(new[] { 11, 12 }, rows.Select(r => r.Id));
        Assert.Equal(System.Windows.Visibility.Visible, rows[1].TypeBadgeVisibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, rows[0].TypeBadgeVisibility);

        panel.TasksList.SelectedItem = rows[1];
        Pump(Task.Delay(50));
        Assert.Equal(System.Windows.Visibility.Visible, panel.DetailBorder.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, panel.EditTaskButton.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, panel.DeleteTaskButton.Visibility);
        Assert.Contains("workflow", panel.DetailRestrictionText.Text);
        Assert.True(panel.SaveProgressButton.IsEnabled);
        Assert.Equal("0", panel.PercentBox.Text);

        panel.TasksList.SelectedItem = rows[0];
        Pump(Task.Delay(50));
        Assert.Equal(System.Windows.Visibility.Visible, panel.EditTaskButton.Visibility);
        Assert.Equal(System.Windows.Visibility.Visible, panel.DeleteTaskButton.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, panel.CompleteButton.Visibility);

        // Loading and selecting never writes, and the template list is only
        // read when Apply Template is opened.
        Assert.DoesNotContain(api.Calls, c => c.StartsWith("feed") || c.StartsWith("put") || c.StartsWith("delete") || c is "create" or "apply");
        Assert.Equal(0, catalogReads);
        Assert.Empty(changes);

        panel.Clear();
        Assert.Equal(System.Windows.Visibility.Collapsed, panel.Visibility);
    });

    [Fact]
    public void ThePanelSaysWhenThereAreNoTasksOrTheyCannotBeRead() => OnSta(() =>
    {
        var api = new FakeApi();
        var panel = new TicketTasksPanel();
        panel.Attach(api, (_, _) => Task.FromResult<IReadOnlyList<TaskTemplateChoice>>(Array.Empty<TaskTemplateChoice>()));

        Pump(panel.LoadAsync(42));
        Assert.Equal("Tasks · none", panel.TasksHeaderText.Text);
        Assert.Equal("No tasks on this ticket.", panel.MessageText.Text);
        Assert.True(panel.AddTaskButton.IsEnabled);

        api.BeforeGetTasks = () => throw new TdxTicketTaskException("You do not have permission to read the tasks of ticket 42.", 403);
        Pump(panel.LoadAsync(42));
        Assert.Equal("Tasks · unavailable", panel.TasksHeaderText.Text);
        Assert.Contains("permission", panel.MessageText.Text);
        Assert.False(panel.AddTaskButton.IsEnabled);
    });

    [Fact]
    public void TheDialogsBuildForAddEditAndApply() => OnSta(() =>
    {
        var api = new FakeApi();
        api.Tasks[11] = Standard();
        var controller = new TicketTaskController(api);
        Pump(controller.LoadAsync(42));

        var add = new TicketTaskEditorDialog(controller, 42, null);
        Assert.Equal("Add Task", add.SaveButton.Content);
        var edit = new TicketTaskEditorDialog(controller, 42, api.Tasks[11]);
        Assert.Equal("Task 11", edit.TitleBox.Text);
        Assert.Equal("30", edit.EstimateBox.Text);
        var apply = new ApplyTaskTemplateDialog(controller, 42,
            _ => Task.FromResult<IReadOnlyList<TaskTemplateChoice>>(new[] { new TaskTemplateChoice(3, "New hire") }));
        Assert.False(apply.RemoveExistingBox.IsChecked);
        Assert.False(apply.ApplyButton.IsEnabled);

        add.Close();
        edit.Close();
        apply.Close();
        Assert.DoesNotContain(api.Calls, c => c != "list 42");
    });

    // ── How tasks read ─────────────────────────────────────────────────

    [Fact]
    public void SummaryCountsCompleteTasks()
    {
        Assert.Equal("No tasks", TicketTaskFormat.Summary(Array.Empty<TdxTicketTask>()));
        Assert.Equal("1 of 2 complete", TicketTaskFormat.Summary(new[] { Standard(1, 100), Standard(2, 10) }));
    }

    [Fact]
    public void StateSaysWhatATaskIsWaitingOn()
    {
        var waiting = Standard();
        waiting.IsActive = false;
        waiting.PredecessorId = 5;
        waiting.PredecessorTitle = "Order parts";
        Assert.Equal("Waiting on Order parts", TicketTaskFormat.State(waiting));
        Assert.Equal("In progress", TicketTaskFormat.State(Standard(1, 10)));
        Assert.Equal("Not started", TicketTaskFormat.State(Standard(1, 0)));
        Assert.Equal("Complete", TicketTaskFormat.State(Standard(1, 100)));
    }

    [Fact]
    public void ResponsibilityNamesPersonAndGroup()
    {
        var task = Standard();
        Assert.Equal("Unassigned", TicketTaskFormat.Responsibility(task));
        task.ResponsibleGroupId = 5;
        task.ResponsibleGroupName = "Sample Group";
        Assert.Equal("Sample Group", TicketTaskFormat.Responsibility(task));
        task.ResponsibleFullName = "Sample Person";
        Assert.Equal("Sample Person (Sample Group)", TicketTaskFormat.Responsibility(task));
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(45, "45m")]
    [InlineData(90, "1h 30m")]
    [InlineData(1500, "1d 1h")]
    public void MinutesReadAsPeopleSayThem(int minutes, string expected) =>
        Assert.Equal(expected, TicketTaskFormat.Minutes(minutes));

    [Fact]
    public void FeedHtmlBecomesPlainText() =>
        Assert.Equal("Line one\nLine & two", TicketTaskFormat.PlainText("<p>Line one</p><b>Line</b>&nbsp;&amp; two"));

    [Theory]
    [InlineData("0", true, 0)]
    [InlineData("100", true, 100)]
    [InlineData(" 55% ", true, 55)]
    [InlineData("101", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("12.5", false, 0)]
    [InlineData("", false, 0)]
    public void ProgressIsAWholeNumberFromZeroToOneHundred(string text, bool ok, int expected)
    {
        Assert.Equal(ok, TicketTasksPanel.TryReadPercent(text, out var percent));
        if (ok) Assert.Equal(expected, percent);
    }

    [Fact]
    public void AnUntouchedDateIsKeptExactly()
    {
        var original = new DateTimeOffset(2026, 10, 1, 16, 0, 30, TimeSpan.Zero);
        var local = original.ToLocalTime();

        Assert.True(TicketTaskEditorDialog.TryReadDate(local.Date, local.ToString("HH:mm"), original, false, out var kept, out _));
        Assert.Equal(original, kept);

        Assert.True(TicketTaskEditorDialog.TryReadDate(new DateTime(2026, 10, 3), "", null, true, out var end, out _));
        Assert.Equal(new TimeSpan(23, 59, 0), end!.Value.TimeOfDay);

        Assert.True(TicketTaskEditorDialog.TryReadDate(null, "10:00", original, false, out var cleared, out _));
        Assert.Null(cleared);

        Assert.False(TicketTaskEditorDialog.TryReadDate(new DateTime(2026, 10, 3), "25:00", null, false, out _, out var error));
        Assert.Contains("24-hour", error);
    }
}
