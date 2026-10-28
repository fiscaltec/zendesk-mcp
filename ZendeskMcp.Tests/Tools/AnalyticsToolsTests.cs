using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

/// <summary>Get_ticket_metrics_summary, Get_sla_summary and Get_sla_queue against a fake Zendesk.</summary>
public class AnalyticsToolsTests
{
    private const string Me = """{"user":{"id":900,"iana_time_zone":"Europe/London"}}""";

    private static string Export(params long[] ids) =>
        new JsonObject
        {
            ["results"] = new JsonArray(ids.Select(id => (JsonNode?)new JsonObject { ["id"] = id }).ToArray()),
            ["meta"] = new JsonObject { ["has_more"] = false, ["after_cursor"] = null },
        }.ToJsonString();

    private static readonly string ShowManyWithMetrics = """
        {
          "tickets": [
            {"id": 1, "status": "solved", "created_at": "2026-03-02T09:00:00Z", "group_id": 7, "via": {"channel": "email"}},
            {"id": 2, "status": "open",   "created_at": "2026-03-03T09:00:00Z", "group_id": null, "via": {"channel": "web"}},
            {"id": 3, "status": "closed", "created_at": "2026-03-03T10:00:00Z", "group_id": 7, "via": {"channel": "email"}}
          ],
          "metric_sets": [
            {"ticket_id": 1, "reopens": 0, "replies": 1, "solved_at": "2026-03-04T09:00:00Z",
             "reply_time_in_minutes": {"calendar": 10, "business": 5},
             "full_resolution_time_in_minutes": {"calendar": 100, "business": 50}},
            {"ticket_id": 2, "reopens": 0, "replies": 2,
             "reply_time_in_minutes": {"calendar": 30, "business": 30}},
            {"ticket_id": 3, "reopens": 1, "replies": 0, "solved_at": "2026-03-05T09:00:00Z",
             "reply_time_in_minutes": {"calendar": null, "business": null},
             "full_resolution_time_in_minutes": {"calendar": 300, "business": 200}}
          ]
        }
        """;

    private static FakeResponse Route(RecordedRequest r) => r.Path switch
    {
        "/api/v2/users/me" => FakeResponse.Json(Me),
        "/api/v2/search/export" => FakeResponse.Json(Export(1, 2, 3)),
        "/api/v2/tickets/show_many" => FakeResponse.Json(ShowManyWithMetrics),
        "/api/v2/groups" => FakeResponse.Json("""{"groups":[{"id":7,"name":"Support"}],"meta":{"has_more":false}}"""),
        _ => FakeResponse.NotFound(),
    };

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    // ---- Get_ticket_metrics_summary ----

    [Fact]
    public async Task MetricsSummary_RequiresAStartDate()
    {
        using var fake = TestHelpers.CreateFakeZendesk(Route);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => AnalyticsTools.GetTicketMetricsSummary(zendesk, status: "open");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*start_date*solved_start_date*");
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task MetricsSummary_SearchesThenFetchesMetricSets()
    {
        using var fake = TestHelpers.CreateFakeZendesk(Route);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = Parse(await AnalyticsTools.GetTicketMetricsSummary(zendesk, start_date: "2026-03-01", end_date: "2026-03-07", assignee: "Susan Warren"));

        var export = fake.Requests.Single(r => r.Path == "/api/v2/search/export");
        export.Method.Should().Be(HttpMethod.Get);
        export.Query("query").Should().Be("type:ticket assignee:\"Susan Warren\" created>=2026-03-01 created<=2026-03-07");
        export.Query("filter[type]").Should().Be("ticket");
        export.Query("page[size]").Should().Be("100");
        var showMany = fake.Requests.Single(r => r.Path == "/api/v2/tickets/show_many");
        showMany.Query("ids").Should().Be("1,2,3");
        showMany.Query("include").Should().Be("metric_sets");
        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);

        result["date_basis"]!.GetValue<string>().Should().Be("created");
        result["timezone"]!.GetValue<string>().Should().Be("Europe/London");
        result["search_url"]!.GetValue<string>().Should().StartWith("https://acme.zendesk.com/agent/search/1?type=ticket&q=");
        result["tickets"]!["total"]!.GetValue<int>().Should().Be(3);
        result["tickets"]!["solved"]!.GetValue<int>().Should().Be(2);
        result["first_reply_time"]!["calendar"]!["n"]!.GetValue<int>().Should().Be(2);
        result["first_reply_time"]!["calendar"]!["median"]!.GetValue<double>().Should().Be(20);
        result["full_resolution_time"]!["calendar"]!["avg"]!.GetValue<double>().Should().Be(200);
        result["full_resolution_time"]!["business"]!["p90"]!.GetValue<double>().Should().Be(200);
        result["reopens"]!["reopen_rate_pct"]!.GetValue<double>().Should().Be(50);
        result["one_touch"]!["tickets"]!.GetValue<int>().Should().Be(1);
        result["one_touch"]!["zero_touch_tickets"]!.GetValue<int>().Should().Be(1);
        result["groups"].Should().BeNull();
        result["trend"].Should().BeNull();
    }

