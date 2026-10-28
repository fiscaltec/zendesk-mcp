using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

public class ConfigurationToolsTests
{
    private static (FakeZendesk Fake, ZendeskService Zendesk) Build(Func<RecordedRequest, FakeResponse> route)
    {
        var fake = TestHelpers.CreateFakeZendesk(route);
        return (fake, TestHelpers.BuildZendeskService(fake.Client));
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    [Fact]
    public async Task ListTicketFields_AddsCustomStatusesToTheStatusField_AndPassesPagination()
    {
        var (fake, zendesk) = Build(r => r.Path switch
        {
            "/api/v2/ticket_fields" => FakeResponse.Json("""
                {"ticket_fields":[{"id":1,"type":"subject","title":"Subject","url":"x"},
                                  {"id":2,"type":"custom_status","title":"Ticket status"},
                                  {"id":3,"type":"tagger","title":"Product","custom_field_options":[{"name":"A","value":"a"}]}],
                 "meta":{"has_more":true,"after_cursor":"tf2"}}
                """),
            "/api/v2/custom_statuses" => FakeResponse.Json("""{"custom_statuses":[{"id":11,"agent_label":"Waiting on vendor","status_category":"hold","active":true,"description":"x"}]}"""),
            _ => FakeResponse.NotFound(),
        });
        using var _ = fake;

        var result = Parse(await ConfigurationTools.ListTicketFields(zendesk, page_size: 3, after_cursor: "tf1"));

        var request = fake.Requests.First();
        request.Query("page[size]").Should().Be("3");
        request.Query("page[after]").Should().Be("tf1");
        var fields = result["ticket_fields"]!.AsArray();
        fields[0]!.AsObject().ContainsKey("url").Should().BeFalse();
        fields[0]!.AsObject().ContainsKey("custom_statuses").Should().BeFalse();
        var status = fields[1]!["custom_statuses"]![0]!;
        status["id"]!.GetValue<long>().Should().Be(11);
        status["agent_label"]!.GetValue<string>().Should().Be("Waiting on vendor");
        status["status_category"]!.GetValue<string>().Should().Be("hold");
        fields[2]!["custom_field_options"]!.AsArray().Should().HaveCount(1);
        result["pagination_info"]!["next_cursor"]!.GetValue<string>().Should().Be("tf2");
    }

    [Fact]
    public async Task ListTicketFields_WithoutAStatusField_DoesNotReadCustomStatuses()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json("""{"ticket_fields":[{"id":1,"type":"subject"}],"meta":{"has_more":false}}"""));
        using var _ = fake;

        await ConfigurationTools.ListTicketFields(zendesk);

        fake.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(null, "true")]
    [InlineData(false, "false")]
    public async Task ListTicketForms_DefaultsToActive(bool? active, string expected)
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json("""{"ticket_forms":[{"id":1,"name":"Default","active":true,"ticket_field_ids":[1,2],"url":"x"}]}"""));
        using var _ = fake;

        var result = Parse(await ConfigurationTools.ListTicketForms(zendesk, active));

        fake.Requests.Single().Query("active").Should().Be(expected);
        result["ticket_forms"]![0]!["ticket_field_ids"]!.AsArray().Should().HaveCount(2);
        result["count"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task ListTriggers_SendsFiltersAndUsageSideloads_AndShapesUsage()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json("""
            {"triggers":[{"id":5,"title":"Notify","active":true,"position":1,"category_id":"10","conditions":{"all":[],"any":[]},"actions":[{"field":"status","value":"open"}],"usage_1h":0,"usage_24h":1,"usage_7d":2,"usage_30d":3},
                         {"id":6,"title":"No usage","active":false,"conditions":{},"actions":[]}],
             "meta":{"has_more":false}}
            """));
        using var _ = fake;

        var result = Parse(await ConfigurationTools.ListTriggers(zendesk, active: true, category_id: "10", sort: "alphabetical", sort_order: "desc", page_size: 20));

        var request = fake.Requests.Single();
        request.Path.Should().Be("/api/v2/triggers");
        request.Query("active").Should().Be("true");
        request.Query("category_id").Should().Be("10");
        request.Query("sort_by").Should().Be("alphabetical");
        request.Query("sort_order").Should().Be("desc");
        request.Query("include").Should().Be("usage_1h,usage_24h,usage_7d,usage_30d");
        request.Query("page[size]").Should().Be("20");
        var triggers = result["triggers"]!.AsArray();
        triggers[0]!["usage"]!["7d"]!.GetValue<int>().Should().Be(2);
        triggers[0]!["url"]!.GetValue<string>().Should().Be("https://acme.zendesk.com/admin/objects-rules/rules/triggers/5");
        triggers[1]!["usage"].Should().BeNull();
    }

    [Theory]
    [InlineData("random", null, null)]
    [InlineData(null, "up", null)]
    [InlineData(null, null, "10&x=1")]
    public async Task ListTriggers_RejectsBadArguments(string? sort, string? order, string? category)
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json("{}"));
        using var _ = fake;

        var act = () => ConfigurationTools.ListTriggers(zendesk, sort: sort, sort_order: order, category_id: category);

        await act.Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ListBrands_ReturnsTheBrandFields_AndPagination()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json("""
            {"brands":[{"id":1,"name":"Acme","subdomain":"acme","brand_url":"https://acme.zendesk.com","host_mapping":"help.acme.test","active":true,"default":true,"has_help_center":true,"help_center_state":"enabled","logo":{"id":9}}],
             "meta":{"has_more":true,"after_cursor":"b2"}}
            """));
        using var _ = fake;

        var result = Parse(await ConfigurationTools.ListBrands(zendesk, page_size: 1));

        fake.Requests.Single().Query("page[size]").Should().Be("1");
        var brand = result["brands"]![0]!.AsObject();
        brand["host_mapping"]!.GetValue<string>().Should().Be("help.acme.test");
        brand.ContainsKey("logo").Should().BeFalse();
        result["pagination_info"]!["has_more"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task ListTools_RejectAnOutOfRangePageSize()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json("{}"));
        using var _ = fake;

        await ((Func<Task>)(() => ConfigurationTools.ListBrands(zendesk, page_size: 0))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => ConfigurationTools.ListTicketFields(zendesk, page_size: 101))).Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().BeEmpty();
    }
}
