using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

public class MacrosToolsTests
{
    private const string Definitions = """
        {"actions":[
          {"output_key":"status","title":"Status","type":"list","values":[{"value":"open","title":"Open"},{"value":"solved","title":"Solved"}]},
          {"output_key":"group_id","title":"Group","type":"list","values":[{"value":"77","title":"Billing"}]},
          {"output_key":"current_tags","title":"Add tags","type":"text"},
          {"output_key":"priority","title":"Priority","type":"list","values":[{"value":"high","title":"High"}]}
        ]}
        """;

    private const string DynamicContent = """
        {"items":[{"id":5,"name":"refund_policy","placeholder":"{{dc.refund_policy}}","default_locale_id":1,
          "variants":[{"id":51,"locale_id":1,"content":"Refunds take ten days","active":true,"default":true},
                      {"id":52,"locale_id":8,"content":"Rückerstattungen dauern zehn Tage","active":true,"default":false}]}],
         "meta":{"has_more":false}}
        """;

    private static (FakeZendesk Fake, ZendeskService Zendesk) Build(Func<RecordedRequest, FakeResponse> route, Action<ZendeskFeatureOptions>? features = null)
    {
        var fake = TestHelpers.CreateFakeZendesk(route);
        var service = TestHelpers.BuildZendeskService(fake.Client, configure: o => features?.Invoke(o.Features));
        return (fake, service);
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    private static string Macro(long id, string title, string actions = "[]", bool active = true, string restriction = "null", string usage = "") =>
        $$$"""{"id":{{{id}}},"title":"{{{title}}}","active":{{{(active ? "true" : "false")}}},"position":{{{id}}},"description":null,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-02-01T00:00:00Z","restriction":{{{restriction}}},"actions":{{{actions}}}{{{usage}}}}""";

    private static MacroActionInput Action(string field, object value) =>
        new() { Field = field, Value = JsonSerializer.SerializeToElement(value) };

    // ---------------------------------------------------------------- Search_macros

    [Fact]
    public async Task SearchMacros_Text_MatchesTitleCommentAndDynamicContent_AndSaysWhere()
    {
        var page = $$$"""
            {"macros":[
              {{{Macro(1, "Billing::Refund approved")}}},
              {{{Macro(2, "Other", """[{"field":"comment_value_html","value":"<p>Your refund is on its way</p>"}]""")}}},
              {{{Macro(3, "Policy", """[{"field":"comment_value_html","value":"<p>{{dc.refund_policy}}</p>"}]""")}}},
              {{{Macro(4, "Nothing here")}}}
            ],"meta":{"has_more":false}}
            """;
        var (fake, zendesk) = Build(r => r.Path switch
        {
            "/api/v2/macros" => FakeResponse.Json(page),
            "/api/v2/macros/actions" => FakeResponse.Json(Definitions),
            "/api/v2/dynamic_content/items" => FakeResponse.Json(DynamicContent),
            _ => FakeResponse.NotFound(),
        });
        using var _ = fake;

        var result = Parse(await MacrosTools.SearchMacros(zendesk, text: "REFUND"));

        var macros = result["macros"]!.AsArray();
        macros.Select(m => (long)m!["id"]!).Should().Equal(1, 2, 3);
        macros[0]!["matched_in"]!.AsArray().Select(n => (string)n!).Should().Equal("title");
        macros[1]!["matched_in"]!.AsArray().Select(n => (string)n!).Should().Equal("comment");
        macros[2]!["matched_in"]!.AsArray().Select(n => (string)n!).Should().Equal("dynamic_content");
        macros[0]!["category"]!.GetValue<string>().Should().Be("Billing");
        macros[0]!["url"]!.GetValue<string>().Should().Be("https://acme.zendesk.com/admin/workspaces/agent-workspace/macros/1");
        macros[1]!.AsObject().ContainsKey("comment_text").Should().BeFalse("the comment is only returned on request");

        var list = fake.Requests.First(r => r.Path == "/api/v2/macros");
        list.Query("include").Should().Be("usage_1h,usage_24h,usage_7d,usage_30d");
        list.Query("page[size]").Should().Be("100");
        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "a read tool only reads");
    }

    [Fact]
    public async Task SearchMacros_EveryWordMustAppear()
    {
        var page = $$$"""{"macros":[{{{Macro(1, "Refund approved")}}},{{{Macro(2, "Refund denied")}}}],"meta":{"has_more":false}}""";
        var (fake, zendesk) = Build(r => r.Path == "/api/v2/macros" ? FakeResponse.Json(page) : FakeResponse.Json("{}"));
        using var _ = fake;

        var result = Parse(await MacrosTools.SearchMacros(zendesk, text: "refund denied"));

        result["macros"]!.AsArray().Select(m => (long)m!["id"]!).Should().Equal(2);
    }

