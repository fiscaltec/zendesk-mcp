using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

/// <summary>Get_satisfaction_ratings (legacy CSAT) against a fake Zendesk.</summary>
public class SatisfactionToolsTests
{
    private const string Page = """
        {
          "satisfaction_ratings": [
            {"id": 11, "url": "https://acme.zendesk.com/api/v2/satisfaction_ratings/11.json", "score": "good", "comment": "Great, thanks", "reason": null, "ticket_id": 101, "requester_id": 501, "assignee_id": 701, "group_id": 7, "created_at": "2026-03-02T10:00:00Z", "updated_at": "2026-03-02T10:00:00Z"},
            {"id": 12, "score": "bad", "comment": "=cmd|' /C calc'!A0", "reason": "Issue not resolved", "ticket_id": 102, "created_at": "2026-03-03T10:00:00Z"}
          ],
          "meta": {"has_more": true, "after_cursor": "next1"}
        }
        """;

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    [Fact]
    public async Task Json_BuildsLegacyQueryAndPaginationInfo()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json(Page));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = Parse(await SatisfactionTools.GetSatisfactionRatings(zendesk, score: "Good", start_date: "2026-03-01", end_date: "2026-03-31", page_size: 50, after_cursor: "abc"));

        var request = fake.Requests.Single();
        request.Method.Should().Be(HttpMethod.Get);
        request.Path.Should().Be("/api/v2/satisfaction_ratings");
        request.Query("score").Should().Be("good");
        request.Query("start_time").Should().Be("1772323200"); // 2026-03-01T00:00:00Z
        request.Query("end_time").Should().Be("1775001599"); // 2026-03-31T23:59:59Z
        request.Query("page[size]").Should().Be("50");
        request.Query("page[after]").Should().Be("abc");

        result["csat_system"]!.GetValue<string>().Should().Be("legacy");
        result["satisfaction_ratings"]!.AsArray().Should().HaveCount(2);
        result["satisfaction_ratings"]![0]!["comment"]!.GetValue<string>().Should().Be("Great, thanks");
        result["pagination_info"]!["has_more"]!.GetValue<bool>().Should().BeTrue();
        result["pagination_info"]!["next_cursor"]!.GetValue<string>().Should().Be("next1");
        result["pagination_info"]!["page_size"]!.GetValue<int>().Should().Be(50);
        result["notes"]![0]!.GetValue<string>().Should().Contain("Count_tickets");
    }

    [Fact]
    public async Task Csv_PagesThroughAndEscapes()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => FakeResponse.Json(r.Query("page[after]") is null
            ? Page
            : """{"satisfaction_ratings":[{"id":13,"score":"offered","ticket_id":103}],"meta":{"has_more":false,"after_cursor":null}}"""));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = Parse(await SatisfactionTools.GetSatisfactionRatings(zendesk, output_format: "csv"));

        fake.Requests.Should().HaveCount(2);
        fake.Requests[1].Query("page[after]").Should().Be("next1");
        result["row_count"]!.GetValue<int>().Should().Be(3);
        result["truncated"]!.GetValue<bool>().Should().BeFalse();
        var lines = result["csv"]!.GetValue<string>().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("id,url,score,comment,reason,");
        lines[1].Should().StartWith("11,https://acme.zendesk.com/api/v2/satisfaction_ratings/11.json,good,\"Great, thanks\",,");
        lines[2].Should().Contain(",bad,'=cmd|' /C calc'!A0,Issue not resolved,", "a formula in customer text is neutralised");
        lines[3].Should().StartWith("13,,offered,");
    }

    [Fact]
    public async Task TicketId_ReadsTheTicketsRating()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/101" => FakeResponse.Json("""{"ticket":{"id":101,"satisfaction_rating":{"id":11,"score":"good"}}}"""),
            "/api/v2/satisfaction_ratings/11" => FakeResponse.Json("""{"satisfaction_rating":{"id":11,"score":"good","ticket_id":101,"created_at":"2026-03-02T10:00:00Z"}}"""),
            _ => FakeResponse.NotFound(),
        });
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var good = Parse(await SatisfactionTools.GetSatisfactionRatings(zendesk, ticket_id: 101));
        var bad = Parse(await SatisfactionTools.GetSatisfactionRatings(zendesk, ticket_id: 101, score: "bad"));
        var outOfRange = Parse(await SatisfactionTools.GetSatisfactionRatings(zendesk, ticket_id: 101, start_date: "2026-03-05"));

        good["satisfaction_ratings"]!.AsArray().Should().ContainSingle().Which!["id"]!.GetValue<long>().Should().Be(11);
        good.AsObject().ContainsKey("pagination_info").Should().BeFalse();
        bad["satisfaction_ratings"]!.AsArray().Should().BeEmpty();
        outOfRange["satisfaction_ratings"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task TicketId_WithoutRating_IsEmpty()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("""{"ticket":{"id":5,"satisfaction_rating":null}}"""));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = Parse(await SatisfactionTools.GetSatisfactionRatings(zendesk, ticket_id: 5));

        result["satisfaction_ratings"]!.AsArray().Should().BeEmpty();
        fake.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("great", null, null, null, null, "*score*")]
    [InlineData(null, "xml", null, null, null, "*output_format*")]
    [InlineData(null, null, 0, null, null, "*page_size*")]
    [InlineData(null, null, null, "03/01/2026", null, "*start_date*")]
    [InlineData(null, null, null, "2026-03-05", "2026-03-01", "*before*")]
    public async Task Validates(string? score, string? format, int? pageSize, string? start, string? end, string message)
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json(Page));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => SatisfactionTools.GetSatisfactionRatings(zendesk, score: score, output_format: format, page_size: pageSize, start_date: start, end_date: end);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Csv_RefusesPagingParameters()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json(Page));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => SatisfactionTools.GetSatisfactionRatings(zendesk, output_format: "csv", after_cursor: "x");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*csv*");
    }

    [Theory]
    [InlineData("2026-03-01", false, 1772323200L)]
    [InlineData("2026-03-01", true, 1772409599L)]
    [InlineData("2026-03-01T12:00:00Z", false, 1772366400L)]
    [InlineData("2026-03-01T12:00:00+01:00", true, 1772362800L)]
    public void UnixSeconds_DayBoundsAndTimes(string value, bool endOfDay, long expected)
    {
        SatisfactionTools.UnixSeconds(value, "start_date", endOfDay).Should().Be(expected);
    }
}
