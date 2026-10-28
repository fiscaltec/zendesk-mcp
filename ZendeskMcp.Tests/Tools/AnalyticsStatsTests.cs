using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

/// <summary>The pure arithmetic of the analytics tools: statistics, bucketing, CSV, metrics figures and SLA counting.</summary>
public class AnalyticsStatsTests
{
    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);

    // ---- statistics ----

    [Fact]
    public void Median_OddAndEven()
    {
        AnalyticsStats.Median([5, 1, 3]).Should().Be(3);
        AnalyticsStats.Median([4, 1, 3, 2]).Should().Be(2.5);
        AnalyticsStats.Median([]).Should().BeNull();
    }

    [Fact]
    public void Percentile_IsNearestRank()
    {
        var values = Enumerable.Range(1, 10).Select(i => (double)i).ToList();
        AnalyticsStats.Percentile(values, 90).Should().Be(9);
        AnalyticsStats.Percentile([7], 90).Should().Be(7);
        AnalyticsStats.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], 90).Should().Be(10); // rank ceil(9.9) = 10
        AnalyticsStats.Percentile([], 90).Should().BeNull();
    }

    [Fact]
    public void Summary_RoundsAndHandlesEmpty()
    {
        var summary = AnalyticsStats.Summary([1, 2, 2]);
        summary["n"]!.GetValue<int>().Should().Be(3);
        summary["median"]!.GetValue<double>().Should().Be(2);
        summary["avg"]!.GetValue<double>().Should().Be(1.7);
        summary["p90"]!.GetValue<double>().Should().Be(2);

        var empty = AnalyticsStats.Summary([]);
        empty["n"]!.GetValue<int>().Should().Be(0);
        empty["median"].Should().BeNull();
    }

    [Fact]
    public void RatePct_NullWhenNoDenominator()
    {
        AnalyticsStats.RatePct(1, 3).Should().Be(33.3);
        AnalyticsStats.RatePct(2, 3).Should().Be(66.7);
        AnalyticsStats.RatePct(0, 0).Should().BeNull();
    }

    // ---- bucketing ----

    [Fact]
    public void LocalDay_FollowsTheTimezone()
    {
        var zone = AnalyticsStats.ResolveTimeZone("America/New_York");
        AnalyticsStats.LocalDay(At("2026-03-10T03:00:00Z"), zone).Should().Be(new DateOnly(2026, 3, 9));
        AnalyticsStats.LocalDay(At("2026-03-10T03:00:00Z"), TimeZoneInfo.Utc).Should().Be(new DateOnly(2026, 3, 10));
    }

    [Fact]
    public void BucketKeys_DayWeekMonth()
    {
        var zone = AnalyticsStats.ResolveTimeZone("Europe/London");
        var sunday = At("2026-03-15T23:30:00Z"); // Sunday in London
        AnalyticsStats.BucketKey(sunday, zone, "day").Should().Be("2026-03-15");
        AnalyticsStats.BucketKey(sunday, zone, "week").Should().Be("2026-03-09");
        AnalyticsStats.BucketKey(At("2026-03-16T00:30:00Z"), zone, "week").Should().Be("2026-03-16");
        AnalyticsStats.BucketKey(At("2026-03-31T23:30:00Z"), zone, "month").Should().Be("2026-04"); // BST
        AnalyticsStats.BucketName("2026-04", "month").Should().Be("April 2026");
        AnalyticsStats.BucketName("2026-03-09", "week").Should().Be("Week of 2026-03-09");
    }

    [Fact]
    public void ResolveTimeZone_UnknownFallsBackToUtc()
    {
        AnalyticsStats.ResolveTimeZone("Not/AZone").Should().Be(TimeZoneInfo.Utc);
        AnalyticsStats.ResolveTimeZone(null).Should().Be(TimeZoneInfo.Utc);
    }

    // ---- CSV ----

    [Fact]
    public void Csv_QuotesAndNeutralisesFormulas()
    {
        AnalyticsStats.CsvField("plain").Should().Be("plain");
        AnalyticsStats.CsvField("a,b").Should().Be("\"a,b\"");
        AnalyticsStats.CsvField("say \"hi\"").Should().Be("\"say \"\"hi\"\"\"");
        AnalyticsStats.CsvField("line\nbreak").Should().Be("\"line\nbreak\"");
        AnalyticsStats.CsvField("=HYPERLINK(1)").Should().Be("'=HYPERLINK(1)");
        AnalyticsStats.CsvField(-5).Should().Be("-5");
        AnalyticsStats.CsvField(null).Should().Be("");
        AnalyticsStats.CsvField(true).Should().Be("true");
        AnalyticsStats.CsvField(2.5).Should().Be("2.5");
        AnalyticsStats.Csv(["a", "b"], [[1, "x"], [null, "y,z"]]).Should().Be("a,b\r\n1,x\r\n,\"y,z\"\r\n");
    }

    // ---- ticket metrics figures ----

    private static AnalyticsMetrics.Row Row(long id, string status, double? firstReply, double? fullResolution, long reopens, long replies, string created = "2026-03-02T10:00:00Z", string? solved = null, JsonObject? extra = null)
    {
        var ticket = new JsonObject { ["id"] = id, ["status"] = status, ["created_at"] = created };
        foreach (var (k, v) in extra ?? [])
        {
            ticket[k] = v?.DeepClone();
        }

        var metricSet = new JsonObject
        {
            ["ticket_id"] = id,
            ["reopens"] = reopens,
            ["replies"] = replies,
            ["solved_at"] = solved,
            ["reply_time_in_minutes"] = new JsonObject { ["calendar"] = firstReply, ["business"] = firstReply / 2 },
            ["full_resolution_time_in_minutes"] = new JsonObject { ["calendar"] = fullResolution, ["business"] = fullResolution },
        };
        return AnalyticsMetrics.ToRow(ticket, metricSet);
    }

    [Fact]
    public void Figures_FirstReplyOverAllTickets_OthersOverSolvedOnly()
    {
        var rows = new[]
        {
            Row(1, "solved", 10, 100, 0, 1, solved: "2026-03-03T10:00:00Z"),
            Row(2, "closed", 20, 300, 2, 3, solved: "2026-03-04T10:00:00Z"),
            Row(3, "open", 30, 999, 1, 0),
            Row(4, "solved", null, 50, 0, 0, solved: "2026-03-05T10:00:00Z"),
        };

        var figures = AnalyticsMetrics.Figures(rows);

        figures["tickets"]!["total"]!.GetValue<int>().Should().Be(4);
        figures["tickets"]!["solved"]!.GetValue<int>().Should().Be(3);
        figures["tickets"]!["unsolved"]!.GetValue<int>().Should().Be(1);
        figures["first_reply_time"]!["calendar"]!["n"]!.GetValue<int>().Should().Be(3);
        figures["first_reply_time"]!["calendar"]!["median"]!.GetValue<double>().Should().Be(20);
        figures["first_reply_time"]!["business"]!["avg"]!.GetValue<double>().Should().Be(10);
        figures["full_resolution_time"]!["calendar"]!["n"]!.GetValue<int>().Should().Be(3);
        figures["full_resolution_time"]!["calendar"]!["avg"]!.GetValue<double>().Should().Be(150);
        figures["agent_wait_time"]!["calendar"]!["n"]!.GetValue<int>().Should().Be(0);
        figures["reopens"]!["tickets_reopened"]!.GetValue<int>().Should().Be(1);
        figures["reopens"]!["reopen_rate_pct"]!.GetValue<double>().Should().Be(33.3);
        figures["reopens"]!["total_reopens"]!.GetValue<long>().Should().Be(2);
        figures["replies"]!["median"]!.GetValue<double>().Should().Be(1);
        figures["replies"]!["avg"]!.GetValue<double>().Should().Be(1.3);
        figures["one_touch"]!["tickets"]!.GetValue<int>().Should().Be(1);
        figures["one_touch"]!["rate_pct"]!.GetValue<double>().Should().Be(33.3);
        figures["one_touch"]!["zero_touch_tickets"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void Trend_SumsPerDayInColumnOrder()
    {
        var rows = new[]
        {
            Row(1, "solved", 10, 100, 1, 1, created: "2026-03-02T10:00:00Z", solved: "2026-03-03T10:00:00Z"),
            Row(2, "open", 20, null, 0, 0, created: "2026-03-02T11:00:00Z"),
        };
        var days = AnalyticsStats.Days(new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 3)).ToList();

        var byCreated = AnalyticsMetrics.Trend(rows, days, TimeZoneInfo.Utc, bySolved: false);
        var bySolved = AnalyticsMetrics.Trend(rows, days, TimeZoneInfo.Utc, bySolved: true);

        var columns = byCreated["columns"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        columns.Take(8).Should().Equal("date", "tickets", "solved", "reopened_tickets", "total_reopens", "replies", "one_touch", "zero_touch");
        columns.Should().Contain(["first_reply_time_n", "first_reply_time_sum", "first_reply_time_business_n", "on_hold_time_business_sum"]);
        var day1 = byCreated["rows"]![0]!.GetValue<string>().Split(',');
        day1[0].Should().Be("2026-03-02");
        day1[1].Should().Be("2");
        day1[2].Should().Be("1");
        day1[columns.IndexOf("first_reply_time_n")].Should().Be("2");
        day1[columns.IndexOf("first_reply_time_sum")].Should().Be("30");
        day1[columns.IndexOf("full_resolution_time_sum")].Should().Be("100");
        byCreated["rows"]![1]!.GetValue<string>().Should().StartWith("2026-03-03,0,0,");
        bySolved["rows"]![1]!.GetValue<string>().Should().StartWith("2026-03-03,1,1,1,1,1,1,0,");
    }

    [Fact]
    public void GroupKeys_ByDimension()
    {
        var extra = new JsonObject
        {
            ["group_id"] = 7,
            ["via"] = new JsonObject { ["channel"] = "email" },
            ["satisfaction_rating"] = new JsonObject { ["score"] = "good", ["comment"] = "Thanks" },
            ["custom_fields"] = new JsonArray(
                new JsonObject { ["id"] = 100, ["value"] = new JsonArray("a", "b") },
                new JsonObject { ["id"] = 101, ["value"] = false },
                new JsonObject { ["id"] = 102, ["value"] = null }),
        };
        var row = Row(1, "solved", 1, 1, 0, 1, created: "2026-03-15T23:30:00Z", solved: "2026-03-16T10:00:00Z", extra: extra);
        var zone = AnalyticsStats.ResolveTimeZone("Europe/London");

        AnalyticsMetrics.GroupKeys(row, "group", null, zone, false).Should().Equal("7");
        AnalyticsMetrics.GroupKeys(row, "brand", null, zone, false).Should().Equal(new string?[] { null });
        AnalyticsMetrics.GroupKeys(row, "channel", null, zone, false).Should().Equal("email");
        AnalyticsMetrics.GroupKeys(row, "satisfaction", null, zone, false).Should().Equal("good_with_comment");
        AnalyticsMetrics.GroupKeys(row, "custom_field", 100, zone, false).Should().Equal("a", "b");
        AnalyticsMetrics.GroupKeys(row, "custom_field", 101, zone, false).Should().Equal("false");
        AnalyticsMetrics.GroupKeys(row, "custom_field", 102, zone, false).Should().Equal(new string?[] { null });
        AnalyticsMetrics.GroupKeys(row, "week", null, zone, bySolved: false).Should().Equal("2026-03-09");
        AnalyticsMetrics.GroupKeys(row, "week", null, zone, bySolved: true).Should().Equal("2026-03-16");
    }

    [Theory]
    [InlineData("good", null, "good")]
    [InlineData("bad", "slow", "bad_with_comment")]
    [InlineData("offered", null, "offered")]
    [InlineData("unoffered", null, "unoffered")]
    [InlineData(null, null, "unoffered")]
    public void SatisfactionKey(string? score, string? comment, string expected)
    {
        var rating = score is null ? null : new JsonObject { ["score"] = score, ["comment"] = comment };
        AnalyticsMetrics.SatisfactionKey(rating).Should().Be(expected);
    }

    // ---- SLA summary counting ----

    private static JsonObject T(string metric, string status, string? breachAt = null, string? completedAt = null, long? minutesLate = null, long policyId = 1, long instance = 1) => new()
    {
        ["metric"] = metric,
        ["instance"] = instance,
        ["status"] = status,
        ["target_minutes"] = 60,
        ["business_hours"] = false,
        ["policy_id"] = policyId,
        ["policy_title"] = $"Policy {policyId}",
        ["breach_at"] = breachAt,
        ["completed_at"] = completedAt,
        ["minutes_late"] = minutesLate,
    };

    private static TicketSla.SlaTicket SlaItem(long id, JsonObject? sla, string created = "2026-03-02T09:00:00Z", long? groupId = 7, params JsonObject[] targets)
    {
        var ticket = new JsonObject { ["id"] = id, ["subject"] = $"Ticket {id}", ["status"] = "open", ["created_at"] = created, ["group_id"] = groupId, ["assignee_id"] = 50 };
        if (sla is null && targets.Length > 0)
        {
            sla = new JsonObject { ["breached"] = targets.Any(t => t["status"]!.GetValue<string>() == "breached"), ["targets"] = new JsonArray(targets.Select(t => (JsonNode?)t).ToArray()) };
        }

        return new TicketSla.SlaTicket(id, ticket, sla);
    }

    private static AnalyticsSla.SummaryOptions Options(string basis = "outcome", string? groupBy = null, int limit = 100) => new(
        basis, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 7), TimeZoneInfo.Utc, groupBy, limit,
        (dimension, key) => $"{dimension}:{key}", id => $"https://acme.zendesk.com/agent/tickets/{id}");

    [Fact]
    public void Summarise_Outcome_CountsSettledTargetsInThePeriodOnly()
    {
        var items = new[]
        {
            SlaItem(1, null, targets:
            [
                T("first_reply_time", "achieved", completedAt: "2026-03-02T10:00:00Z"),
                T("next_reply_time", "breached", breachAt: "2026-03-03T10:00:00Z", completedAt: "2026-03-03T11:00:00Z", minutesLate: 60),
                T("next_reply_time", "active", breachAt: "2026-03-08T10:00:00Z", instance: 2),
            ]),
            SlaItem(2, null, targets:
            [
                T("first_reply_time", "achieved", completedAt: "2026-02-20T10:00:00Z"), // before the period
                T("total_resolution_time", "breached", breachAt: "2026-03-05T10:00:00Z", minutesLate: 300), // running past breach
            ]),
            SlaItem(3, null, targets: [T("first_reply_time", "achieved", completedAt: "2026-03-04T10:00:00Z")]),
            SlaItem(4, null),
            SlaItem(5, new JsonObject { ["events_missing"] = true }),
        };

        var result = AnalyticsSla.Summarise(items, Options());

        result["metrics"]!["first_reply_time"]!["achieved"]!.GetValue<int>().Should().Be(2);
        result["metrics"]!["first_reply_time"]!["achieved_pct"]!.GetValue<double>().Should().Be(100);
        result["metrics"]!["next_reply_time"]!["breached"]!.GetValue<int>().Should().Be(1);
        result["metrics"]!["next_reply_time"]!["achieved_pct"]!.GetValue<double>().Should().Be(0);
        result["metrics"]!["agent_work_time"]!["achieved_pct"].Should().BeNull();
        result["metrics"]!["first_reply_time"]!.AsObject().ContainsKey("active").Should().BeFalse();
        result["all_metrics"]!["achieved"]!.GetValue<int>().Should().Be(2);
        result["all_metrics"]!["breached"]!.GetValue<int>().Should().Be(2);
        result["all_metrics"]!["achieved_pct"]!.GetValue<double>().Should().Be(50);
        var tickets = result["tickets"]!;
        tickets["read"]!.GetValue<int>().Should().Be(5);
        tickets["with_sla"]!.GetValue<int>().Should().Be(3);
        tickets["without_sla"]!.GetValue<int>().Should().Be(1);
        tickets["archived_without_events"]!.GetValue<int>().Should().Be(1);
        tickets["with_targets"]!.GetValue<int>().Should().Be(3);
        tickets["with_breach"]!.GetValue<int>().Should().Be(2);
        tickets["all_achieved"]!.GetValue<int>().Should().Be(1);

        result["breached_total"]!.GetValue<int>().Should().Be(2);
        var rows = result["breached"]!["rows"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        rows[0].Should().StartWith("2,https://acme.zendesk.com/agent/tickets/2,Ticket 2,open,total_resolution_time,1,Policy 1,60,false,2026-03-05T10:00:00Z,,300,50,assignee:50,7,group:7");
        rows[1].Should().Contain(",next_reply_time,");
        result["breached"]!["columns"]!.AsArray().Should().HaveCount(16);
    }

    [Fact]
    public void Summarise_Created_IncludesRunningTargets()
    {
        var items = new[]
        {
            SlaItem(1, null, targets:
            [
                T("first_reply_time", "active", breachAt: "2026-03-08T10:00:00Z"),
                T("requester_wait_time", "paused"),
                T("next_reply_time", "achieved", completedAt: "2026-01-01T00:00:00Z"),
            ]),
        };

        var result = AnalyticsSla.Summarise(items, Options(basis: "created"));

        result["metrics"]!["first_reply_time"]!["active"]!.GetValue<int>().Should().Be(1);
        result["metrics"]!["requester_wait_time"]!["paused"]!.GetValue<int>().Should().Be(1);
        result["metrics"]!["next_reply_time"]!["achieved"]!.GetValue<int>().Should().Be(1);
        result["all_metrics"]!["active"]!.GetValue<int>().Should().Be(1);
        result["tickets"]!["all_achieved"]!.GetValue<int>().Should().Be(0, "a running target is not achieved yet");
    }

    [Fact]
    public void Summarise_GroupsAndBreachedLimit()
    {
        var items = new[]
        {
            SlaItem(1, null, groupId: 7, targets: [T("first_reply_time", "breached", breachAt: "2026-03-02T10:00:00Z", completedAt: "2026-03-02T10:05:00Z", minutesLate: 5)]),
            SlaItem(2, null, groupId: null, targets: [T("first_reply_time", "breached", breachAt: "2026-03-03T10:00:00Z", completedAt: "2026-03-03T12:00:00Z", minutesLate: 120)]),
            SlaItem(3, null, groupId: 8, targets: [T("first_reply_time", "achieved", completedAt: "2026-03-09T10:00:00Z")]), // after the period
        };

        var result = AnalyticsSla.Summarise(items, Options(groupBy: "group", limit: 1));

        var groups = result["groups"]!.AsArray();
        groups.Select(g => g!["key"]?.GetValue<string>()).Should().Equal("7", null);
        groups[0]!["name"]!.GetValue<string>().Should().Be("group:7");
        groups[1]!["name"]!.GetValue<string>().Should().Be("(none)");
        groups[0]!["tickets"]!["with_breach"]!.GetValue<int>().Should().Be(1);
        result["breached_total"]!.GetValue<int>().Should().Be(2);
        result["breached"]!["rows"]!.AsArray().Should().ContainSingle()
            .Which!.GetValue<string>().Should().StartWith("2,", "the most late breach comes first");
    }

    [Fact]
    public void Summarise_WeekGroupsBySettledDate()
    {
        var items = new[]
        {
            SlaItem(1, null, targets:
            [
                T("first_reply_time", "achieved", completedAt: "2026-03-01T10:00:00Z"), // Sunday → week of 23 Feb
                T("next_reply_time", "achieved", completedAt: "2026-03-02T10:00:00Z"), // Monday → week of 2 Mar
            ]),
        };

        var result = AnalyticsSla.Summarise(items, Options(groupBy: "week"));

        result["groups"]!.AsArray().Select(g => g!["key"]!.GetValue<string>()).Should().Equal("2026-02-23", "2026-03-02");
    }

    // ---- queue ranking ----

    private static JsonObject QueueTicket(long id, params (string Metric, string Stage, DateTimeOffset? BreachAt)[] metrics) => new()
    {
        ["id"] = id,
        ["status"] = "open",
        ["slas"] = new JsonObject
        {
            ["policy_metrics"] = new JsonArray(metrics.Select(m => (JsonNode?)new JsonObject
            {
                ["metric"] = m.Metric,
                ["stage"] = m.Stage,
                ["breach_at"] = m.BreachAt is { } at ? AnalyticsStats.Iso(at) : null,
            }).ToArray()),
        },
    };

    [Fact]
    public void RankQueue_OverdueFirstThenSoonestBreach()
    {
        var now = At("2026-03-10T12:00:00Z");
        var tickets = new List<JsonObject>
        {
            QueueTicket(1, ("first_reply_time", "active", now.AddMinutes(30))),
            QueueTicket(2, ("next_reply_time", "active", now.AddMinutes(-10))),
            QueueTicket(3, ("first_reply_time", "active", now.AddMinutes(-90)), ("agent_work_time", "active", now.AddMinutes(5))),
            QueueTicket(4, ("requester_wait_time", "paused", null)),
            QueueTicket(5, ("total_resolution_time", "active", now.AddMinutes(200))),
            QueueTicket(6, ("first_reply_time", "active", now.AddHours(10))),
            new JsonObject { ["id"] = 7, ["status"] = "new" },
        };

        var queue = AnalyticsSla.RankQueue(tickets, now);

        queue.Ranked.Select(e => ToolJson.Long(e.Ticket["id"])).Should().Equal(3L, 2L, 1L, 5L, 6L);
        queue.Ranked[0].Urgency.Should().Be("overdue");
        queue.Ranked[0].Metric.Should().Be("first_reply_time");
        queue.Ranked[0].Minutes.Should().Be(90);
        queue.Ranked[2].Urgency.Should().Be("due");
        queue.Ranked[2].Minutes.Should().Be(30);
        queue.Scanned.Should().Be(7);
        queue.WithSla.Should().Be(6);
        queue.Overdue.Should().Be(2);
        queue.DueWithin1h.Should().Be(1);
        queue.DueWithin4h.Should().Be(2);
    }

    [Fact]
    public void RankQueue_ATargetMetLateIsNotOverdue()
    {
        // Live, a target met after its breach reads stage 'achieved' and keeps its breach_at.
        var now = At("2026-03-10T12:00:00Z");
        var tickets = new List<JsonObject>
        {
            QueueTicket(1, ("first_reply_time", "achieved", now.AddHours(-5)), ("agent_work_time", "active", now.AddMinutes(45))),
        };

        var queue = AnalyticsSla.RankQueue(tickets, now);

        queue.Overdue.Should().Be(0);
        queue.Ranked.Single().Urgency.Should().Be("due");
        queue.Ranked.Single().Metric.Should().Be("agent_work_time");
    }
}
