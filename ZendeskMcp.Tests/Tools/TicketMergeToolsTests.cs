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
        // The fake has no comments endpoint, so the default note names the tickets without quotes and says so.
        body["target_comment"]!.GetValue<string>().Should().Be(
            "Request #102 \"Subject 102\" was closed and merged into this request.\n\nRequest #103 \"Subject 103\" was closed and merged into this request.");
        body["source_comment"]!.GetValue<string>().Should().Be("This request was closed and merged into request #101 \"Subject 101\".");
        body["target_comment_is_public"]!.GetValue<bool>().Should().BeFalse();
        body["source_comment_is_public"]!.GetValue<bool>().Should().BeFalse();

        fake.Requests.Count(r => r.Path == JobPath).Should().Be(1);
        fake.Requests.Last(r => r.Path == ShowManyPath).Query("ids").Should().Be("102,103,101");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete, "a write tool must never issue DELETE");

        result["merged"]!.GetValue<bool>().Should().BeTrue();
        result["result"]!.GetValue<string>().Should().Be("merged");
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
            .Should().Be("Request #102 \"Subject 102\" was closed and merged into this request.");

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
    [InlineData("solved", "\"closed_by_merge\"", "*Ticket 102 (source) is tagged closed_by_merge but is not closed*")]
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
    public async Task MergeTickets_SolvedSource_IsRefused_ButASolvedTargetIsNot()
    {
        using var refused = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            ShowManyPath => FakeResponse.Json(ShowMany(Ticket(101), Ticket(102, "solved"))),
            _ => FakeResponse.NotFound(),
        });

        var act = () => Merge(TestHelpers.BuildZendeskService(refused.Client), [102]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Ticket 102 (source) is solved*");
        refused.Requests.Should().NotContain(r => r.Path == MergePath);

        // Negative control: the same tickets with the target solved instead go through.
        var reads = 0;
        using var allowed = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1
                ? ShowMany(Ticket(101, "solved"), Ticket(102))
                : ShowMany(Ticket(102, "closed", "\"closed_by_merge\""))),
            ("POST", MergePath) => FakeResponse.Json(Job("completed")),
            _ => FakeResponse.NotFound(),
        });

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(allowed.Client), [102]))!;
        result["result"]!.GetValue<string>().Should().Be("merged");
    }

    [Fact]
    public async Task MergeTickets_DifferentRequester_IsRefusedNamingTheTicket()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            ShowManyPath => FakeResponse.Json(ShowMany(Ticket(101, requester: 501), Ticket(102, requester: 501), Ticket(103, requester: 502))),
            _ => FakeResponse.NotFound(),
        });

        var act = () => Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103]);

        var thrown = await act.Should().ThrowAsync<ArgumentException>();
        thrown.Which.Message.Should().Contain("Ticket 103 (source) has a different requester").And.NotContain("Ticket 102");
        fake.Requests.Should().NotContain(r => r.Path == MergePath);
    }

    [Fact]
    public async Task MergeTickets_SomeSourcesConfirmed_IsPartiallyMerged()
    {
        var reads = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1
                ? ShowMany(Ticket(101), Ticket(102), Ticket(103))
                : ShowMany(Ticket(102, "closed", "\"closed_by_merge\""), Ticket(103))),
            ("POST", MergePath) => FakeResponse.Json(Job("completed")),
            _ => FakeResponse.NotFound(),
        });

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103]))!;

        result["merged"]!.GetValue<bool>().Should().BeFalse();
        result["result"]!.GetValue<string>().Should().Be("partially_merged");
        result["sources_after"]!.AsArray().Select(x => x!["result"]!.GetValue<string>()).Should().Equal("merged", "not_confirmed");
    }

    private const string Merged = "\"closed_by_merge\"";

    private static string Audits(string via) => $$$"""{"audits":[{"id":9001,"via":{"channel":"web"},"events":[{"id":1,"type":"Comment","via":{{{via}}}}]}],"meta":{"has_more":false}}""";

    private static string MergeVia(string end, long ticket) => "{\"channel\":\"web\",\"source\":{\"rel\":\"merge\",\"" + end + "\":{\"ticket_id\":" + ticket.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}";

    /// <summary>A fake where 102 is already merged (its audits answered by <paramref name="audits102"/>) and 103 is open.</summary>
    private static FakeZendesk AlreadyMergedFake(Func<RecordedRequest, FakeResponse> audits102, bool include103 = true)
    {
        var reads = 0;
        return TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1
                ? (include103 ? ShowMany(Ticket(101), Ticket(102, "closed", Merged), Ticket(103)) : ShowMany(Ticket(101), Ticket(102, "closed", Merged)))
                : ShowMany(Ticket(103, "closed", Merged))),
            ("GET", "/api/v2/tickets/102/audits") => audits102(r),
            ("POST", MergePath) => FakeResponse.Json(Job("completed")),
            _ => FakeResponse.NotFound(),
        });
    }

    [Fact]
    public async Task MergeTickets_AllSourcesAlreadyMergedIntoTarget_IsAlreadyMergedAndSendsNothing()
    {
        using var fake = AlreadyMergedFake(_ => FakeResponse.Json(Audits(MergeVia("to", 101))), include103: false);

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102]))!;

        result["result"]!.GetValue<string>().Should().Be("already_merged");
        result["merged"]!.GetValue<bool>().Should().BeTrue();
        result["already_merged"]!.ToJsonString().Should().Be("[102]");
        result["sources_after"]![0]!["result"]!.GetValue<string>().Should().Be("already_merged");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post, "a repeat sends nothing to Zendesk");
        var audits = fake.Requests.Single(r => r.Path == "/api/v2/tickets/102/audits");
        audits.Query("sort_order").Should().Be("desc");
        audits.Query("page[size]").Should().Be("100");
    }

    [Fact]
    public async Task MergeTickets_SomeSourcesAlreadyMerged_MergesOnlyTheRest()
    {
        using var fake = AlreadyMergedFake(_ => FakeResponse.Json(Audits(MergeVia("to", 101))));

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103]))!;

        var body = JsonNode.Parse(fake.Requests.Single(r => r.Path == MergePath).Body!)!;
        body["ids"]!.ToJsonString().Should().Be("[103]");
        body["target_comment"]!.GetValue<string>().Should().Be("Request #103 \"Subject 103\" was closed and merged into this request.");
        fake.Requests.Last(r => r.Path == ShowManyPath).Query("ids").Should().Be("103,101");

        result["result"]!.GetValue<string>().Should().Be("merged");
        result["merged"]!.GetValue<bool>().Should().BeTrue();
        result["already_merged"]!.ToJsonString().Should().Be("[102]");
        result["sources_after"]!.AsArray().Select(x => x!["result"]!.GetValue<string>()).Should().Equal("already_merged", "merged");
    }

    [Theory]
    [InlineData("to")]
    [InlineData("from")]
    public async Task MergeTickets_AlreadyMergedIntoAnotherTicket_IsRefused(string end)
    {
        // Negative control for the two tests above: the same audit naming a different ticket.
        using var fake = AlreadyMergedFake(_ => FakeResponse.Json(Audits(MergeVia(end, 999))));

        var act = () => Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Ticket 102 (source) has already been merged into another ticket*does not show it going into ticket 101*");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task MergeTickets_MergeRecordedAsTicketIds_IsRecognised()
    {
        using var fake = AlreadyMergedFake(_ => FakeResponse.Json(Audits("""{"channel":"web","source":{"rel":"merge","from":{"ticket_ids":[101,555]}}}""")), include103: false);

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102]))!;

        result["result"]!.GetValue<string>().Should().Be("already_merged");
    }

    [Theory]
    [InlineData("""{"channel":"web","source":{"rel":"follow_up","to":{"ticket_id":101}}}""")]
    [InlineData("""{"channel":"web"}""")]
    public async Task MergeTickets_NoMergeRecordNamingTheTarget_IsRefused(string via)
    {
        using var fake = AlreadyMergedFake(_ => FakeResponse.Json(Audits(via)));

        var act = () => Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*does not show it going into ticket 101*");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task MergeTickets_AuditsUnreadable_IsRefusedNotGuessed()
    {
        using var fake = AlreadyMergedFake(_ => FakeResponse.WithStatus(HttpStatusCode.Forbidden, """{"error":"Forbidden"}"""));

        var act = () => Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Ticket 102*history could not be read*");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    // ---------- Swifteq parity: default note, side effects, comments as saved ----------

    private static string RichTicket(long id, string status, string tags, string ccs, string assignee) =>
        $$$"""{"id":{{{id}}},"subject":"Subject {{{id}}}","status":"{{{status}}}","requester_id":501,"assignee_id":{{{assignee}}},"email_cc_ids":[{{{ccs}}}],"tags":[{{{tags}}}]}""";

    private static string RichShowMany(params string[] tickets) =>
        $$"""{"tickets":[{{string.Join(",", tickets)}}],"users":[{"id":501,"name":"Alice"},{"id":601,"name":"Bob"},{"id":701,"name":"Agent Smith"}]}""";

    private static string Comments(params (long Id, bool Public, string Body)[] comments) =>
        "{\"comments\":[" + string.Join(",", comments.Select(c => new JsonObject
        {
            ["id"] = c.Id, ["public"] = c.Public, ["plain_body"] = c.Body, ["created_at"] = "2026-11-04T10:00:00Z",
        }.ToJsonString())) + "],\"meta\":{\"has_more\":false}}";

    /// <summary>
    /// 101 (target, no CCs) and 102 (source, unassigned). After the merge 102 is closed and assigned to
    /// 701, and 101 has 601 as a CC. Comments are answered per ticket by <paramref name="comments"/>.
    /// </summary>
    private static FakeZendesk SideEffectsFake(Func<RecordedRequest, FakeResponse> comments)
    {
        var reads = 0;
        return TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1
                ? RichShowMany(RichTicket(101, "open", "", "", "null"), RichTicket(102, "open", "", "", "null"))
                : RichShowMany(RichTicket(102, "closed", Merged, "", "701"), RichTicket(101, "open", "", "601", "null"))),
            ("GET", "/api/v2/tickets/101/comments") or ("GET", "/api/v2/tickets/102/comments") => comments(r),
            ("POST", MergePath) => FakeResponse.Json(Job("completed")),
            _ => FakeResponse.NotFound(),
        });
    }

    [Fact]
    public async Task MergeTickets_DefaultTargetNote_QuotesTheLatestPublicCommentClipped()
    {
        var longPublic = new string('a', TicketMerge.MaxQuotedCommentLength + 50);
        var merged = false;
        using var fake = SideEffectsFake(r =>
        {
            if (r.Path.EndsWith("/102/comments", StringComparison.Ordinal) && !merged)
            {
                // Newest first: a private note, then the latest public comment, then an older public one.
                return FakeResponse.Json(Comments((3, false, "internal only"), (2, true, longPublic), (1, true, "older")));
            }

            return FakeResponse.Json(Comments((9, false, "saved")));
        });

        await Merge(TestHelpers.BuildZendeskService(fake.Client), [102]);
        merged = true;

        var read = fake.Requests.First(r => r.Path == "/api/v2/tickets/102/comments");
        read.Query("sort_order").Should().Be("desc");
        var note = JsonNode.Parse(fake.Requests.Single(r => r.Path == MergePath).Body!)!["target_comment"]!.GetValue<string>();
        note.Should().StartWith("Request #102 \"Subject 102\" was closed and merged into this request. Last comment in request #102:\n\n");
        note.Should().EndWith(new string('a', TicketMerge.MaxQuotedCommentLength) + "…");
        note.Should().NotContain("internal only", "a private note is never quoted").And.NotContain("older");
    }

    [Fact]
    public async Task MergeTickets_GivenTargetComment_ReplacesTheDefaultAndReadsNoQuotes()
    {
        using var fake = SideEffectsFake(_ => FakeResponse.Json(Comments((9, false, "saved"))));

        await Merge(TestHelpers.BuildZendeskService(fake.Client), [102], targetComment: "Duplicates merged.");

        JsonNode.Parse(fake.Requests.Single(r => r.Path == MergePath).Body!)!["target_comment"]!.GetValue<string>().Should().Be("Duplicates merged.");
        fake.Requests.Should().NotContain(r => r.Path.EndsWith("/comments", StringComparison.Ordinal) && r.Query("page[size]") == "100",
            "no comment is read for a default that is not used");
    }

    [Fact]
    public async Task MergeTickets_UnreadableSourceComments_FallBackToANamingNoteWithAWarning()
    {
        using var fake = SideEffectsFake(_ => FakeResponse.WithStatus(HttpStatusCode.InternalServerError));

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102]))!;

        JsonNode.Parse(fake.Requests.Single(r => r.Path == MergePath).Body!)!["target_comment"]!.GetValue<string>()
            .Should().Be("Request #102 \"Subject 102\" was closed and merged into this request.");
        var warnings = result["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        warnings.Should().Contain(w => w.Contains("Could not read ticket 102's comments"));
        warnings.Should().Contain(w => w.Contains("Could not read the merge comment on ticket 101"));
        result["result"]!.GetValue<string>().Should().Be("merged", "a failed read after the merge never changes its result");
    }

    [Fact]
    public async Task MergeTickets_ReportsAddedCcsAssignedSourcesAndSavedComments()
    {
        using var fake = SideEffectsFake(_ => FakeResponse.Json(Comments((9, false, "Merged note"))));

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102]))!;

        var warnings = result["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        warnings.Should().Contain("Zendesk added Bob (601) as CC(s) on ticket 101; they will receive its public replies.");
        warnings.Should().Contain("Zendesk assigned ticket 102 to Agent Smith (701) when it closed it.");
        warnings.Should().NotContain(w => w.Contains("private note"), "nothing public was asked for");

        result["comments"]!["target"]!["comment_id"]!.GetValue<long>().Should().Be(9);
        result["comments"]!["target"]!["public"]!.GetValue<bool>().Should().BeFalse();
        result["comments"]!["target"]!["body"]!.GetValue<string>().Should().Be("Merged note");
        result["comments"]!["sources"]![0]!["ticket_id"]!.GetValue<long>().Should().Be(102);
        fake.Requests.Where(r => r.Path.EndsWith("/comments", StringComparison.Ordinal) && r.Query("page[size]") == "1")
            .Should().HaveCount(2, "the target's and the source's newest comment are read after the merge");
    }

    [Fact]
    public async Task MergeTickets_PublicCommentSavedAsPrivate_IsWarned()
    {
        using var fake = SideEffectsFake(_ => FakeResponse.Json(Comments((9, false, "Merged note"))));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client, configure: o => o.Features.AllowPublicReplies = true);

        var result = JsonNode.Parse(await Merge(zendesk, [102], targetPublic: true))!;

        var warnings = result["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        warnings.Should().Contain(w => w.Contains("ticket 101 (target) as a private note"));
        warnings.Should().NotContain(w => w.Contains("ticket 102 (source) as a private note"), "the source comment was asked to be private");
    }

    [Fact]
    public async Task MergeTickets_NoSideEffects_NoSideEffectWarnings()
    {
        // Negative control for the warnings test: the same tickets with nothing changed by Zendesk.
        var reads = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1
                ? RichShowMany(RichTicket(101, "open", "", "601", "null"), RichTicket(102, "open", "", "", "701"))
                : RichShowMany(RichTicket(102, "closed", Merged, "", "701"), RichTicket(101, "open", "", "601", "null"))),
            ("GET", _) when r.Path.EndsWith("/comments", StringComparison.Ordinal) => FakeResponse.Json(Comments((9, true, "Merged note"))),
            ("POST", MergePath) => FakeResponse.Json(Job("completed")),
            _ => FakeResponse.NotFound(),
        });
        var zendesk = TestHelpers.BuildZendeskService(fake.Client, configure: o => o.Features.AllowPublicReplies = true);

        var result = JsonNode.Parse(await Merge(zendesk, [102], targetPublic: true, sourcePublic: true))!;

        result["warnings"]!.AsArray().Should().BeEmpty();
    }

    [Theory]
    [InlineData(101, "target")]
    [InlineData(102, "source")]
    public async Task MergeTickets_SharedTicket_IsRefused(long shared, string role)
    {
        string T(long id) => $$$"""{"id":{{{id}}},"subject":"S","status":"open","requester_id":501,"tags":[],"sharing_agreement_ids":[{{{(id == shared ? "77" : "")}}}]}""";
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            ShowManyPath => FakeResponse.Json(ShowMany(T(101), T(102))),
            _ => FakeResponse.NotFound(),
        });

        var act = () => Merge(TestHelpers.BuildZendeskService(fake.Client), [102]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage($"*Ticket {shared} ({role}) is shared*");
        fake.Requests.Should().NotContain(r => r.Path == MergePath);
    }

    [Theory]
    [InlineData("""{"comments":[1]}""")]
    [InlineData("""{"comments":[[{"id":1}]]}""")]
    [InlineData("""[]""")]
    public async Task MergeTickets_MalformedCommentsAfterACompletedMerge_StillReportTheMerge(string comments)
    {
        // The merge is done; a malformed best-effort read must not turn it into a plain error.
        using var fake = SideEffectsFake(_ => FakeResponse.Json(comments));

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102]))!;

        result["result"]!.GetValue<string>().Should().Be("merged");
        result["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).Should().Contain(w => w.Contains("merge comment on ticket 101"));
    }

    [Fact]
    public async Task MergeTickets_TaggedButOpenSource_IsRefusedNotAlreadyMerged()
    {
        using var open = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(ShowMany(Ticket(101), Ticket(102, "open", Merged))),
            ("GET", "/api/v2/tickets/102/audits") => FakeResponse.Json(Audits(MergeVia("to", 101))),
            _ => FakeResponse.NotFound(),
        });

        var act = () => Merge(TestHelpers.BuildZendeskService(open.Client), [102]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Ticket 102 (source) is tagged closed_by_merge but is not closed*");
        open.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"audits":[1,{"via":"web"},{"via":{"source":"x"}}]}""")]
    public async Task MergeTickets_MalformedAudits_AreRefusedNotGuessed(string audits)
    {
        using var fake = AlreadyMergedFake(_ => FakeResponse.Json(audits));

        var act = () => Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Ticket 102*");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task MergeTickets_AuditsReadWithoutAConnection_IsRefusedNotAnError()
    {
        // A non-HTTP failure reading the audits (here the development credential refused) still refuses
        // by name rather than escaping.
        using var fake = AlreadyMergedFake(_ => FakeResponse.WithStatus(HttpStatusCode.Unauthorized));

        var act = () => Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Ticket 102*history could not be read*");
    }

    [Fact]
    public async Task MergeTickets_PublicTargetWithTheDefaultNote_SendsTheQuoteAsAsked()
    {
        // Swifteq's behaviour: a public target comment with the default note quotes the sources' latest
        // public comments publicly. Pinned so a change to it is deliberate.
        var merged = false;
        using var fake = SideEffectsFake(r => FakeResponse.Json(!merged && r.Path.EndsWith("/102/comments", StringComparison.Ordinal)
            ? Comments((2, true, "Original question"))
            : Comments((9, true, "saved"))));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client, configure: o => o.Features.AllowPublicReplies = true);

        await Merge(zendesk, [102], targetPublic: true);

        var body = JsonNode.Parse(fake.Requests.Single(r => r.Path == MergePath).Body!)!;
        body["target_comment_is_public"]!.GetValue<bool>().Should().BeTrue();
        body["target_comment"]!.GetValue<string>().Should().EndWith("Last comment in request #102:\n\nOriginal question");
    }

    [Fact]
    public async Task MergeTickets_DuplicateKeyInTheMergeAnswer_IsNotConfirmedNotAnError()
    {
        // System.Text.Json accepts a duplicate key on parse and throws only when the object is indexed.
        var reads = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1 ? ShowMany(Ticket(101), Ticket(102)) : ShowMany()),
            ("POST", MergePath) => FakeResponse.Json("""{"job_status":{"id":"job1","status":"queued","status":"completed"}}"""),
            _ => FakeResponse.NotFound(),
        });

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102]))!;

        result["result"]!.GetValue<string>().Should().Be("not_confirmed");
        result["next_step"]!.GetValue<string>().Should().Contain("Do not call Merge_tickets again");
    }

    [Fact]
    public async Task MergeTickets_DuplicateKeyInTheReadBack_IsNotConfirmedNotAnError()
    {
        var reads = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1
                ? ShowMany(Ticket(101), Ticket(102))
                : """{"tickets":[{"id":102,"status":"closed","status":"open","tags":["closed_by_merge"]}],"users":[]}"""),
            ("POST", MergePath) => FakeResponse.Json(Job("completed")),
            _ => FakeResponse.NotFound(),
        });

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102]))!;

        result["result"]!.GetValue<string>().Should().Be("not_confirmed");
        result["merged"]!.GetValue<bool>().Should().BeFalse();
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
        result["result"]!.GetValue<string>().Should().Be("not_confirmed");
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
        thrown.Which.Message.Should().Contain($"merge job job1 {status}").And.Contain("Ticket could not be merged").And.Contain("before calling Merge_tickets again");
        thrown.Which.StatusCode.Should().BeNull("Zendesk answered every request with success; the job failed");
        fake.Requests.Count(r => r.Path == ShowManyPath).Should().Be(1, "a failed job is not read back as a merge");
    }

    [Fact]
    public async Task MergeTickets_CompletedButSourcesNotClosed_IsReportedUnconfirmed()
    {
        using var fake = MergingFake(sourcesMerged: false);

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103]))!;

        result["merged"]!.GetValue<bool>().Should().BeFalse();
        result["result"]!.GetValue<string>().Should().Be("not_confirmed");
        result["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).Should().Contain(w => w.Contains("102, 103") && w.Contains("closed_by_merge"));
        result["next_step"]!.GetValue<string>().Should().Contain("Do not call Merge_tickets again");
    }

    [Fact]
    public async Task MergeTickets_NoJobStatusInResponse_IsReportedUnconfirmed()
    {
        var reads = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1 ? ShowMany(Ticket(101), Ticket(102)) : ShowMany()),
            ("POST", MergePath) => FakeResponse.Json("{}"),
            _ => FakeResponse.NotFound(),
        });

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102]))!;

        result["merged"]!.GetValue<bool>().Should().BeFalse();
        result["result"]!.GetValue<string>().Should().Be("not_confirmed");
        result["next_step"]!.GetValue<string>().Should().Contain("no job status").And.Contain("Do not call Merge_tickets again");
        fake.Requests.Should().NotContain(r => r.Path.StartsWith("/api/v2/job_statuses/", StringComparison.Ordinal));
        fake.Requests.Count(r => r.Path == ShowManyPath).Should().Be(1, "nothing is read back as though the merge had finished");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task MergeTickets_JobPollFails_IsReportedUnconfirmedNotAsAnError(HttpStatusCode status)
    {
        // The merge was accepted, so a failed poll must not read as "nothing changed": a retry would
        // pass the pre-check (the sources are not yet tagged) and POST a second merge.
        using var fake = MergingFake(job: _ => FakeResponse.WithStatus(status, """{"error":"Oops"}"""));

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103]))!;

        result["merged"]!.GetValue<bool>().Should().BeFalse();
        result["result"]!.GetValue<string>().Should().Be("not_confirmed");
        result["job"]!["id"]!.GetValue<string>().Should().Be("job1");
        result["next_step"]!.GetValue<string>().Should().Contain("Checking the merge job failed").And.Contain("Do not call Merge_tickets again");
        fake.Requests.Count(r => r.Path == MergePath).Should().Be(1);
        fake.Requests.Count(r => r.Path == ShowManyPath).Should().Be(1, "nothing is read back after a failed poll");
    }

    [Fact]
    public async Task MergeTickets_ConnectionDroppedDuringPoll_IsUnconfirmedAndKeepsTheConnectLink()
    {
        // A 401 on the poll forgets the grant and raises ZendeskConnectionRequiredException, whose own
        // message says "then try again" — exactly the retry that could merge twice.
        using var fake = MergingFake(job: _ => FakeResponse.WithStatus(HttpStatusCode.Unauthorized, """{"error":"invalid_token"}"""));
        var (zendesk, _) = TestHelpers.BuildUserZendeskService(fake.Client, TestHelpers.SampleToken(refreshToken: null));

        var result = JsonNode.Parse(await Merge(zendesk, [102, 103]))!;

        result["merged"]!.GetValue<bool>().Should().BeFalse();
        result["result"]!.GetValue<string>().Should().Be("not_confirmed");
        result["next_step"]!.GetValue<string>().Should().Contain("/zendesk/connect?ticket=").And.Contain("Do not call Merge_tickets again");
    }

    [Fact]
    public async Task MergeTickets_DevelopmentCredentialRejectedOnReadBack_IsUnconfirmed()
    {
        // The development token's 401 is an UnauthorizedAccessException: also not an HTTP error.
        var reads = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => Interlocked.Increment(ref reads) == 1
                ? FakeResponse.Json(ShowMany(Ticket(101), Ticket(102)))
                : FakeResponse.WithStatus(HttpStatusCode.Unauthorized),
            ("POST", MergePath) => FakeResponse.Json(Job("completed")),
            _ => FakeResponse.NotFound(),
        });

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102]))!;

        result["result"]!.GetValue<string>().Should().Be("not_confirmed");
        result["next_step"]!.GetValue<string>().Should().Contain("UnauthorizedAccessException");
    }

    [Fact]
    public async Task MergeTickets_CallerCancellationAfterTheMerge_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        using var fake = MergingFake(job: _ => FakeResponse.Json(Job("working")));

        var act = () => TicketMerge.MergeAsync(TestHelpers.BuildZendeskService(fake.Client), 101, [102, 103], null, null, null, null,
            [TimeSpan.Zero], (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MergeTickets_ReadBackFails_IsReportedUnconfirmedNotAsAnError()
    {
        var reads = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => Interlocked.Increment(ref reads) == 1
                ? FakeResponse.Json(ShowMany(Ticket(101), Ticket(102)))
                : FakeResponse.WithStatus(HttpStatusCode.InternalServerError),
            ("POST", MergePath) => FakeResponse.Json(Job("completed")),
            _ => FakeResponse.NotFound(),
        });

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102]))!;

        result["merged"]!.GetValue<bool>().Should().BeFalse();
        result["result"]!.GetValue<string>().Should().Be("not_confirmed");
        result["job"]!["status"]!.GetValue<string>().Should().Be("completed");
        result["next_step"]!.GetValue<string>().Should().Contain("reading the source tickets back failed");
    }

    [Fact]
    public async Task MergeTickets_JobWithoutId_IsNotPolledAndReportedUnconfirmed()
    {
        var reads = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1 ? ShowMany(Ticket(101), Ticket(102)) : ShowMany()),
            ("POST", MergePath) => FakeResponse.Json("""{"job_status":{"status":"queued"}}"""),
            _ => FakeResponse.NotFound(),
        });

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102]))!;

        fake.Requests.Should().NotContain(r => r.Path.StartsWith("/api/v2/job_statuses/", StringComparison.Ordinal));
        result["merged"]!.GetValue<bool>().Should().BeFalse();
        result["result"]!.GetValue<string>().Should().Be("not_confirmed");
        result["next_step"]!.GetValue<string>().Should().Contain("carried no id");
    }

    [Fact]
    public async Task MergeTickets_NumericJobId_IsStillPolled()
    {
        var reads = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1 ? ShowMany(Ticket(101), Ticket(102)) : ShowMany(Ticket(102, "closed", "\"closed_by_merge\""))),
            ("POST", MergePath) => FakeResponse.Json("""{"job_status":{"id":42,"status":"queued"}}"""),
            ("GET", "/api/v2/job_statuses/42") => FakeResponse.Json("""{"job_status":{"id":42,"status":"completed"}}"""),
            _ => FakeResponse.NotFound(),
        });

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102]))!;

        result["merged"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task MergeTickets_JobWithoutStatus_IsNeverReportedAsMerged()
    {
        using var fake = MergingFake(job: _ => FakeResponse.Json("""{"job_status":{"id":"job1"}}"""));

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102, 103],
            delays: [TimeSpan.Zero, TimeSpan.Zero]))!;

        fake.Requests.Count(r => r.Path == JobPath).Should().Be(2, "a job with no status is not terminal, so it is polled to the bound");
        result["merged"]!.GetValue<bool>().Should().BeFalse();
        result["result"]!.GetValue<string>().Should().Be("not_confirmed");
        result["next_step"]!.GetValue<string>().Should().Contain("'unknown'");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, """{"error":"Bad gateway"}""")]
    [InlineData(HttpStatusCode.OK, "<html>not json</html>")]
    [InlineData(HttpStatusCode.OK, "[]")]
    [InlineData(HttpStatusCode.OK, "42")]
    public async Task MergeTickets_AmbiguousAnswerToTheMergeRequest_IsNotConfirmedNotAnError(HttpStatusCode status, string body)
    {
        // Zendesk may already have queued the merge behind a gateway error or an unreadable answer, so
        // an error here would invite a second, irreversible merge.
        var reads = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r => (r.Method.Method, r.Path) switch
        {
            ("GET", ShowManyPath) => FakeResponse.Json(Interlocked.Increment(ref reads) == 1 ? ShowMany(Ticket(101), Ticket(102)) : ShowMany()),
            ("POST", MergePath) => new FakeResponse(status, body),
            _ => FakeResponse.NotFound(),
        });

        var result = JsonNode.Parse(await Merge(TestHelpers.BuildZendeskService(fake.Client), [102]))!;

        result["merged"]!.GetValue<bool>().Should().BeFalse();
        result["result"]!.GetValue<string>().Should().Be("not_confirmed");
        var nextStep = result["next_step"]!.GetValue<string>();
        nextStep.Should().Contain("Do not call Merge_tickets again");
        nextStep.Should().Contain("ask them to check the tickets in Zendesk").And.NotContain("unless");
        fake.Requests.Count(r => r.Path == MergePath).Should().Be(1);
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
        fake.Requests.Should().NotContain(r => r.Path.StartsWith("/api/v2/job_statuses/", StringComparison.Ordinal));
        fake.Requests.Count(r => r.Path == ShowManyPath).Should().Be(1, "a refused merge is not read back");
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
    public async Task MergeTickets_AuditRecord_NegativeControl_JobResponsesAloneNameNoTicket()
    {
        // Shows the transport finds no ticket id in the merge and job responses, so the ids in the test
        // above can only have come from the ticket read before the merge.
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