    [Fact]
    public async Task SearchMacros_PagesWithItsOwnCursor_ResumingInsideAZendeskPage()
    {
        var page = $$$"""{"macros":[{{{Macro(1, "Billing::A")}}},{{{Macro(2, "Sales::B")}}},{{{Macro(3, "Billing::Sub::C")}}}],"meta":{"has_more":false}}""";
        var (fake, zendesk) = Build(r => r.Path == "/api/v2/macros" ? FakeResponse.Json(page) : FakeResponse.Json("{}"));
        using var _ = fake;

        var first = Parse(await MacrosTools.SearchMacros(zendesk, category: "billing", page_size: 1));
        first["macros"]!.AsArray().Select(m => (long)m!["id"]!).Should().Equal(1);
        first["pagination_info"]!["has_more"]!.GetValue<bool>().Should().BeTrue();
        var cursor = first["pagination_info"]!["next_cursor"]!.GetValue<string>();

        var second = Parse(await MacrosTools.SearchMacros(zendesk, category: "billing", page_size: 1, after_cursor: cursor));
        second["macros"]!.AsArray().Select(m => (long)m!["id"]!).Should().ContainSingle().Which.Should().Be(3, "subcategories are included");
        second["pagination_info"]!["has_more"]!.GetValue<bool>().Should().BeFalse();
        fake.Requests.Where(r => r.Path == "/api/v2/macros").Should().OnlyContain(r => r.Query("page[after]") == null);
    }

    [Fact]
    public async Task SearchMacros_FollowsZendeskCursor_AndFiltersAccessAndGroup()
    {
        var group = """{"type":"Group","id":77,"ids":[77]}""";
        var page1 = $$$"""{"macros":[{{{Macro(1, "Shared")}}},{{{Macro(2, "Grouped", restriction: group)}}}],"meta":{"has_more":true,"after_cursor":"zc1"}}""";
        var page2 = $$$"""{"macros":[{{{Macro(3, "Personal", restriction: """{"type":"User","id":9}""")}}},{{{Macro(4, "Grouped too", restriction: group)}}}],"meta":{"has_more":false}}""";
        var (fake, zendesk) = Build(r => r.Path switch
        {
            "/api/v2/macros" => FakeResponse.Json(r.Query("page[after]") == "zc1" ? page2 : page1),
            "/api/v2/groups" => FakeResponse.Json("""{"groups":[{"id":77,"name":"Billing"}],"meta":{"has_more":false}}"""),
            "/api/v2/macros/actions" => FakeResponse.Json(Definitions),
            _ => FakeResponse.NotFound(),
        });
        using var _ = fake;

        var result = Parse(await MacrosTools.SearchMacros(zendesk, access: "group", group_id: 77, active: true, sort_by: "usage_30d", sort_order: "asc"));

        var macros = result["macros"]!.AsArray();
        macros.Select(m => (long)m!["id"]!).Should().Equal(2, 4);
        macros[0]!["access"]!.GetValue<string>().Should().Be("group");
        macros[0]!["restricted_to"]![0]!["name"]!.GetValue<string>().Should().Be("Billing");
        result["note"]!.GetValue<string>().Should().Contain("usage", "the fake returned no usage data");

        var first = fake.Requests.First(r => r.Path == "/api/v2/macros");
        first.Query("group_id").Should().Be("77");
        first.Query("active").Should().Be("true");
        first.Query("sort_by").Should().Be("usage_30d");
        first.Query("sort_order").Should().Be("asc");
    }

