using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp;

namespace ZendeskMcp.Tests;

/// <summary>
/// Wiring tests for the audit trail's record-id capture (vitally-mcp#147). These exist because the capture point
/// is easy to put in the wrong place and the wrong place looks correct.
/// </summary>
public class ZendeskServiceAuditTests
{
    [Fact]
    public async Task GetAsync_RecordsTheReturnedIds_EvenWhenTheToolThenShapesThemAway()
    {
        // The trap: tools reshape Zendesk's response *after* the upstream call — a narrow projection,
        // a summary, a count — so the result the client sees may carry no `id` at all. Capturing ids
        // from the tool's result rather than the raw response would therefore name nobody on exactly
        // the calls where the result was narrowed — a silent gap that looks like a complete record.
        using var client = TestHelpers.CreateMockHttpClient(
            """{"organizations":[{"id":601,"name":"A"},{"id":602,"name":"B"}]}""");
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildZendeskService(client, auditContext: context);

        var result = await service.GetAsync("/api/v2/organizations");

        // What a tool asking for names only would do with the response.
        foreach (var org in result["organizations"]!.AsArray())
        {
            org!.AsObject().Remove("id");
        }

        result.ToJsonString().Should().NotContain("601", "the shaped result carries no ids");
        // NB `Equal(params string[])` would swallow a `because` argument as another expected
        // element, so the reason goes here: the audit record still names the organisations read.
        context.Summarise().Ids.Should().Equal(["601", "602"]);
    }

    [Fact]
    public async Task GetAllPagesAsync_MarksThePagerTruncation_SoTheRecordDoesNotImplyACompleteRead()
    {
        // `truncated` means the pager gave up at MaxAutoPageFetches, so the number of MATCHING
        // records is unknowable — Zendesk's cursor envelope exposes only `meta.has_more`, there is no
        // total. A record that omitted this would imply RecordsFetched was everything that matched.
        var page = """{"tickets":[{"id":101,"subject":"Login fails"}],"meta":{"has_more":true,"after_cursor":"more"}}""";
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(page, page, page);
        using var _client = client;
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildZendeskService(client, auditContext: context, maxAutoPageFetches: 2);

        await service.GetAllPagesAsync("/api/v2/tickets", null, "tickets", maxItems: 1000);

        context.Summarise().Truncated.Should().BeTrue(
            "the page cap was hit before the result set was exhausted");
    }

    [Fact]
    public async Task GetAllPagesAsync_MarksTheTruncation_WhenThePageCapAndTheItemCapAreReachedTogether()
    {
        // Ten full pages of 100 with more to come, against maxItems 1,000 and the default ten-page cap:
        // both caps land on the same page. The read is incomplete because the page cap was reached, so
        // the record must say so even though the item cap was reached at the same moment.
        var pages = Enumerable.Range(0, 11).Select(n => FullPage(n, 100, hasMore: true)).ToArray();
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(pages);
        using var _client = client;
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildZendeskService(client, auditContext: context);

        var result = await service.GetAllPagesAsync("/api/v2/tickets", null, "tickets", maxItems: 1000);

        result.Items.Should().HaveCount(1000);
        result.PagesFetched.Should().Be(10);
        result.HasMore.Should().BeTrue();
        context.Summarise().Truncated.Should().BeTrue("the page cap was reached with more pages to come");
    }

