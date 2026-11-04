using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

public class TicketsBulkToolsTests
{
    private const string Tickets = """
        {"tickets":[
          {"id":101,"subject":"Phish 1","status":"open","type":"incident","priority":"normal","assignee_id":701,"group_id":801,"tags":["phish","inbox"],"custom_fields":[{"id":9001,"value":"a"}]},
          {"id":102,"subject":"Phish 2","status":"new","type":null,"priority":null,"assignee_id":null,"group_id":null,"tags":[],"custom_fields":[]}
        ]}
        """;

    private const string JobQueued = """{"job_status":{"id":"job1","status":"queued","results":null}}""";

    private const string JobCompleted = """{"job_status":{"id":"job1","status":"completed","results":[{"id":101,"action":"update","success":true,"status":"Updated"},{"id":102,"action":"update","success":true,"status":"Updated"}]}}""";

    private static FakeResponse Json(string body) => FakeResponse.Json(body);

    private static FakeZendesk BulkFake(string tickets = Tickets, Func<int, string>? job = null, Func<RecordedRequest, FakeResponse?>? extra = null)
    {
        var polls = 0;
        return TestHelpers.CreateFakeZendesk(r => extra?.Invoke(r) ?? (r.Path, r.Method.Method) switch
        {
            ("/api/v2/tickets/show_many", "GET") => Json(tickets),
            ("/api/v2/tickets/update_many", "PUT") => Json(JobQueued),
            ("/api/v2/job_statuses/job1", "GET") => Json((job ?? (_ => JobCompleted))(++polls)),
            _ => FakeResponse.NotFound(),
        });
    }

    private static ZendeskService Service(FakeZendesk fake, Action<ZendeskServerOptions>? configure = null, ToolCallAuditContext? auditContext = null) =>
        TestHelpers.BuildZendeskService(fake.Client, auditContext: auditContext, configure: o =>
        {
            o.JobStatusPollInterval = TimeSpan.Zero;
            configure?.Invoke(o);
        });