    [Fact]
    public async Task SearchMacros_MacroIds_UsesShowMany_ReportsNotFound_AndReadsActions()
    {
        var show = $$$"""
            {"macros":[{{{Macro(1, "Close", """[{"field":"status","value":"solved"},{"field":"group_id","value":77},{"field":"comment_value","value":"Thanks {{dc.refund_policy}}"},{"field":"comment_mode_is_public","value":"true"}]""", usage: ",\"usage_1h\":0,\"usage_24h\":1,\"usage_7d\":2,\"usage_30d\":3")}}}]}
            """;
        var (fake, zendesk) = Build(r => r.Path switch
        {
            "/api/v2/macros/show_many" => FakeResponse.Json(show),
            "/api/v2/macros/actions" => FakeResponse.Json(Definitions),
            "/api/v2/dynamic_content/items" => FakeResponse.Json(DynamicContent),
            _ => FakeResponse.NotFound(),
        });
        using var _ = fake;

        var result = Parse(await MacrosTools.SearchMacros(zendesk, macro_ids: [1, 2], include: ["comment", "comment_html"]));

        fake.Requests.First().Query("ids").Should().Be("1,2");
        result["not_found"]!.AsArray().Select(n => (long)n!).Should().Equal(2);
        var macro = result["macros"]![0]!;
        macro["usage"]!["30d"]!.GetValue<int>().Should().Be(3);
        macro["comment_public"]!.GetValue<bool>().Should().BeTrue();
        var actions = macro["actions"]!.AsArray();
        actions.Should().HaveCount(2, "comment actions are reported separately");
        actions[0]!["field_label"]!.GetValue<string>().Should().Be("Status");
        actions[0]!["value_label"]!.GetValue<string>().Should().Be("Solved");
        actions[1]!["value_label"]!.GetValue<string>().Should().Be("Billing");
        macro["comment_text"]!.GetValue<string>().Should().Be("Thanks {{dc.refund_policy}}");
        macro["comment_html"]!.GetValue<string>().Should().Contain("Thanks");
        result["dynamic_content"]![0]!["variants"]!.AsArray().Should().HaveCount(2);
    }

