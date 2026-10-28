using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

public class TicketsToolsTests
{
    private const string Ticket101 = """{"ticket":{"id":101,"subject":"Login fails","description":"Cannot sign in","status":"open","type":"incident","priority":"high","problem_id":900,"requester_id":501,"assignee_id":701,"organization_id":601,"custom_status_id":44,"tags":["login"],"email_cc_ids":[502],"via":{"channel":"email"},"created_at":"2026-01-02T09:00:00Z","updated_at":"2026-01-02T10:00:00Z"},"users":[{"id":701,"name":"Agent Smith","email":"smith@example.com","role":"agent"},{"id":501,"name":"Alice","email":"alice@example.com","role":"end-user"}],"organizations":[{"id":601,"name":"Acme Ltd","external_id":"A-1"}]}""";

    private static FakeResponse Json(string body) => FakeResponse.Json(body);

    private static FakeResponse Count(long n) => Json($"{{\"count\":{n}}}");

    private static void NoDeletes(FakeZendesk fake) =>
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete, "a ticket tool must never issue DELETE");

    // ---------- Search_tickets ----------

    [Fact]
    public async Task SearchTickets_Json_BuildsExportRequestAndShapesResult()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/search/export" => Json("""{"results":[{"id":101,"status":"open","subject":"S","created_at":"2026-01-02T09:00:00Z"}],"meta":{"has_more":true,"after_cursor":"c2"}}"""),
            "/api/v2/search/count" => Count(42),
            _ => FakeResponse.NotFound(),
        });
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = JsonNode.Parse(await TicketsTools.SearchTickets(zendesk, status: "open", priority: "high,urgent",
            page_size: 25, after_cursor: "c1", include_fields: ["subject"]))!;

        var export = fake.Requests.Single(r => r.Path == "/api/v2/search/export");
        export.Query("query").Should().Be("type:ticket status:open priority:high priority:urgent");
        export.Query("filter[type]").Should().Be("ticket");
        export.Query("page[size]").Should().Be("25");
        export.Query("page[after]").Should().Be("c1");
        fake.Requests.Single(r => r.Path == "/api/v2/search/count").Query("query").Should().Be("type:ticket status:open priority:high priority:urgent");

        result["total_results"]!.GetValue<long>().Should().Be(42);
        result["tickets"]![0]!.ToJsonString().Should().Be("""{"id":101,"status":"open","created_at":"2026-01-02T09:00:00Z","subject":"S"}""");
        result["pagination_info"]!["has_more"]!.GetValue<bool>().Should().BeTrue();
        result["pagination_info"]!["next_cursor"]!.GetValue<string>().Should().Be("c2");
        result["pagination_info"]!["page_size"]!.GetValue<int>().Should().Be(25);
        result["search_url"]!.GetValue<string>().Should().StartWith("https://acme.zendesk.com/agent/search/1?type=ticket&q=");
        result["pages_fetched"]!.GetValue<int>().Should().Be(1);
        NoDeletes(fake);
    }

    [Fact]
    public async Task SearchTickets_IncludeFieldsAndIncludes_ResolveNamesMetricsAndRelatedRecords()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/search/export" => Json("""{"results":[{"id":101,"status":"open","created_at":"x","brand_id":9,"organization_id":601,"requester_id":501},{"id":102,"status":"new","created_at":"y","brand_id":9,"organization_id":601,"requester_id":501}],"meta":{"has_more":false}}"""),
            "/api/v2/search/count" => Count(2),
            "/api/v2/brands" => Json("""{"brands":[{"id":9,"name":"Main brand"}],"meta":{"has_more":false}}"""),
            "/api/v2/organizations/show_many" => Json("""{"organizations":[{"id":601,"name":"Acme Ltd","external_id":"A-1","organization_fields":{"tier":"gold"}}]}"""),
            "/api/v2/users/show_many" => Json("""{"users":[{"id":501,"name":"Alice","email":"alice@example.com","role":"end-user","photo":{"big":"thing"}}]}"""),
            "/api/v2/tickets/show_many" => Json("""{"tickets":[{"id":101,"comment_count":3}],"metric_sets":[{"ticket_id":101,"reply_time_in_minutes":{"calendar":10,"business":5},"reopens":1,"replies":2,"solved_at":null}]}"""),
            _ => FakeResponse.NotFound(),
        });
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = JsonNode.Parse(await TicketsTools.SearchTickets(zendesk,
            include_fields: ["metrics", "brand_name", "organization_name", "comment_count"], include: ["users", "organizations"]))!;

        fake.Requests.Single(r => r.Path == "/api/v2/organizations/show_many").Query("ids").Should().Be("601");
        var showMany = fake.Requests.Single(r => r.Path == "/api/v2/tickets/show_many");
        showMany.Query("ids").Should().Be("101,102");
        showMany.Query("include").Should().Be("metric_sets,comment_count");

        var first = result["tickets"]![0]!;
        first["brand_name"]!.GetValue<string>().Should().Be("Main brand");
        first["organization_name"]!.GetValue<string>().Should().Be("Acme Ltd");
        first["comment_count"]!.GetValue<int>().Should().Be(3);
        first["metrics"]!["first_reply_time"]!["business"]!.GetValue<int>().Should().Be(5);
        first["metrics"]!["reopens"]!.GetValue<int>().Should().Be(1);
        result["tickets"]![1]!["metrics"].Should().BeNull();

        result["organizations"]!.AsArray().Should().ContainSingle();
        result["organizations"]![0]!["organization_fields"]!["tier"]!.GetValue<string>().Should().Be("gold");
        result["users"]!.AsArray().Should().ContainSingle();
        result["users"]![0]!["email"]!.GetValue<string>().Should().Be("alice@example.com");
        result["users"]![0]!["photo"].Should().BeNull("only the useful user fields are passed on");
    }

    [Fact]
    public async Task SearchTickets_CustomObjects_LooksUpByExternalIdAndAttachesUnderKey()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/search/export" => Json("""{"results":[{"id":101,"status":"open","created_at":"x"},{"id":102,"status":"open","created_at":"y"}],"meta":{"has_more":false}}"""),
            "/api/v2/search/count" => Count(2),
            "/api/v2/custom_objects/contract/records" => Json("""{"custom_object_records":[{"id":"r1","name":"C-1","external_id":"101","custom_object_fields":{"value":1000}}]}"""),
            _ => FakeResponse.NotFound(),
        });
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = JsonNode.Parse(await TicketsTools.SearchTickets(zendesk, include_custom_objects: ["contract"]))!;

        fake.Requests.Single(r => r.Path.StartsWith("/api/v2/custom_objects/", StringComparison.Ordinal))
            .Query("filter[external_ids]").Should().Be("101,102");
        result["tickets"]![0]!["contract"]!["value"]!.GetValue<int>().Should().Be(1000);
        result["tickets"]![1]!["contract"].Should().BeNull();
        result["custom_objects_note"].Should().NotBeNull();
    }

    [Theory]
    [InlineData("../tickets")]
    [InlineData("status")]
    public async Task SearchTickets_RejectsBadCustomObjectKeys(string key)
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.NotFound());
        var act = () => TicketsTools.SearchTickets(TestHelpers.BuildZendeskService(fake.Client), include_custom_objects: [key]);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*include_custom_objects*");
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchTickets_Csv_ExpandsCustomFieldsQuotesAndNeutralisesFormulas()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/search/export" => Json("""{"results":[{"id":101,"status":"open","created_at":"2026-01-02","subject":"Hello, \"world\"","organization_id":601,"custom_fields":[{"id":7,"value":"=HYPERLINK(1)"}]},{"id":102,"status":"new","created_at":"2026-01-01","subject":"Plain","custom_fields":[{"id":7,"value":null}]}],"meta":{"has_more":false}}"""),
            "/api/v2/search/count" => Count(2),
            "/api/v2/ticket_fields" => Json("""{"ticket_fields":[{"id":7,"title":"Product"}],"meta":{"has_more":false}}"""),
            "/api/v2/organizations/show_many" => Json("""{"organizations":[{"id":601,"name":"Acme Ltd","external_id":"A-1","domain_names":["acme.com","acme.co.uk"],"tags":[],"organization_fields":{"tier":"gold"}}]}"""),
            _ => FakeResponse.NotFound(),
        });
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = JsonNode.Parse(await TicketsTools.SearchTickets(zendesk, output_format: "csv",
            include_fields: ["subject", "custom_fields"], include: ["organizations"]))!;

        result["format"]!.GetValue<string>().Should().Be("csv");
        result["total_results"]!.GetValue<long>().Should().Be(2);
        result["first_created_at"]!.GetValue<string>().Should().Be("2026-01-02");
        result["last_created_at"]!.GetValue<string>().Should().Be("2026-01-01");
        var lines = result["csv"]!.GetValue<string>().Split("\r\n");
        lines[0].Should().Be("id,status,created_at,subject,Product,organization.id,organization.name,organization.external_id,organization.domain_names,organization.tags,organization.tier");
        lines[1].Should().Be("101,open,2026-01-02,\"Hello, \"\"world\"\"\",'=HYPERLINK(1),601,Acme Ltd,A-1,\"acme.com, acme.co.uk\",,gold");
        lines[2].Should().Be("102,new,2026-01-01,Plain,,,,,,,");
        fake.Requests.Single(r => r.Path == "/api/v2/search/export").Query("page[size]").Should().Be("100");
    }

    [Fact]
    public async Task SearchTickets_Csv_RejectsCursor()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/search/count" ? Count(1) : FakeResponse.NotFound());
        var act = () => TicketsTools.SearchTickets(TestHelpers.BuildZendeskService(fake.Client), output_format: "csv", after_cursor: "abc");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*after_cursor*");
    }

    [Theory]
    [InlineData(null, new[] { "secret_field" }, null, "*include_fields*")]
    [InlineData(null, null, new[] { "groups" }, "*include*")]
    [InlineData("xml", null, null, "*output_format*")]
    public async Task SearchTickets_ValidatesEnums(string? format, string[]? fields, string[]? include, string message)
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.NotFound());
        var act = () => TicketsTools.SearchTickets(TestHelpers.BuildZendeskService(fake.Client), output_format: format, include_fields: fields, include: include);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchTickets_RejectsPageSizeOutOfRange()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.NotFound());
        var act = () => TicketsTools.SearchTickets(TestHelpers.BuildZendeskService(fake.Client), page_size: 101);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*page_size*");
    }

    // ---------- Count_tickets ----------

    [Fact]
    public async Task CountTickets_WithoutGroupBy_ReturnsOneCount()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/search/count" ? Count(17) : FakeResponse.NotFound());
        var result = JsonNode.Parse(await TicketsTools.CountTickets(TestHelpers.BuildZendeskService(fake.Client), group: "Support"))!;

        result["count"]!.GetValue<long>().Should().Be(17);
        result["query"]!.GetValue<string>().Should().Be("type:ticket group:Support");
        result["groups"]!.AsArray().Should().BeEmpty();
        fake.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task CountTickets_GroupByStatus_CountsEachValueKeepsZerosAndAddsRemainder()
    {
        var counts = new Dictionary<string, long>
        {
            ["type:ticket"] = 20, ["type:ticket status:new"] = 2, ["type:ticket status:open"] = 10,
            ["type:ticket status:pending"] = 0, ["type:ticket status:hold"] = 0, ["type:ticket status:solved"] = 5,
            ["type:ticket status:closed"] = 1,
        };
        using var fake = TestHelpers.CreateFakeZendesk(r => Count(counts[r.Query("query")!]));
        var result = JsonNode.Parse(await TicketsTools.CountTickets(TestHelpers.BuildZendeskService(fake.Client), group_by: "status"))!;

        fake.Requests.Should().HaveCount(7);
        var groups = result["groups"]!.AsArray();
        groups.Select(g => g!["name"]!.GetValue<string>()).Should().Equal("new", "open", "pending", "hold", "solved", "closed", "(other)");
        groups[^1]!["key"].Should().BeNull();
        groups[^1]!["count"]!.GetValue<long>().Should().Be(2);
        result["count"]!.GetValue<long>().Should().Be(20);
    }

    [Fact]
    public async Task CountTickets_RefusesGroupingByAFilteredDimension()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => Count(1));
        var act = () => TicketsTools.CountTickets(TestHelpers.BuildZendeskService(fake.Client), status: "open", group_by: "status");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*status filter*");
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CountTickets_GroupByBrand_DropsEmptyBrands()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/brands" => Json("""{"brands":[{"id":1,"name":"One"},{"id":2,"name":"Two"}],"meta":{"has_more":false}}"""),
            _ => Count(r.Query("query") switch { "type:ticket" => 10, "type:ticket brand_id:1" => 7, _ => 0 }),
        });
        var result = JsonNode.Parse(await TicketsTools.CountTickets(TestHelpers.BuildZendeskService(fake.Client), group_by: "brand"))!;

        var groups = result["groups"]!.AsArray();
        groups.Should().HaveCount(2);
        groups[0]!["key"]!.GetValue<long>().Should().Be(1);
        groups[0]!["name"]!.GetValue<string>().Should().Be("One");
        groups[1]!["name"]!.GetValue<string>().Should().Be("(other)");
        groups[1]!["count"]!.GetValue<long>().Should().Be(3);
    }

    [Fact]
    public async Task CountTickets_GroupBySatisfaction_AddsCsat()
    {
        var counts = new Dictionary<string, long>
        {
            ["satisfaction:good"] = 8, ["satisfaction:goodwithcomment"] = 3, ["satisfaction:bad"] = 2,
            ["satisfaction:badwithcomment"] = 1, ["satisfaction:offered"] = 5, ["satisfaction:unoffered"] = 5,
        };
        using var fake = TestHelpers.CreateFakeZendesk(r =>
        {
            var query = r.Query("query")!;
            var term = query.Split(' ').Last();
            return Count(counts.TryGetValue(term, out var n) ? n : 20);
        });
        var result = JsonNode.Parse(await TicketsTools.CountTickets(TestHelpers.BuildZendeskService(fake.Client), group_by: "satisfaction"))!;

        result["csat"]!["rated"]!.GetValue<long>().Should().Be(10);
        result["csat"]!["score_pct"]!.GetValue<double>().Should().Be(80.0);
        result["groups"]!.AsArray().Should().HaveCount(6, "with-comment counts are subsets, so good+bad+offered+unoffered = total and there is no remainder");
    }

    [Fact]
    public async Task CountTickets_GroupByWeek_BucketsMondayToSundayInUserTimeZone()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/users/me" => Json("""{"user":{"id":1,"time_zone":"London","iana_time_zone":"Europe/London"}}"""),
            _ => Count(r.Query("query")!.Contains("created>=2026-01-05 created<2026-01-12", StringComparison.Ordinal) ? 4 : 6),
        });
        var result = JsonNode.Parse(await TicketsTools.CountTickets(TestHelpers.BuildZendeskService(fake.Client),
            start_date: "2026-01-07", end_date: "2026-01-20", group_by: "week"))!;

        result["date_basis"]!.GetValue<string>().Should().Be("created");
        result["timezone"]!["iana_time_zone"]!.GetValue<string>().Should().Be("Europe/London");
        var keys = result["groups"]!.AsArray().Select(g => g!["key"]?.GetValue<string>()).ToList();
        keys.Should().StartWith(["2026-01-05", "2026-01-12", "2026-01-19"]);
        result["groups"]![0]!["name"]!.GetValue<string>().Should().Be("2026-01-05 to 2026-01-11");
        result["groups"]![0]!["count"]!.GetValue<long>().Should().Be(4);
    }

    [Fact]
    public async Task CountTickets_TimeBucketsNeedAStartDate()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => Count(1));
        var act = () => TicketsTools.CountTickets(TestHelpers.BuildZendeskService(fake.Client), group_by: "day");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*start_date*");
    }

    [Fact]
    public async Task CountTickets_TooManyBucketsRefused()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/users/me" ? Json("""{"user":{}}""") : Count(1));
        var act = () => TicketsTools.CountTickets(TestHelpers.BuildZendeskService(fake.Client), start_date: "2025-01-01", end_date: "2026-01-01", group_by: "day");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*more than 200*");
        fake.Requests.Should().NotContain(r => r.Path == "/api/v2/search/count");
    }

    [Fact]
    public async Task CountTickets_GroupByCustomField_UsesOptionTags()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/ticket_fields/77" => Json("""{"ticket_field":{"id":77,"title":"Product","type":"tagger","custom_field_options":[{"name":"Widget","value":"product_widget"},{"name":"Gadget","value":"product_gadget"}]}}"""),
            _ => Count(r.Query("query") switch { "type:ticket" => 9, "type:ticket custom_field_77:product_widget" => 4, _ => 0 }),
        });
        var result = JsonNode.Parse(await TicketsTools.CountTickets(TestHelpers.BuildZendeskService(fake.Client), group_by: "custom_field", group_by_field_id: 77))!;

        var groups = result["groups"]!.AsArray();
        groups[0]!["key"]!.GetValue<string>().Should().Be("product_widget");
        groups[0]!["name"]!.GetValue<string>().Should().Be("Widget");
        groups[1]!["name"]!.GetValue<string>().Should().Be("(none)");
        groups[1]!["count"]!.GetValue<long>().Should().Be(5);
        result["field"]!["title"]!.GetValue<string>().Should().Be("Product");
    }

    [Fact]
    public async Task CountTickets_GroupByCustomFieldNeedsFieldId()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => Count(1));
        var act = () => TicketsTools.CountTickets(TestHelpers.BuildZendeskService(fake.Client), group_by: "custom_field");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*group_by_field_id*");
    }

    // ---------- Get_ticket ----------

    [Fact]
    public async Task GetTicket_ShapesTicketWithLabelsAndIncludes()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/101" => Json(Ticket101),
            "/api/v2/custom_statuses/44" => Json("""{"custom_status":{"id":44,"agent_label":"Awaiting vendor","status_category":"pending"}}"""),
            _ => FakeResponse.NotFound(),
        });
        var result = JsonNode.Parse(await TicketsTools.GetTicket(TestHelpers.BuildZendeskService(fake.Client), 101, include: ["users", "organizations"]))!;

        fake.Requests[0].Query("include").Should().Be("users,organizations");
        var ticket = result["ticket"]!;
        ticket["custom_status_label"]!.GetValue<string>().Should().Be("Awaiting vendor");
        ticket["organization_name"]!.GetValue<string>().Should().Be("Acme Ltd");
        ticket["assignee"]!["name"]!.GetValue<string>().Should().Be("Agent Smith");
        ticket["problem_id"]!.GetValue<long>().Should().Be(900);
        ticket["email_cc_ids"]![0]!.GetValue<long>().Should().Be(502);
        result["users"]!.AsArray().Should().HaveCount(2);
        result["organizations"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task GetTicket_IncidentsForProblemTicket()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/900" => Json("""{"ticket":{"id":900,"type":"problem","status":"open"}}"""),
            "/api/v2/tickets/900/incidents" => Json("""{"tickets":[{"id":101,"subject":"S","status":"open","requester_id":5,"description":"long"}],"count":1}"""),
            _ => FakeResponse.NotFound(),
        });
        var result = JsonNode.Parse(await TicketsTools.GetTicket(TestHelpers.BuildZendeskService(fake.Client), 900, include: ["incidents"]))!;

        result["incident_count"]!.GetValue<long>().Should().Be(1);
        result["incidents"]![0]!["url"]!.GetValue<string>().Should().Be("https://acme.zendesk.com/agent/tickets/101");
        result["incidents"]![0]!["description"].Should().BeNull();
        result["ticket"]!["problem_id"].Should().BeNull("only incidents report a problem_id");
        result["users"].Should().BeNull("users were not asked for");
    }

    [Fact]
    public async Task GetTicket_RejectsNonPositiveId()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.NotFound());
        var act = () => TicketsTools.GetTicket(TestHelpers.BuildZendeskService(fake.Client), 0);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*ticket_id*");
    }

    // ---------- Get_ticket_conversation ----------

    [Fact]
    public async Task GetTicketConversation_MapsCommentsToEntries()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/101" => Json(Ticket101),
            "/api/v2/tickets/101/comments" => Json("""
                {"comments":[
                  {"id":1,"author_id":501,"public":true,"created_at":"t1","html_body":"<p>Hello&nbsp;there</p><p><img src=\"https://acme.zendesk.com/attachments/token/x/?name=a.png\" alt=\"screen\"></p><ul><li>one</li></ul>","attachments":[{"id":55,"file_name":"a.png","content_type":"image/png","size":10,"content_url":"https://acme.zendesk.com/attachments/token/x/?name=a.png","inline":true}]},
                  {"id":2,"author_id":701,"public":false,"created_at":"t2","plain_body":"Internal note","attachments":[]}
                ],"users":[{"id":501,"name":"Alice","role":"end-user"},{"id":701,"name":"Agent Smith","role":"agent"}],"meta":{"has_more":false}}
                """),
            _ => FakeResponse.NotFound(),
        });
        var result = JsonNode.Parse(await TicketsTools.GetTicketConversation(TestHelpers.BuildZendeskService(fake.Client), 101))!;

        fake.Requests.Single(r => r.Path.EndsWith("/comments", StringComparison.Ordinal)).Query("include").Should().Be("users");
        result["source"]!.GetValue<string>().Should().Be("comments");
        var entries = result["entries"]!.AsArray();
        entries[0]!["side"]!.GetValue<string>().Should().Be("customer");
        entries[0]!["author"]!.GetValue<string>().Should().Be("Alice");
        entries[0]!["message"]!.GetValue<string>().Should().Be("Hello there\n[Image: screen](https://acme.zendesk.com/attachments/token/x/?name=a.png)\n\n- one");
        entries[0]!["attachments"]![0]!["inline"]!.GetValue<bool>().Should().BeTrue();
        entries[1]!["side"]!.GetValue<string>().Should().Be("agent");
        entries[1]!["visibility"]!.GetValue<string>().Should().Be("private");
        entries[1]!["message"]!.GetValue<string>().Should().Be("Internal note");
    }

    [Fact]
    public async Task GetTicketConversation_MessagingTicketFallsBackToCommentsWhenLogUnavailable()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/5" => Json("""{"ticket":{"id":5,"via":{"channel":"native_messaging"}}}"""),
            "/api/v2/tickets/5/comments" => Json("""{"comments":[{"id":1,"author_id":-1,"public":true,"created_at":"t","body":"Hi"}],"users":[],"meta":{"has_more":false}}"""),
            _ => FakeResponse.NotFound(),
        });
        var result = JsonNode.Parse(await TicketsTools.GetTicketConversation(TestHelpers.BuildZendeskService(fake.Client), 5))!;

        fake.Requests.Should().Contain(r => r.Path == "/api/v2/tickets/5/conversation_log");
        result["source"]!.GetValue<string>().Should().Be("comments");
        result["entries"]![0]!["author"]!.GetValue<string>().Should().Be("(system)");
    }

    [Fact]
    public async Task GetTicketConversation_UsesConversationLogForMessaging()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/5" => Json("""{"ticket":{"id":5,"via":{"channel":"whatsapp"}}}"""),
            "/api/v2/tickets/5/conversation_log" => Json("""{"events":[{"created_at":"t1","author":{"type":"user","display_name":"Bob"},"content":{"type":"text","body":"Help"}},{"created_at":"t2","author":{"type":"agent","display_name":"Ann"},"content":{"type":"text","body":"Sure"}}]}"""),
            _ => FakeResponse.NotFound(),
        });
        var result = JsonNode.Parse(await TicketsTools.GetTicketConversation(TestHelpers.BuildZendeskService(fake.Client), 5))!;

        result["source"]!.GetValue<string>().Should().Be("conversation_log");
        result["entries"]![0]!["side"]!.GetValue<string>().Should().Be("customer");
        result["entries"]![1]!["author"]!.GetValue<string>().Should().Be("Ann");
        fake.Requests.Should().NotContain(r => r.Path.EndsWith("/comments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetTicketConversation_ReadsTheLiveConversationLogShape()
    {
        // The shape the live API returns: HTML bodies, visibility under metadata.is_public, cursor pages.
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/tickets/5" => Json("""{"ticket":{"id":5,"via":{"channel":"native_messaging"}}}"""),
            "/api/v2/tickets/5/conversation_log" when r.Query("page[after]") is null => Json("""
                {"events":[{"id":"e1","type":"Comment","created_at":"t1","author":{"type":"user","zen:support:user_id":7,"display_name":"Bob"},
                  "content":{"type":"html","body":"<div><p>Help &amp; thanks</p></div>"},"attachments":[],"metadata":{"system":{},"custom":{},"is_public":true}}],
                 "meta":{"has_more":true,"after_cursor":"c1"},"links":{}}
                """),
            "/api/v2/tickets/5/conversation_log" => Json("""
                {"events":[{"id":"e2","type":"Comment","created_at":"t2","author":{"type":"agent","zen:support:user_id":8,"display_name":"Ann"},
                  "content":{"type":"html","body":"<p>Checking</p>"},"attachments":[],"metadata":{"system":{},"custom":{},"is_public":false}}],
                 "meta":{"has_more":false,"after_cursor":"c2"},"links":{}}
                """),
            _ => FakeResponse.NotFound(),
        });
        var result = JsonNode.Parse(await TicketsTools.GetTicketConversation(TestHelpers.BuildZendeskService(fake.Client), 5))!;

        var entries = result["entries"]!.AsArray();
        entries.Should().HaveCount(2, "the log is cursor-paged");
        entries[0]!["message"]!.GetValue<string>().Should().Be("Help & thanks", "an html body is read as text");
        entries[0]!["visibility"]!.GetValue<string>().Should().Be("public");
        entries[1]!["visibility"]!.GetValue<string>().Should().Be("private", "an internal note must not read as public");
    }

    // ---------- Create_ticket ----------

    private static FakeZendesk CreateFake(Func<RecordedRequest, FakeResponse?>? extra = null) =>
        TestHelpers.CreateFakeZendesk(r => extra?.Invoke(r) ?? (r.Path, r.Method.Method) switch
        {
            ("/api/v2/tickets", "POST") => Json("""{"ticket":{"id":321,"subject":"New","status":"new","organization_id":601,"created_at":"t"}}"""),
            ("/api/v2/users/search", _) => Json("""{"users":[]}"""),
            _ => FakeResponse.NotFound(),
        });

    [Fact]
    public async Task CreateTicket_PrivateNote_PostsTicketWithDeterministicIdempotencyKey()
    {
        using var fake = CreateFake();
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var result = JsonNode.Parse(await TicketsTools.CreateTicket(zendesk, "New", "Body text", priority: "HIGH", tags: ["vip"],
            custom_fields: [new TicketCustomFieldInput(7, "x")]))!;
        await TicketsTools.CreateTicket(zendesk, "New", "Body text", priority: "HIGH", tags: ["vip"], custom_fields: [new TicketCustomFieldInput(7, "x")]);
        await TicketsTools.CreateTicket(zendesk, "New", "Different body");

        var posts = fake.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        var body = JsonNode.Parse(posts[0].Body!)!["ticket"]!;
        body["subject"]!.GetValue<string>().Should().Be("New");
        body["priority"]!.GetValue<string>().Should().Be("high");
        body["comment"]!["body"]!.GetValue<string>().Should().Be("Body text");
        body["comment"]!["public"]!.GetValue<bool>().Should().BeFalse();
        body["custom_fields"]![0]!["id"]!.GetValue<long>().Should().Be(7);
        body["requester_id"].Should().BeNull("without a requester the connected agent is the requester");

        posts[0].Headers["Idempotency-Key"].Should().MatchRegex("^[0-9a-f]{64}$");
        posts[1].Headers["Idempotency-Key"].Should().Be(posts[0].Headers["Idempotency-Key"]);
        posts[2].Headers["Idempotency-Key"].Should().NotBe(posts[0].Headers["Idempotency-Key"]);

        result["ticket"]!["id"]!.GetValue<long>().Should().Be(321);
        result["ticket"]!["agent_url"]!.GetValue<string>().Should().Be("https://acme.zendesk.com/agent/tickets/321");
        result["warnings"]!.AsArray().Should().BeEmpty();
        NoDeletes(fake);
    }

    [Fact]
    public async Task CreateTicket_PublicCommentNeedsFeatureSwitch()
    {
        using var fake = CreateFake();
        var act = () => TicketsTools.CreateTicket(TestHelpers.BuildZendeskService(fake.Client), "S", "C", @public: true);
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*AllowPublicReplies*");
        fake.Requests.Should().BeEmpty();

        var allowed = TestHelpers.BuildZendeskService(fake.Client, configure: o => o.Features.AllowPublicReplies = true);
        await TicketsTools.CreateTicket(allowed, "S", "C", @public: true);
        JsonNode.Parse(fake.Requests.Single(r => r.Method == HttpMethod.Post).Body!)!["ticket"]!["comment"]!["public"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CreateTicket_CcsNeedAPublicComment()
    {
        using var fake = CreateFake();
        var zendesk = TestHelpers.BuildZendeskService(fake.Client, configure: o => o.Features.AllowPublicReplies = true);
        var act = () => TicketsTools.CreateTicket(zendesk, "S", "C", ccs: ["42"]);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*public comment*");
    }

    [Fact]
    public async Task CreateTicket_UnknownRequesterEmailNeedsUserChangesSwitch()
    {
        using var fake = CreateFake();
        var act = () => TicketsTools.CreateTicket(TestHelpers.BuildZendeskService(fake.Client), "S", "C", requester_email: "new@example.com", requester_name: "New Person");
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*AllowUserAndOrganizationChanges*");
        fake.Requests.Single().Query("query").Should().Be("email:new@example.com");

        var allowed = TestHelpers.BuildZendeskService(fake.Client, configure: o => o.Features.AllowUserAndOrganizationChanges = true);
        await TicketsTools.CreateTicket(allowed, "S", "C", requester_email: "new@example.com", requester_name: "New Person");
        var body = JsonNode.Parse(fake.Requests.Single(r => r.Method == HttpMethod.Post).Body!)!["ticket"]!;
        body["requester"]!["email"]!.GetValue<string>().Should().Be("new@example.com");
        body["requester"]!["name"]!.GetValue<string>().Should().Be("New Person");
    }

    [Fact]
    public async Task CreateTicket_KnownRequesterEmailUsesTheirId()
    {
        using var fake = CreateFake(r => r.Path == "/api/v2/users/search" ? Json("""{"users":[{"id":88,"email":"Known@Example.com"}]}""") : null);
        await TicketsTools.CreateTicket(TestHelpers.BuildZendeskService(fake.Client), "S", "C", requester_email: "known@example.com");
        JsonNode.Parse(fake.Requests.Single(r => r.Method == HttpMethod.Post).Body!)!["ticket"]!["requester_id"]!.GetValue<long>().Should().Be(88);
    }

    [Theory]
    [InlineData("""{"ticket":{"id":900,"type":"question","status":"open"}}""", "*not a problem*")]
    [InlineData("""{"ticket":{"id":900,"type":"problem","status":"closed"}}""", "*closed*")]
    public async Task CreateTicket_ProblemIdMustBeAnOpenProblem(string problem, string message)
    {
        using var fake = CreateFake(r => r.Path == "/api/v2/tickets/900" ? Json(problem) : null);
        var act = () => TicketsTools.CreateTicket(TestHelpers.BuildZendeskService(fake.Client), "S", "C", problem_id: 900);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task CreateTicket_ProblemIdLinksAsIncident()
    {
        using var fake = CreateFake(r => r.Path == "/api/v2/tickets/900" ? Json("""{"ticket":{"id":900,"type":"problem","status":"open"}}""") : null);
        await TicketsTools.CreateTicket(TestHelpers.BuildZendeskService(fake.Client), "S", "C", problem_id: 900);
        var body = JsonNode.Parse(fake.Requests.Single(r => r.Method == HttpMethod.Post).Body!)!["ticket"]!;
        body["problem_id"]!.GetValue<long>().Should().Be(900);
        body["type"]!.GetValue<string>().Should().Be("incident");
    }

    [Theory]
    [InlineData(5L, "x@example.com", null, null, "*not both*")]
    [InlineData(null, null, "Name", null, "*requester_name*")]
    [InlineData(null, null, null, "solved", "*status or custom_status_id*")]
    public async Task CreateTicket_ValidatesArgumentCombinations(long? requesterId, string? email, string? name, string? status, string message)
    {
        using var fake = CreateFake();
        var act = () => TicketsTools.CreateTicket(TestHelpers.BuildZendeskService(fake.Client), "S", "C",
            requester_id: requesterId, requester_email: email, requester_name: name, status: status, custom_status_id: status is null ? null : 4);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateTicket_EnforcesLengthLimits()
    {
        using var fake = CreateFake();
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);
        await ((Func<Task>)(() => TicketsTools.CreateTicket(zendesk, new string('s', 501), "C"))).Should().ThrowAsync<ArgumentException>().WithMessage("*subject*500*");
        await ((Func<Task>)(() => TicketsTools.CreateTicket(zendesk, "S", new string('c', 10_001)))).Should().ThrowAsync<ArgumentException>().WithMessage("*comment*10000*");
    }

    [Fact]
    public async Task CreateTicket_CopiesAttachmentAndPlacesItInline()
    {
        using var fake = CreateFake(r => (r.Path, r.Method.Method) switch
        {
            ("/api/v2/attachments/55", _) => Json("""{"attachment":{"id":55,"file_name":"shot.png","content_type":"image/png","size":4,"content_url":"https://acme.zendesk.com/attachments/token/abc/?name=shot.png"}}"""),
            ("/attachments/token/abc/", _) => new FakeResponse(HttpStatusCode.OK, "PNG!", ContentType: "image/png"),
            ("/api/v2/uploads", "POST") => Json("""{"upload":{"token":"tok1","attachment":{"content_url":"https://acme.zendesk.com/attachments/token/new/?name=shot.png"}}}"""),
            _ => null,
        });
        var result = JsonNode.Parse(await TicketsTools.CreateTicket(TestHelpers.BuildZendeskService(fake.Client), "S",
            "See below:\n![the error](file:shot.png)\nThanks <b>", attachment_ids: [55]))!;

        var upload = fake.Requests.Single(r => r.Path == "/api/v2/uploads");
        upload.Query("filename").Should().Be("shot.png");
        upload.Query("inline").Should().Be("true");
        upload.Body.Should().Be("PNG!");

        var post = fake.Requests.Single(r => r.Path == "/api/v2/tickets");
        var comment = JsonNode.Parse(post.Body!)!["ticket"]!["comment"]!;
        comment["uploads"]![0]!.GetValue<string>().Should().Be("tok1");
        comment["html_body"]!.GetValue<string>().Should().Be("<p>See below:<br><img src=\"https://acme.zendesk.com/attachments/token/new/?name=shot.png\" alt=\"the error\"><br>Thanks &lt;b&gt;</p>");
        comment["body"].Should().BeNull();
        post.Headers.Should().NotContainKey("Idempotency-Key", "copied attachments are re-uploaded on every call, so a key could not hold");
        result["warnings"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task CreateTicket_RefusesOversizedAttachmentCopy()
    {
        using var fake = CreateFake(r => r.Path == "/api/v2/attachments/55"
            ? Json("""{"attachment":{"id":55,"file_name":"big.zip","size":999999999,"content_url":"https://acme.zendesk.com/a"}}""")
            : null);
        var act = () => TicketsTools.CreateTicket(TestHelpers.BuildZendeskService(fake.Client), "S", "C", attachment_ids: [55]);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*limit*");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task CreateTicket_UploadTokensAttachWithoutInlinePlacement()
    {
        using var fake = CreateFake();
        var result = JsonNode.Parse(await TicketsTools.CreateTicket(TestHelpers.BuildZendeskService(fake.Client), "S",
            "![x](file:pic.png)", upload_tokens: ["utok"]))!;
        var comment = JsonNode.Parse(fake.Requests.Single(r => r.Method == HttpMethod.Post).Body!)!["ticket"]!["comment"]!;
        comment["uploads"]![0]!.GetValue<string>().Should().Be("utok");
        comment["body"]!.GetValue<string>().Should().Be("![x](file:pic.png)");
        result["warnings"]!.AsArray().Should().ContainSingle().Which!.GetValue<string>().Should().Contain("pic.png");
    }

    [Fact]
    public async Task CreateTicket_WarnsWhenZendeskFilesUnderAnotherOrganisation()
    {
        using var fake = CreateFake();
        var result = JsonNode.Parse(await TicketsTools.CreateTicket(TestHelpers.BuildZendeskService(fake.Client), "S", "C", organization_id: 999))!;
        result["warnings"]![0]!.GetValue<string>().Should().Contain("601").And.Contain("999");
    }

    // ---------- Update_ticket ----------

    private static FakeZendesk UpdateFake(string current, Func<RecordedRequest, FakeResponse?>? extra = null) =>
        TestHelpers.CreateFakeZendesk(r => extra?.Invoke(r) ?? (r.Path, r.Method.Method) switch
        {
            ("/api/v2/tickets/101", "GET") => Json(current),
            ("/api/v2/tickets/101", "PUT") => Json("""{"ticket":{"id":101,"status":"solved","tags":["a","b"]}}"""),
            ("/api/v2/users/search", _) => Json("""{"users":[]}"""),
            _ => FakeResponse.NotFound(),
        });

    [Fact]
    public async Task UpdateTicket_PutsSafeUpdateWithTagsAndCcs()
    {
        using var fake = UpdateFake(Ticket101);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client, configure: o => o.Features.AllowPublicReplies = true);

        var result = JsonNode.Parse(await TicketsTools.UpdateTicket(zendesk, 101, priority: "low", additional_tags: ["b"], remove_tags: ["c"],
            additional_ccs: ["42"], remove_ccs: ["43"]))!;

        var put = fake.Requests.Single(r => r.Method == HttpMethod.Put);
        var body = JsonNode.Parse(put.Body!)!["ticket"]!;
        body["safe_update"]!.GetValue<bool>().Should().BeTrue();
        body["updated_stamp"]!.GetValue<string>().Should().Be("2026-01-02T10:00:00Z");
        body["priority"]!.GetValue<string>().Should().Be("low");
        body["additional_tags"]![0]!.GetValue<string>().Should().Be("b");
        body["remove_tags"]![0]!.GetValue<string>().Should().Be("c");
        body["email_ccs"]!.ToJsonString().Should().Be("""[{"user_id":42,"action":"put"},{"user_id":43,"action":"delete"}]""");
        body["comment"].Should().BeNull();
        result["ticket"]!["id"]!.GetValue<long>().Should().Be(101);
        NoDeletes(fake);
    }

    [Fact]
    public async Task UpdateTicket_AddingCcsNeedsPublicReplies()
    {
        using var fake = UpdateFake(Ticket101);
        var act = () => TicketsTools.UpdateTicket(TestHelpers.BuildZendeskService(fake.Client), 101, additional_ccs: ["42"]);
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*AllowPublicReplies*");
    }

    [Fact]
    public async Task UpdateTicket_CcChangesCannotGoWithPrivateComment()
    {
        using var fake = UpdateFake(Ticket101);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client, configure: o => o.Features.AllowPublicReplies = true);
        var act = () => TicketsTools.UpdateTicket(zendesk, 101, comment: "note", remove_ccs: ["43"]);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*private comment*");
    }

    [Fact]
    public async Task UpdateTicket_UnknownCcEmailNeedsUserChanges()
    {
        using var fake = UpdateFake(Ticket101);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client, configure: o => o.Features.AllowPublicReplies = true);
        var act = () => TicketsTools.UpdateTicket(zendesk, 101, additional_ccs: ["stranger@example.com"]);
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*AllowUserAndOrganizationChanges*");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task UpdateTicket_PrivateCommentByDefault_PublicNeedsSwitch()
    {
        using var fake = UpdateFake(Ticket101);
        await TicketsTools.UpdateTicket(TestHelpers.BuildZendeskService(fake.Client), 101, comment: "Checked logs");
        var comment = JsonNode.Parse(fake.Requests.Single(r => r.Method == HttpMethod.Put).Body!)!["ticket"]!["comment"]!;
        comment["public"]!.GetValue<bool>().Should().BeFalse();
        comment["body"]!.GetValue<string>().Should().Be("Checked logs");

        var act = () => TicketsTools.UpdateTicket(TestHelpers.BuildZendeskService(fake.Client), 101, comment: "Hi", @public: true);
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*AllowPublicReplies*");
    }

    [Fact]
    public async Task UpdateTicket_SolvingProblemWithUnsolvedIncidentsNeedsConfirmation()
    {
        const string problem = """{"ticket":{"id":101,"type":"problem","status":"open","updated_at":"u"}}""";
        using var fake = UpdateFake(problem, r => r.Path == "/api/v2/tickets/101/incidents"
            ? Json("""{"tickets":[{"id":1,"status":"open"},{"id":2,"status":"solved"}],"meta":{"has_more":false}}""")
            : null);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => TicketsTools.UpdateTicket(zendesk, 101, status: "solved");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*1 unsolved linked incident*solve_incidents*");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);

        var result = JsonNode.Parse(await TicketsTools.UpdateTicket(zendesk, 101, status: "solved", solve_incidents: true))!;
        fake.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Put);
        result["warnings"]![0]!.GetValue<string>().Should().Contain("1 unsolved linked incident");
    }

    [Fact]
    public async Task UpdateTicket_SolvingViaCustomStatusIsAlsoChecked()
    {
        const string problem = """{"ticket":{"id":101,"type":"problem","status":"open","updated_at":"u"}}""";
        using var fake = UpdateFake(problem, r => r.Path switch
        {
            "/api/v2/custom_statuses/8" => Json("""{"custom_status":{"id":8,"status_category":"solved"}}"""),
            "/api/v2/tickets/101/incidents" => Json("""{"tickets":[{"id":1,"status":"open"}],"meta":{"has_more":false}}"""),
            _ => null,
        });
        var act = () => TicketsTools.UpdateTicket(TestHelpers.BuildZendeskService(fake.Client), 101, custom_status_id: 8);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*solve_incidents*");
    }

    [Fact]
    public async Task UpdateTicket_UnlinkProblemSetsNull()
    {
        using var fake = UpdateFake(Ticket101);
        await TicketsTools.UpdateTicket(TestHelpers.BuildZendeskService(fake.Client), 101, unlink_problem: true);
        var body = JsonNode.Parse(fake.Requests.Single(r => r.Method == HttpMethod.Put).Body!)!["ticket"]!.AsObject();
        body.ContainsKey("problem_id").Should().BeTrue();
        body["problem_id"].Should().BeNull();
    }

    [Fact]
    public async Task UpdateTicket_ConflictExplainsConcurrentEdit()
    {
        using var fake = UpdateFake(Ticket101, r => r.Method == HttpMethod.Put ? new FakeResponse(HttpStatusCode.Conflict, "{\"error\":\"UpdateConflict\"}") : null);
        var act = () => TicketsTools.UpdateTicket(TestHelpers.BuildZendeskService(fake.Client), 101, priority: "low");
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*changed by someone else*");
    }

    [Theory]
    [InlineData(null, null, false, "*at least one change*")]
    [InlineData("solved", 4L, false, "*status or custom_status_id*")]
    [InlineData(null, null, true, "*give comment*")]
    public async Task UpdateTicket_ValidatesArguments(string? status, long? customStatus, bool attachmentsWithoutComment, string message)
    {
        using var fake = UpdateFake(Ticket101);
        var act = () => TicketsTools.UpdateTicket(TestHelpers.BuildZendeskService(fake.Client), 101, status: status, custom_status_id: customStatus,
            upload_tokens: attachmentsWithoutComment ? ["t"] : null, priority: attachmentsWithoutComment ? "low" : null);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateTicket_RejectsTagsWithSpaces()
    {
        using var fake = UpdateFake(Ticket101);
        var act = () => TicketsTools.UpdateTicket(TestHelpers.BuildZendeskService(fake.Client), 101, additional_tags: ["two words"]);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*additional_tags*");
    }
}
