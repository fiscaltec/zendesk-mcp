using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

public class TicketConversationsToolsTests
{
    private static FakeResponse Json(string body) => FakeResponse.Json(body);

    private static string Ticket(long id, string channel = "email") =>
        $$$$"""{"ticket":{"id":{{{{id}}}},"subject":"Subject {{{{id}}}}","status":"open","via":{"channel":"{{{{channel}}}}"}}}""";

    // Users the comments below refer to: 501 an end user, 701 a human agent.
    private const string Users = """[{"id":501,"name":"Alice","role":"end-user"},{"id":701,"name":"Agent Smith","role":"agent"}]""";

    private static string Comments(string comments, bool hasMore = false) =>
        $$$$"""{"comments":[{{{{comments}}}}],"users":{{{{Users}}}},"meta":{"has_more":{{{{(hasMore ? "true" : "false")}}}},"after_cursor":"c"}}""";

    private static string Comment(long id, long author, string body, bool isPublic = true, string via = "email") =>
        $$$$"""{"id":{{{{id}}}},"author_id":{{{{author}}}},"public":{{{{(isPublic ? "true" : "false")}}}},"created_at":"2026-01-01T00:00:{{{{id % 60:00}}}}Z","plain_body":"{{{{body}}}}","via":{"channel":"{{{{via}}}}"},"attachments":[]}""";

    // Ticket {id}, email, with {count} comments from the customer whose ids are {id}000 + n.
    private static FakeResponse Simple(RecordedRequest r, int count = 2)
    {
        var parts = r.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var id = long.Parse(parts[3], CultureInfo.InvariantCulture);
        return parts.Length == 4
            ? Json(Ticket(id))
            : Json(Comments(string.Join(",", Enumerable.Range(1, count).Select(n => Comment(id * 1000 + n, 501, $"Message {n}")))));
    }

    private static async Task<JsonNode> Bulk(
        ZendeskService zendesk, long[] ids, string[]? include = null, int? lastN = null, string[]? fields = null, bool? publicOnly = null, string[]? sides = null) =>
        JsonNode.Parse(await TicketConversationsTools.BulkGetTicketConversations(zendesk, ids, include, lastN, fields, publicOnly, sides))!;

