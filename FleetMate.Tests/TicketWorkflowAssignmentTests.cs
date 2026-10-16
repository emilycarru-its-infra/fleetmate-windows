using System.Net;
using System.Text;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Restarting, replacing and assigning a ticket's workflow: the PUT's wire
/// contract and the controller's checks before it sends. Every request goes
/// to a stub; no real ticket is ever written.
/// </summary>
public class TicketWorkflowAssignmentTests
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

    private static TdxService Service(StubHandler handler, bool signedIn = true)
    {
        var service = new TdxService(new TdxConfig
        {
            BaseUrl = "https://service.example.com",
            AppId = 1,
            TicketingAppId = 7,
            AssetsAppId = 9,
        }, handler);
        if (signedIn) service.SetSsoToken(Token, DateTime.UtcNow.AddHours(1));
        return service;
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task ThePutCarriesBothParametersInTheQueryAndNoBody(bool allowRemove, string expected)
    {
        var handler = new StubHandler(_ => Json("""{"ID": 42, "Title": "Synthetic"}"""));
        using var service = Service(handler);

        var ticket = await service.AssignTicketWorkflowAsync(42, 15, allowRemove);

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"/TDWebApi/api/7/tickets/42/workflow?newWorkflowId=15&allowRemoveExisting={expected}",
            request.RequestUri!.PathAndQuery);
        Assert.True(string.IsNullOrEmpty(body));
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
        Assert.Equal(42, ticket!.Id);
    }

    [Fact]
    public async Task OnlyOneRequestIsSentAndItIsNeverADelete()
    {
        var handler = new StubHandler(_ => Json("""{"ID": 42}"""));
        using var service = Service(handler);

        await service.AssignTicketWorkflowAsync(42, 15, true);

        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Delete);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task WithoutASignInThePutIsNotSent()
    {
        var handler = new StubHandler(_ => Json("""{"ID": 42}"""));
        using var service = Service(handler, signedIn: false);

        var ex = await Assert.ThrowsAsync<TdxWorkflowException>(() => service.AssignTicketWorkflowAsync(42, 15, true));

        Assert.Empty(handler.Requests);
        Assert.Equal(401, ex.StatusCode);
        Assert.False(ex.OutcomeUnknown);
    }

    [Fact]
    public async Task AnExpiredSignInSendsNothing()
    {
        var handler = new StubHandler(_ => Json("""{"ID": 42}"""));
        using var service = Service(handler, signedIn: false);
        service.SetSsoToken(Token, DateTime.UtcNow.AddMinutes(-1));

        await Assert.ThrowsAsync<TdxWorkflowException>(() => service.AssignTicketWorkflowAsync(42, 15, true));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AMissingWorkflowIdIsNotSent()
    {
        var handler = new StubHandler(_ => Json("""{"ID": 42}"""));
        using var service = Service(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.AssignTicketWorkflowAsync(42, 0, true));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "permission")]
    [InlineData(HttpStatusCode.BadRequest, "HTTP 400")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task HttpErrorsFailWithAPlainMessageAndNoServerText(HttpStatusCode status, string expected)
    {
        using var service = Service(new StubHandler(_ => Json("""{"Message": "private-marker detail"}""", status)));

        var ex = await Assert.ThrowsAsync<TdxWorkflowException>(() => service.AssignTicketWorkflowAsync(42, 15, true));

        Assert.Equal((int)status, ex.StatusCode);
        Assert.False(ex.OutcomeUnknown);
        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("private-marker", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task ATimeoutLeavesTheOutcomeUnknown()
    {
        using var service = Service(new StubHandler(_ => throw new TaskCanceledException("https://service.example.com/x")));

        var ex = await Assert.ThrowsAsync<TdxWorkflowException>(() => service.AssignTicketWorkflowAsync(42, 15, true));

        Assert.True(ex.OutcomeUnknown);
        Assert.DoesNotContain("service.example.com", ex.Message);
    }

    [Fact]
    public async Task AnUnreadableSuccessLeavesTheOutcomeUnknown()
    {
        using var service = Service(new StubHandler(_ => Json("<html>proxy</html>")));

        var ex = await Assert.ThrowsAsync<TdxWorkflowException>(() => service.AssignTicketWorkflowAsync(42, 15, true));

        Assert.True(ex.OutcomeUnknown);
    }

    [Fact]
    public async Task AReadFailureIsNotAnUnknownOutcome()
    {
        // Only writes can have landed; a failed read changed nothing.
        using var service = Service(new StubHandler(_ => throw new HttpRequestException("reset")));

        var ex = await Assert.ThrowsAsync<TdxWorkflowException>(() => service.GetTicketWorkflowAsync(42));

        Assert.False(ex.OutcomeUnknown);
    }

    // ── Requests and their confirmation ────────────────────────────────

    // The running instance's ID and its definition's ID differ on purpose, so a
    // restart that sent the instance ID would show up.
    private static readonly DateTime Started = new(2026, 1, 5, 9, 30, 0, DateTimeKind.Utc);

    private static TdxTicketWorkflow Workflow(int id = 3, string name = "Purchase approval", params string[] current) =>
        Instance(id, 900 + id, Started, name, current);

    private static TdxTicketWorkflow Instance(int definition, int instance, DateTime? started, string name = "Purchase approval",
        params string[] current) => new()
    {
        Id = instance,
        WorkflowConfigurationId = definition,
        StartDateUtc = started,
        Name = name,
        CurrentStepIds = current.Length == 0 ? new() { StepA } : current.ToList(),
        Steps = new() { new TdxWorkflowStep { Id = StepA, Name = "Budget review", IsCurrent = current.Length == 0 } },
    };

    [Fact]
    public void RestartTargetsTheCurrentWorkflowDefinitionAndRemovesTheExisting()
    {
        var (request, reason) = TicketWorkflowChangeRequest.Restart(42, Workflow());

        Assert.Null(reason);
        Assert.Equal(TicketWorkflowChangeKind.Restart, request!.Kind);
        Assert.Equal(3, request.TargetWorkflowId);
        Assert.True(request.AllowRemoveExisting);
    }

    [Fact]
    public void RestartNeverSendsTheRunningInstanceId()
    {
        var (request, _) = TicketWorkflowChangeRequest.Restart(42, Instance(definition: 3, instance: 903, Started));

        Assert.Equal(3, request!.TargetWorkflowId);
    }

    [Fact]
    public void RestartRefusesAWorkflowWhoseDefinitionIsUnknown()
    {
        var (request, reason) = TicketWorkflowChangeRequest.Restart(42, Instance(definition: 0, instance: 903, Started));

        Assert.Null(request);
        Assert.Contains("definition", reason);
    }

    [Fact]
    public void RestartRefusesATicketWithoutAWorkflow()
    {
        var (request, reason) = TicketWorkflowChangeRequest.Restart(42, null);

        Assert.Null(request);
        Assert.Contains("no workflow", reason);
    }

    [Fact]
    public void ChangeOnATicketWithoutAWorkflowIsAnAssignmentThatKeepsAnyExisting()
    {
        var (request, _) = TicketWorkflowChangeRequest.Change(42, null, new TdxWorkflowChoice(15, "Onboarding"));

        Assert.Equal(TicketWorkflowChangeKind.Assign, request!.Kind);
        Assert.False(request.AllowRemoveExisting);
    }

    [Fact]
    public void ChangeToTheSameWorkflowIsRefusedInFavourOfRestart()
    {
        var (request, reason) = TicketWorkflowChangeRequest.Change(42, Workflow(), new TdxWorkflowChoice(3, "Purchase approval"));

        Assert.Null(request);
        Assert.Contains("Restart", reason);
    }

    [Fact]
    public void TheConfirmationNamesTicketCurrentAndTargetAndWarnsAboutTheReset()
    {
        var (request, _) = TicketWorkflowChangeRequest.Change(42, Workflow(), new TdxWorkflowChoice(15, "Onboarding"));

        var text = request!.ConfirmationText();

        Assert.Contains("#42", text);
        Assert.Contains("Current workflow: Purchase approval", text);
        Assert.Contains("New workflow: Onboarding", text);
        Assert.Contains("reset", text);
        Assert.Contains("notifications", text);
        Assert.Contains("tasks", text);
        Assert.Contains("automations", text);
    }

    [Fact]
    public void AFirstAssignmentRemovesNothingSoDoesNotWarnAboutAReset()
    {
        var (request, _) = TicketWorkflowChangeRequest.Change(42, null, new TdxWorkflowChoice(15, "Onboarding"));

        var text = request!.ConfirmationText();

        Assert.Contains("Current workflow: none", text);
        Assert.DoesNotContain("reset", text);
    }

    [Fact]
    public void TheExpectationSeesProgressNotJustTheWorkflowId()
    {
        var expected = TicketWorkflowExpectation.From(Workflow());

        Assert.True(expected.Matches(Workflow(current: StepA.ToUpperInvariant())));
        Assert.False(expected.Matches(Workflow(current: StepB)));
        Assert.False(expected.Matches(Workflow(id: 4)));
        // Restarted by someone else: same definition, a new instance and start.
        Assert.False(expected.Matches(Instance(3, 950, Started.AddMinutes(5))));
        Assert.False(expected.Matches(Instance(3, 903, Started.AddMinutes(5))));
        Assert.False(expected.Matches(Instance(3, 950, Started)));
        Assert.False(expected.Matches(null));
        Assert.True(TicketWorkflowExpectation.From(null).Matches(null));
    }

    // ── Controller ─────────────────────────────────────────────────────

    private sealed class FakeApi : ITicketWorkflowAssignmentApi
    {
        public Func<int, Task<TdxTicketWorkflow?>> Live { get; set; } = _ => Task.FromResult<TdxTicketWorkflow?>(Workflow());
        public Func<int, int, bool, Task<TdxTicket?>> Put { get; set; } =
            (id, _, _) => Task.FromResult<TdxTicket?>(new TdxTicket { Id = id });
        public List<(int TicketId, int WorkflowId, bool AllowRemove)> Puts { get; } = new();
        public int Reads { get; private set; }

        public Task<TdxTicketWorkflow?> GetTicketWorkflowAsync(int ticketId, CancellationToken ct = default)
        {
            Reads++;
            return Live(ticketId);
        }

        public Task<TdxTicket?> AssignTicketWorkflowAsync(int ticketId, int newWorkflowId, bool allowRemoveExisting, CancellationToken ct = default)
        {
            Puts.Add((ticketId, newWorkflowId, allowRemoveExisting));
            return Put(ticketId, newWorkflowId, allowRemoveExisting);
        }
    }

    private sealed class FakeCatalog : ITicketWorkflowCatalog
    {
        public Func<int, Task<IReadOnlyList<TdxWorkflowChoice>>> Choices { get; set; } = _ =>
            Task.FromResult<IReadOnlyList<TdxWorkflowChoice>>(new[]
            {
                new TdxWorkflowChoice(3, "Purchase approval"),
                new TdxWorkflowChoice(15, "Onboarding"),
                new TdxWorkflowChoice(15, "Onboarding (duplicate)"),
                new TdxWorkflowChoice(0, "Broken"),
            });

        public Task<IReadOnlyList<TdxWorkflowChoice>> GetAssignableWorkflowsAsync(int ticketId, CancellationToken ct = default) =>
            Choices(ticketId);
    }

    private static TicketWorkflowChangeRequest RestartRequest() => TicketWorkflowChangeRequest.Restart(42, Workflow()).Request!;

    private static TicketWorkflowChangeRequest ReplaceRequest() =>
        TicketWorkflowChangeRequest.Change(42, Workflow(), new TdxWorkflowChoice(15, "Onboarding")).Request!;

    private static TicketWorkflowChangeRequest AssignRequest() =>
        TicketWorkflowChangeRequest.Change(42, null, new TdxWorkflowChoice(15, "Onboarding")).Request!;

    [Fact]
    public async Task ARestartReadsTheWorkflowAgainThenSendsOnePutWithRemoveExisting()
    {
        var api = new FakeApi();

        var outcome = await new TicketWorkflowAssignmentController(api).SubmitAsync(RestartRequest());

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, api.Reads);
        Assert.Equal((42, 3, true), Assert.Single(api.Puts));
        Assert.True(outcome.RequiresReload);
    }

    [Fact]
    public async Task ARestartDoesNotNeedTheCatalog()
    {
        var api = new FakeApi();

        var outcome = await new TicketWorkflowAssignmentController(api, catalog: null).SubmitAsync(RestartRequest());

        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task AnAssignmentSendsRemoveExistingFalse()
    {
        var api = new FakeApi { Live = _ => Task.FromResult<TdxTicketWorkflow?>(null) };

        var outcome = await new TicketWorkflowAssignmentController(api, new FakeCatalog()).SubmitAsync(AssignRequest());

        Assert.True(outcome.Succeeded);
        Assert.Equal((42, 15, false), Assert.Single(api.Puts));
    }

    [Fact]
    public async Task AReplacementSendsRemoveExistingTrue()
    {
        var api = new FakeApi();

        var outcome = await new TicketWorkflowAssignmentController(api, new FakeCatalog()).SubmitAsync(ReplaceRequest());

        Assert.True(outcome.Succeeded);
        Assert.Equal((42, 15, true), Assert.Single(api.Puts));
    }

    public static TheoryData<string> Moves => new() { "progressed", "replaced", "restarted", "removed", "added" };

    [Theory]
    [MemberData(nameof(Moves))]
    public async Task AWorkflowThatMovedSinceTheChoiceBlocksTheChange(string how)
    {
        var api = new FakeApi
        {
            Live = _ => Task.FromResult<TdxTicketWorkflow?>(how switch
            {
                "progressed" => Workflow(current: StepB),
                "replaced" => Workflow(id: 9, name: "Something else"),
                "restarted" => Instance(3, 960, Started.AddHours(1)),
                "added" => Workflow(id: 9, name: "Someone else's"),
                _ => null,
            }),
        };
        var controller = new TicketWorkflowAssignmentController(api, new FakeCatalog());
        var request = how == "added" ? AssignRequest() : RestartRequest();

        var outcome = await controller.SubmitAsync(request);

        Assert.Equal(TicketWorkflowChangeStatus.NotSent, outcome.Status);
        Assert.True(outcome.WorkflowChanged);
        Assert.True(outcome.RequiresReload);
        Assert.Empty(api.Puts);
    }

    [Fact]
    public async Task WhenTheCurrentWorkflowCannotBeReadNothingIsSent()
    {
        var api = new FakeApi { Live = _ => Task.FromException<TdxTicketWorkflow?>(new TdxWorkflowException("No answer.")) };

        var outcome = await new TicketWorkflowAssignmentController(api).SubmitAsync(RestartRequest());

        Assert.Equal(TicketWorkflowChangeStatus.NotSent, outcome.Status);
        Assert.Contains("nothing was changed", outcome.Message);
        Assert.Empty(api.Puts);
    }

    [Fact]
    public async Task AChangeWithoutACatalogIsNotSent()
    {
        var api = new FakeApi();
        var controller = new TicketWorkflowAssignmentController(api, catalog: null);

        Assert.False(controller.CanChange);
        var outcome = await controller.SubmitAsync(ReplaceRequest());

        Assert.Equal(TicketWorkflowChangeStatus.NotSent, outcome.Status);
        Assert.Empty(api.Puts);
    }

    [Fact]
    public async Task ATargetNoLongerOfferedIsNotSent()
    {
        var api = new FakeApi();
        var catalog = new FakeCatalog
        {
            Choices = _ => Task.FromResult<IReadOnlyList<TdxWorkflowChoice>>(new[] { new TdxWorkflowChoice(20, "Other") }),
        };

        var outcome = await new TicketWorkflowAssignmentController(api, catalog).SubmitAsync(ReplaceRequest());

        Assert.Equal(TicketWorkflowChangeStatus.NotSent, outcome.Status);
        Assert.Contains("no longer offered", outcome.Message);
        Assert.Empty(api.Puts);
    }

    [Fact]
    public async Task ACatalogThatCannotBeReadAtSubmitBlocksTheChange()
    {
        var api = new FakeApi();
        var catalog = new FakeCatalog
        {
            Choices = _ => Task.FromException<IReadOnlyList<TdxWorkflowChoice>>(new TdxWorkflowException("Denied.", 403)),
        };

        var outcome = await new TicketWorkflowAssignmentController(api, catalog).SubmitAsync(ReplaceRequest());

        Assert.Equal(TicketWorkflowChangeStatus.NotSent, outcome.Status);
        Assert.Empty(api.Puts);
    }

    [Fact]
    public async Task ChoicesLeaveOutTheCurrentWorkflowDuplicatesAndUnusableIds()
    {
        var controller = new TicketWorkflowAssignmentController(new FakeApi(), new FakeCatalog());

        var choices = await controller.GetChoicesAsync(42, Workflow());

        Assert.Equal(new[] { 15 }, choices.Select(c => c.Id));
    }

    [Fact]
    public async Task AnHttpErrorIsAFailureThatStillReloads()
    {
        var api = new FakeApi { Put = (_, _, _) => Task.FromException<TdxTicket?>(new TdxWorkflowException("You do not have permission.", 403)) };

        var outcome = await new TicketWorkflowAssignmentController(api).SubmitAsync(RestartRequest());

        Assert.Equal(TicketWorkflowChangeStatus.Failed, outcome.Status);
        Assert.Equal("You do not have permission.", outcome.Message);
        Assert.True(outcome.RequiresReload);
    }

    [Fact]
    public async Task ATimeoutIsUncertainAndIsNeverRetried()
    {
        var api = new FakeApi
        {
            Put = (_, _, _) => Task.FromException<TdxTicket?>(new TdxWorkflowException("No answer in time.", null, null, outcomeUnknown: true)),
        };

        var outcome = await new TicketWorkflowAssignmentController(api).SubmitAsync(RestartRequest());

        Assert.Equal(TicketWorkflowChangeStatus.Uncertain, outcome.Status);
        Assert.Contains("may or may not", outcome.Message);
        Assert.True(outcome.RequiresReload);
        Assert.Single(api.Puts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(43)]
    public async Task AnAnswerForNoTicketOrAnotherTicketIsUncertain(int? returnedId)
    {
        var api = new FakeApi
        {
            Put = (_, _, _) => Task.FromResult(returnedId is int id ? new TdxTicket { Id = id } : null),
        };

        var outcome = await new TicketWorkflowAssignmentController(api).SubmitAsync(RestartRequest());

        Assert.Equal(TicketWorkflowChangeStatus.Uncertain, outcome.Status);
        Assert.Equal(42, outcome.TicketId);
        Assert.Null(outcome.Ticket);
    }

    [Fact]
    public async Task ASecondChangeWhileOneIsInFlightIsNotSent()
    {
        var gate = new TaskCompletionSource<TdxTicket?>();
        var api = new FakeApi { Put = (_, _, _) => gate.Task };
        var controller = new TicketWorkflowAssignmentController(api, new FakeCatalog());

        var first = controller.SubmitAsync(RestartRequest());
        Assert.True(controller.IsSubmitting);
        var second = await controller.SubmitAsync(ReplaceRequest());
        gate.SetResult(new TdxTicket { Id = 42 });

        Assert.Equal(TicketWorkflowChangeStatus.NotSent, second.Status);
        Assert.False(second.RequiresReload);
        Assert.True((await first).Succeeded);
        Assert.Single(api.Puts);
        Assert.False(controller.IsSubmitting);
    }

    [Fact]
    public async Task AHandBuiltRequestThatContradictsItselfIsNotSent()
    {
        var api = new FakeApi();
        var controller = new TicketWorkflowAssignmentController(api, new FakeCatalog());
        var expected = TicketWorkflowExpectation.From(Workflow());

        // A restart naming another workflow, and an assignment over an existing one.
        var restartElsewhere = new TicketWorkflowChangeRequest(42, TicketWorkflowChangeKind.Restart, expected, 15, "Onboarding");
        var assignOver = new TicketWorkflowChangeRequest(42, TicketWorkflowChangeKind.Assign, expected, 15, "Onboarding");

        Assert.Equal(TicketWorkflowChangeStatus.NotSent, (await controller.SubmitAsync(restartElsewhere)).Status);
        Assert.Equal(TicketWorkflowChangeStatus.NotSent, (await controller.SubmitAsync(assignOver)).Status);
        Assert.Empty(api.Puts);
    }

    // ── The workflow picker page ───────────────────────────────────────

    private static readonly TdxConfig PickerConfig = new()
    {
        BaseUrl = "https://service.example.com/TDWebApi",
        AppId = 1,
        TicketingAppId = 7,
    };

    private static string ScriptResult(string inner) => System.Text.Json.JsonSerializer.Serialize(inner);

    [Fact]
    public void ThePickerIsTheTicketsReassignWorkflowPageInTheTicketingApp()
    {
        var url = TdxWorkflowPicker.PageUrl(PickerConfig, 42);

        Assert.Equal("https://service.example.com/TDNext/Apps/7/Tickets/TicketAssignWorkflow?TicketID=42", url!.ToString());
    }

    [Fact]
    public void ThePickerIsOnlyBuiltOverHttps()
    {
        Assert.Null(TdxWorkflowPicker.PageUrl(new TdxConfig { BaseUrl = "http://service.example.com", AppId = 7 }, 42));
        Assert.Null(TdxWorkflowPicker.PageUrl(PickerConfig, 0));
    }

    [Theory]
    [InlineData("https://service.example.com/TDNext/Apps/7/Tickets/TicketAssignWorkflow?TicketID=42", true)]
    [InlineData("https://SERVICE.example.com/tdnext/apps/7/tickets/ticketassignworkflow?ticketid=42", true)]
    [InlineData("https://service.example.com/TDNext/Apps/7/Tickets/TicketAssignWorkflow?TicketID=43", false)]
    [InlineData("https://service.example.com/TDNext/Apps/7/Tickets/TicketAssignWorkflow?TicketID=42&TicketID=43", false)]
    [InlineData("https://service.example.com/TDNext/Apps/8/Tickets/TicketAssignWorkflow?TicketID=42", false)]
    [InlineData("https://service.example.com/TDNext/Login.aspx?ReturnUrl=TicketAssignWorkflow%3FTicketID%3D42", false)]
    [InlineData("https://login.example.net/TDNext/Apps/7/Tickets/TicketAssignWorkflow?TicketID=42", false)]
    [InlineData("http://service.example.com/TDNext/Apps/7/Tickets/TicketAssignWorkflow?TicketID=42", false)]
    [InlineData("https://service.example.com:8443/TDNext/Apps/7/Tickets/TicketAssignWorkflow?TicketID=42", false)]
    [InlineData("about:blank", false)]
    [InlineData(null, false)]
    public void OnlyThatTicketsPickerOnTheConfiguredOriginIsRead(string? actual, bool expected)
    {
        var url = TdxWorkflowPicker.PageUrl(PickerConfig, 42)!;

        Assert.Equal(expected, TdxWorkflowPicker.IsPickerPage(url, actual));
    }

    [Fact]
    public void TheOptionsBecomeUniquePositiveChoicesWithTheirDisplayText()
    {
        var result = ScriptResult("""
            {"found": true, "options": [
              {"value": "", "text": "", "disabled": false},
              {"value": "15", "text": "  Synthetic \t onboarding\n ", "disabled": false},
              {"value": "16", "text": "Synthetic & approval <review>", "disabled": false},
              {"value": "15", "text": "Duplicate", "disabled": false},
              {"value": "17", "text": "Disabled", "disabled": true},
              {"value": "0", "text": "Zero", "disabled": false},
              {"value": "-4", "text": "Negative", "disabled": false},
              {"value": "abc", "text": "Not a number", "disabled": false},
              {"value": "18", "text": "   ", "disabled": false}
            ]}
            """);

        var choices = TdxWorkflowPicker.ParseOptions(result);

        Assert.Equal(new[] { 15, 16 }, choices.Select(c => c.Id));
        Assert.Equal("Synthetic onboarding", choices[0].Name);
        Assert.Equal("Synthetic & approval <review>", choices[1].Name);
    }

    [Fact]
    public void APickerWithOnlyTheBlankOptionOffersNothing()
    {
        var choices = TdxWorkflowPicker.ParseOptions(ScriptResult("""{"found": true, "options": [{"value": "", "text": ""}]}"""));

        Assert.Empty(choices);
    }

    [Theory]
    [InlineData("""{"found": false}""")]
    [InlineData("""{"found": true}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void APageWithoutTheDropDownIsAFailureNotAnEmptyList(string inner)
    {
        Assert.Throws<TdxWorkflowException>(() => TdxWorkflowPicker.ParseOptions(ScriptResult(inner)));
    }

    [Fact]
    public void ANullScriptResultIsAFailure()
    {
        Assert.Throws<TdxWorkflowException>(() => TdxWorkflowPicker.ParseOptions("null"));
        Assert.Throws<TdxWorkflowException>(() => TdxWorkflowPicker.ParseOptions(null));
    }

    [Fact]
    public void TheScriptOnlyReadsTheSelect()
    {
        var script = TdxWorkflowPicker.ReadOptionsScript;

        Assert.Contains("ddlWorkflow", script);
        Assert.DoesNotContain("submit", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("click", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fetch", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".value =", script);
    }
}
