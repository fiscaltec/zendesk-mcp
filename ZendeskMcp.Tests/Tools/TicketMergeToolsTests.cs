using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

public class TicketMergeToolsTests
{
    private const string ShowManyPath = "/api/v2/tickets/show_many";
    private const string MergePath = "/api/v2/tickets/101/merge";
    private const string JobPath = "/api/v2/job_statuses/job1";

    private static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, _) => Task.CompletedTask;

    private static string Ticket(long id, string status = "open", string tags = "", long requester = 501) =>
        $$"""{"id":{{id}},"subject":"Subject {{id}}","status":"{{status}}","requester_id":{{requester}},"tags":[{{tags}}],"created_at":"2026-01-02T09:00:00Z","updated_at":"2026-01-02T10:00:00Z"}""";

    private static string ShowMany(params string[] tickets) =>
        $$"""{"tickets":[{{string.Join(",", tickets)}}],"users":[{"id":501,"name":"Alice","email":"alice@example.com","role":"end-user"}]}""";

    private static string Job(string status) =>
        $$$"""{"job_status":{"id":"job1","status":"{{{status}}}","total":2,"progress":{{{(status == "completed" ? 2 : 0)}}},"message":null,"results":null,"url":"https://acme.zendesk.com/api/v2/job_statuses/job1.json"}}""";

    /// <summary>
    /// A fake that answers the first show_many with the tickets as they were, and every later one with
    /// the sources closed and tagged closed_by_merge — what a successful merge leaves.
    /// </summary>
    private static FakeZendesk MergingFake(Func<RecordedRequest, FakeResponse>? job = null, bool sourcesMerged = true)
    {
        var reads = 0;
        return TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => Interlocked.Increment(ref reads) == 1
                ? FakeResponse.Json(ShowMany(Ticket(101), Ticket(102), Ticket(103)))
                : FakeResponse.Json(sourcesMerged
                    ? ShowMany(Ticket(102, "closed", "\"closed_by_merge\""), Ticket(103, "closed", "\"closed_by_merge\""))
                    : ShowMany(Ticket(102), Ticket(103))),
            ("POST", MergePath) => FakeResponse.Json(Job("queued")),
            ("GET", JobPath) => job?.Invoke(r) ?? FakeResponse.Json(Job("completed")),
            _ => FakeResponse.NotFound(),
        });
    }

    private static Task<string> Merge(ZendeskService zendesk, long[] sources, string? targetComment = null, string? sourceComment = null,
        bool? targetPublic = null, bool? sourcePublic = null, IReadOnlyList<TimeSpan>? delays = null) =>
        TicketMerge.MergeAsync(zendesk, 101, sources, targetComment, sourceComment, targetPublic, sourcePublic,
            delays ?? TicketMerge.DefaultPollDelays, NoWait, CancellationToken.None);

    [Fact]
    public async Task MergeTickets_ReadsTicketsPostsMergePollsJobAndConfirmsSources()
    {
        using var fake = MergingFake();
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = JsonNode.Parse(await Merge(zendesk, [102, 103]))!;

        var firstRead = fake.Requests.First(r => r.Path == ShowManyPath);
        firstRead.Query("ids").Should().Be("101,102,103");
        firstRead.Query("include").Should().Be("users");

        var merge = fake.Requests.Single(r => r.Path == MergePath);
        merge.Method.Should().Be(HttpMethod.Post);
        var body = JsonNode.Parse(merge.Body!)!;
        body["ids"]!.ToJsonString().Should().Be("[102,103]");
        body["target_comment"]!.GetValue<string>().Should().Be("Requests #102, #103 were closed and merged into this request.");
        body["source_comment"]!.GetValue<string>().Should().Be("This request was closed and merged into request #101.");
        body["target_comment_is_public"]!.GetValue<bool>().Should().BeFalse();
        body["source_comment_is_public"]!.GetValue<bool>().Should().BeFalse();

        fake.Requests.Count(r => r.Path == JobPath).Should().Be(1);
        fake.Requests.Last(r => r.Path == ShowManyPath).Query("ids").Should().Be("102,103");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete, "a write tool must never issue DELETE");

        result["merged"]!.GetValue<bool>().Should().BeTrue();
        result["outcome"]!.GetValue<string>().Should().Be("completed");
        result["job"]!["status"]!.GetValue<string>().Should().Be("completed");
        result["polls"]!.GetValue<int>().Should().Be(1);
        result["sources_after"]!.AsArray().Select(s => s!["closed_by_merge"]!.GetValue<bool>()).Should().AllSatisfy(b => b.Should().BeTrue());
        result["target_agent_url"]!.GetValue<string>().Should().Be("https://acme.zendesk.com/agent/tickets/101");

        var before = result["tickets_before"]!.AsArray();
        before.Select(t => t!["id"]!.GetValue<long>()).Should().Equal(101, 102, 103);
        before[0]!["role"]!.GetValue<string>().Should().Be("target");
        before[1]!["role"]!.GetValue<string>().Should().Be("source");
        before[1]!["subject"]!.GetValue<string>().Should().Be("Subject 102");
        before[1]!["requester"]!["email"]!.GetValue<string>().Should().Be("alice@example.com");
    }

    [Fact]
    public async Task MergeTickets_SingleSourceDefaultComment_NamesTheOneTicket()
    {
        var reads = 0;
        using var clean = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1
                ? ShowMany(Ticket(101), Ticket(102))
                : ShowMany(Ticket(102, "closed", "\"closed_by_merge\""))),
            ("POST", MergePath) => FakeResponse.Json(Job("completed")),
            _ => FakeResponse.NotFound(),
        });
        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(clean.Client), [102]))!;

        JsonNode.Parse(clean.Requests.Single(r => r.Path == MergePath).Body!)!["target_comment"]!.GetValue<string>()
            .Should().Be("Request #102 was closed and merged into this request.");

        // Already completed in the POST response: no poll is made.
        clean.Requests.Should().NotContain(r => r.Path.StartsWith("/api/v2/job_statuses/", StringComparison.Ordinal));
        result["polls"]!.GetValue<int>().Should().Be(0);
        result["merged"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task MergeTickets_CallerComments_AreSentAsGiven()
    {
        using var fake = MergingFake();
        var zendesk = TestHelpers.BuildZendeskService(fake.Client, configure: o => o.Features.AllowPublicReplies = true);

        await Merge(zendesk, [102, 103], targetComment: "Duplicates merged.", sourceComment: "Merged into #101.", targetPublic: true, sourcePublic: true);

        var body = JsonNode.Parse(fake.Requests.Single(r => r.Path == MergePath).Body!)!;
        body["target_comment"]!.GetValue<string>().Should().Be("Duplicates merged.");
        body["source_comment"]!.GetValue<string>().Should().Be("Merged into #101.");
        body["target_comment_is_public"]!.GetValue<bool>().Should().BeTrue();
        body["source_comment_is_public"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MergeTickets_PublicComment_RequiresAllowPublicReplies(bool targetPublic, bool sourcePublic)
    {
        using var fake = MergingFake();
        var refused = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => Merge(refused, [102, 103], targetPublic: targetPublic, sourcePublic: sourcePublic);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*AllowPublicReplies*");
        fake.Requests.Should().BeEmpty("the switch is checked before anything is read or changed");

        // Negative control: the same call with the switch on goes through.
        var allowed = TestHelpers.BuildZendeskService(fake.Client, configure: o => o.Features.AllowPublicReplies = true);
        var result = JsonNode.Parse(await Merge(allowed, [102, 103], targetPublic: targetPublic, sourcePublic: sourcePublic))!;
        result["merged"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task MergeTickets_PrivateComments_NeedNoSwitch()
    {
        using var fake = MergingFake();
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = JsonNode.Parse(await Merge(zendesk, [102, 103], targetPublic: false, sourcePublic: false))!;

        result["merged"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData(new long[0], "*1 to 5*")]
    [InlineData(new long[] { 102, 103, 104, 105, 106, 107 }, "*1 to 5*")]
    [InlineData(new long[] { 102, 102 }, "*more than once*")]
    [InlineData(new long[] { 102, 101 }, "*cannot be merged into itself*")]
    [InlineData(new long[] { 0 }, "*source_ticket_ids*positive*")]
    public async Task MergeTickets_InvalidSources_AreRefusedBeforeAnyRequest(long[] sources, string message)
    {
        using var fake = MergingFake();
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => Merge(zendesk, sources);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task MergeTickets_FiveSources_IsAccepted()
    {
        // Negative control for the upper bound: five is allowed, six (above) is not.
        var reads = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1
                ? ShowMany(Ticket(101), Ticket(102), Ticket(103), Ticket(104), Ticket(105), Ticket(106))
                : ShowMany([.. new[] { 102, 103, 104, 105, 106 }.Select(id => Ticket(id, "closed", "\"closed_by_merge\""))])),
            ("POST", MergePath) => FakeResponse.Json(Job("completed")),
            _ => FakeResponse.NotFound(),
        });

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103, 104, 105, 106]))!;

        result["merged"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task MergeTickets_NonPositiveTarget_IsRefused()
    {
        using var fake = MergingFake();
        var act = () => TicketMerge.MergeAsync(TestHelpers.BuildZendeskService(fake.Client), 0, [102], null, null, null, null,
            TicketMerge.DefaultPollDelays, NoWait, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*target_ticket_id*");
        fake.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("target_comment")]
    [InlineData("source_comment")]
    public async Task MergeTickets_BlankOrOverlongComment_IsRefused(string parameter)
    {
        using var fake = MergingFake();
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);
        var tooLong = new string('x', TicketWriteHelpers.MaxCommentLength + 1);

        var blank = () => parameter == "target_comment" ? Merge(zendesk, [102], targetComment: " ") : Merge(zendesk, [102], sourceComment: " ");
        var overlong = () => parameter == "target_comment" ? Merge(zendesk, [102], targetComment: tooLong) : Merge(zendesk, [102], sourceComment: tooLong);

        await blank.Should().ThrowAsync<ArgumentException>().WithMessage($"*{parameter}*");
        await overlong.Should().ThrowAsync<ArgumentException>().WithMessage($"*{parameter}*");
        fake.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("closed", "", "*Ticket 102 (source) is closed*")]
    [InlineData("solved", "\"closed_by_merge\"", "*Ticket 102 (source) has already been merged*")]
    public async Task MergeTickets_UnmergeableSource_IsRefusedNamingTheTicket(string status, string tags, string message)
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            ShowManyPath => FakeResponse.Json(ShowMany(Ticket(101), Ticket(102, status, tags), Ticket(103))),
            _ => FakeResponse.NotFound(),
        });
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => Merge(zendesk, [102, 103]);

        (await act.Should().ThrowAsync<ArgumentException>().WithMessage(message)).Which.Message.Should().StartWith("Nothing was merged.");
        fake.Requests.Should().NotContain(r => r.Path == MergePath);
    }

    [Fact]
    public async Task MergeTickets_ClosedTarget_IsRefused()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            ShowManyPath => FakeResponse.Json(ShowMany(Ticket(101, "closed"), Ticket(102))),
            _ => FakeResponse.NotFound(),
        });

        var act = () => Merge(TestHelpers.BuildZendeskService(fake.Client), [102]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Ticket 101 (target) is closed*");
        fake.Requests.Should().NotContain(r => r.Path == MergePath);
    }

    [Fact]
    public async Task MergeTickets_MissingOrHiddenTicket_IsRefusedNamingEveryProblem()
    {
        // show_many leaves out what it cannot find or the agent cannot see.
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            ShowManyPath => FakeResponse.Json(ShowMany(Ticket(101), Ticket(103, "closed"))),
            _ => FakeResponse.NotFound(),
        });

        var act = () => Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103]);

        var thrown = await act.Should().ThrowAsync<ArgumentException>();
        thrown.Which.Message.Should().Contain("Ticket 102 (source) was not found").And.Contain("Ticket 103 (source) is closed");
        fake.Requests.Should().NotContain(r => r.Path == MergePath);
    }

    [Fact]
    public async Task MergeTickets_JobStillRunningAtTheBound_IsNeverReportedAsMerged()
    {
        using var fake = MergingFake(job: _ => FakeResponse.Json(Job("working")));
        var waits = new List<TimeSpan>();
        var delays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) };

        var result = JsonNode.Parse(await TicketMerge.MergeAsync(TestHelpers.BuildZendeskService(fake.Client), 101, [102, 103],
            null, null, null, null, delays, (d, _) => { waits.Add(d); return Task.CompletedTask; }, CancellationToken.None))!;

        waits.Should().Equal(delays);
        fake.Requests.Count(r => r.Path == JobPath).Should().Be(3);
        result["merged"]!.GetValue<bool>().Should().BeFalse();
        result["outcome"]!.GetValue<string>().Should().Be("still_running");
        result["job"]!["status"]!.GetValue<string>().Should().Be("working");
        result["next_step"]!.GetValue<string>().Should().Contain("Do not call Merge_tickets again");

        // Nothing is read back as though the merge had finished.
        fake.Requests.Count(r => r.Path == ShowManyPath).Should().Be(1);
    }

    [Fact]
    public async Task MergeTickets_JobCompletesOnALaterPoll_StopsPolling()
    {
        var polls = 0;
        using var fake = MergingFake(job: _ => FakeResponse.Json(Job(Interlocked.Increment(ref polls) < 3 ? "working" : "completed")));

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103]))!;

        fake.Requests.Count(r => r.Path == JobPath).Should().Be(3);
        result["polls"]!.GetValue<int>().Should().Be(3);
        result["merged"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("killed")]
    public async Task MergeTickets_FailedJob_IsSurfacedAsAnError(string status)
    {
        using var fake = MergingFake(job: _ => FakeResponse.Json(
            $$$"""{"job_status":{"id":"job1","status":"{{{status}}}","message":"Ticket could not be merged"}}"""));

        var act = () => Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103]);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.Message.Should().Contain($"merge job job1 {status}").And.Contain("Ticket could not be merged");
        fake.Requests.Count(r => r.Path == ShowManyPath).Should().Be(1, "a failed job is not read back as a merge");
    }

    [Fact]
    public async Task MergeTickets_CompletedButSourcesNotClosed_IsReportedUnconfirmed()
    {
        using var fake = MergingFake(sourcesMerged: false);

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103]))!;

        result["merged"]!.GetValue<bool>().Should().BeFalse();
        result["outcome"]!.GetValue<string>().Should().Be("completed_unconfirmed");
        result["warning"]!.GetValue<string>().Should().Contain("102, 103");
    }

    [Fact]
    public async Task MergeTickets_NoJobStatusInResponse_IsAnError()
    {
        var reads = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1 ? ShowMany(Ticket(101), Ticket(102)) : ShowMany()),
            ("POST", MergePath) => FakeResponse.Json("{}"),
            _ => FakeResponse.NotFound(),
        });

        var act = () => Merge(TestHelpers.BuildZendeskService(fake.Client), [102]);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*no job status*");
    }

    [Fact]
    public async Task MergeTickets_ZendeskRefusal_SurfacesItsBody()
    {
        var reads = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1 ? ShowMany(Ticket(101), Ticket(102)) : ShowMany()),
            ("POST", MergePath) => FakeResponse.WithStatus(HttpStatusCode.Forbidden, """{"error":"Forbidden","description":"You do not have access"}"""),
            _ => FakeResponse.NotFound(),
        });

        var act = () => Merge(TestHelpers.BuildZendeskService(fake.Client), [102]);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*Forbidden*");
    }

    [Fact]
    public async Task MergeTickets_AuditRecord_NamesTargetAndEverySource()
    {
        // The merge's own response is a job status, which names no ticket; the read before it is what
        // puts the tickets into the tool call's audit record.
        using var fake = MergingFake();
        var context = new ToolCallAuditContext();
        var zendesk = TestHelpers.BuildZendeskService(fake.Client, auditContext: context);

        await Merge(zendesk, [102, 103]);

        context.Summarise().Ids.Should().Contain(["101", "102", "103"]);
    }

    [Fact]
    public async Task MergeTickets_AuditRecord_NegativeControl_WithoutThePreReadNoTicketIsNamed()
    {
        // Proves the test above depends on the read: the merge and job responses alone name only the job.
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            MergePath => FakeResponse.Json(Job("queued")),
            JobPath => FakeResponse.Json(Job("completed")),
            _ => FakeResponse.NotFound(),
        });
        var context = new ToolCallAuditContext();
        var zendesk = TestHelpers.BuildZendeskService(fake.Client, auditContext: context);

        await zendesk.PostAsync(MergePath, new JsonObject { ["ids"] = new JsonArray(102, 103) });
        await zendesk.GetAsync(JobPath);

        context.Summarise().Ids.Should().NotContain(["101", "102", "103"]);
    }

    [Fact]
    public async Task MergeTickets_ToolEntryPoint_UsesTheSameRequests()
    {
        // The registered tool delegates to TicketMerge with the real wait; a job completed in the POST
        // response needs no wait at all, so this runs instantly.
        var reads = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1
                ? ShowMany(Ticket(101), Ticket(102))
                : ShowMany(Ticket(102, "closed", "\"closed_by_merge\""))),
            ("POST", MergePath) => FakeResponse.Json(Job("completed")),
            _ => FakeResponse.NotFound(),
        });

        var result = JsonNode.Parse(await TicketMergeTools.MergeTickets(TestHelpers.BuildZendeskService(fake.Client), 101, [102]))!;

        result["merged"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void DefaultPollSchedule_IsBounded()
    {
        TicketMerge.DefaultPollDelays.Should().HaveCount(10);
        TicketMerge.DefaultPollDelays.Aggregate(TimeSpan.Zero, (a, b) => a + b).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }
}