    [Fact]
    public async Task MetricsSummary_FollowsExportCursor()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/search/export"
            ? FakeResponse.Json(r.Query("page[after]") is null
                ? """{"results":[{"id":1},{"id":2}],"meta":{"has_more":true,"after_cursor":"c2"}}"""
                : """{"results":[{"id":3}],"meta":{"has_more":false,"after_cursor":null}}""")
            : Route(r));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await AnalyticsTools.GetTicketMetricsSummary(zendesk, solved_start_date: "2026-03-01");

        fake.Requests.Where(r => r.Path == "/api/v2/search/export").Select(r => r.Query("page[after]")).Should().Equal(null, "c2");
        fake.Requests.Single(r => r.Path == "/api/v2/tickets/show_many").Query("ids").Should().Be("1,2,3");
    }

    [Fact]
    public async Task MetricsSummary_OverTheCap_ComputesNothing()
    {
        using var fake = TestHelpers.CreateFakeZendesk(Route);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client, configure: o => o.MaxAnalyticsTickets = 2);

        var result = Parse(await AnalyticsTools.GetTicketMetricsSummary(zendesk, start_date: "2026-03-01"));

        result["too_many_tickets"]!.GetValue<bool>().Should().BeTrue();
        result["limit"]!.GetValue<int>().Should().Be(2);
        result["note"]!.GetValue<string>().Should().Contain("Split the date range");
        fake.Requests.Should().NotContain(r => r.Path == "/api/v2/tickets/show_many");
    }

    [Fact]
    public async Task MetricsSummary_GroupByGroup_ResolvesNamesAndNone()
    {
        using var fake = TestHelpers.CreateFakeZendesk(Route);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = Parse(await AnalyticsTools.GetTicketMetricsSummary(zendesk, start_date: "2026-03-01", group_by: "group"));

        var groups = result["groups"]!.AsArray();
        groups.Should().HaveCount(2);
        groups[0]!["key"]!.GetValue<string>().Should().Be("7");
        groups[0]!["name"]!.GetValue<string>().Should().Be("Support");
        groups[0]!["tickets"]!["total"]!.GetValue<int>().Should().Be(2);
        groups[1]!["key"].Should().BeNull();
        groups[1]!["name"]!.GetValue<string>().Should().Be("(none)");
    }

    [Fact]
    public async Task MetricsSummary_SolvedBasisTrend_ByDay()
    {
        using var fake = TestHelpers.CreateFakeZendesk(Route);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = Parse(await AnalyticsTools.GetTicketMetricsSummary(zendesk, solved_start_date: "2026-03-03", solved_end_date: "2026-03-05", trend: true, group_by: "channel"));

        result["date_basis"]!.GetValue<string>().Should().Be("solved");
        var rows = result["trend"]!["rows"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        rows.Should().HaveCount(3);
        rows[1].Should().StartWith("2026-03-04,1,1,");
        rows[2].Should().StartWith("2026-03-05,1,1,1,1,0,0,1,");
        result["groups"]![0]!["trend"]!["rows"]!.AsArray().Should().HaveCount(3);
    }

    [Theory]
    [InlineData("custom_field", null, "*group_by_field_id*")]
    [InlineData("group", 5L, "*only used with group_by custom_field*")]
    [InlineData("colour", null, "*group_by must be one of*")]
    public async Task MetricsSummary_ValidatesGroupBy(string groupBy, long? fieldId, string message)
    {
        using var fake = TestHelpers.CreateFakeZendesk(Route);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => AnalyticsTools.GetTicketMetricsSummary(zendesk, start_date: "2026-03-01", group_by: groupBy, group_by_field_id: fieldId);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
    }

    [Fact]
    public async Task MetricsSummary_TrendLimitedTo31Days()
    {
        using var fake = TestHelpers.CreateFakeZendesk(Route);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => AnalyticsTools.GetTicketMetricsSummary(zendesk, start_date: "2026-01-01", end_date: "2026-02-01", trend: true);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*31 days*");
        fake.Requests.Should().NotContain(r => r.Path == "/api/v2/search/export");
    }

    [Fact]
    public async Task MetricsSummary_CustomFieldGroups_UseOptionNames()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/show_many" => FakeResponse.Json("""
                {"tickets":[
                  {"id":1,"status":"open","created_at":"2026-03-02T09:00:00Z","custom_fields":[{"id":55,"value":["opt_a","opt_b"]}]},
                  {"id":2,"status":"open","created_at":"2026-03-02T09:00:00Z","custom_fields":[{"id":55,"value":["opt_a"]}]}],
                 "metric_sets":[]}
                """),
            "/api/v2/ticket_fields/55" => FakeResponse.Json("""{"ticket_field":{"id":55,"type":"multiselect","custom_field_options":[{"name":"Option A","value":"opt_a"},{"name":"Option B","value":"opt_b"}]}}"""),
            _ => Route(r),
        });
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = Parse(await AnalyticsTools.GetTicketMetricsSummary(zendesk, start_date: "2026-03-01", group_by: "custom_field", group_by_field_id: 55));

        var groups = result["groups"]!.AsArray();
        groups.Select(g => $"{g!["name"]}={g["tickets"]!["total"]}").Should().Equal("Option A=2", "Option B=1");
        result["notes"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain(n => n.Contains("no metric set"));
    }

    // ---- Get_sla_summary ----

    private static string SlaTicketJson(long id, string targetsEvents, string status = "open") => $$$"""
        {"ticket":{"id":{{{id}}},"subject":"Ticket {{{id}}}","status":"{{{status}}}","created_at":"2026-03-02T09:00:00Z","group_id":7,"assignee_id":50},
         "metric_events":{"reply_time":[{{{targetsEvents}}}]}}
        """;

    private const string AchievedEvents = """
        {"metric":"reply_time","instance_id":1,"type":"apply_sla","time":"2026-03-02T09:00:00Z","sla":{"target":60,"business_hours":false,"policy":{"id":3,"title":"Gold"}}},
        {"metric":"reply_time","instance_id":1,"type":"fulfill","time":"2026-03-02T09:30:00Z"}
        """;

    private const string BreachedEvents = """
        {"metric":"reply_time","instance_id":1,"type":"apply_sla","time":"2026-03-03T09:00:00Z","sla":{"target":60,"business_hours":false,"policy":{"id":3,"title":"Gold"}}},
        {"metric":"reply_time","instance_id":1,"type":"breach","time":"2026-03-03T10:00:00Z"},
        {"metric":"reply_time","instance_id":1,"type":"fulfill","time":"2026-03-03T10:20:00Z"}
        """;

    private static FakeResponse SlaRoute(RecordedRequest r) => r.Path switch
    {
        "/api/v2/users/me" => FakeResponse.Json(Me),
        "/api/v2/search/export" => FakeResponse.Json(r.Query("query")!.Contains("updated>=", StringComparison.Ordinal) ? Export(1, 2) : Export(2, 3)),
        "/api/v2/tickets/1" => FakeResponse.Json(SlaTicketJson(1, AchievedEvents)),
        "/api/v2/tickets/2" => FakeResponse.Json(SlaTicketJson(2, BreachedEvents)),
        "/api/v2/tickets/3" => FakeResponse.Json("""{"ticket":{"id":3,"status":"closed"},"metric_events":{}}"""),
        "/api/v2/users/show_many" => FakeResponse.Json("""{"users":[{"id":50,"name":"Agent Smith"}]}"""),
        "/api/v2/groups" => FakeResponse.Json("""{"groups":[{"id":7,"name":"Support"}],"meta":{"has_more":false}}"""),
        _ => FakeResponse.NotFound(),
    };

    [Fact]
    public async Task SlaSummary_Outcome_UnionsTwoSearchesAndCountsTargets()
    {
        using var fake = TestHelpers.CreateFakeZendesk(SlaRoute);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = Parse(await AnalyticsTools.GetSlaSummary(zendesk, start_date: "2026-03-01", end_date: "2026-03-07", group: "Support", group_by: "policy"));

        fake.Requests.Where(r => r.Path == "/api/v2/search/export").Select(r => r.Query("query")).Should().Equal(
            "type:ticket group:Support updated>=2026-03-01",
            "type:ticket group:Support status<solved");
        fake.Requests.Where(r => r.Path.StartsWith("/api/v2/tickets/", StringComparison.Ordinal)).Should().HaveCount(3, "ticket 2 is in both searches but read once");
        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);

        result["date_basis"]!.GetValue<string>().Should().Be("outcome");
        result["period"]!["start"]!.GetValue<string>().Should().Be("2026-03-01");
        result["timezone"]!.GetValue<string>().Should().Be("Europe/London");
        result["metrics"]!["first_reply_time"]!["achieved"]!.GetValue<int>().Should().Be(1);
        result["metrics"]!["first_reply_time"]!["breached"]!.GetValue<int>().Should().Be(1);
        result["metrics"]!["first_reply_time"]!["achieved_pct"]!.GetValue<double>().Should().Be(50);
        result["tickets"]!["read"]!.GetValue<int>().Should().Be(3);
        result["tickets"]!["archived_without_events"]!.GetValue<int>().Should().Be(1);
        result["groups"]![0]!["name"]!.GetValue<string>().Should().Be("Gold");
        result["breached_total"]!.GetValue<int>().Should().Be(1);
        result["breached"]!["rows"]![0]!.GetValue<string>().Should().Be(
            "2,https://acme.zendesk.com/agent/tickets/2,Ticket 2,open,first_reply_time,1,Gold,60,false,2026-03-03T10:00:00Z,2026-03-03T10:20:00Z,20,50,Agent Smith,7,Support");
        result["notes"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain(n => n.Contains("archived"));
    }

    [Fact]
    public async Task SlaSummary_Created_SearchesByCreatedDate()
    {
        using var fake = TestHelpers.CreateFakeZendesk(SlaRoute);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = Parse(await AnalyticsTools.GetSlaSummary(zendesk, start_date: "2026-03-01", end_date: "2026-03-07", date_basis: "created"));

        fake.Requests.Where(r => r.Path == "/api/v2/search/export").Select(r => r.Query("query")).Should().Equal(
            "type:ticket created>=2026-03-01 created<=2026-03-07");
        result["metrics"]!["first_reply_time"]!["active"]!.GetValue<int>().Should().Be(0);
    }

    [Theory]
    [InlineData("2026-3-1", null, null, null, "*start_date*")]
    [InlineData("2026-03-07", "2026-03-01", null, null, "*before*")]
    [InlineData("2026-03-01", null, "resolved", null, "*date_basis*")]
    [InlineData("2026-03-01", null, null, 1001, "*breached_limit*")]
    public async Task SlaSummary_Validates(string start, string? end, string? basis, int? limit, string message)
    {
        using var fake = TestHelpers.CreateFakeZendesk(SlaRoute);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => AnalyticsTools.GetSlaSummary(zendesk, start_date: start, end_date: end, date_basis: basis, breached_limit: limit);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
        fake.Requests.Should().NotContain(r => r.Path == "/api/v2/search/export");
    }

    [Fact]
    public async Task SlaSummary_OverTheCapAcrossBothSearches_ComputesNothing()
    {
        using var fake = TestHelpers.CreateFakeZendesk(SlaRoute);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client, configure: o => o.MaxAnalyticsTickets = 2);

        var result = Parse(await AnalyticsTools.GetSlaSummary(zendesk, start_date: "2026-03-01", end_date: "2026-03-07"));

        result["too_many_tickets"]!.GetValue<bool>().Should().BeTrue();
        result["note"]!.GetValue<string>().Should().Contain("weeks");
        fake.Requests.Should().NotContain(r => r.Path.StartsWith("/api/v2/tickets/", StringComparison.Ordinal));
    }

    // ---- Get_sla_queue ----

    [Fact]
    public async Task SlaQueue_RanksFromPolicyMetricsAndReadsEventsForTheTopOnly()
    {
        var now = DateTimeOffset.UtcNow;
        string At(double minutes) => AnalyticsStats.Iso(now.AddMinutes(minutes));
        var showMany = $$$"""
            {"tickets":[
              {"id":1,"subject":"Soon","status":"open","priority":"high","assignee_id":50,"group_id":7,"slas":{"policy_metrics":[{"metric":"first_reply_time","stage":"active","breach_at":"{{{At(30)}}}"}]}},
              {"id":2,"subject":"Late","status":"open","priority":"urgent","assignee_id":null,"group_id":null,"slas":{"policy_metrics":[{"metric":"next_reply_time","stage":"active","breach_at":"{{{At(-45)}}}"}]}},
              {"id":3,"subject":"Later","status":"pending","slas":{"policy_metrics":[{"metric":"total_resolution_time","stage":"active","breach_at":"{{{At(600)}}}"}]}},
              {"id":4,"subject":"No SLA","status":"new","slas":{"policy_metrics":[]}}
            ]}
            """;
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/search/export" => FakeResponse.Json(Export(1, 2, 3, 4)),
            "/api/v2/tickets/show_many" => FakeResponse.Json(showMany),
            "/api/v2/tickets/1" or "/api/v2/tickets/2" => FakeResponse.Json($$$"""{"ticket":{"id":{{{r.Path[^1]}}},"status":"open"},"metric_events":{}}"""),
            "/api/v2/users/show_many" => FakeResponse.Json("""{"users":[{"id":50,"name":"Agent Smith"}]}"""),
            "/api/v2/groups" => FakeResponse.Json("""{"groups":[{"id":7,"name":"Support"}],"meta":{"has_more":false}}"""),
            _ => FakeResponse.NotFound(),
        });
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = Parse(await AnalyticsTools.GetSlaQueue(zendesk, limit: 2, assignee: "me"));

        fake.Requests.Single(r => r.Path == "/api/v2/search/export").Query("query").Should().Be("type:ticket assignee:me status<solved");
        fake.Requests.Single(r => r.Path == "/api/v2/tickets/show_many").Query("include").Should().Be("slas");
        fake.Requests.Where(r => r.Path.StartsWith("/api/v2/tickets/", StringComparison.Ordinal) && r.Path != "/api/v2/tickets/show_many")
            .Select(r => r.Path).Should().BeEquivalentTo(["/api/v2/tickets/2", "/api/v2/tickets/1"]);

        var counts = result["counts"]!;
        counts["scanned"]!.GetValue<int>().Should().Be(4);
        counts["with_sla"]!.GetValue<int>().Should().Be(3);
        counts["without_sla"]!.GetValue<int>().Should().Be(1);
        counts["overdue"]!.GetValue<int>().Should().Be(1);
        counts["due_within_1h"]!.GetValue<int>().Should().Be(1);
        counts["due_within_4h"]!.GetValue<int>().Should().Be(1);

        var tickets = result["tickets"]!.AsArray();
        tickets.Should().HaveCount(2);
        tickets[0]!["id"]!.GetValue<long>().Should().Be(2);
        tickets[0]!["urgency"]!.GetValue<string>().Should().Be("overdue");
        tickets[0]!["metric"]!.GetValue<string>().Should().Be("next_reply_time");
        tickets[0]!["minutes_overdue"]!.GetValue<long>().Should().BeInRange(44, 46);
        tickets[1]!["id"]!.GetValue<long>().Should().Be(1);
        tickets[1]!["urgency"]!.GetValue<string>().Should().Be("due");
        tickets[1]!["minutes_left"]!.GetValue<long>().Should().BeInRange(28, 30);
        tickets[1]!["assignee"]!.GetValue<string>().Should().Be("Agent Smith");
        tickets[1]!["group"]!.GetValue<string>().Should().Be("Support");
        tickets[1]!["url"]!.GetValue<string>().Should().Be("https://acme.zendesk.com/agent/tickets/1");
        tickets[1]!.AsObject().ContainsKey("sla").Should().BeTrue();
    }

    [Theory]
    [InlineData("solved", null, "*status*")]
    [InlineData(null, 0, "*limit*")]
    [InlineData(null, 101, "*limit*")]
    public async Task SlaQueue_Validates(string? status, int? limit, string message)
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json(Export()));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => AnalyticsTools.GetSlaQueue(zendesk, limit: limit, status: status);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SlaQueue_WithStatus_DoesNotAddStatusRange()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/search/export"
            ? FakeResponse.Json(Export())
            : FakeResponse.Json("""{"tickets":[]}"""));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = Parse(await AnalyticsTools.GetSlaQueue(zendesk, status: "pending"));

        fake.Requests[0].Query("query").Should().Be("type:ticket status:pending");
        result["counts"]!["scanned"]!.GetValue<int>().Should().Be(0);
        result["tickets"]!.AsArray().Should().BeEmpty();
    }
}