    [Theory]
    [InlineData("text-too-long")]
    [InlineData("both-finders")]
    [InlineData("bad-cursor")]
    [InlineData("bad-sort")]
    public async Task SearchMacros_RejectsBadArguments(string scenario)
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json("""{"macros":[]}"""));
        using var _ = fake;

        Func<Task> act = scenario switch
        {
            "text-too-long" => () => MacrosTools.SearchMacros(zendesk, text: new string('a', 501)),
            "both-finders" => () => MacrosTools.SearchMacros(zendesk, text: "x", macro_ids: [1]),
            "bad-cursor" => () => MacrosTools.SearchMacros(zendesk, after_cursor: "not-ours"),
            _ => () => MacrosTools.SearchMacros(zendesk, sort_by: "popularity"),
        };

        await act.Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Preview_macro

    private const string Ticket = """
        {"ticket":{"id":42,"status":"open","type":"question","priority":"normal","subject":"Help","group_id":null,"assignee_id":null,
                   "requester_id":500,"tags":["a","b"],"custom_fields":[{"id":900,"value":null}],"email_cc_ids":[],"updated_at":"2026-03-01T10:00:00Z"}}
        """;

    private const string ApplyResult = """
        {"result":{"ticket":{"id":42,"status":"solved","tags":["a","refund"],"group_id":77,"custom_fields":[{"id":900,"value":"refunded"}]},
                   "comment":{"body":"Hi Ann, refunded.","html_body":"<p>Hi Ann, refunded.</p>","public":true}}}
        """;

    private static string ReplyMacro(bool active = true, string extra = "") =>
        $$$"""{"macro":{{{Macro(10, "Billing::Refund", $$$"""[{"field":"status","value":"solved"},{"field":"comment_value_html","value":"<p>Hi {{ticket.requester.first_name}}, refunded.</p>"}{{{extra}}}]""", active)}}}}""";

    [Fact]
    public async Task PreviewMacro_WithTicket_ListsChangesAndRenderedComment()
    {
        var (fake, zendesk) = Build(r => r.Path switch
        {
            "/api/v2/macros/10" => FakeResponse.Json(ReplyMacro()),
            "/api/v2/tickets/42" => FakeResponse.Json(Ticket),
            "/api/v2/tickets/42/macros/10/apply" => FakeResponse.Json(ApplyResult),
            "/api/v2/groups/77" => FakeResponse.Json("""{"group":{"id":77,"name":"Billing"}}"""),
            _ => FakeResponse.NotFound(),
        });
        using var _ = fake;

        var result = Parse(await MacrosTools.PreviewMacro(zendesk, macro_id: 10, ticket_id: 42));

        var changes = result["changes"]!.AsArray().ToDictionary(c => (string)c!["field"]!);
        changes["status"]!["before"]!.GetValue<string>().Should().Be("open");
        changes["status"]!["after"]!.GetValue<string>().Should().Be("solved");
        changes["group_id"]!["after_name"]!.GetValue<string>().Should().Be("Billing");
        changes["tags"]!["added"]!.AsArray().Select(n => (string)n!).Should().Equal("refund");
        changes["tags"]!["removed"]!.AsArray().Select(n => (string)n!).Should().Equal("b");
        changes["custom_field_900"]!["after"]!.GetValue<string>().Should().Be("refunded");
        result["comment"]!["body"]!.GetValue<string>().Should().Be("Hi Ann, refunded.");
        result["comment"]!["public"].Should().BeNull("the macro leaves visibility to the agent");
        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task PreviewMacro_WithoutTicket_UsesTheGeneralApplyEndpoint()
    {
        var (fake, zendesk) = Build(r => r.Path switch
        {
            "/api/v2/macros/10" => FakeResponse.Json(ReplyMacro()),
            "/api/v2/macros/10/apply" => FakeResponse.Json("""{"result":{"ticket":{"status":"solved"},"comment":{"body":"Hi {{ticket.requester.first_name}}"}}}"""),
            _ => FakeResponse.NotFound(),
        });
        using var _ = fake;

        var result = Parse(await MacrosTools.PreviewMacro(zendesk, macro_id: 10));

        result["changes"]![0]!["field"]!.GetValue<string>().Should().Be("status");
        result["comment"]!["body"]!.GetValue<string>().Should().Contain("{{ticket.requester.first_name}}");
        result["note"].Should().NotBeNull();
    }

    // ---------------------------------------------------------------- Apply_macro

    private static Func<RecordedRequest, FakeResponse> ApplyRoutes(string macro, string ticket = Ticket, string apply = ApplyResult, string? incidents = null) => r => (r.Method.Method, r.Path) switch
    {
        ("GET", "/api/v2/macros/10") => FakeResponse.Json(macro),
        ("GET", "/api/v2/tickets/42") => FakeResponse.Json(ticket),
        ("GET", "/api/v2/tickets/42/macros/10/apply") => FakeResponse.Json(apply),
        ("GET", "/api/v2/tickets/42/incidents") => FakeResponse.Json(incidents ?? """{"tickets":[],"meta":{"has_more":false}}"""),
        ("GET", "/api/v2/groups/77") => FakeResponse.Json("""{"group":{"id":77,"name":"Billing"}}"""),
        ("GET", "/api/v2/users/show_many") => FakeResponse.Json("""{"users":[{"id":500,"name":"Ann Smith","email":"ann@example.com"}]}"""),
        ("PUT", "/api/v2/tickets/42") => FakeResponse.Json("""{"ticket":{"id":42},"audit":{"events":[{"type":"Comment","attachments":[{"id":1,"file_name":"policy.pdf","size":10}]}]}}"""),
        _ => FakeResponse.NotFound(),
    };

    [Fact]
    public async Task ApplyMacro_WritesOnlyTheChanges_AsAnInternalNoteByDefault()
    {
        var (fake, zendesk) = Build(ApplyRoutes(ReplyMacro()));
        using var _ = fake;
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var result = Parse(await MacrosTools.ApplyMacro(zendesk, cache, ticket_id: 42, macro_id: 10));

        result["result"]!.GetValue<string>().Should().Be("applied");
        result["public"]!.GetValue<bool>().Should().BeFalse("a macro that does not set visibility is an internal note");
        result["attachments"]![0]!["file_name"]!.GetValue<string>().Should().Be("policy.pdf");

        var put = fake.Requests.Single(r => r.Method == HttpMethod.Put);
        var ticket = Parse(put.Body!)["ticket"]!;
        ticket["status"]!.GetValue<string>().Should().Be("solved");
        ticket["group_id"]!.GetValue<long>().Should().Be(77);
        ticket["custom_fields"]![0]!["value"]!.GetValue<string>().Should().Be("refunded");
        ticket["comment"]!["html_body"]!.GetValue<string>().Should().Be("<p>Hi Ann, refunded.</p>");
        ticket["comment"]!["public"]!.GetValue<bool>().Should().BeFalse();
        ticket["safe_update"]!.GetValue<bool>().Should().BeTrue();
        ticket["updated_stamp"]!.GetValue<string>().Should().Be("2026-03-01T10:00:00Z");
        ticket["macro_ids"]![0]!.GetValue<long>().Should().Be(10);
        ticket.AsObject().ContainsKey("subject").Should().BeFalse("unchanged fields are not written back");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ApplyMacro_Repeated_IsAlreadyApplied_UnlessApplyAgain()
    {
        var (fake, zendesk) = Build(ApplyRoutes(ReplyMacro()));
        using var _ = fake;
        using var cache = new MemoryCache(new MemoryCacheOptions());

        await MacrosTools.ApplyMacro(zendesk, cache, ticket_id: 42, macro_id: 10);
        var second = Parse(await MacrosTools.ApplyMacro(zendesk, cache, ticket_id: 42, macro_id: 10));
        second["result"]!.GetValue<string>().Should().Be("already_applied");
        fake.Requests.Count(r => r.Method == HttpMethod.Put).Should().Be(1, "the repeat sent nothing");

        var third = Parse(await MacrosTools.ApplyMacro(zendesk, cache, ticket_id: 42, macro_id: 10, apply_again: true));
        third["result"]!.GetValue<string>().Should().Be("applied");
        fake.Requests.Count(r => r.Method == HttpMethod.Put).Should().Be(2);
    }

    [Fact]
    public async Task ApplyMacro_PublicTrue_IsRefusedWhenPublicRepliesAreOff()
    {
        var (fake, zendesk) = Build(ApplyRoutes(ReplyMacro()));
        using var _ = fake;
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var act = () => MacrosTools.ApplyMacro(zendesk, cache, ticket_id: 42, macro_id: 10, @public: true);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*AllowPublicReplies*");
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyMacro_MacroThatIsPublic_IsRefusedRatherThanDowngraded_WhenPublicRepliesAreOff()
    {
        var macro = ReplyMacro(extra: """,{"field":"comment_mode_is_public","value":"true"}""");
        var (fake, zendesk) = Build(ApplyRoutes(macro));
        using var _ = fake;
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var act = () => MacrosTools.ApplyMacro(zendesk, cache, ticket_id: 42, macro_id: 10);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*public false*");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task ApplyMacro_PublicTrue_WithTheSwitchOn_SendsAPublicReply()
    {
        var (fake, zendesk) = Build(ApplyRoutes(ReplyMacro()), f => f.AllowPublicReplies = true);
        using var _ = fake;
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var result = Parse(await MacrosTools.ApplyMacro(zendesk, cache, ticket_id: 42, macro_id: 10, @public: true));

        Parse(fake.Requests.Single(r => r.Method == HttpMethod.Put).Body!)["ticket"]!["comment"]!["public"]!.GetValue<bool>().Should().BeTrue();
        result["warnings"]!.AsArray().Select(w => (string)w!).Should().Contain(w => w.Contains("public reply"));
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("inactive")]
    [InlineData("side-conversation")]
    public async Task ApplyMacro_RefusesClosedTicketsInactiveMacrosAndSideConversations(string scenario)
    {
        var macro = scenario switch
        {
            "inactive" => ReplyMacro(active: false),
            "side-conversation" => ReplyMacro(extra: """,{"field":"side_conversation","value":["Subject","Body","text/html"]}"""),
            _ => ReplyMacro(),
        };
        var ticket = scenario == "closed" ? Ticket.Replace("\"open\"", "\"closed\"", StringComparison.Ordinal) : Ticket;
        var (fake, zendesk) = Build(ApplyRoutes(macro, ticket));
        using var _ = fake;
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var act = () => MacrosTools.ApplyMacro(zendesk, cache, ticket_id: 42, macro_id: 10);

        await act.Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task ApplyMacro_SolvingAProblemWithUnsolvedIncidents_NeedsSolveIncidents()
    {
        var problem = Ticket.Replace("\"question\"", "\"problem\"", StringComparison.Ordinal);
        var incidents = """{"tickets":[{"id":43,"status":"open"},{"id":44,"status":"solved"}],"meta":{"has_more":false}}""";
        var (fake, zendesk) = Build(ApplyRoutes(ReplyMacro(), problem, incidents: incidents));
        using var _ = fake;
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var act = () => MacrosTools.ApplyMacro(zendesk, cache, ticket_id: 42, macro_id: 10);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*solve_incidents*");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);

        var result = Parse(await MacrosTools.ApplyMacro(zendesk, cache, ticket_id: 42, macro_id: 10, solve_incidents: true));
        result["warnings"]!.AsArray().Select(w => (string)w!).Should().Contain(w => w.Contains("1 incident"));
    }

    [Fact]
    public async Task ApplyMacro_CommentHtml_FillsKnownPlaceholders_AndNamesTheOthers()
    {
        var (fake, zendesk) = Build(ApplyRoutes(ReplyMacro()));
        using var _ = fake;
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var result = Parse(await MacrosTools.ApplyMacro(zendesk, cache, ticket_id: 42, macro_id: 10,
            comment_html: "<p>Dear {{ticket.requester.first_name}}, ticket {{ticket.id}}. {{ticket.requester.name | upcase}}</p>"));

        var html = Parse(fake.Requests.Single(r => r.Method == HttpMethod.Put).Body!)["ticket"]!["comment"]!["html_body"]!.GetValue<string>();
        html.Should().StartWith("<p>Dear Ann, ticket 42.");
        result["warnings"]!.AsArray().Select(w => (string)w!).Should().Contain(w => w.Contains("upcase"));
    }

    [Fact]
    public async Task ApplyMacro_ThatChangesNothing_IsUnchanged_AndWritesNothing()
    {
        var macro = $$$"""{"macro":{{{Macro(10, "Noop", """[{"field":"status","value":"open"}]""")}}}}""";
        var apply = """{"result":{"ticket":{"id":42,"status":"open"},"comment":{}}}""";
        var (fake, zendesk) = Build(ApplyRoutes(macro, apply: apply));
        using var _ = fake;
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var result = Parse(await MacrosTools.ApplyMacro(zendesk, cache, ticket_id: 42, macro_id: 10));

        result["result"]!.GetValue<string>().Should().Be("unchanged");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);
    }

    // ---------------------------------------------------------------- Create_macro

    [Fact]
    public async Task CreateMacro_IsRefusedWhenMacroChangesAreOff()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json("{}"));
        using var _ = fake;

        var act = () => MacrosTools.CreateMacro(zendesk, title: "T", comment_html: "<p>x</p>");

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*AllowMacroChanges*");
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateMacro_PostsCommentActionsAndGroupRestriction()
    {
        var (fake, zendesk) = Build(r => (r.Method.Method, r.Path) switch
        {
            ("GET", "/api/v2/macros/actions") => FakeResponse.Json(Definitions),
            ("GET", "/api/v2/macros/search") => FakeResponse.Json("""{"macros":[]}"""),
            ("POST", "/api/v2/macros") => FakeResponse.Json($$$"""{"macro":{{{Macro(99, "Billing::Refund", """[{"field":"status","value":"solved"}]""", restriction: """{"type":"Group","id":77,"ids":[77]}""")}}}}"""),
            ("GET", "/api/v2/groups") => FakeResponse.Json("""{"groups":[{"id":77,"name":"Billing"}],"meta":{"has_more":false}}"""),
            _ => FakeResponse.NotFound(),
        }, f => f.AllowMacroChanges = true);
        using var _ = fake;

        var result = Parse(await MacrosTools.CreateMacro(zendesk, title: "Billing::Refund", comment_html: "<p>Done</p>", comment_public: false,
            actions: [Action("status", "solved")], access: "group", group_ids: [77]));

        result["result"]!.GetValue<string>().Should().Be("created");
        result["id"]!.GetValue<long>().Should().Be(99);
        result["url"]!.GetValue<string>().Should().EndWith("/macros/99");

        var body = Parse(fake.Requests.Single(r => r.Method == HttpMethod.Post).Body!)["macro"]!;
        body["title"]!.GetValue<string>().Should().Be("Billing::Refund");
        body["active"]!.GetValue<bool>().Should().BeTrue();
        body["restriction"]!["type"]!.GetValue<string>().Should().Be("Group");
        body["restriction"]!["ids"]![0]!.GetValue<long>().Should().Be(77);
        var actions = body["actions"]!.AsArray().Select(a => ((string)a!["field"]!, (string)a["value"]!)).ToList();
        actions.Should().Equal(("status", "solved"), ("comment_value_html", "<p>Done</p>"), ("comment_mode_is_public", "false"));
        result["warnings"]!.AsArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateMacro_SameTitleActionsAndAccess_IsAlreadyExists()
    {
        var existing = Macro(5, "Refund", """[{"field":"comment_value_html","value":"<p>Done</p>"}]""");
        var (fake, zendesk) = Build(r => r.Path switch
        {
            "/api/v2/macros/search" => FakeResponse.Json($$$"""{"macros":[{{{existing}}}]}"""),
            "/api/v2/macros/actions" => FakeResponse.Json(Definitions),
            _ => FakeResponse.NotFound(),
        }, f => f.AllowMacroChanges = true);
        using var _ = fake;

        var result = Parse(await MacrosTools.CreateMacro(zendesk, title: "Refund", comment_html: "<p>Done</p>"));

        result["result"]!.GetValue<string>().Should().Be("already_exists");
        result["id"]!.GetValue<long>().Should().Be(5);
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task CreateMacro_UnknownFieldOrValue_IsRefusedWithTheValidOnes()
    {
        var (fake, zendesk) = Build(r => FakeResponse.Json(Definitions), f => f.AllowMacroChanges = true);
        using var _ = fake;

        var badField = () => MacrosTools.CreateMacro(zendesk, title: "T", actions: [Action("colour", "red")]);
        await badField.Should().ThrowAsync<ArgumentException>().WithMessage("*priority*status*");

        var badValue = () => MacrosTools.CreateMacro(zendesk, title: "T", actions: [Action("status", "exploded")]);
        await badValue.Should().ThrowAsync<ArgumentException>().WithMessage("*open*solved*");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData("group-without-ids")]
    [InlineData("ids-without-group")]
    [InlineData("title-too-long")]
    [InlineData("empty")]
    public async Task CreateMacro_ValidatesArguments(string scenario)
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json(Definitions), f => f.AllowMacroChanges = true);
        using var _ = fake;

        Func<Task> act = scenario switch
        {
            "group-without-ids" => () => MacrosTools.CreateMacro(zendesk, title: "T", comment_html: "x", access: "group"),
            "ids-without-group" => () => MacrosTools.CreateMacro(zendesk, title: "T", comment_html: "x", group_ids: [1]),
            "title-too-long" => () => MacrosTools.CreateMacro(zendesk, title: new string('t', 256), comment_html: "x"),
            _ => () => MacrosTools.CreateMacro(zendesk, title: "T"),
        };

        await act.Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Update_macro

    private static string Existing(string restriction = "null") =>
        $$$"""{"macro":{{{Macro(10, "Refund", """[{"field":"status","value":"solved"},{"field":"comment_value_html","value":"<p>{{dc.refund_policy}}</p>"}]""", restriction: restriction)}}}}""";

    [Fact]
    public async Task UpdateMacro_CommentHtml_ReplacesOnlyTheComment_AndWarns()
    {
        var (fake, zendesk) = Build(r => (r.Method.Method, r.Path) switch
        {
            ("GET", "/api/v2/macros/10") => FakeResponse.Json(Existing()),
            ("PUT", "/api/v2/macros/10") => FakeResponse.Json($$$"""{"macro":{{{Macro(10, "Refund", """[{"field":"status","value":"solved"},{"field":"comment_value_html","value":"<p>New</p>"}]""")}}}}"""),
            ("GET", "/api/v2/macros/actions") => FakeResponse.Json(Definitions),
            _ => FakeResponse.NotFound(),
        }, f => f.AllowMacroChanges = true);
        using var _ = fake;

        var result = Parse(await MacrosTools.UpdateMacro(zendesk, macro_id: 10, comment_html: "<p>New</p>"));

        result["result"]!.GetValue<string>().Should().Be("updated");
        var change = result["changes"]!.AsArray().Single();
        change!["field"]!.GetValue<string>().Should().Be("comment_html");
        change["before"]!.GetValue<string>().Should().Contain("dc.refund_policy");

        var body = Parse(fake.Requests.Single(r => r.Method == HttpMethod.Put).Body!)["macro"]!;
        body.AsObject().Select(p => p.Key).Should().Equal("actions");
        body["actions"]!.AsArray().Select(a => (string)a!["field"]!).Should().Equal("status", "comment_value_html");
        var warnings = result["warnings"]!.AsArray().Select(w => (string)w!).ToList();
        warnings.Should().Contain(w => w.Contains("every agent"));
        warnings.Should().Contain(w => w.Contains("{{dc.refund_policy}}"));
    }

    [Fact]
    public async Task UpdateMacro_NothingToChange_IsUnchanged_AndWritesNothing()
    {
        var (fake, zendesk) = Build(r => r.Path switch
        {
            "/api/v2/macros/10" => FakeResponse.Json(Existing()),
            "/api/v2/macros/actions" => FakeResponse.Json(Definitions),
            _ => FakeResponse.NotFound(),
        }, f => f.AllowMacroChanges = true);
        using var _ = fake;

        var result = Parse(await MacrosTools.UpdateMacro(zendesk, macro_id: 10, title: "Refund", position: 10));

        result["result"]!.GetValue<string>().Should().Be("unchanged");
        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task UpdateMacro_TitleAndAccess_ReportBeforeAndAfter()
    {
        var (fake, zendesk) = Build(r => (r.Method.Method, r.Path) switch
        {
            ("GET", "/api/v2/macros/10") => FakeResponse.Json(Existing()),
            ("PUT", "/api/v2/macros/10") => FakeResponse.Json(Existing("""{"type":"Group","id":3,"ids":[3]}""")),
            ("GET", "/api/v2/groups") => FakeResponse.Json("""{"groups":[],"meta":{"has_more":false}}"""),
            ("GET", "/api/v2/macros/actions") => FakeResponse.Json(Definitions),
            _ => FakeResponse.NotFound(),
        }, f => f.AllowMacroChanges = true);
        using var _ = fake;

        var result = Parse(await MacrosTools.UpdateMacro(zendesk, macro_id: 10, title: "Refund v2", access: "group", group_ids: [3]));

        var changes = result["changes"]!.AsArray().ToDictionary(c => (string)c!["field"]!);
        changes["title"]!["before"]!.GetValue<string>().Should().Be("Refund");
        changes["access"]!["before"]!["access"]!.GetValue<string>().Should().Be("shared");
        changes["access"]!["after"]!["group_ids"]![0]!.GetValue<long>().Should().Be(3);
        var body = Parse(fake.Requests.Single(r => r.Method == HttpMethod.Put).Body!)["macro"]!;
        body["title"]!.GetValue<string>().Should().Be("Refund v2");
        body["restriction"]!["type"]!.GetValue<string>().Should().Be("Group");
    }

    [Fact]
    public async Task UpdateMacro_CommentHtmlWithActions_IsRefused()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json(Existing()), f => f.AllowMacroChanges = true);
        using var _ = fake;

        var act = () => MacrosTools.UpdateMacro(zendesk, macro_id: 10, comment_html: "<p>x</p>", actions: [Action("status", "open")]);

        await act.Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Set_macros_active

    [Fact]
    public async Task SetMacrosActive_SendsOnlyMacrosThatChange_AndWarnsAboutRecentUse()
    {
        var show = $$$"""
            {"macros":[{{{Macro(1, "Used", usage: ",\"usage_7d\":4,\"usage_30d\":9")}}},{{{Macro(2, "Already off", active: false)}}}]}
            """;
        var (fake, zendesk) = Build(r => (r.Method.Method, r.Path) switch
        {
            ("GET", "/api/v2/macros/show_many") => FakeResponse.Json(show),
            ("PUT", "/api/v2/macros/update_many") => FakeResponse.Json("""{"macros":[]}"""),
            _ => FakeResponse.NotFound(),
        }, f => f.AllowMacroChanges = true);
        using var _ = fake;

        var result = Parse(await MacrosTools.SetMacrosActive(zendesk, macro_ids: [1, 2, 3], active: false));

        var rows = result["macros"]!.AsArray().ToDictionary(m => (long)m!["id"]!, m => (string)m!["result"]!);
        rows.Should().BeEquivalentTo(new Dictionary<long, string> { [1] = "deactivated", [2] = "unchanged", [3] = "not_found" });
        result["macros"]![0]!["usage_30d"]!.GetValue<int>().Should().Be(9);
        result["summary"]!.GetValue<string>().Should().Be("1 deactivated, 1 unchanged, 1 not found");
        result["warnings"]!.AsArray().Should().ContainSingle();

        var body = Parse(fake.Requests.Single(r => r.Method == HttpMethod.Put).Body!);
        body["macros"]!.ToJsonString().Should().Be("""[{"id":1,"active":false}]""");
    }

    [Fact]
    public async Task SetMacrosActive_AllAlreadyInState_WritesNothing()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json($$$"""{"macros":[{{{Macro(1, "On")}}}]}"""), f => f.AllowMacroChanges = true);
        using var _ = fake;

        await MacrosTools.SetMacrosActive(zendesk, macro_ids: [1], active: true);

        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task SetMacrosActive_IsRefusedWhenMacroChangesAreOff_OrTooManyIds()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json("{}"));
        using var _ = fake;

        var off = () => MacrosTools.SetMacrosActive(zendesk, macro_ids: [1], active: false);
        await off.Should().ThrowAsync<UnauthorizedAccessException>();

        var (fake2, enabled) = Build(_ => FakeResponse.Json("{}"), f => f.AllowMacroChanges = true);
        using var __ = fake2;
        var tooMany = () => MacrosTools.SetMacrosActive(enabled, macro_ids: Enumerable.Range(1, 101).Select(i => (long)i).ToArray(), active: false);
        await tooMany.Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().BeEmpty();
        fake2.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UpstreamRefusal_SurfacesZendesksExplanation()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.WithStatus(HttpStatusCode.Forbidden, """{"error":"Forbidden"}"""));
        using var _ = fake;

        var act = () => MacrosTools.PreviewMacro(zendesk, macro_id: 1);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*403*");
    }
}