    private static void NoWrites(FakeZendesk fake) =>
        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "a preview or a refusal must not change anything");

    // ---------- the preview ----------

    [Fact]
    public async Task DryRunIsTheDefault_ReadsOnlyAndShowsCurrentBesideProposed()
    {
        using var fake = BulkFake();

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102],
            status: "solved", priority: "LOW", additional_tags: ["spam"], remove_tags: ["inbox"]))!;

        NoWrites(fake);
        fake.Requests.Single().Query("ids").Should().Be("101,102");
        result["dry_run"]!.GetValue<bool>().Should().BeTrue();
        result["changed"]!.GetValue<bool>().Should().BeFalse();
        result["next_step"]!.GetValue<string>().Should().Contain("Show this preview to the person");

        var first = result["tickets"]![0]!;
        first["outcome"]!.GetValue<string>().Should().Be("would_update");
        first["current"]!["status"]!.GetValue<string>().Should().Be("open");
        first["proposed"]!["status"]!.GetValue<string>().Should().Be("solved");
        first["proposed"]!["priority"]!.GetValue<string>().Should().Be("low");
        first["current"]!["tags"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("phish", "inbox");
        first["proposed"]!["tags"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("phish", "spam");
        first["agent_url"]!.GetValue<string>().Should().EndWith("/agent/tickets/101");

        // Only the fields the change touches are shown.
        first["current"]!["assignee_id"].Should().BeNull();
        first["current"]!.AsObject().ContainsKey("assignee_id").Should().BeFalse();
        result["tickets"]![1]!["current"]!["priority"].Should().BeNull();
        result["tickets"]![1]!["proposed"]!["priority"]!.GetValue<string>().Should().Be("low");
    }

    [Fact]
    public async Task DryRun_ShowsCustomFieldValuesAndTheComment()
    {
        using var fake = BulkFake();

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102],
            custom_fields: [new TicketCustomFieldInput(9001, "b")], comment: "Closing as phishing"))!;

        NoWrites(fake);
        result["change"]!["comment"]!["public"]!.GetValue<bool>().Should().BeFalse();
        result["change"]!["comment"]!["body"]!.GetValue<string>().Should().Be("Closing as phishing");
        result["tickets"]![0]!["current"]!["custom_fields"]!.ToJsonString().Should().Be("""[{"id":9001,"value":"a"}]""");
        result["tickets"]![0]!["proposed"]!["custom_fields"]!.ToJsonString().Should().Be("""[{"id":9001,"value":"b"}]""");
        result["tickets"]![1]!["current"]!["custom_fields"]!.ToJsonString().Should().Be("""[{"id":9001,"value":null}]""");
    }

    [Fact]
    public async Task DryRun_ReportsMissingAndClosedTicketsAsWouldRefuse()
    {
        const string withClosed = """{"tickets":[{"id":101,"status":"closed","tags":[]}]}""";
        using var fake = BulkFake(withClosed);

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 103], priority: "high"))!;

        NoWrites(fake);
        result["tickets"]![0]!["outcome"]!.GetValue<string>().Should().Be("closed");
        result["tickets"]![1]!["outcome"]!.GetValue<string>().Should().Be("not_found");
        var refusals = result["would_refuse"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        refusals.Should().Contain(r => r.Contains("103") && r.Contains("cannot see"));
        refusals.Should().Contain(r => r.Contains("101") && r.Contains("closed"));
        result["next_step"]!.GetValue<string>().Should().Contain("would be refused");
    }

    // ---------- the write ----------

    [Fact]
    public async Task Apply_PutsOneChangeWithTagDeltas_AndReportsTheJobResults()
    {
        using var fake = BulkFake();

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102],
            status: "pending", assignee_id: 702, additional_tags: ["spam"], remove_tags: ["inbox"], comment: "Bulk note", dry_run: false))!;

        var put = fake.Requests.Single(r => r.Method == HttpMethod.Put);
        put.Path.Should().Be("/api/v2/tickets/update_many");
        put.Query("ids").Should().Be("101,102");
        var body = JsonNode.Parse(put.Body!)!["ticket"]!;
        body["status"]!.GetValue<string>().Should().Be("pending");
        body["assignee_id"]!.GetValue<long>().Should().Be(702);
        body["additional_tags"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("spam");
        body["remove_tags"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("inbox");

        // Never a replacing tag list: that would overwrite each ticket's own tags with one list.
        body["tags"].Should().BeNull();
        body["comment"]!["public"]!.GetValue<bool>().Should().BeFalse();
        body["comment"]!["body"]!.GetValue<string>().Should().Be("Bulk note");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);

        result["dry_run"]!.GetValue<bool>().Should().BeFalse();
        result["job"]!["status"]!.GetValue<string>().Should().Be("completed");
        result["updated"]!.AsArray().Select(i => i!.GetValue<long>()).Should().Equal(101, 102);
        result["failed"]!.AsArray().Should().BeEmpty();
        result["unconfirmed"]!.AsArray().Should().BeEmpty();
        result["summary"]!.GetValue<string>().Should().Be("Updated all 2 ticket(s).");
    }

    [Fact]
    public async Task Apply_ReportsFailedAndUnconfirmedTicketsSeparately()
    {
        const string mixed = """{"job_status":{"id":"job1","status":"completed","results":[{"id":101,"success":true},{"id":102,"success":false,"error":"TicketUpdateFailed","details":"Status: closed prevents ticket update"}]}}""";
        const string three = """{"tickets":[{"id":101,"status":"open"},{"id":102,"status":"open"},{"id":103,"status":"open"}]}""";
        using var fake = BulkFake(three, _ => mixed);

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102, 103], priority: "high", dry_run: false))!;

        result["updated"]!.AsArray().Select(i => i!.GetValue<long>()).Should().Equal(101);
        result["failed"]![0]!["id"]!.GetValue<long>().Should().Be(102);
        result["failed"]![0]!["error"]!.GetValue<string>().Should().Be("TicketUpdateFailed");
        result["failed"]![0]!["details"]!.GetValue<string>().Should().Contain("closed");

        // 103 is in no result, so nothing says it changed: it is unconfirmed, not updated.
        result["unconfirmed"]!.AsArray().Select(i => i!.GetValue<long>()).Should().Equal(103);
        result["summary"]!.GetValue<string>().Should().StartWith("Updated 1 of 3");
    }

    [Fact]
    public async Task Apply_AResultThatClaimsNeitherSuccessNorFailureIsUnconfirmed()
    {
        const string vague = """{"job_status":{"id":"job1","status":"completed","results":[{"id":101,"status":"Skipped"},{"id":102,"success":true}]}}""";
        using var fake = BulkFake(job: _ => vague);

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], priority: "high", dry_run: false))!;

        result["updated"]!.AsArray().Select(i => i!.GetValue<long>()).Should().Equal(102);
        result["unconfirmed"]!.AsArray().Select(i => i!.GetValue<long>()).Should().Equal(101);
    }

    [Fact]
    public async Task Apply_PollsUntilTheJobFinishes()
    {
        using var fake = BulkFake(job: n => n < 3 ? """{"job_status":{"id":"job1","status":"working"}}""" : JobCompleted);

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], priority: "high", dry_run: false))!;

        fake.Requests.Count(r => r.Path == "/api/v2/job_statuses/job1").Should().Be(3);
        result["job"]!["status"]!.GetValue<string>().Should().Be("completed");
    }

    [Fact]
    public async Task Apply_AJobStillRunningAtTheBoundIsReportedAsRunning_NeverAsSuccess()
    {
        using var fake = BulkFake(job: _ => """{"job_status":{"id":"job1","status":"working","results":[{"id":101,"success":true}]}}""");

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake, o => o.MaxJobStatusPolls = 4), [101, 102],
            priority: "high", dry_run: false))!;

        fake.Requests.Count(r => r.Path == "/api/v2/job_statuses/job1").Should().Be(4, "the poll is bounded by MaxJobStatusPolls");
        result["job"]!["status"]!.GetValue<string>().Should().Be("running");
        result["summary"]!.GetValue<string>().Should().Contain("still applying").And.Contain("Do not call again");
        result["summary"]!.GetValue<string>().Should().NotContain("Updated all");
        result["unconfirmed"]!.AsArray().Select(i => i!.GetValue<long>()).Should().Equal(102);
    }

    [Fact]
    public async Task Apply_AFailedJobIsNotReportedAsComplete()
    {
        using var fake = BulkFake(job: _ => """{"job_status":{"id":"job1","status":"failed","message":"boom"}}""");

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], priority: "high", dry_run: false))!;

        result["job"]!["status"]!.GetValue<string>().Should().Be("failed");
        result["updated"]!.AsArray().Should().BeEmpty();
        result["unconfirmed"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task Apply_RefusesMissingOrClosedTickets_WithoutWriting()
    {
        const string withClosed = """{"tickets":[{"id":101,"status":"closed"}]}""";
        using var fake = BulkFake(withClosed);

        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 103], priority: "high", dry_run: false);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should()
            .StartWith("Nothing was changed.").And.Contain("103").And.Contain("closed");
        NoWrites(fake);
    }

    [Fact]
    public async Task Apply_RecordsEveryTicketIdInTheAudit_NotTheJobId()
    {
        // The job response carries no ticket records, so without the request's ids= the write would
        // be audited as touching nothing (or, read naively, as touching record "job1").
        using var fake = BulkFake(job: _ => """{"job_status":{"id":"job1","status":"completed","results":null}}""");
        var context = new ToolCallAuditContext();

        await TicketsBulkTools.BulkUpdateTickets(Service(fake, auditContext: context), [101, 102], priority: "high", dry_run: false);

        var summary = context.Summarise();
        summary.Ids.Should().Contain(["101", "102"]).And.NotContain("job1");

        // The preview read names the tickets too, so the ids alone would pass even if the write
        // named nobody. Each of the read and the write names both tickets (2 + 2), and only the
        // unfinished job poll is a call without ids.
        summary.RecordsFetched.Should().Be(4);
        summary.CallsWithoutIds.Should().Be(1, "only the job poll, which has no results yet, names nobody");
    }

    // ---------- confirmations and switches ----------

    [Fact]
    public async Task APublicCommentNeedsPublicReplies_EvenForAPreview()
    {
        using var fake = BulkFake();

        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], comment: "Hello", @public: true);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*AllowPublicReplies*");
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task APublicCommentIsSentWhenPublicRepliesAreOn()
    {
        using var fake = BulkFake();

        await TicketsBulkTools.BulkUpdateTickets(Service(fake, o => o.Features.AllowPublicReplies = true), [101, 102],
            comment: "Hello", @public: true, dry_run: false);

        var comment = JsonNode.Parse(fake.Requests.Single(r => r.Method == HttpMethod.Put).Body!)!["ticket"]!["comment"]!;
        comment["public"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task SolvingProblemTicketsNeedsSolveIncidents()
    {
        const string problem = """{"tickets":[{"id":101,"type":"problem","status":"open"},{"id":102,"type":"incident","status":"open"}]}""";
        using var fake = BulkFake(problem);

        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], status: "solved", dry_run: false);
        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("101").And.Contain("solve_incidents").And.NotContain("102");
        NoWrites(fake);

        var preview = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], status: "solved"))!;
        preview["would_refuse"]!.AsArray().Should().ContainSingle(r => r!.GetValue<string>().Contains("solve_incidents"));

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], status: "solved", solve_incidents: true, dry_run: false))!;
        result["warnings"]!.AsArray().Should().ContainSingle(w => w!.GetValue<string>().Contains("101"));
        fake.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task ACustomStatusInTheSolvedCategoryCountsAsSolving()
    {
        const string problem = """{"tickets":[{"id":101,"type":"problem","status":"open"}]}""";
        using var fake = BulkFake(problem, extra: r => r.Path == "/api/v2/custom_statuses/55"
            ? Json("""{"custom_status":{"id":55,"status_category":"solved"}}""")
            : null);

        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], custom_status_id: 55, dry_run: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*solve_incidents*");
    }

    [Fact]
    public async Task NotSolving_AProblemTicketNeedsNoConfirmation()
    {
        // Negative control for the two above: the confirmation is about solving, not about problem tickets.
        const string problem = """{"tickets":[{"id":101,"type":"problem","status":"open"}]}""";
        using var fake = BulkFake(problem);

        await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], priority: "high", dry_run: false);

        fake.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Put);
    }

    // ---------- validation ----------

    public static TheoryData<long[], string> BadIds => new()
    {
        { [], "*at least one*" },
        { [101, 0], "*positive*" },
        { [101, 102, 101], "*distinct*101*" },
        { Enumerable.Range(1, 101).Select(i => (long)i).ToArray(), "*at most 100*" },
    };

    [Theory]
    [MemberData(nameof(BadIds))]
    public async Task RefusesBadTicketIds_WithoutCallingZendesk(long[] ids, string message)
    {
        using var fake = BulkFake();

        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), ids, priority: "high");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AcceptsExactlyOneHundredTickets()
    {
        // The boundary of the test above: 100 is Zendesk's own limit and must be allowed.
        using var fake = BulkFake("""{"tickets":[]}""");

        await TicketsBulkTools.BulkUpdateTickets(Service(fake), Enumerable.Range(1, 100).Select(i => (long)i).ToArray(), priority: "high");

        fake.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task RefusesACallWithNoChange()
    {
        using var fake = BulkFake();
        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101]);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("Give at least one change*");
    }

    [Fact]
    public async Task RefusesStatusWithCustomStatus()
    {
        using var fake = BulkFake();
        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], status: "open", custom_status_id: 5);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*not both*");
    }

    [Fact]
    public async Task RefusesTagsWithCustomFields()
    {
        using var fake = BulkFake();
        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], additional_tags: ["x"], custom_fields: [new TicketCustomFieldInput(9001, "b")]);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*separate calls*");
    }

    [Fact]
    public async Task RefusesATagBothAddedAndRemoved()
    {
        using var fake = BulkFake();
        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], additional_tags: ["Spam"], remove_tags: ["spam"]);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*both added and removed*");
    }

    [Fact]
    public async Task RefusesPublicWithoutAComment()
    {
        using var fake = BulkFake();
        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], priority: "high", @public: false);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*give comment*");
    }

    [Fact]
    public async Task RefusesAnUnknownStatusAndATagWithASpace()
    {
        using var fake = BulkFake();
        var status = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], status: "closed");
        await status.Should().ThrowAsync<ArgumentException>().WithMessage("status must be one of*");
        var tag = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], additional_tags: ["two words"]);
        await tag.Should().ThrowAsync<ArgumentException>().WithMessage("additional_tags entries*");
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AZendeskRefusalOfTheWriteIsSurfaced()
    {
        using var fake = BulkFake(extra: r => r.Method == HttpMethod.Put
            ? FakeResponse.WithStatus(HttpStatusCode.UnprocessableEntity, """{"error":"RecordInvalid"}""")
            : null);

        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], priority: "high", dry_run: false);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*RecordInvalid*");
    }
}