    [Fact]
    public async Task GetAllPagesAsync_DoesNotMarkTheTruncation_WhenOnlyTheCallersItemCapStopsIt()
    {
        // The item cap is the caller's own choice and the caller reports it (HasMore); the audit flag
        // means the page cap. Three full pages against maxItems 300 and a ten-page cap.
        var pages = Enumerable.Range(0, 4).Select(n => FullPage(n, 100, hasMore: true)).ToArray();
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(pages);
        using var _client = client;
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildZendeskService(client, auditContext: context);

        var result = await service.GetAllPagesAsync("/api/v2/tickets", null, "tickets", maxItems: 300);

        result.PagesFetched.Should().Be(3);
        result.HasMore.Should().BeTrue();
        context.Summarise().Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task GetAllPagesAsync_DoesNotMarkTheTruncation_WhenTheLastPageIsAlsoTheCap()
    {
        // Exactly ten full pages and then nothing more: the endpoint was exhausted on the cap's page.
        var pages = Enumerable.Range(0, 10).Select(n => FullPage(n, 100, hasMore: n < 9)).ToArray();
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(pages);
        using var _client = client;
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildZendeskService(client, auditContext: context);

        var result = await service.GetAllPagesAsync("/api/v2/tickets", null, "tickets", maxItems: 1000);

        result.Items.Should().HaveCount(1000);
        result.HasMore.Should().BeFalse();
        context.Summarise().Truncated.Should().BeFalse();
    }

    // A page of `size` tickets, each with its own id, saying whether another page follows.
    private static string FullPage(int number, int size, bool hasMore) =>
        "{\"tickets\":[" + string.Join(',', Enumerable.Range(number * size + 1, size).Select(id => $"{{\"id\":{id}}}"))
        + "],\"meta\":{\"has_more\":" + (hasMore ? "true" : "false") + ",\"after_cursor\":" + (hasMore ? "\"more\"" : "null") + "}}";

    [Fact]
    public async Task DeleteAsync_NamesTheRecord_FromTheUrlWhenTheBodyCarriesNoId()
    {
        // The acceptance criterion is "accessed, MODIFIED OR DELETED data for these customers", and a
        // delete is the event an access record is most often kept for. Zendesk answers one with an
        // empty 204, so the body yields no id and the primary record named nobody — even though the
        // id was right there in the request URL the whole time.
        using var client = TestHelpers.CreateMockHttpClient("");
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildZendeskService(client, auditContext: context);

        await service.DeleteAsync("/api/v2/tickets/123");

        var summary = context.Summarise();
        summary.Ids.Should().Equal(["123"], "the record must name what was deleted");
        summary.CallsWithoutIds.Should().Be(0, "this is not a gap — the id was recoverable");
    }

    [Fact]
    public async Task ListCalls_DoNotInventAnIdFromTheCollectionName()
    {
        // The counterpart, and the reason this is method-scoped rather than a general URL fallback:
        // an unscoped list ends `/api/v2/organizations`, whose last segment is a collection name.
        // Treating that as a record id would put "organizations" in the trail as though it were a
        // customer — a fabricated identifier is far worse than an honest gap.
        using var client = TestHelpers.CreateMockHttpClient("""{"nothing":true}""");
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildZendeskService(client, auditContext: context);

        await service.GetAsync("/api/v2/organizations");

        context.Summarise().Ids.Should().BeEmpty("no id is better than a made-up one");
    }

    [Fact]
    public async Task PutAsync_DoesNotInventAnIdFromANonNumericLastSegment()
    {
        // Zendesk mutation paths also end in locales and actions (`…/translations/en-gb`,
        // `…/update_many`), so only a numeric last segment is taken as a record id.
        using var client = TestHelpers.CreateMockHttpClient("");
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildZendeskService(client, auditContext: context);

        await service.PutAsync("/api/v2/help_center/articles/360001/translations/en-gb", new JsonObject());

        context.Summarise().Ids.Should().BeEmpty("a locale is not a record id");
    }

    [Fact]
    public async Task DeleteAsync_NamesTheRecord_EvenWhenTheBodyIsAnEmptyEnvelope()
    {
        // The URL fallback was gated on IdsAvailable, and an EMPTY result set is deliberately marked
        // available — that rule exists so a "no matches" search does not look like a broken record.
        // But a mutation answering with `{tickets:[]}` is an acknowledgement, not an empty search, so
        // the gate skipped the fallback and the delete named nobody again by a different route.
        using var client = TestHelpers.CreateMockHttpClient("""{"tickets":[]}""");
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildZendeskService(client, auditContext: context);

        await service.DeleteAsync("/api/v2/tickets/124");

        context.Summarise().Ids.Should().Equal(["124"]);
    }
}