    [Fact]
    public async Task TellsTheFourSidesApart_AndReturnsTheDefaultFields()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/101" => Json(Ticket(101)),
            "/api/v2/tickets/101/comments" => Json(Comments(string.Join(",",
                Comment(1, 501, "Help please"),
                Comment(2, -1, "Automatic reply", via: "rule"),
                Comment(3, -1, "AI answer", via: "api"),
                Comment(4, 701, "Internal note", isPublic: false, via: "web"),
                Comment(5, 701, "Trigger reply sent as the assignee", via: "rule")))),
            _ => FakeResponse.NotFound(),
        });

        var result = await Bulk(TestHelpers.BuildZendeskService(fake.Client), [101]);

        fake.Requests.Single(r => r.Path.EndsWith("/comments", StringComparison.Ordinal)).Query("include").Should().Be("users");
        var ticket = result["tickets"]![0]!;
        ticket["read"]!.GetValue<string>().Should().Be("complete");
        ticket["subject"]!.GetValue<string>().Should().Be("Subject 101");
        ticket["url"]!.GetValue<string>().Should().EndWith("/agent/tickets/101");
        ticket["total_messages"]!.GetValue<int>().Should().Be(5);
        ticket["matching_messages"]!.GetValue<int>().Should().Be(5);
        var messages = ticket["messages"]!.AsArray();
        messages.Select(m => m!["side"]!.GetValue<string>()).Should().Equal(["customer", "system", "ai_agent", "agent", "system"], "a trigger's comment is Zendesk's, whoever it is sent as");
        messages[3]!["public"]!.GetValue<bool>().Should().BeFalse();
        messages[0]!["text"]!.GetValue<string>().Should().Be("Help please");
        messages[0]!["attachments"]!.AsArray().Should().BeEmpty();
        messages[0]!.AsObject().ContainsKey("author_id").Should().BeFalse("author is not a default field");
        messages[0]!.AsObject().ContainsKey("type").Should().BeFalse("type is not a default field");
        result["retry"].Should().BeNull("nothing failed");
    }

    [Fact]
    public async Task MessageFields_ChooseWhatEachMessageCarries()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => Simple(r));

        var result = await Bulk(TestHelpers.BuildZendeskService(fake.Client), [101], fields: ["author", "type"]);

        var messages = result["tickets"]![0]!["messages"]!.AsArray();
        messages[0]!["author_id"]!.GetValue<long>().Should().Be(501);
        messages[0]!["author_name"]!.GetValue<string>().Should().Be("Alice");
        messages.Select(m => m!["type"]!.GetValue<string>()).Should().Equal(["description", "comment"]);
        messages[0]!.AsObject().ContainsKey("text").Should().BeFalse();
        messages[0]!.AsObject().ContainsKey("attachments").Should().BeFalse();
        messages[0]!["timestamp"].Should().NotBeNull("timestamp, side and public are always there");
    }

    [Fact]
    public async Task Filters_ApplyInOrder_AndTheCountsSayWhatPassed()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/101" => Json(Ticket(101)),
            "/api/v2/tickets/101/comments" => Json(Comments(string.Join(",",
                Comment(1, 501, "c1"), Comment(2, 701, "a1"), Comment(3, 701, "note", isPublic: false),
                Comment(4, 501, "c2"), Comment(5, 701, "a2"), Comment(6, 501, "c3")))),
            _ => FakeResponse.NotFound(),
        });
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var agentPublic = (await Bulk(zendesk, [101], publicOnly: true, sides: ["agent"]))["tickets"]![0]!;
        agentPublic["total_messages"]!.GetValue<int>().Should().Be(6);
        agentPublic["matching_messages"]!.GetValue<int>().Should().Be(2, "the internal note is left out");
        agentPublic["messages"]!.AsArray().Select(m => m!["text"]!.GetValue<string>()).Should().Equal(["a1", "a2"]);

        var newestAgent = (await Bulk(zendesk, [101], lastN: 1, sides: ["agent"]))["tickets"]![0]!;
        newestAgent["messages"]!.AsArray().Select(m => m!["text"]!.GetValue<string>()).Should().Equal(["a2"], "last_n_messages applies after the filters, not before");

        var newest = (await Bulk(zendesk, [101], lastN: 2))["tickets"]![0]!;
        newest["messages"]!.AsArray().Select(m => m!["text"]!.GetValue<string>()).Should().Equal(["a2", "c3"], "last_n_messages keeps the newest");
        newest["read"]!.GetValue<string>().Should().Be("complete", "messages the caller filtered out are not a cut");

        var none = (await Bulk(zendesk, [101], lastN: 0))["tickets"]![0]!;
        none["messages"]!.AsArray().Should().BeEmpty();
        none["matching_messages"]!.GetValue<int>().Should().Be(6);
    }

    [Fact]
    public async Task ATicketOverTheTextBudget_KeepsTheFirstAndTheNewestThatFit()
    {
        var big = new string('x', 5000);
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/101" => Json(Ticket(101)),
            "/api/v2/tickets/101/comments" => Json(Comments(string.Join(",", Enumerable.Range(1, 5).Select(n => Comment(n, 501, $"{n}{big}"))))),
            _ => FakeResponse.NotFound(),
        });

        var ticket = (await Bulk(TestHelpers.BuildZendeskService(fake.Client), [101]))["tickets"]![0]!;

        // 5 × 5,001 characters: the first (5,001) and the newest (5,001) fit 12,000; the next does not.
        ticket["read"]!.GetValue<string>().Should().Be("cut");
        ticket["cut"]!.GetValue<int>().Should().Be(3);
        ticket["messages"]!.AsArray().Select(m => m!["text"]!.GetValue<string>()[0]).Should().Equal(['1', '5']);
    }

    [Fact]
    public async Task AFirstMessageOverTheBudgetIsCutAtTheBudget()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/101" => Json(Ticket(101)),
            "/api/v2/tickets/101/comments" => Json(Comments(string.Join(",", Comment(1, 501, new string('y', 20_000)), Comment(2, 701, "Reply")))),
            _ => FakeResponse.NotFound(),
        });

        var ticket = (await Bulk(TestHelpers.BuildZendeskService(fake.Client), [101]))["tickets"]![0]!;

        ticket["read"]!.GetValue<string>().Should().Be("cut");
        ticket["cut"]!.GetValue<int>().Should().Be(1, "the reply no longer fits");
        var first = ticket["messages"]![0]!;
        first["text"]!.GetValue<string>().Should().HaveLength(TicketConversationsTools.TextBudget);
        first["text_cut"]!.GetValue<bool>().Should().BeTrue();
        ticket["note"]!.GetValue<string>().Should().Contain("text_cut");
    }

    [Fact]
    public async Task WithoutText_TheBudgetDoesNotCut()
    {
        // The negative control for the two cuts above: the budget is of text, so no text means no cut.
        var big = new string('x', 5000);
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/101" => Json(Ticket(101)),
            "/api/v2/tickets/101/comments" => Json(Comments(string.Join(",", Enumerable.Range(1, 5).Select(n => Comment(n, 501, big))))),
            _ => FakeResponse.NotFound(),
        });

        var ticket = (await Bulk(TestHelpers.BuildZendeskService(fake.Client), [101], fields: ["type"]))["tickets"]![0]!;

        ticket["read"]!.GetValue<string>().Should().Be("complete");
        ticket["messages"]!.AsArray().Should().HaveCount(5);
    }

    [Fact]
    public async Task EachFailure_IsReportedOnItsTicket_WithAReasonAndTheRetryList()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/404" => FakeResponse.NotFound(),
            "/api/v2/tickets/403" => FakeResponse.WithStatus(HttpStatusCode.Forbidden),
            "/api/v2/tickets/429" => FakeResponse.WithStatus(HttpStatusCode.TooManyRequests),
            "/api/v2/tickets/500/comments" => FakeResponse.WithStatus(HttpStatusCode.InternalServerError),
            "/api/v2/tickets/600/comments" => Json("""{"comments":[1],"meta":{"has_more":false}}"""),
            "/api/v2/tickets/422/comments" => FakeResponse.WithStatus(HttpStatusCode.UnprocessableEntity),
            _ => Simple(r),
        });

        var result = await Bulk(TestHelpers.BuildZendeskService(fake.Client), [101, 404, 403, 429, 500, 600, 422]);

        var tickets = result["tickets"]!.AsArray();
        tickets.Select(t => t!["ticket_id"]!.GetValue<long>()).Should().Equal([101L, 404L, 403L, 429L, 500L, 600L, 422L], "every ticket comes back once, in order");
        tickets[0]!["read"]!.GetValue<string>().Should().Be("complete", "the readable ticket is still returned");
        tickets.Skip(1).Select(t => t!["reason"]!.GetValue<string>())
            .Should().Equal(["not_found", "not_found", "rate_limited", "zendesk_error", "error", "error"], "a refusal such as 422 would be refused again");
        tickets.Skip(1).Should().OnlyContain(t => t!["read"]!.GetValue<string>() == "not_read" && t["messages"] == null);
        result["retry"]!.AsArray().Select(n => n!.GetValue<long>()).Should().Equal([429L, 500L], "not_found and error are not worth retrying");
        tickets[5]!["detail"]!.GetValue<string>().Should().Contain("could not be read");
        result["after_seconds"]!.GetValue<int>().Should().Be(60);
        fake.Requests.Should().NotContain(r => r.Path == "/api/v2/tickets/404/comments", "an unreadable ticket's conversation is not requested");
    }

    [Fact]
    public async Task TicketsNotReadInTime_AreReportedAsTime()
    {
        // The first answer outlasts the deadline, so every later step finds the time spent.
        var first = 0;
        using var fake = TestHelpers.CreateFakeZendesk(r =>
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                Thread.Sleep(300);
            }

            return Simple(r);
        });
        var request = TicketConversationsTools.BulkConversationRequest.Parse([101, 102], null, null, null, null, null);

        var result = await TicketConversationsTools.ReadAsync(TestHelpers.BuildZendeskService(fake.Client), request, TimeSpan.FromMilliseconds(50), TimeProvider.System, CancellationToken.None);

        result["tickets"]!.AsArray().Should().OnlyContain(t => t!["reason"]!.GetValue<string>() == "time");
        result["retry"]!.AsArray().Should().HaveCount(2);
        result["after_seconds"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public async Task TheDeadline_StopsPaging_WithoutCancellingTheRequestInFlight()
    {
        // The first comments page outlasts the deadline: it is still used (nothing was cancelled), but
        // the next page is not requested and the ticket reads as time.
        using var fake = TestHelpers.CreateFakeZendesk(r =>
        {
            if (r.Path.EndsWith("/comments", StringComparison.Ordinal))
            {
                Thread.Sleep(300);
                return Json(Comments(Comment(1, 501, "Page"), hasMore: true));
            }

            return Simple(r);
        });
        var request = TicketConversationsTools.BulkConversationRequest.Parse([101], null, null, null, null, null);

        var result = await TicketConversationsTools.ReadAsync(TestHelpers.BuildZendeskService(fake.Client), request, TimeSpan.FromMilliseconds(100), TimeProvider.System, CancellationToken.None);

        result["tickets"]![0]!["reason"]!.GetValue<string>().Should().Be("time");
        fake.Requests.Count(r => r.Path.EndsWith("/comments", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task AnHttpTimeout_IsReportedOnItsTicket_AndTheOthersAreKept()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/tickets/102/comments"
            ? throw new TaskCanceledException("timed out", new TimeoutException())
            : Simple(r));

        var result = await Bulk(TestHelpers.BuildZendeskService(fake.Client), [101, 102]);

        var tickets = result["tickets"]!.AsArray();
        tickets[0]!["read"]!.GetValue<string>().Should().Be("complete");
        tickets[1]!["reason"]!.GetValue<string>().Should().Be("time");
        result["retry"]!.AsArray().Select(n => n!.GetValue<long>()).Should().Equal([102L]);
    }

    [Fact]
    public async Task AMalformedPage_IsReportedOnItsTicket_AndTheOthersAreKept()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/102/comments" => Json("""{"comments":[],"meta":{"has_more":"yes"}}"""),
            "/api/v2/tickets/103" => Json("""[]"""),
            _ => Simple(r),
        });

        var tickets = (await Bulk(TestHelpers.BuildZendeskService(fake.Client), [101, 102, 103]))["tickets"]!.AsArray();

        tickets[0]!["read"]!.GetValue<string>().Should().Be("complete");
        tickets[1]!["reason"]!.GetValue<string>().Should().Be("error");
        tickets[2]!["reason"]!.GetValue<string>().Should().Be("error", "a ticket response that is not a ticket");
    }

    [Fact]
    public async Task TheCallersOwnCancellation_FailsTheCall_RatherThanReadingAsTime()
    {
        // Cancelled while the last ticket's comments are being paged, so the cancellation lands inside
        // the per-ticket handling, where it must not be mistaken for this call running out of time.
        using var cancelled = new CancellationTokenSource();
        using var fake = TestHelpers.CreateFakeZendesk(r =>
        {
            if (r.Path.EndsWith("/comments", StringComparison.Ordinal) && r.Query("page[after]") is null)
            {
                cancelled.Cancel();
                return Json(Comments(Comment(1, 501, "First page"), hasMore: true));
            }

            // The next page's request sees the cancellation, as a real socket handler would.
            return r.Path.EndsWith("/comments", StringComparison.Ordinal) ? throw new TaskCanceledException() : Simple(r);
        });
        var request = TicketConversationsTools.BulkConversationRequest.Parse([101], null, null, null, null, null);

        var act = () => TicketConversationsTools.ReadAsync(TestHelpers.BuildZendeskService(fake.Client), request, TimeSpan.FromMinutes(1), TimeProvider.System, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AMissingConnection_FailsTheCallWithTheLink_RatherThanReadingAsNotRead()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => Simple(r));
        var (service, _) = TestHelpers.BuildUserZendeskService(fake.Client, token: null);

        var act = () => TicketConversationsTools.BulkGetTicketConversations(service, [101, 102]);

        await act.Should().ThrowAsync<ZendeskConnectionRequiredException>();
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordsEveryTicketReadInTheAudit()
    {
        // The audit record keeps at most 100 ids, and each conversation here returns 100 comment ids.
        // Every ticket id must still be in it: the trail is the only record of who read which ticket.
        var context = new ToolCallAuditContext();
        using var fake = TestHelpers.CreateFakeZendesk(r => Simple(r, count: 100));
        var ids = Enumerable.Range(1, 10).Select(n => (long)n).ToArray();

        await Bulk(TestHelpers.BuildZendeskService(fake.Client, auditContext: context), ids);

        context.Summarise().Ids.Should().Contain(ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task AMessagingTicket_ReadsTheLog_AndReportsTheAiAgentAndHandover()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/5" => Json(Ticket(5, "native_messaging")),
            "/api/v2/tickets/5/conversation_log" => Json("""
                {"events":[
                  {"created_at":"2026-01-01T10:00:00Z","author":{"type":"user","display_name":"Bob","zen:support:user_id":7},"content":{"type":"text","body":"Hello"}},
                  {"created_at":"2026-01-01T10:00:05Z","author":{"type":"business","display_name":"Bot"},"content":{"type":"text","body":"How can I help?"}},
                  {"created_at":"2026-01-01T10:01:00Z","author":{"type":"user","display_name":"Bob","zen:support:user_id":7},"content":{"type":"text","body":"A human please"}},
                  {"created_at":"2026-01-01T10:05:00Z","author":{"type":"agent","display_name":"Ann","zen:support:user_id":8},"content":{"type":"text","body":"Hi, Ann here"}}],
                 "meta":{"has_more":false}}
                """),
            _ => FakeResponse.NotFound(),
        });
        var request = TicketConversationsTools.BulkConversationRequest.Parse([5], ["conversation_stats"], null, null, null, null);
        var asOf = DateTimeOffset.Parse("2026-01-01T11:01:00Z", CultureInfo.InvariantCulture);

        var result = await TicketConversationsTools.ReadAsync(TestHelpers.BuildZendeskService(fake.Client), request, TimeSpan.FromMinutes(1), new FixedClock(asOf), CancellationToken.None);

        var ticket = result["tickets"]![0]!;
        ticket["source"]!.GetValue<string>().Should().Be("conversation_log");
        var messages = ticket["messages"]!.AsArray();
        messages.Select(m => m!["side"]!.GetValue<string>()).Should().Equal(["customer", "ai_agent", "customer", "agent"]);
        messages[2]!["handover"]!.GetValue<bool>().Should().BeTrue("the message just before the human took over");
        messages[1]!.AsObject().ContainsKey("handover").Should().BeFalse();

        var ai = ticket["ai_agent"]!;
        ai["kind"]!.GetValue<string>().Should().Be("handed_over");
        ai["basis"]!.GetValue<string>().Should().Be("zendesk");
        ai["handover"]!["at"]!.GetValue<string>().Should().Be("2026-01-01T10:05:00Z");
        ai["handover"]!["by_user_id"]!.GetValue<long>().Should().Be(8);
        ai["handover"]!["after_message"]!["text"]!.GetValue<string>().Should().Be("A human please");

        var stats = ticket["conversation_stats"]!;
        stats["messages"]!["ai_agent"]!.GetValue<int>().Should().Be(1);
        stats["messages"]!["customer"]!.GetValue<int>().Should().Be(2);
        stats["customer_last_wrote_at"]!.GetValue<string>().Should().Be("2026-01-01T10:01:00Z");
        stats["seconds_since_customer_wrote"]!.GetValue<long>().Should().Be(3600);
        stats["human_agent_replies"]!.GetValue<int>().Should().Be(1);
        stats["ai_agent"]!["customer_wrote_after_last"]!.GetValue<bool>().Should().BeTrue();
        stats["last_public_message"]!["side"]!.GetValue<string>().Should().Be("agent");
        result["as_of"]!.GetValue<string>().Should().Be("2026-01-01T11:01:00Z");
    }

    [Fact]
    public async Task WithoutAnAiAgent_ThereIsNoAiAgentObject_AndStatsComeFromTheWholeConversation()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/101" => Json(Ticket(101)),
            "/api/v2/tickets/101/comments" => Json(Comments(string.Join(",",
                Comment(1, 501, "Q"), Comment(2, 701, "Note", isPublic: false), Comment(3, 701, "A")))),
            _ => FakeResponse.NotFound(),
        });

        var ticket = (await Bulk(TestHelpers.BuildZendeskService(fake.Client), [101], include: ["conversation_stats"], lastN: 0))["tickets"]![0]!;

        ticket.AsObject().ContainsKey("ai_agent").Should().BeFalse();
        var stats = ticket["conversation_stats"]!;
        stats["internal_notes"]!.GetValue<int>().Should().Be(1, "stats are worked out before the last_n_messages filter");
        stats["messages"]!["agent"]!.GetValue<int>().Should().Be(2);
        stats["human_agent_replied"]!.GetValue<bool>().Should().BeTrue();
        stats["human_agent_replies"]!.GetValue<int>().Should().Be(1, "an internal note is not a reply");
        stats["ai_agent"].Should().BeNull();
    }

    [Fact]
    public async Task ARepeatedId_IsReadOnce()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => Simple(r));

        var result = await Bulk(TestHelpers.BuildZendeskService(fake.Client), [101, 101, 102]);

        result["ticket_count"]!.GetValue<int>().Should().Be(2);
        fake.Requests.Count(r => r.Path == "/api/v2/tickets/101").Should().Be(1);
    }

    [Fact]
    public async Task OnAnEmailTicket_TheAiAgentIsInferred_AndAnInternalNoteIsNotTheHandover()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/101" => Json(Ticket(101)),
            "/api/v2/tickets/101/comments" => Json(Comments(string.Join(",",
                Comment(1, 501, "Question"), Comment(2, -1, "AI answer", via: "api"),
                Comment(3, 701, "Looking into it", isPublic: false, via: "web"), Comment(4, 501, "Still stuck"),
                Comment(5, 701, "Here is the fix", via: "web")))),
            _ => FakeResponse.NotFound(),
        });

        var ticket = (await Bulk(TestHelpers.BuildZendeskService(fake.Client), [101]))["tickets"]![0]!;

        var ai = ticket["ai_agent"]!;
        ai["basis"]!.GetValue<string>().Should().Be("inferred", "comments carry no Zendesk AI flag");
        ai["kind"]!.GetValue<string>().Should().Be("handed_over");
        ai["handover"]!["at"]!.GetValue<string>().Should().Be("2026-01-01T00:00:05Z", "the public reply, not the internal note");
        ai["handover"]!["after_message"]!["text"]!.GetValue<string>().Should().Be("Still stuck");
        var messages = ticket["messages"]!.AsArray();
        messages.Where(m => m!["handover"] is not null).Should().ContainSingle().Which!["text"]!.GetValue<string>().Should().Be("Still stuck");
    }

    [Fact]
    public async Task AConversationLongerThanThePagerReads_IsCut_NotComplete()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path.EndsWith("/comments", StringComparison.Ordinal)
            ? Json(Comments(Comment(1, 501, "Page one"), hasMore: true))
            : Simple(r));

        var ticket = (await Bulk(TestHelpers.BuildZendeskService(fake.Client, maxAutoPageFetches: 1), [101]))["tickets"]![0]!;

        ticket["read"]!.GetValue<string>().Should().Be("cut", "messages exist beyond what was read");
        ticket["cut"]!.GetValue<int>().Should().Be(0, "nothing read was left out");
        ticket["note"]!.GetValue<string>().Should().Contain("earliest 1 comments");
    }

    [Fact]
    public async Task AMessagingLogThatFails_FallsBackToComments_AndSaysSo_ButARateLimitIsReported()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/5" => Json(Ticket(5, "whatsapp")),
            "/api/v2/tickets/5/conversation_log" => FakeResponse.WithStatus(HttpStatusCode.BadGateway),
            "/api/v2/tickets/5/comments" => Json(Comments(Comment(1, 501, "Hi"))),
            "/api/v2/tickets/6" => Json(Ticket(6, "whatsapp")),
            "/api/v2/tickets/6/conversation_log" => FakeResponse.WithStatus(HttpStatusCode.TooManyRequests),
            _ => FakeResponse.NotFound(),
        });

        var tickets = (await Bulk(TestHelpers.BuildZendeskService(fake.Client), [5, 6]))["tickets"]!.AsArray();

        tickets[0]!["source"]!.GetValue<string>().Should().Be("comments");
        tickets[0]!["note"]!.GetValue<string>().Should().Contain("conversation log could not be read");
        tickets[1]!["reason"]!.GetValue<string>().Should().Be("rate_limited", "a 429 is not an absent log");
        fake.Requests.Should().NotContain(r => r.Path == "/api/v2/tickets/6/comments");
    }

    [Fact]
    public async Task AnRbacDenial_FailsTheCall_RatherThanReadingAsNotRead()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => Simple(r));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client, authorizer: TestHelpers.BuildEnabledAuthorizer());

        var act = () => Bulk(zendesk, [101, 102]);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AThirdConcurrentCall_IsRefusedWithoutReading()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => Simple(r));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);
        using (var first = TicketConversationsTools.Gate.TryEnter())
        using (var second = TicketConversationsTools.Gate.TryEnter())
        {
            first.Should().NotBeNull();
            second.Should().NotBeNull();

            var refused = await Bulk(zendesk, [101]);

            refused["error"]!.GetValue<string>().Should().Contain("try again");
            refused["retry"]!.AsArray().Select(n => n!.GetValue<long>()).Should().Equal([101L]);
            fake.Requests.Should().BeEmpty();
        }

        // The control: with the slots released, the same call reads.
        (await Bulk(zendesk, [101]))["tickets"]![0]!["read"]!.GetValue<string>().Should().Be("complete");
    }

    [Theory]
    [InlineData("ticket_ids", new long[0], null, null, null, null)]
    [InlineData("ticket_ids", new long[] { 1, 0 }, null, null, null, null)]
    [InlineData("ticket_ids", new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, null, null, null, null)]
    [InlineData("last_n_messages", new long[] { 1 }, -1, null, null, null)]
    [InlineData("last_n_messages", new long[] { 1 }, 101, null, null, null)]
    [InlineData("include", new long[] { 1 }, null, "stats", null, null)]
    [InlineData("message_fields", new long[] { 1 }, null, null, "body", null)]
    [InlineData("sides", new long[] { 1 }, null, null, null, "bot")]
    public async Task RejectsInvalidArgumentsBeforeCallingZendesk(string parameter, long[] ids, int? lastN, string? include, string? field, string? side)
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => Simple(r));
        var act = () => TicketConversationsTools.BulkGetTicketConversations(TestHelpers.BuildZendeskService(fake.Client), ids,
            include is null ? null : [include], lastN, field is null ? null : [field], null, side is null ? null : [side]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage($"*{parameter}*");
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AcceptsTheLimits()
    {
        // The positive control for the rejections above.
        using var fake = TestHelpers.CreateFakeZendesk(r => Simple(r));
        var ids = Enumerable.Range(1, 10).Select(n => (long)n).ToArray();

        var result = await Bulk(TestHelpers.BuildZendeskService(fake.Client), ids, ["conversation_stats"], 100,
            ["text", "attachments", "author", "type"], true, ["customer", "agent", "ai_agent", "system"]);

        result["ticket_count"]!.GetValue<int>().Should().Be(10);
        result["tickets"]!.AsArray().Should().OnlyContain(t => t!["read"]!.GetValue<string>() == "complete");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
