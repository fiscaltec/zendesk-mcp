using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

public class TicketsBulkToolsTests
{
    private const string Tickets = """
        {"tickets":[
          {"id":101,"subject":"Phish 1","status":"open","type":"incident","priority":"normal","assignee_id":701,"group_id":801,"tags":["phish","inbox"],"custom_fields":[{"id":9001,"value":"a"}],"updated_at":"2026-11-01T10:00:00Z"},
          {"id":102,"subject":"Phish 2","status":"new","type":null,"priority":null,"assignee_id":null,"group_id":null,"tags":[],"custom_fields":[],"updated_at":"2026-11-01T11:00:00Z"}
        ]}
        """;

    private const string JobQueued = """{"job_status":{"id":"job1","status":"queued","results":null}}""";

    private const string JobCompleted = """{"job_status":{"id":"job1","status":"completed","results":[{"id":101,"action":"update","success":true,"status":"Updated"},{"id":102,"action":"update","success":true,"status":"Updated"}]}}""";

    private static FakeResponse Json(string body) => FakeResponse.Json(body);

    /// <summary>
    /// A fake Zendesk whose show_many answers <paramref name="before"/> until the write is sent and
    /// <paramref name="after"/> (default: the same) afterwards, so the post-write re-read can be steered.
    /// </summary>
    private static FakeZendesk BulkFake(string before = Tickets, string? after = null, Func<int, string>? job = null, Func<RecordedRequest, FakeResponse?>? extra = null)
    {
        var polls = 0;
        var written = false;
        return TestHelpers.CreateFakeZendesk(r =>
        {
            if (extra?.Invoke(r) is { } custom)
            {
                return custom;
            }

            switch (r.Path, r.Method.Method)
            {
                case ("/api/v2/tickets/show_many", "GET"):
                    return Json(written ? after ?? before : before);
                case ("/api/v2/tickets/update_many", "PUT"):
                    written = true;
                    return Json(JobQueued);
                case ("/api/v2/job_statuses/job1", "GET"):
                    return Json((job ?? (_ => JobCompleted))(++polls));
                default:
                    return FakeResponse.NotFound();
            }
        });
    }

    private static ZendeskService Service(FakeZendesk fake, Action<ZendeskServerOptions>? configure = null, ToolCallAuditContext? auditContext = null) =>
        TestHelpers.BuildZendeskService(fake.Client, auditContext: auditContext, configure: o =>
        {
            o.JobStatusPollInterval = TimeSpan.Zero;
            configure?.Invoke(o);
        });

    private static BulkTicketChange Priority(string priority) => new() { Priority = priority };

    /// <summary>Previews, then confirms with that preview's confirmation, as a model following the description does.</summary>
    private static async Task<JsonNode> PreviewThenConfirm(Func<string?, Task<string>> call)
    {
        var preview = JsonNode.Parse(await call(null))!;
        preview["confirmation"].Should().NotBeNull("a preview with something to change and nothing to refuse offers a confirmation");
        return JsonNode.Parse(await call(preview["confirmation"]!.GetValue<string>()))!;
    }

