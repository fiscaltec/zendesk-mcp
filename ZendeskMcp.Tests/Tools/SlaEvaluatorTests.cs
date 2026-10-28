using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

/// <summary>The pure SLA evaluator behind every tool's <c>sla</c> object.</summary>
public class SlaEvaluatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonObject Ev(string type, string time, long instance = 1, string metric = "reply_time", JsonNode? sla = null, bool? deleted = null, long? id = null)
    {
        var ev = new JsonObject { ["metric"] = metric, ["instance_id"] = instance, ["type"] = type, ["time"] = time };
        if (sla is not null)
        {
            ev["sla"] = sla;
        }

        if (deleted is not null)
        {
            ev["deleted"] = deleted;
        }

        if (id is not null)
        {
            ev["id"] = id;
        }

        return ev;
    }

    private static JsonObject Sla(int target, long policyId = 5, string title = "Standard", bool businessHours = false) => new()
    {
        ["target"] = target,
        ["business_hours"] = businessHours,
        ["policy"] = new JsonObject { ["id"] = policyId, ["title"] = title, ["description"] = "" },
    };

    private static JsonObject ByMetric(params JsonObject[] events)
    {
        var result = new JsonObject();
        foreach (var group in events.GroupBy(e => e["metric"]!.GetValue<string>()))
        {
            result[group.Key] = new JsonArray(group.Select(e => (JsonNode?)e).ToArray());
        }

        return result;
    }

    private static JsonObject OpenTicket(JsonArray? policyMetrics = null) => new()
    {
        ["id"] = 1,
        ["status"] = "open",
        ["slas"] = new JsonObject { ["policy_metrics"] = policyMetrics ?? new JsonArray() },
    };

    private static JsonObject Target(JsonNode? sla, int index = 0) => sla!["targets"]!.AsArray()[index]!.AsObject();

    [Fact]
    public void AchievedOnTime()
    {
        var events = ByMetric(
            Ev("apply_sla", "2026-03-10T09:00:00Z", sla: Sla(60)),
            Ev("activate", "2026-03-10T09:00:00Z"),
            Ev("breach", "2026-03-10T10:00:00Z", deleted: true),
            Ev("fulfill", "2026-03-10T09:30:00Z"));

        var sla = TicketSla.Evaluate(OpenTicket(), events, Now);

        sla!["breached"]!.GetValue<bool>().Should().BeFalse();
        sla["next_breach_at"].Should().BeNull();
        var target = Target(sla);
        target["metric"]!.GetValue<string>().Should().Be("first_reply_time");
        target["instance"]!.GetValue<long>().Should().Be(1);
        target["status"]!.GetValue<string>().Should().Be("achieved");
        target["target_minutes"]!.GetValue<long>().Should().Be(60);
        target["business_hours"]!.GetValue<bool>().Should().BeFalse();
        target["policy_id"]!.GetValue<long>().Should().Be(5);
        target["policy_title"]!.GetValue<string>().Should().Be("Standard");
        target["completed_at"]!.GetValue<string>().Should().Be("2026-03-10T09:30:00Z");
        target["breach_at"].Should().BeNull();
        target["minutes_late"].Should().BeNull();
    }

    [Fact]
    public void BreachedThenFulfilledLate_ReportsMinutesLate()
    {
        var events = ByMetric(
            Ev("apply_sla", "2026-03-10T09:00:00Z", sla: Sla(60)),
            Ev("activate", "2026-03-10T09:00:00Z"),
            Ev("breach", "2026-03-10T10:00:00Z"),
            Ev("fulfill", "2026-03-10T10:45:00Z"));

        var sla = TicketSla.Evaluate(OpenTicket(), events, Now);

        sla!["breached"]!.GetValue<bool>().Should().BeTrue();
        var target = Target(sla);
        target["status"]!.GetValue<string>().Should().Be("breached");
        target["breach_at"]!.GetValue<string>().Should().Be("2026-03-10T10:00:00Z");
        target["completed_at"]!.GetValue<string>().Should().Be("2026-03-10T10:45:00Z");
        target["minutes_late"]!.GetValue<long>().Should().Be(45);
    }

    [Fact]
    public void Active_WithBreachInFuture_IsTheNextBreach()
    {
        var events = ByMetric(
            Ev("apply_sla", "2026-03-10T11:00:00Z", sla: Sla(120)),
            Ev("activate", "2026-03-10T11:00:00Z"),
            Ev("breach", "2026-03-10T13:00:00Z"));

        var sla = TicketSla.Evaluate(OpenTicket(), events, Now);

        sla!["breached"]!.GetValue<bool>().Should().BeFalse();
        sla["next_breach_at"]!.GetValue<string>().Should().Be("2026-03-10T13:00:00Z");
        sla["next_breach_metric"]!.GetValue<string>().Should().Be("first_reply_time");
        Target(sla)["status"]!.GetValue<string>().Should().Be("active");
    }

    [Fact]
    public void RunningPastItsBreach_IsBreachedWithNoCompletion()
    {
        var events = ByMetric(
            Ev("apply_sla", "2026-03-10T10:00:00Z", sla: Sla(60)),
            Ev("activate", "2026-03-10T10:00:00Z"),
            Ev("breach", "2026-03-10T11:00:00Z"));

        var target = Target(TicketSla.Evaluate(OpenTicket(), events, Now));

        target["status"]!.GetValue<string>().Should().Be("breached");
        target["completed_at"].Should().BeNull();
        target["minutes_late"]!.GetValue<long>().Should().Be(60);
    }

    [Fact]
    public void Paused_IsNotDue()
    {
        var events = ByMetric(
            Ev("apply_sla", "2026-03-10T09:00:00Z", metric: "pausable_update_time", sla: Sla(240)),
            Ev("activate", "2026-03-10T09:00:00Z", metric: "pausable_update_time"),
            Ev("breach", "2026-03-10T13:00:00Z", metric: "pausable_update_time", deleted: true),
            Ev("pause", "2026-03-10T09:10:00Z", metric: "pausable_update_time"));

        var sla = TicketSla.Evaluate(OpenTicket(), events, Now);

        var target = Target(sla);
        target["metric"]!.GetValue<string>().Should().Be("pausable_update_time");
        target["status"]!.GetValue<string>().Should().Be("paused");
        target["breach_at"].Should().BeNull();
        sla!["next_breach_at"].Should().BeNull();
    }

    [Fact]
    public void ReplyTimeInstances_AfterTheFirst_AreNextReplyTargets()
    {
        var events = ByMetric(
            Ev("apply_sla", "2026-03-09T09:00:00Z", 1, sla: Sla(60)),
            Ev("fulfill", "2026-03-09T09:20:00Z", 1),
            Ev("apply_sla", "2026-03-09T12:00:00Z", 2, sla: Sla(120)),
            Ev("breach", "2026-03-09T14:00:00Z", 2),
            Ev("fulfill", "2026-03-09T15:00:00Z", 2),
            Ev("apply_sla", "2026-03-10T11:30:00Z", 3, sla: Sla(120)),
            Ev("activate", "2026-03-10T11:30:00Z", 3),
            Ev("breach", "2026-03-10T13:30:00Z", 3));

        var targets = TicketSla.Evaluate(OpenTicket(), events, Now)!["targets"]!.AsArray();

        targets.Select(t => $"{t!["metric"]}#{t["instance"]}:{t["status"]}").Should().Equal(
            "first_reply_time#1:achieved",
            "next_reply_time#1:breached",
            "next_reply_time#2:active");
        targets[1]!["minutes_late"]!.GetValue<long>().Should().Be(60);
    }

    [Fact]
    public void PolicyReapplied_UsesTheNewTargetAndBreach()
    {
        var events = ByMetric(
            Ev("apply_sla", "2026-03-10T09:00:00Z", sla: Sla(60, 5, "Normal"), id: 1),
            Ev("activate", "2026-03-10T09:00:00Z", id: 2),
            Ev("breach", "2026-03-10T10:00:00Z", deleted: true, id: 3),
            Ev("apply_sla", "2026-03-10T09:10:00Z", sla: Sla(30, 6, "Urgent"), id: 4),
            Ev("breach", "2026-03-10T09:40:00Z", id: 5),
            Ev("fulfill", "2026-03-10T09:50:00Z", id: 6));

        var target = Target(TicketSla.Evaluate(OpenTicket(), events, Now));

        target["status"]!.GetValue<string>().Should().Be("breached");
        target["target_minutes"]!.GetValue<long>().Should().Be(30);
        target["policy_id"]!.GetValue<long>().Should().Be(6);
        target["policy_title"]!.GetValue<string>().Should().Be("Urgent");
        target["minutes_late"]!.GetValue<long>().Should().Be(10);
    }

    [Fact]
    public void NoApplySla_IsNull()
    {
        var events = ByMetric(
            Ev("activate", "2026-03-10T09:00:00Z"),
            Ev("fulfill", "2026-03-10T09:30:00Z"));

        TicketSla.Evaluate(OpenTicket(), events, Now).Should().BeNull();
    }

    [Fact]
    public void NoEventsAndNoPolicyMetrics_IsNull()
    {
        TicketSla.Evaluate(OpenTicket(), new JsonObject(), Now).Should().BeNull();
        TicketSla.Evaluate(OpenTicket(), null, Now).Should().BeNull();
    }

    [Fact]
    public void ClosedTicketWithoutEvents_IsEventsMissing()
    {
        var ticket = new JsonObject { ["id"] = 1, ["status"] = "closed" };

        var sla = TicketSla.Evaluate(ticket, new JsonObject(), Now);

        sla!["events_missing"]!.GetValue<bool>().Should().BeTrue();
        TicketSla.Evaluate(ticket, null, Now)!["events_missing"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void ClosedTicketWithEvents_IsEvaluated()
    {
        var ticket = new JsonObject { ["id"] = 1, ["status"] = "closed" };
        var events = ByMetric(
            Ev("apply_sla", "2026-01-10T09:00:00Z", metric: "resolution_time", sla: Sla(1440)),
            Ev("fulfill", "2026-01-10T15:00:00Z", metric: "resolution_time"));

        var target = Target(TicketSla.Evaluate(ticket, events, Now));

        target["metric"]!.GetValue<string>().Should().Be("total_resolution_time");
        target["status"]!.GetValue<string>().Should().Be("achieved");
    }

    [Fact]
    public void ActiveTargetWithoutBreachEvent_TakesBreachFromPolicyMetrics()
    {
        var policy = new JsonArray(new JsonObject { ["metric"] = "first_reply_time", ["stage"] = "active", ["breach_at"] = "2026-03-10T14:00:00Z" });
        var events = ByMetric(
            Ev("apply_sla", "2026-03-10T11:00:00Z", sla: Sla(180)),
            Ev("activate", "2026-03-10T11:00:00Z"));

        var sla = TicketSla.Evaluate(OpenTicket(policy), events, Now);

        Target(sla)["breach_at"]!.GetValue<string>().Should().Be("2026-03-10T14:00:00Z");
        sla!["next_breach_at"]!.GetValue<string>().Should().Be("2026-03-10T14:00:00Z");
    }

    [Fact]
    public void NoEvents_FallsBackToPolicyMetrics()
    {
        var policy = new JsonArray(
            new JsonObject { ["metric"] = "first_reply_time", ["stage"] = "active", ["breach_at"] = "2026-03-10T11:30:00Z", ["hours"] = 1, ["minutes"] = 0, ["days"] = 0, ["business_hours"] = true },
            new JsonObject { ["metric"] = "requester_wait_time", ["stage"] = "paused", ["breach_at"] = null },
            new JsonObject { ["metric"] = "agent_work_time", ["stage"] = "active", ["breach_at"] = "2026-03-10T15:00:00Z" });

        var sla = TicketSla.Evaluate(OpenTicket(policy), null, Now);

        sla!["breached"]!.GetValue<bool>().Should().BeTrue();
        var targets = sla["targets"]!.AsArray();
        targets[0]!["status"]!.GetValue<string>().Should().Be("breached");
        targets[0]!["minutes_late"]!.GetValue<long>().Should().Be(30);
        targets[0]!["target_minutes"]!.GetValue<long>().Should().Be(60);
        targets[0]!["business_hours"]!.GetValue<bool>().Should().BeTrue();
        targets[1]!["status"]!.GetValue<string>().Should().Be("paused");
        sla["next_breach_metric"]!.GetValue<string>().Should().Be("agent_work_time");
    }

    [Fact]
    public void FlatEventArray_IsAccepted()
    {
        var events = new JsonArray(
            Ev("apply_sla", "2026-03-10T09:00:00Z", sla: Sla(60)),
            Ev("fulfill", "2026-03-10T09:05:00Z"));

        Target(TicketSla.Evaluate(OpenTicket(), events, Now))["status"]!.GetValue<string>().Should().Be("achieved");
    }

    [Fact]
    public void ReactivationAfterFulfil_RestartsTheClock()
    {
        var events = ByMetric(
            Ev("apply_sla", "2026-03-09T09:00:00Z", metric: "resolution_time", sla: Sla(2880)),
            Ev("fulfill", "2026-03-09T10:00:00Z", metric: "resolution_time"),
            Ev("activate", "2026-03-10T08:00:00Z", metric: "resolution_time"),
            Ev("breach", "2026-03-11T09:00:00Z", metric: "resolution_time"));

        var target = Target(TicketSla.Evaluate(OpenTicket(), events, Now));

        target["status"]!.GetValue<string>().Should().Be("active");
        target["completed_at"].Should().BeNull();
        target["breach_at"]!.GetValue<string>().Should().Be("2026-03-11T09:00:00Z");
    }

    [Fact]
    public async Task GetAsync_RequestsMetricEventsAndSlas()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("""
            {"ticket":{"id":42,"status":"open","slas":{"policy_metrics":[]}},
             "metric_events":{"reply_time":[
               {"metric":"reply_time","instance_id":1,"type":"apply_sla","time":"2026-03-10T09:00:00Z","sla":{"target":60,"business_hours":false,"policy":{"id":1,"title":"P"}}},
               {"metric":"reply_time","instance_id":1,"type":"fulfill","time":"2026-03-10T09:10:00Z"}]}}
            """));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var sla = await TicketSla.GetAsync(zendesk, 42);

        fake.Requests.Should().ContainSingle();
        fake.Requests[0].Method.Should().Be(HttpMethod.Get);
        fake.Requests[0].Path.Should().Be("/api/v2/tickets/42");
        fake.Requests[0].Query("include").Should().Be("metric_events,slas");
        Target(sla)["status"]!.GetValue<string>().Should().Be("achieved");
    }

    [Fact]
    public async Task GetManyAsync_KeysByIdAndToleratesDeletedTickets()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path.EndsWith("/2", StringComparison.Ordinal)
            ? FakeResponse.NotFound()
            : FakeResponse.Json("""{"ticket":{"id":1,"status":"open"},"metric_events":{}}"""));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var slas = await TicketSla.GetManyAsync(zendesk, [1, 2, 1]);

        slas.Keys.Should().BeEquivalentTo([1L, 2L]);
        slas[1].Should().BeNull();
        slas[2].Should().BeNull();
        fake.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAsync_PropagatesNotFound()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.NotFound());
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => TicketSla.GetAsync(zendesk, 9);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
