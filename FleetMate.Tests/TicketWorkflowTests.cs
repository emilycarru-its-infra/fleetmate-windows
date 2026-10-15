using System.Net;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The ticket workflow routes against the documented TDWebApi contract, and the
/// controller that guards them for the UI. Every fixture is synthetic.
/// </summary>
public class TicketWorkflowTests
{
    private const string StepA = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string StepB = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string Token = "synthetic-bearer-token-value";

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

    private static TdxService Service(StubHandler handler)
    {
        // The ticketing app differs from the base and assets apps, so a request
        // to the wrong application shows up in the URL.
        var service = new TdxService(new TdxConfig
        {
            BaseUrl = "https://service.example.com",
            AppId = 1,
            TicketingAppId = 7,
            AssetsAppId = 9,
        }, handler);
        service.SetSsoToken(Token, DateTime.UtcNow.AddHours(1));
        return service;
    }

    [Fact]
    public async Task ReadsTheWorkflowFromTheTicketingApplicationAsTheSignedInUser()
    {
        var handler = new StubHandler(_ => Json($$"""
            {"ID": 3, "Name": "Purchase approval", "Description": "Two reviews", "IsComplete": false, "Status": 0,
             "CurrentStepIDs": ["{{StepA}}", "{{StepB.ToUpperInvariant()}}"],
             "Steps": [
               {"ID": "{{StepA}}", "Name": "Budget review", "TypeName": "Approval", "IsCurrent": true, "IsInError": false},
               {"ID": "11111111-2222-3333-4444-555555555555", "Name": "Intake", "IsCurrent": false},
               {"ID": "{{StepB}}", "Name": "Security review", "TypeName": "Approval", "IsCurrent": false, "IsInError": true}
             ]}
            """));
        using var service = Service(handler);

        var workflow = await service.GetTicketWorkflowAsync(42);

        var (request, _) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://service.example.com/TDWebApi/api/7/tickets/42/workflow", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);

        Assert.NotNull(workflow);
        Assert.Equal("Purchase approval", workflow!.Name);
        Assert.Equal("In process", workflow.StatusLabel);
        // Both parallel steps are active — one by IsCurrent, one only through
        // CurrentStepIDs and in a different case — and the inactive one is not.
        Assert.Equal(new[] { "Budget review", "Security review" }, workflow.ActiveSteps().Select(s => s.Name));
        Assert.True(workflow.ActiveSteps()[1].IsInError);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    public async Task ATicketWithoutAWorkflowIsNullNotAnError(string body)
    {
        using var service = Service(new StubHandler(_ => Json(body)));

        Assert.Null(await service.GetTicketWorkflowAsync(42));
    }

    [Theory]
    [InlineData(1, true, "Approved")]
    [InlineData(2, true, "Rejected")]
    [InlineData(0, true, "Complete")]
    [InlineData(0, false, "In process")]
    public void StatusReadsAsOneWord(int status, bool complete, string expected)
    {
        Assert.Equal(expected, new TdxTicketWorkflow { Status = status, IsComplete = complete }.StatusLabel);
    }

    [Fact]
    public void ACurrentStepIdWithNoMatchingStepIsStillShown()
    {
        var workflow = new TdxTicketWorkflow { CurrentStepIds = new() { StepA }, Steps = new() };

        Assert.Equal(StepA, Assert.Single(workflow.ActiveSteps()).Id);
    }

    [Fact]
    public void ACompleteWorkflowHasNoActiveSteps()
    {
        var workflow = new TdxTicketWorkflow
        {
            IsComplete = true, Status = 1, CurrentStepIds = new() { StepA },
            Steps = new() { new TdxWorkflowStep { Id = StepA, IsCurrent = true } },
        };

        Assert.Empty(workflow.ActiveSteps());
    }

    [Fact]
    public async Task AsksForActionsWithTheRequiredStepIdAndKeepsActionIdsVerbatim()
    {
        // Action IDs are opaque: punctuation and case must survive untouched.
        var handler = new StubHandler(_ => Json("""
            [{"ID": "act:Approve/1+x", "Name": "Approve", "Tooltip": "Approve this step"},
             {"ID": "act:reject/2", "Name": "Reject", "Tooltip": null},
             {"ID": "", "Name": "Broken"}]
            """));
        using var service = Service(handler);

        var actions = await service.GetWorkflowStepActionsAsync(42, StepA);

        var (request, _) = Assert.Single(handler.Requests);
        Assert.Equal($"/TDWebApi/api/7/tickets/42/workflow/actions?stepId={StepA}", request.RequestUri!.PathAndQuery);
        // An action with no ID cannot be performed, so it is not offered.
        Assert.Equal(new[] { "act:Approve/1+x", "act:reject/2" }, actions.Select(a => a.Id));
        Assert.Equal("Approve this step", actions[0].Tooltip);
    }

    [Fact]
    public async Task NullActionsMeanNoneAvailable()
    {
        using var service = Service(new StubHandler(_ => Json("null")));

        Assert.Empty(await service.GetWorkflowStepActionsAsync(42, StepA));
    }

    [Fact]
    public async Task ActionsWithoutAStepAreNotRequested()
    {
        var handler = new StubHandler(_ => Json("[]"));
        using var service = Service(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetWorkflowStepActionsAsync(42, " "));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ApprovePostsTheStepActionAndCommentsAsTheContractNamesThem()
    {
        var handler = new StubHandler(_ => Json("""{"IsSuccessful": true, "Message": "Done", "Errors": [], "WasWorkflowUpdated": true}"""));
        using var service = Service(handler);

        var result = await service.PerformWorkflowActionAsync(42, new TdxWorkflowApproveRequest
        {
            StepId = StepA, ActionId = "act:Approve/1+x", Comments = "Looks fine",
        });

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/TDWebApi/api/7/tickets/42/workflow/approve", request.RequestUri!.AbsolutePath);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal(StepA, doc.RootElement.GetProperty("StepID").GetString());
        Assert.Equal("act:Approve/1+x", doc.RootElement.GetProperty("ActionID").GetString());
        Assert.Equal("Looks fine", doc.RootElement.GetProperty("Comments").GetString());
        Assert.True(result.IsSuccessful);
        Assert.True(result.WasWorkflowUpdated);
    }

    [Fact]
    public async Task BlankCommentsAreLeftOutOfTheBody()
    {
        var handler = new StubHandler(_ => Json("""{"IsSuccessful": true}"""));
        using var service = Service(handler);

        await service.PerformWorkflowActionAsync(42, new TdxWorkflowApproveRequest { StepId = StepA, ActionId = "a", Comments = "  " });

        using var doc = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.False(doc.RootElement.TryGetProperty("Comments", out _));
    }

    [Fact]
    public async Task ABusinessRefusalArrivesAsHttp200AndIsStillAFailure()
    {
        using var service = Service(new StubHandler(_ => Json($$"""
            {"IsSuccessful": false, "Message": "The step could not be approved.",
             "Errors": [{"Message": "A required field is empty.", "MemberNames": ["Field"], "StepIDs": ["{{StepA}}"]},
                        {"Message": "The step could not be approved."}],
             "WasWorkflowUpdated": false}
            """)));

        var result = await service.PerformWorkflowActionAsync(42, new TdxWorkflowApproveRequest { StepId = StepA, ActionId = "a" });

        Assert.False(result.IsSuccessful);
        Assert.Equal(StepA, result.Errors![0].StepIds![0]);
        // The message and each distinct error, once.
        Assert.Equal("The step could not be approved. A required field is empty.", result.FailureSummary());
    }

    [Fact]
    public void ARefusalWithNoWordsStillSaysSomething()
    {
        Assert.False(string.IsNullOrWhiteSpace(new TdxWorkflowActionResult { IsSuccessful = false }.FailureSummary()));
    }

    [Fact]
    public async Task AnEmptyApproveAnswerIsNotTakenAsSuccess()
    {
        using var service = Service(new StubHandler(_ => Json("")));

        await Assert.ThrowsAsync<TdxWorkflowException>(() =>
            service.PerformWorkflowActionAsync(42, new TdxWorkflowApproveRequest { StepId = StepA, ActionId = "a" }));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    public async Task HttpFailuresThrowWithTheirStatusAndNoCredential(HttpStatusCode status, int expected)
    {
        using var service = Service(new StubHandler(_ => Json("""{"Message": "denied"}""", status)));

        var ex = await Assert.ThrowsAsync<TdxWorkflowException>(() => service.GetWorkflowStepActionsAsync(42, StepA));

        Assert.Equal(expected, ex.StatusCode);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task AnUnreadableWorkflowIsAnErrorNotAnEmptyWorkflow()
    {
        using var service = Service(new StubHandler(_ => Json("<html>proxy error</html>")));

        await Assert.ThrowsAsync<TdxWorkflowException>(() => service.GetTicketWorkflowAsync(42));
    }

    [Fact]
    public async Task ATransportFailureIsAWorkflowError()
    {
        using var service = Service(new StubHandler(_ => throw new HttpRequestException("connection reset")));

        var ex = await Assert.ThrowsAsync<TdxWorkflowException>(() => service.GetTicketWorkflowAsync(42));
        Assert.Null(ex.StatusCode);
    }

    // ── Controller ─────────────────────────────────────────────────────

    private sealed class FakeApi : ITicketWorkflowApi
    {
        public Func<int, CancellationToken, Task<TdxTicketWorkflow?>> Workflow { get; set; } =
            (_, _) => Task.FromResult<TdxTicketWorkflow?>(null);
        public Func<int, string, Task<IReadOnlyList<TdxWorkflowStepAction>>> Actions { get; set; } =
            (_, _) => Task.FromResult<IReadOnlyList<TdxWorkflowStepAction>>(Array.Empty<TdxWorkflowStepAction>());
        public Func<int, TdxWorkflowApproveRequest, Task<TdxWorkflowActionResult>> Perform { get; set; } =
            (_, _) => Task.FromResult(new TdxWorkflowActionResult { IsSuccessful = true });
        public List<(int TicketId, TdxWorkflowApproveRequest Request)> Performed { get; } = new();

        public Task<TdxTicketWorkflow?> GetTicketWorkflowAsync(int ticketId, CancellationToken ct = default) => Workflow(ticketId, ct);

        public Task<IReadOnlyList<TdxWorkflowStepAction>> GetWorkflowStepActionsAsync(int ticketId, string stepId, CancellationToken ct = default) =>
            Actions(ticketId, stepId);

        public Task<TdxWorkflowActionResult> PerformWorkflowActionAsync(int ticketId, TdxWorkflowApproveRequest request, CancellationToken ct = default)
        {
            Performed.Add((ticketId, request));
            return Perform(ticketId, request);
        }
    }

    private static TdxTicketWorkflow TwoParallelSteps() => new()
    {
        Id = 3, Name = "Purchase approval",
        CurrentStepIds = new() { StepA, StepB },
        Steps = new()
        {
            new TdxWorkflowStep { Id = StepA, Name = "Budget review", IsCurrent = true },
            new TdxWorkflowStep { Id = StepB, Name = "Security review", IsCurrent = true },
        },
    };

    private static IReadOnlyList<TdxWorkflowStepAction> Offer(params string[] ids) =>
        ids.Select(id => new TdxWorkflowStepAction { Id = id, Name = id.ToUpperInvariant() }).ToList();

    private static FakeApi ApiWithParallelSteps() => new()
    {
        Workflow = (_, _) => Task.FromResult<TdxTicketWorkflow?>(TwoParallelSteps()),
        Actions = (_, step) => Task.FromResult(step == StepA ? Offer("approve-a", "reject-a") : Offer("approve-b")),
    };

    [Fact]
    public async Task EveryParallelStepGetsItsOwnActions()
    {
        var controller = new TicketWorkflowController(ApiWithParallelSteps());

        var snapshot = await controller.LoadAsync(42);

        Assert.Equal(TicketWorkflowPhase.Loaded, snapshot!.Phase);
        Assert.Equal(2, snapshot.ActiveSteps.Count);
        Assert.Equal(new[] { "approve-a", "reject-a" }, snapshot.ActiveSteps[0].Actions.Select(a => a.Id));
        Assert.Equal(new[] { "approve-b" }, snapshot.ActiveSteps[1].Actions.Select(a => a.Id));
    }

    [Fact]
    public async Task OneStepRefusingTheUserDoesNotHideTheOthers()
    {
        var api = ApiWithParallelSteps();
        api.Actions = (_, step) => step == StepA
            ? Task.FromException<IReadOnlyList<TdxWorkflowStepAction>>(new TdxWorkflowException("no", 403))
            : Task.FromResult(Offer("approve-b"));
        var controller = new TicketWorkflowController(api);

        var snapshot = await controller.LoadAsync(42);

        Assert.Empty(snapshot!.ActiveSteps[0].Actions);
        Assert.Contains("permission", snapshot.ActiveSteps[0].ActionsNote);
        Assert.Single(snapshot.ActiveSteps[1].Actions);
        Assert.Null(snapshot.ActiveSteps[1].ActionsNote);
    }

    [Fact]
    public async Task AStepWithNoActionsSaysSo()
    {
        var api = ApiWithParallelSteps();
        api.Actions = (_, _) => Task.FromResult<IReadOnlyList<TdxWorkflowStepAction>>(Array.Empty<TdxWorkflowStepAction>());

        var snapshot = await new TicketWorkflowController(api).LoadAsync(42);

        Assert.All(snapshot!.ActiveSteps, s => Assert.Equal("No actions available to you on this step.", s.ActionsNote));
    }

    [Fact]
    public async Task NoWorkflowIsItsOwnPhase()
    {
        var snapshot = await new TicketWorkflowController(new FakeApi()).LoadAsync(42);

        Assert.Equal(TicketWorkflowPhase.None, snapshot!.Phase);
        Assert.Null(snapshot.Workflow);
    }

    [Fact]
    public async Task AFailedReadIsReportedNotShownAsNoWorkflow()
    {
        var api = new FakeApi { Workflow = (_, _) => Task.FromException<TdxTicketWorkflow?>(new TdxWorkflowException("Server said no", 500)) };

        var snapshot = await new TicketWorkflowController(api).LoadAsync(42);

        Assert.Equal(TicketWorkflowPhase.Failed, snapshot!.Phase);
        Assert.Equal("Server said no", snapshot.Error);
    }

    [Fact]
    public async Task ASlowLoadForThePreviousTicketIsDropped()
    {
        var first = new TaskCompletionSource<TdxTicketWorkflow?>();
        var api = ApiWithParallelSteps();
        api.Workflow = (id, _) => id == 1 ? first.Task : Task.FromResult<TdxTicketWorkflow?>(TwoParallelSteps());
        var controller = new TicketWorkflowController(api);

        var slow = controller.LoadAsync(1);
        var fresh = await controller.LoadAsync(2);
        first.SetResult(TwoParallelSteps());

        Assert.Null(await slow);
        Assert.Equal(2, fresh!.TicketId);
        Assert.Equal(2, controller.Current!.TicketId);
        Assert.True(controller.IsCurrent(fresh));
    }

    [Fact]
    public async Task ClearAbandonsALoadInFlight()
    {
        var pending = new TaskCompletionSource<TdxTicketWorkflow?>();
        var api = new FakeApi { Workflow = (_, _) => pending.Task };
        var controller = new TicketWorkflowController(api);

        var load = controller.LoadAsync(1);
        controller.Clear();
        pending.SetResult(TwoParallelSteps());

        Assert.Null(await load);
        Assert.Null(controller.Current);
    }

    [Fact]
    public async Task SendsExactlyTheOfferedIdsWithTrimmedComments()
    {
        var api = ApiWithParallelSteps();
        var controller = new TicketWorkflowController(api);
        await controller.LoadAsync(42);

        var outcome = await controller.SubmitAsync(42, StepB.ToUpperInvariant(), "approve-b", "  ok  ");

        Assert.True(outcome.Succeeded);
        var (ticket, request) = Assert.Single(api.Performed);
        Assert.Equal(42, ticket);
        // The step ID goes back as TeamDynamix sent it, not as the caller typed it.
        Assert.Equal(StepB, request.StepId);
        Assert.Equal("approve-b", request.ActionId);
        Assert.Equal("ok", request.Comments);
    }

    [Fact]
    public async Task BlankCommentsAreSentAsNone()
    {
        var api = ApiWithParallelSteps();
        var controller = new TicketWorkflowController(api);
        await controller.LoadAsync(42);

        await controller.SubmitAsync(42, StepA, "approve-a", "   ");

        Assert.Null(api.Performed[0].Request.Comments);
    }

    [Theory]
    // Never offered: a guessed "approve" is refused before any request.
    [InlineData(StepA, "approve")]
    // Offered on the other step, not this one.
    [InlineData(StepA, "approve-b")]
    // Action IDs are opaque and compared exactly.
    [InlineData(StepA, "APPROVE-A")]
    // Not an active step.
    [InlineData("11111111-2222-3333-4444-555555555555", "approve-a")]
    public async Task OnlyAnActionOfferedOnThatStepIsSent(string stepId, string actionId)
    {
        var api = ApiWithParallelSteps();
        var controller = new TicketWorkflowController(api);
        await controller.LoadAsync(42);

        var outcome = await controller.SubmitAsync(42, stepId, actionId, null);

        Assert.Equal(TicketWorkflowSubmitStatus.NotSent, outcome.Status);
        Assert.Empty(api.Performed);
    }

    [Fact]
    public async Task AnActionForATicketNoLongerLoadedIsNotSent()
    {
        var api = ApiWithParallelSteps();
        var controller = new TicketWorkflowController(api);
        await controller.LoadAsync(42);
        await controller.LoadAsync(43);

        var outcome = await controller.SubmitAsync(42, StepA, "approve-a", null);

        Assert.Equal(TicketWorkflowSubmitStatus.NotSent, outcome.Status);
        Assert.Empty(api.Performed);
    }

    [Fact]
    public async Task NothingIsSentBeforeTheWorkflowHasLoaded()
    {
        var api = ApiWithParallelSteps();

        var outcome = await new TicketWorkflowController(api).SubmitAsync(42, StepA, "approve-a", null);

        Assert.Equal(TicketWorkflowSubmitStatus.NotSent, outcome.Status);
        Assert.Empty(api.Performed);
    }

    [Fact]
    public async Task ASecondSubmitWhileOneIsInFlightIsNotSent()
    {
        var gate = new TaskCompletionSource<TdxWorkflowActionResult>();
        var api = ApiWithParallelSteps();
        api.Perform = (_, _) => gate.Task;
        var controller = new TicketWorkflowController(api);
        await controller.LoadAsync(42);

        var first = controller.SubmitAsync(42, StepA, "approve-a", null);
        Assert.True(controller.IsSubmitting);
        var second = await controller.SubmitAsync(42, StepB, "approve-b", null);
        gate.SetResult(new TdxWorkflowActionResult { IsSuccessful = true });

        Assert.Equal(TicketWorkflowSubmitStatus.NotSent, second.Status);
        Assert.True((await first).Succeeded);
        Assert.Single(api.Performed);
        Assert.False(controller.IsSubmitting);
    }

    [Fact]
    public async Task IsSuccessfulFalseIsAFailureWithTheServersReasons()
    {
        var api = ApiWithParallelSteps();
        api.Perform = (_, _) => Task.FromResult(new TdxWorkflowActionResult
        {
            IsSuccessful = false,
            Errors = new() { new TdxWorkflowValidationMessage { Message = "A required field is empty." } },
        });
        var controller = new TicketWorkflowController(api);
        await controller.LoadAsync(42);

        var outcome = await controller.SubmitAsync(42, StepA, "approve-a", null);

        Assert.Equal(TicketWorkflowSubmitStatus.Failed, outcome.Status);
        Assert.Equal("A required field is empty.", outcome.Message);
        Assert.False(controller.IsSubmitting);
    }

    [Fact]
    public async Task ARequestFailureIsAFailureAndReleasesTheGuard()
    {
        var api = ApiWithParallelSteps();
        api.Perform = (_, _) => Task.FromException<TdxWorkflowActionResult>(new TdxWorkflowException("Sign in again.", 401));
        var controller = new TicketWorkflowController(api);
        await controller.LoadAsync(42);

        var outcome = await controller.SubmitAsync(42, StepA, "approve-a", null);

        Assert.Equal(TicketWorkflowSubmitStatus.Failed, outcome.Status);
        Assert.Equal("Sign in again.", outcome.Message);
        Assert.False(controller.IsSubmitting);
    }

    // ── Rows ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RowsCarryTheTicketStepAndOpaqueActionForEachButton()
    {
        var api = ApiWithParallelSteps();
        api.Actions = (_, step) => Task.FromResult<IReadOnlyList<TdxWorkflowStepAction>>(step == StepA
            ? new[] { new TdxWorkflowStepAction { Id = "x/1", Name = "Approve", Tooltip = "  " } }
            : Array.Empty<TdxWorkflowStepAction>());
        var snapshot = await new TicketWorkflowController(api).LoadAsync(42);

        var rows = WorkflowStepRow.From(snapshot!);

        Assert.Equal(2, rows.Count);
        var button = Assert.Single(rows[0].Actions);
        Assert.Equal(new WorkflowActionRow(42, StepA, "Budget review", "x/1", "Approve", null), button);
        Assert.Null(rows[0].Note);
        Assert.Empty(rows[1].Actions);
        Assert.Equal("No actions available to you on this step.", rows[1].Note);
    }

    // ── Acting only as the signed-in operator ──────────────────────────

    private static TdxService Unsigned(StubHandler handler) => new(new TdxConfig
    {
        BaseUrl = "https://service.example.com",
        AppId = 1,
        TicketingAppId = 7,
    }, handler);

    public static TheoryData<string> WorkflowCalls => new() { "workflow", "actions", "approve" };

    private static Task Call(TdxService service, string which) => which switch
    {
        "workflow" => service.GetTicketWorkflowAsync(42),
        "actions" => service.GetWorkflowStepActionsAsync(42, StepA),
        _ => service.PerformWorkflowActionAsync(42, new TdxWorkflowApproveRequest { StepId = StepA, ActionId = "a" }),
    };

    [Theory]
    [MemberData(nameof(WorkflowCalls))]
    public async Task WithoutASignInNothingIsSent(string which)
    {
        var handler = new StubHandler(_ => Json("""{"IsSuccessful": true}"""));
        using var service = Unsigned(handler);

        var ex = await Assert.ThrowsAsync<TdxWorkflowException>(() => Call(service, which));

        // No silent sign-in, no other credential, no request of any kind.
        Assert.Empty(handler.Requests);
        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("Sign in", ex.Message);
    }

    [Theory]
    [MemberData(nameof(WorkflowCalls))]
    public async Task AnExpiredSignInSendsNothing(string which)
    {
        var handler = new StubHandler(_ => Json("""{"IsSuccessful": true}"""));
        using var service = Unsigned(handler);
        service.SetSsoToken(Token, DateTime.UtcNow.AddMinutes(-1));

        var ex = await Assert.ThrowsAsync<TdxWorkflowException>(() => Call(service, which));

        Assert.Empty(handler.Requests);
        Assert.Contains("expired", ex.Message);
    }

    [Fact]
    public async Task AStaleDefaultAuthorizationIsNeverInherited()
    {
        // An earlier, unrelated call leaves a bearer on the client's default
        // headers. Once the operator's session expires, a workflow call must
        // not ride on it.
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/tickets/42") ? Json("""{"ID": 42}""") : Json("null"));
        using var service = Unsigned(handler);
        service.SetSsoToken("earlier-session-token", DateTime.UtcNow.AddHours(1));
        await service.GetTicketAsync(42);
        service.SetSsoToken("earlier-session-token", DateTime.UtcNow.AddMinutes(-1));

        await Assert.ThrowsAsync<TdxWorkflowException>(() => service.GetTicketWorkflowAsync(42));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EachWorkflowRequestCarriesTheCurrentOperatorToken()
    {
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/tickets/42") ? Json("""{"ID": 42}""") : Json("null"));
        using var service = Unsigned(handler);
        service.SetSsoToken("earlier-session-token", DateTime.UtcNow.AddHours(1));
        await service.GetTicketAsync(42);
        service.SetSsoToken(Token, DateTime.UtcNow.AddHours(1));

        await service.GetTicketWorkflowAsync(42);

        var auth = handler.Requests[^1].Request.Headers.Authorization!;
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal(Token, auth.Parameter);
    }

    // ── What failures say and log ──────────────────────────────────────

    private const string PrivateBody = "<html><title>Sign in</title>private-marker ticket text</html>";

    private sealed class CaptureSink : Serilog.Core.ILogEventSink
    {
        public List<string> Lines { get; } = new();
        public void Emit(Serilog.Events.LogEvent e)
        {
            lock (Lines) Lines.Add(e.RenderMessage() + " " + e.Exception);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.OK)]
    public async Task NeitherTheMessageNorTheLogCarriesTheResponseBody(HttpStatusCode status)
    {
        var sink = new CaptureSink();
        var previous = Serilog.Log.Logger;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        try
        {
            using var service = Service(new StubHandler(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent(PrivateBody, Encoding.UTF8, "text/html"),
            }));

            var ex = await Assert.ThrowsAsync<TdxWorkflowException>(() => service.GetTicketWorkflowAsync(42));

            Assert.DoesNotContain("private-marker", ex.Message);
            Assert.DoesNotContain("<html", ex.Message);
            Assert.True(ex.Message.Length < 200);
        }
        finally
        {
            Serilog.Log.Logger = previous;
        }

        lock (sink.Lines)
        {
            // The workflow lines: operation, ticket and status, and nothing
            // from the request or the response.
            var workflowLines = sink.Lines.Where(l => l.Contains("Workflow")).ToList();
            Assert.Contains(workflowLines, l => l.Contains("ticket 42"));
            Assert.DoesNotContain(workflowLines, l => l.Contains("service.example.com"));
            Assert.DoesNotContain(sink.Lines, l => l.Contains("private-marker"));
            Assert.DoesNotContain(sink.Lines, l => l.Contains(Token));
        }
    }

    [Fact]
    public async Task ARefusedActionLogsNoServerText()
    {
        var sink = new CaptureSink();
        var previous = Serilog.Log.Logger;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        try
        {
            using var service = Service(new StubHandler(_ => Json(
                """{"IsSuccessful": false, "Message": "private-marker reason", "Errors": [{"Message": "private-marker detail"}]}""")));

            var result = await service.PerformWorkflowActionAsync(42, new TdxWorkflowApproveRequest { StepId = StepA, ActionId = "a" });
            Assert.False(result.IsSuccessful);
        }
        finally
        {
            Serilog.Log.Logger = previous;
        }

        lock (sink.Lines) Assert.DoesNotContain(sink.Lines, l => l.Contains("private-marker"));
    }

    [Fact]
    public async Task ATimeoutSaysSoWithoutTheUrl()
    {
        using var service = Service(new StubHandler(_ => throw new TaskCanceledException("https://service.example.com/secret-path")));

        var ex = await Assert.ThrowsAsync<TdxWorkflowException>(() => service.GetTicketWorkflowAsync(42));

        Assert.Contains("did not answer in time", ex.Message);
        Assert.DoesNotContain("service.example.com", ex.Message);
    }

    // ── No blind resubmit ──────────────────────────────────────────────

    public static TheoryData<string> AttemptOutcomes => new() { "refused", "transport", "succeeded" };

    [Theory]
    [MemberData(nameof(AttemptOutcomes))]
    public async Task TheSameLoadedActionCannotBeSentTwice(string how)
    {
        var api = ApiWithParallelSteps();
        api.Perform = (_, _) => how switch
        {
            "refused" => Task.FromResult(new TdxWorkflowActionResult { IsSuccessful = false, Message = "No." }),
            // A timeout after TeamDynamix may already have committed the action.
            "transport" => Task.FromException<TdxWorkflowActionResult>(new TdxWorkflowException("TeamDynamix did not answer in time.")),
            _ => Task.FromResult(new TdxWorkflowActionResult { IsSuccessful = true }),
        };
        var controller = new TicketWorkflowController(api);
        await controller.LoadAsync(42);

        var first = await controller.SubmitAsync(42, StepA, "approve-a", "comment");
        var again = await controller.SubmitAsync(42, StepA, "approve-a", "comment");
        var otherStep = await controller.SubmitAsync(42, StepB, "approve-b", null);

        Assert.NotEqual(TicketWorkflowSubmitStatus.NotSent, first.Status);
        Assert.Equal(TicketWorkflowSubmitStatus.NotSent, again.Status);
        Assert.Equal(TicketWorkflowSubmitStatus.NotSent, otherStep.Status);
        Assert.Single(api.Performed);
        Assert.Null(controller.Current);

        // A fresh load offers the actions again, and only then can one be sent.
        await controller.LoadAsync(42);
        var retried = await controller.SubmitAsync(42, StepA, "approve-a", "comment");
        Assert.NotEqual(TicketWorkflowSubmitStatus.NotSent, retried.Status);
        Assert.Equal(2, api.Performed.Count);
    }

    [Fact]
    public async Task NothingIsSentWhileTheWorkflowIsReloading()
    {
        // During a refresh the buttons on screen may describe a workflow that
        // is already gone, so nothing is sendable until the refresh lands.
        var slow = new TaskCompletionSource<TdxTicketWorkflow?>();
        var api = ApiWithParallelSteps();
        var controller = new TicketWorkflowController(api);
        await controller.LoadAsync(42);
        api.Workflow = (_, _) => slow.Task;

        var refresh = controller.LoadAsync(42);
        // The refresh has replaced the loaded snapshot with a Loading one.
        Assert.Equal(TicketWorkflowPhase.Loading, controller.Current!.Phase);
        var blocked = await controller.SubmitAsync(42, StepA, "approve-a", null);
        slow.SetResult(TwoParallelSteps());
        await refresh;

        Assert.Equal(TicketWorkflowSubmitStatus.NotSent, blocked.Status);
        Assert.Empty(api.Performed);
    }
}