    private static void NoWrites(FakeZendesk fake) =>
        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "a preview or a refusal must not change anything");

    private static RecordedRequest Put(FakeZendesk fake) => fake.Requests.Single(r => r.Method == HttpMethod.Put);

    private static JsonArray SentTickets(FakeZendesk fake) => JsonNode.Parse(Put(fake).Body!)!["tickets"]!.AsArray();

    // ---------- the preview ----------

    [Fact]
    public async Task WithoutConfirmation_ItPreviewsOnly_ShowingFromAndToForEachChangedField()
    {
        using var fake = BulkFake();

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102],
            new BulkTicketChange { Status = "solved", Priority = "LOW", AdditionalTags = ["spam"], RemoveTags = ["inbox"] }))!;

        NoWrites(fake);
        fake.Requests.Single().Query("ids").Should().Be("101,102");
        result["preview"]!.GetValue<bool>().Should().BeTrue();
        result["summary"]!["will_change"]!.GetValue<int>().Should().Be(2);
        result["confirmation"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        result["confirmation_expires_at"].Should().NotBeNull();
        result["next_step"]!.GetValue<string>().Should().Contain("only once they say yes");

        var first = result["will_change"]![0]!;
        first["id"]!.GetValue<long>().Should().Be(101);
        first["subject"]!.GetValue<string>().Should().Be("Phish 1");
        first["changes"]!["status"]!.ToJsonString().Should().Be("""{"from":"open","to":"solved"}""");
        first["changes"]!["priority"]!.ToJsonString().Should().Be("""{"from":"normal","to":"low"}""");
        first["changes"]!["tags"]!.ToJsonString().Should().Be("""{"from":["phish","inbox"],"to":["phish","spam"]}""");
        first["agent_url"]!.GetValue<string>().Should().EndWith("/agent/tickets/101");

        // Only the fields the change touches are shown.
        first["changes"]!.AsObject().ContainsKey("assignee_id").Should().BeFalse();
        result["will_change"]![1]!["changes"]!["priority"]!.ToJsonString().Should().Be("""{"from":null,"to":"low"}""");
    }

    [Fact]
    public async Task ATicketAlreadyAsAskedIsUnchanged_AndOnlyTheDifferingFieldsAreShown()
    {
        using var fake = BulkFake();

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102],
            new BulkTicketChange { Priority = "normal", AssigneeId = 701, AdditionalTags = ["phish"] }))!;

        result["unchanged"]!.AsArray().Select(i => i!.GetValue<long>()).Should().Equal(101);
        var second = result["will_change"]!.AsArray().Should().ContainSingle().Subject!;
        second["id"]!.GetValue<long>().Should().Be(102);
        second["changes"]!.AsObject().Select(kv => kv.Key).Should().BeEquivalentTo("priority", "assignee_id", "tags");
    }

    [Fact]
    public async Task NullClearsTheAssigneeGroupAndType()
    {
        using var fake = BulkFake();

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101],
            new BulkTicketChange { AssigneeId = null, GroupId = null, Type = null }))!;

        var changes = result["will_change"]![0]!["changes"]!;
        changes["assignee_id"]!.ToJsonString().Should().Be("""{"from":701,"to":null}""");
        changes["group_id"]!.ToJsonString().Should().Be("""{"from":801,"to":null}""");
        changes["type"]!.ToJsonString().Should().Be("""{"from":"incident","to":null}""");
    }

    [Fact]
    public async Task CustomFieldsCompareByValue_EmptyAndNullBothMeaningNone()
    {
        using var fake = BulkFake();

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102],
            new BulkTicketChange { CustomFields = [new TicketCustomFieldInput(9001, "")] }))!;

        result["will_change"]![0]!["changes"]!["custom_fields"]!.ToJsonString().Should().Be("""[{"id":9001,"from":"a","to":""}]""");
        result["unchanged"]!.AsArray().Select(i => i!.GetValue<long>()).Should().Equal(102);
    }

    [Fact]
    public async Task MissingAndClosedTicketsCannotChange_ButTagsMayChangeOnAClosedTicket()
    {
        const string closed = """{"tickets":[{"id":101,"status":"closed","tags":[]},{"id":102,"status":"closed","tags":[]}]}""";
        using var fake = BulkFake(closed);

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), null, null,
            [new BulkTicketEntry { Id = 101, Priority = "high" }, new BulkTicketEntry { Id = 102, AdditionalTags = ["spam"] }, new BulkTicketEntry { Id = 103, Priority = "high" }]))!;

        NoWrites(fake);
        var cannot = result["cannot_change"]!.AsArray();
        cannot.Should().HaveCount(2);
        cannot[0]!["id"]!.GetValue<long>().Should().Be(101);
        cannot[0]!["reason"]!.GetValue<string>().Should().StartWith("closed");
        cannot[1]!["id"]!.GetValue<long>().Should().Be(103);
        cannot[1]!["reason"]!.GetValue<string>().Should().Contain("not found");
        result["will_change"]!.AsArray().Should().ContainSingle(t => t!["id"]!.GetValue<long>() == 102);
    }

    [Fact]
    public async Task AClosedTicketWhoseOnlyRealDifferenceIsTagsCanChange()
    {
        // The rule is about what would change, not what was asked: the priority already matches.
        const string closed = """{"tickets":[{"id":101,"status":"closed","priority":"high","tags":[]}]}""";
        using var fake = BulkFake(closed);

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101],
            new BulkTicketChange { Priority = "high", AdditionalTags = ["spam"] }))!;

        result["cannot_change"]!.AsArray().Should().BeEmpty();
        result["will_change"]![0]!["changes"]!.AsObject().Select(kv => kv.Key).Should().Equal("tags");
    }

    [Fact]
    public async Task ACheckboxClearedAndAMultiSelectInAnyOrderAreAlreadyAsAsked()
    {
        const string fields = """{"tickets":[{"id":101,"status":"open","custom_fields":[{"id":1,"value":false},{"id":2,"value":["b","a"]}],"updated_at":"x"}]}""";
        using var fake = BulkFake(fields);

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101],
            new BulkTicketChange { CustomFields = [new TicketCustomFieldInput(1, ""), new TicketCustomFieldInput(2, "a b")] }))!;

        result["unchanged"]!.AsArray().Select(i => i!.GetValue<long>()).Should().Equal(101);
    }

    [Fact]
    public async Task AMultiSelectIsWrittenAsAListOfOptionTags()
    {
        const string fields = """{"tickets":[{"id":101,"status":"open","custom_fields":[{"id":2,"value":["a"]}],"updated_at":"x"}]}""";
        using var fake = BulkFake(fields);

        await PreviewThenConfirm(c => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101],
            new BulkTicketChange { CustomFields = [new TicketCustomFieldInput(2, "a,b")] }, confirmation: c));

        SentTickets(fake)[0]!["custom_fields"]!.ToJsonString().Should().Be("""[{"id":2,"value":["a","b"]}]""");
    }

    [Fact]
    public async Task NothingToChange_OffersNoConfirmation()
    {
        using var fake = BulkFake();

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], Priority("normal")))!;

        result["confirmation"].Should().BeNull();
        result["next_step"]!.GetValue<string>().Should().Contain("nothing to change");
    }

    // ---------- the confirmation ----------

    [Fact]
    public async Task Confirmed_SendsOneEntryPerTicket_OnlyWhatDiffers_GuardedBySafeUpdate()
    {
        using var fake = BulkFake();

        var result = await PreviewThenConfirm(c => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102],
            new BulkTicketChange { Status = "pending", AssigneeId = 701, AdditionalTags = ["spam"], RemoveTags = ["inbox"] }, confirmation: c));

        Put(fake).Path.Should().Be("/api/v2/tickets/update_many");
        Put(fake).Query("ids").Should().BeNull("each entry names its own ticket");
        var sent = SentTickets(fake);
        sent.Select(t => t!["id"]!.GetValue<long>()).Should().Equal(101, 102);

        var first = sent[0]!;
        first["safe_update"]!.GetValue<bool>().Should().BeTrue();
        first["updated_stamp"]!.GetValue<string>().Should().Be("2026-11-01T10:00:00Z");
        first["status"]!.GetValue<string>().Should().Be("pending");
        first.AsObject().ContainsKey("assignee_id").Should().BeFalse("101 is already assigned to 701");
        first["tags"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("phish", "spam");
        sent[1]!["assignee_id"]!.GetValue<long>().Should().Be(701);
        sent[1]!["tags"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("spam");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);

        // The re-read still shows the old values (the fake does not apply writes), so the job's
        // success is not taken on trust: both are not_confirmed (steered re-reads are covered below).
        result["preview"]!.GetValue<bool>().Should().BeFalse();
        result["job"]!["status"]!.GetValue<string>().Should().Be("completed");
        result["changed"]!.AsArray().Should().BeEmpty();
        result["not_confirmed"]!.AsArray().Select(n => n!["id"]!.GetValue<long>()).Should().Equal(101, 102);
    }

    [Fact]
    public async Task Confirmed_ATicketIsChangedOnlyWhenTheJobSaysSoAndTheReReadShowsIt()
    {
        const string applied = """
            {"tickets":[
              {"id":101,"status":"pending","tags":["phish","inbox"],"updated_at":"2026-11-04T10:00:00Z"},
              {"id":102,"status":"new","tags":[],"updated_at":"2026-11-04T10:00:00Z"}
            ]}
            """;
        using var fake = BulkFake(after: applied);

        var result = await PreviewThenConfirm(c => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102],
            new BulkTicketChange { Status = "pending" }, confirmation: c));

        result["changed"]!.AsArray().Select(i => i!.GetValue<long>()).Should().Equal(101);

        // 102: the job said Updated, but the ticket does not show it (the 2026-10-28 bug class).
        var unconfirmed = result["not_confirmed"]!.AsArray().Should().ContainSingle().Subject!;
        unconfirmed["id"]!.GetValue<long>().Should().Be(102);
        unconfirmed["reason"]!.GetValue<string>().Should().Contain("does not show the change").And.Contain("status");
        result["summary"]!.GetValue<string>().Should().StartWith("Changed 1 of 2");
    }

    [Fact]
    public async Task Confirmed_ReportsFailedTicketsWithZendesksReason_AndTicketsTheJobDidNotMention()
    {
        const string mixed = """{"job_status":{"id":"job1","status":"completed","results":[{"id":101,"success":false,"error":"TicketUpdateFailed","details":"Ticket was updated by someone else (collision)"}]}}""";
        using var fake = BulkFake(job: _ => mixed);

        var result = await PreviewThenConfirm(c => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], Priority("high"), confirmation: c));

        var failed = result["failed"]!.AsArray().Should().ContainSingle().Subject!;
        failed["id"]!.GetValue<long>().Should().Be(101);
        failed["reason"]!.GetValue<string>().Should().Contain("collision");
        failed["error"]!.GetValue<string>().Should().Be("TicketUpdateFailed");
        result["not_confirmed"]!.AsArray().Should().ContainSingle(n => n!["id"]!.GetValue<long>() == 102
            && n["reason"]!.GetValue<string>().Contains("did not report"));
        result["changed"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Confirmed_ReturnsUndoWithThePreviousValues()
    {
        const string applied = """{"tickets":[{"id":101,"status":"pending","priority":"high","assignee_id":null,"tags":["phish","spam"],"updated_at":"x"}]}""";
        using var fake = BulkFake(after: applied);

        var result = await PreviewThenConfirm(c => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101],
            new BulkTicketChange { Status = "pending", Priority = "high", AssigneeId = null, AdditionalTags = ["spam"], RemoveTags = ["inbox"] }, confirmation: c));

        result["changed"]!.AsArray().Select(i => i!.GetValue<long>()).Should().Equal(101);
        var undo = result["undo"]!.AsArray().Should().ContainSingle().Subject!;
        undo["id"]!.GetValue<long>().Should().Be(101);
        undo["status"]!.GetValue<string>().Should().Be("open");
        undo["priority"]!.GetValue<string>().Should().Be("normal");
        undo["assignee_id"]!.GetValue<long>().Should().Be(701);
        undo["additional_tags"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("inbox");
        undo["remove_tags"]!.AsArray().Select(t => t!.GetValue<string>()).Should().Equal("spam");
    }

    [Fact]
    public async Task TheUndoOutputIsAcceptedBackAsTickets()
    {
        // Fed back exactly as returned. Ticket 102 had no priority, which Zendesk cannot restore, so its
        // undo would hold nothing; an entry with nothing to restore would be refused, and the whole
        // batch with it, so it is left out.
        const string applied = """{"tickets":[{"id":101,"status":"open","priority":"high","tags":["phish","inbox"],"updated_at":"x"},{"id":102,"status":"new","priority":"high","tags":[],"updated_at":"y"}]}""";
        using var fake = BulkFake(after: applied);
        var result = await PreviewThenConfirm(c => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102],
            new BulkTicketChange { Priority = "high" }, confirmation: c));
        result["changed"]!.AsArray().Should().HaveCount(2);
        result["undo"]!.AsArray().Should().ContainSingle("102 had no priority, so its undo would be bare and is left out");
        result["warnings"]!.AsArray().Should().Contain(w => w!.GetValue<string>().Contains("no priority before"));

        var undo = JsonSerializer.Deserialize<BulkTicketEntry[]>(result["undo"]!.ToJsonString(), McpJsonUtilities.DefaultOptions)!;
        using var again = BulkFake(applied);
        var preview = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(again), tickets: undo))!;

        preview["will_change"]![0]!["changes"]!["priority"]!.ToJsonString().Should().Be("""{"from":"high","to":"normal"}""");
    }

    [Fact]
    public async Task UndoRestoresTheCustomStatus_NotJustTheStatusCategory()
    {
        const string withCustom = """{"tickets":[{"id":101,"status":"open","custom_status_id":44,"tags":[],"updated_at":"x"}]}""";
        const string applied = """{"tickets":[{"id":101,"status":"pending","custom_status_id":2,"tags":[],"updated_at":"y"}]}""";
        using var fake = BulkFake(withCustom, applied);

        var result = await PreviewThenConfirm(c => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], new BulkTicketChange { Status = "pending" }, confirmation: c));

        var undo = result["undo"]![0]!;
        undo["custom_status_id"]!.GetValue<long>().Should().Be(44);
        undo.AsObject().ContainsKey("status").Should().BeFalse("status and custom_status_id cannot go together");
    }

    [Fact]
    public async Task Confirmed_TheConfirmationFromADifferentBatchIsRefused()
    {
        using var fake = BulkFake();
        var preview = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], Priority("high")))!;

        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], Priority("urgent"),
            confirmation: preview["confirmation"]!.GetValue<string>());

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().StartWith("Nothing was changed.").And.Contain("Preview the batch again");
        NoWrites(fake);
    }

    [Fact]
    public async Task Confirmed_ATicketUpdatedSinceThePreviewIsRefused()
    {
        var reads = 0;
        const string later = """{"tickets":[{"id":101,"status":"open","priority":"normal","tags":[],"updated_at":"2026-11-04T09:00:00Z"}]}""";
        using var fake = BulkFake(extra: r => r.Path == "/api/v2/tickets/show_many" && ++reads > 1 ? Json(later) : null);
        var preview = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], Priority("high")))!;

        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], Priority("high"), confirmation: preview["confirmation"]!.GetValue<string>());

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*updated since*");
        NoWrites(fake);
    }

    [Fact]
    public async Task Confirmed_TheSameBatchInAnotherOrderIsTheSameBatch()
    {
        // A model re-sending the batch need not repeat its key, tag or ticket order.
        using var fake = BulkFake();
        var preview = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102],
            new BulkTicketChange { Status = "pending", Priority = "high", AdditionalTags = ["a", "B"] }))!;

        await TicketsBulkTools.BulkUpdateTickets(Service(fake), [102, 101],
            new BulkTicketChange { AdditionalTags = ["b", "a"], Priority = "high", Status = "pending" },
            confirmation: preview["confirmation"]!.GetValue<string>());

        fake.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Confirmed_AnotherPersonsPreviewDoesNotConfirm()
    {
        using var fake = BulkFake();
        var preview = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], Priority("high")))!;

        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake, o => o.DevelopmentEmail = "someone.else@example.com"), [101], Priority("high"),
            confirmation: preview["confirmation"]!.GetValue<string>());

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("Nothing was changed.*");
        NoWrites(fake);
    }

    [Fact]
    public async Task CancellingAfterTheWriteStillReportsWhatHappened()
    {
        // Once the PUT has gone, a cancelled call would read as failed and invite a retry.
        using var cancel = new CancellationTokenSource();
        using var fake = BulkFake(extra: r =>
        {
            if (r.Method == HttpMethod.Put)
            {
                cancel.Cancel();
            }

            return null;
        });
        var preview = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], Priority("high")))!;

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], Priority("high"),
            confirmation: preview["confirmation"]!.GetValue<string>(), cancellationToken: cancel.Token))!;

        cancel.IsCancellationRequested.Should().BeTrue();
        result["job"]!["status"]!.GetValue<string>().Should().Be("completed");
    }

    [Theory]
    [InlineData("")]
    [InlineData("made-up")]
    [InlineData("1730000000.00")]
    public async Task Confirmed_AConfirmationNoPreviewIssuedIsRefused(string confirmation)
    {
        using var fake = BulkFake();

        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], Priority("high"), confirmation: confirmation);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("Nothing was changed.*");
        NoWrites(fake);
    }

    [Fact]
    public void AConfirmationExpiresAfterThirtyMinutes()
    {
        var issued = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
        var confirmation = BulkTicketConfirmation.Issue("batch", issued);

        BulkTicketConfirmation.Check("batch", confirmation, issued.AddMinutes(29)).Should().BeNull();
        BulkTicketConfirmation.Check("batch", confirmation, issued.AddMinutes(31)).Should().Contain("expired");
        BulkTicketConfirmation.Check("other batch", confirmation, issued.AddMinutes(1)).Should().Contain("differ");
    }

    // ---------- the job ----------

    [Fact]
    public async Task PollsUntilTheJobFinishes()
    {
        using var fake = BulkFake(job: n => n < 3 ? """{"job_status":{"id":"job1","status":"working"}}""" : JobCompleted);

        var result = await PreviewThenConfirm(c => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], Priority("high"), confirmation: c));

        fake.Requests.Count(r => r.Path == "/api/v2/job_statuses/job1").Should().Be(3);
        result["job"]!["status"]!.GetValue<string>().Should().Be("completed");
    }

    [Fact]
    public async Task AJobStillRunningAtTheBoundIsReportedAsRunning_NeverAsChanged()
    {
        using var fake = BulkFake(job: _ => """{"job_status":{"id":"job1","status":"working","results":[{"id":101,"success":true}]}}""");

        var result = await PreviewThenConfirm(c => TicketsBulkTools.BulkUpdateTickets(Service(fake, o => o.MaxJobStatusPolls = 4), [101, 102],
            Priority("high"), confirmation: c));

        fake.Requests.Count(r => r.Path == "/api/v2/job_statuses/job1").Should().Be(4, "the poll is bounded by MaxJobStatusPolls");
        result["job"]!["status"]!.GetValue<string>().Should().Be("running");
        result["changed"]!.AsArray().Should().BeEmpty();
        result["not_confirmed"]!.AsArray().Should().HaveCount(2);
        result["summary"]!.GetValue<string>().Should().Contain("still applying").And.Contain("Do not call again");
    }

    [Fact]
    public async Task AFailureReadingTheJobAfterTheWriteIsReportedNotThrown()
    {
        // The write was accepted; a thrown error would read as a failed call and invite a retry.
        using var fake = BulkFake(extra: r => r.Path == "/api/v2/job_statuses/job1" ? FakeResponse.WithStatus(HttpStatusCode.InternalServerError) : null);

        var result = await PreviewThenConfirm(c => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], Priority("high"), confirmation: c));

        result["job"]!["status"]!.GetValue<string>().Should().Be("unknown");
        result["summary"]!.GetValue<string>().Should().Contain("was submitted").And.Contain("Do not call again");
        result["not_confirmed"]!.AsArray().Should().HaveCount(2);
        result["undo"]!.AsArray().Should().ContainSingle("the change may have landed, so the way back is still offered; 102 had no priority to restore")
            .Which!["id"]!.GetValue<long>().Should().Be(101);
    }

    [Fact]
    public async Task AFailedJobIsNotReportedAsComplete()
    {
        using var fake = BulkFake(job: _ => """{"job_status":{"id":"job1","status":"failed","message":"boom"}}""");

        var result = await PreviewThenConfirm(c => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], Priority("high"), confirmation: c));

        result["job"]!["status"]!.GetValue<string>().Should().Be("failed");
        result["changed"]!.AsArray().Should().BeEmpty();
        result["not_confirmed"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task TheAuditRecordsEveryTicketTheWriteWasSentFor_NotTheJobId()
    {
        // The job response carries no ticket records, so without the request body the write would be
        // audited as touching nothing (or, read naively, as touching record "job1").
        using var fake = BulkFake(job: _ => """{"job_status":{"id":"job1","status":"completed","results":null}}""");
        var preview = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], Priority("high")))!;
        var context = new ToolCallAuditContext();

        await TicketsBulkTools.BulkUpdateTickets(Service(fake, auditContext: context), [101, 102], Priority("high"),
            confirmation: preview["confirmation"]!.GetValue<string>());

        var summary = context.Summarise();
        summary.Ids.Should().Contain(["101", "102"]).And.NotContain("job1");

        // The read before the write names the tickets too, so the ids alone would pass even if the
        // write named nobody. The read and the write each name both tickets (2 + 2); the only call
        // without ids is the job poll, a finished job that reported no results.
        summary.RecordsFetched.Should().Be(4);
        summary.CallsWithoutIds.Should().Be(1, "only the job poll, whose results are empty, names nobody");
    }

    // ---------- solving problem tickets ----------

    [Fact]
    public async Task SolvingProblemTicketsNeedsSolveIncidents()
    {
        const string problem = """{"tickets":[{"id":101,"type":"problem","status":"open","updated_at":"x"},{"id":102,"type":"incident","status":"open","updated_at":"y"}]}""";
        using var fake = BulkFake(problem);

        var preview = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], new BulkTicketChange { Status = "solved" }))!;
        preview["would_refuse"]!.AsArray().Should().ContainSingle(r => r!.GetValue<string>().Contains("101") && r.GetValue<string>().Contains("solve_incidents"));
        preview["confirmation"].Should().BeNull();

        var result = await PreviewThenConfirm(c => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101, 102], new BulkTicketChange { Status = "solved" },
            confirmation: c, solve_incidents: true));
        result["warnings"]!.AsArray().Should().ContainSingle(w => w!.GetValue<string>().StartsWith("Solving problem ticket(s) 101", StringComparison.Ordinal));
        result["warnings"]!.AsArray().Should().Contain(w => w!.GetValue<string>().Contains("does not reopen the incidents"));
        fake.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task ACustomStatusInTheSolvedCategoryCountsAsSolving()
    {
        const string problem = """{"tickets":[{"id":101,"type":"problem","status":"open"}]}""";
        using var fake = BulkFake(problem, extra: r => r.Path == "/api/v2/custom_statuses/55"
            ? Json("""{"custom_status":{"id":55,"status_category":"solved"}}""")
            : null);

        var preview = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], new BulkTicketChange { CustomStatusId = 55 }))!;

        preview["would_refuse"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task NotSolving_AProblemTicketNeedsNoConfirmation()
    {
        // Negative control for the two above: the confirmation is about solving, not about problem tickets.
        const string problem = """{"tickets":[{"id":101,"type":"problem","status":"open"}]}""";
        using var fake = BulkFake(problem);

        var preview = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], Priority("high")))!;

        preview["would_refuse"]!.AsArray().Should().BeEmpty();
        preview["confirmation"].Should().NotBeNull();
    }

    // ---------- the two shapes ----------

    [Fact]
    public async Task TicketsCarryTheirOwnChanges_PlusTheSharedOnes()
    {
        using var fake = BulkFake();

        var result = JsonNode.Parse(await TicketsBulkTools.BulkUpdateTickets(Service(fake), changes: Priority("urgent"),
            tickets: [new BulkTicketEntry { Id = 101, Subject = "Renamed 1" }, new BulkTicketEntry { Id = 102, Subject = "Renamed 2" }]))!;

        var first = result["will_change"]![0]!["changes"]!;
        first["subject"]!.ToJsonString().Should().Be("""{"from":"Phish 1","to":"Renamed 1"}""");
        first["priority"]!["to"]!.GetValue<string>().Should().Be("urgent");
        result["will_change"]![1]!["changes"]!["subject"]!["to"]!.GetValue<string>().Should().Be("Renamed 2");
    }

    [Fact]
    public async Task AFieldSetBothInChangesAndInAnEntryIsRefused()
    {
        using var fake = BulkFake();

        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), changes: Priority("urgent"),
            tickets: [new BulkTicketEntry { Id = 101, Priority = "low" }]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("ticket 101: priority is set both*");
        fake.Requests.Should().BeEmpty();
    }

    public static TheoryData<long[]?, BulkTicketEntry[]?, string> BadShapes => new()
    {
        { null, null, "*not both and not neither*" },
        { [101], [new BulkTicketEntry { Id = 101, Priority = "low" }], "*not both and not neither*" },
        { [], null, "*at least one*" },
        { [101, 0], null, "*positive*" },
        { [101, 102, 101], null, "*distinct*101*" },
        { Enumerable.Range(1, 101).Select(i => (long)i).ToArray(), null, "*at most 100*" },
        { null, [new BulkTicketEntry { Id = 101, Priority = "low" }, new BulkTicketEntry { Id = 101, Priority = "high" }], "*distinct*" },
    };

    [Theory]
    [MemberData(nameof(BadShapes))]
    public async Task RefusesABadBatchShape_WithoutCallingZendesk(long[]? ids, BulkTicketEntry[]? entries, string message)
    {
        using var fake = BulkFake();

        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), ids, ids is null ? null : Priority("high"), entries);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AcceptsExactlyOneHundredTickets()
    {
        // The boundary of the test above: 100 is Zendesk's own limit and must be allowed.
        using var fake = BulkFake("""{"tickets":[]}""");

        await TicketsBulkTools.BulkUpdateTickets(Service(fake), Enumerable.Range(1, 100).Select(i => (long)i).ToArray(), Priority("high"));

        fake.Requests.Should().ContainSingle();
    }

    public static TheoryData<BulkTicketChange?, string> BadChanges => new()
    {
        { null, "*needs changes*" },
        { new BulkTicketChange(), "*at least one change*" },
        { new BulkTicketChange { AdditionalTags = null }, "*at least one change*" },
        { new BulkTicketChange { Status = "open", CustomStatusId = 5 }, "*not both*" },
        { new BulkTicketChange { Status = "closed" }, "status must be one of*" },
        { new BulkTicketChange { Status = null }, "*status cannot be cleared*" },
        { new BulkTicketChange { Priority = null }, "*cannot clear a priority*" },
        { new BulkTicketChange { Subject = "  " }, "*subject cannot be empty*" },
        { new BulkTicketChange { Subject = new string('x', 501) }, "subject is limited*" },
        { new BulkTicketChange { AssigneeId = 0 }, "assignee_id must be a positive*" },
        { new BulkTicketChange { AdditionalTags = ["two words"] }, "additional_tags entries*" },
        { new BulkTicketChange { AdditionalTags = ["Spam"], RemoveTags = ["spam"] }, "*both added and removed*" },
        { new BulkTicketChange { AdditionalTags = ["x"], CustomFields = [new TicketCustomFieldInput(9001, "b")] }, "*separate calls*" },
        { new BulkTicketChange { CustomFields = [new TicketCustomFieldInput(9001, "a"), new TicketCustomFieldInput(9001, "b")] }, "*more than once*" },
    };

    [Theory]
    [MemberData(nameof(BadChanges))]
    public async Task RefusesABadChange_WithoutCallingZendesk(BulkTicketChange? change, string message)
    {
        using var fake = BulkFake();

        var act = () => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], change);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AZendeskRefusalOfTheWriteIsSurfaced()
    {
        using var fake = BulkFake(extra: r => r.Method == HttpMethod.Put
            ? FakeResponse.WithStatus(HttpStatusCode.UnprocessableEntity, """{"error":"RecordInvalid"}""")
            : null);

        var act = () => PreviewThenConfirm(c => TicketsBulkTools.BulkUpdateTickets(Service(fake), [101], Priority("high"), confirmation: c));

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*RecordInvalid*");
    }

    // ---------- binding: what the MCP SDK hands the tool ----------

    [Fact]
    public void AnExplicitNullIsGiven_AnAbsentFieldIsNot()
    {
        // The SDK binds tool arguments with McpJsonUtilities.DefaultOptions; null-to-clear depends on
        // the setter running for an explicit null and not for an absent property.
        var change = JsonSerializer.Deserialize<BulkTicketChange>("""{"assignee_id":null,"priority":"high"}""", McpJsonUtilities.DefaultOptions)!;

        change.Given.Should().BeEquivalentTo("assignee_id", "priority");
        change.AssigneeId.Should().BeNull();
    }

    [Fact]
    public void TheSchemaOffersNullForClearableFields_AndNotTheBookkeeping()
    {
        var schema = AIJsonUtilities.CreateJsonSchema(typeof(BulkTicketEntry), serializerOptions: McpJsonUtilities.DefaultOptions);
        var properties = schema.GetProperty("properties");

        properties.TryGetProperty("Given", out _).Should().BeFalse();
        properties.TryGetProperty("given", out _).Should().BeFalse();
        properties.GetProperty("id").GetRawText().Should().Contain("integer");
        properties.GetProperty("assignee_id").GetRawText().Should().Contain("null");
        properties.GetProperty("additional_tags").GetRawText().Should().Contain("array");
    }
}
