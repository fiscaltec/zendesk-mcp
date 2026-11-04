using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

public class UsersToolsTests
{
    private const string Ann = """{"id":500,"name":"Ann Smith","email":"ann@example.com","role":"end-user","external_id":"crm-1","organization_id":7,"tags":["vip"],"active":true,"suspended":false,"verified":true,"phone":null,"time_zone":"London","locale":"en-GB","user_fields":{"tier":"gold"},"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-02T00:00:00Z","shared_phone_number":null,"photo":null}""";

    private static (FakeZendesk Fake, ZendeskService Zendesk) Build(Func<RecordedRequest, FakeResponse> route)
    {
        var fake = TestHelpers.CreateFakeZendesk(route);
        return (fake, TestHelpers.BuildZendeskService(fake.Client));
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    [Fact]
    public async Task SearchUsers_UserIds_UsesShowMany_ShapesRecords_AndReportsNotFound()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json($$$"""{"users":[{{{Ann}}}]}"""));
        using var _ = fake;

        var result = Parse(await UsersTools.SearchUsers(zendesk, user_ids: [500, 501, 500]));

        var request = fake.Requests.Single();
        request.Path.Should().Be("/api/v2/users/show_many");
        request.Query("ids").Should().Be("500,501");
        var user = result["users"]![0]!.AsObject();
        user["url"]!.GetValue<string>().Should().Be("https://acme.zendesk.com/agent/users/500");
        user["user_fields"]!["tier"]!.GetValue<string>().Should().Be("gold");
        user.ContainsKey("photo").Should().BeFalse("only the documented fields are returned");
        result["not_found"]!.AsArray().Select(n => (long)n!).Should().Equal(501);
        result["pagination_info"]!["has_more"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task SearchUsers_ExternalIds_UsesShowManyByExternalId()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json($$$"""{"users":[{{{Ann}}}]}"""));
        using var _ = fake;

        var result = Parse(await UsersTools.SearchUsers(zendesk, external_ids: ["crm-1", "crm-2"]));

        fake.Requests.Single().Query("external_ids").Should().Be("crm-1,crm-2");
        result["not_found"]!.AsArray().Select(n => (string)n!).Should().Equal("crm-2");
    }

    [Fact]
    public async Task SearchUsers_Filters_BuildAnExportSearch_WithNormalisedTags_AndPassPagination()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json($$$"""{"results":[{{{Ann}}}],"meta":{"has_more":true,"after_cursor":"next1"}}"""));
        using var _ = fake;

        var result = Parse(await UsersTools.SearchUsers(zendesk, email: "ann@example.com", name: "Ann Smith", role: "End-User",
            tags: "+6281234567890, *81234567890, VIP", page_size: 10, after_cursor: "cur0"));

        var request = fake.Requests.Single();
        request.Path.Should().Be("/api/v2/search/export");
        request.Query("query").Should().Be("type:user email:\"ann@example.com\" name:\"Ann Smith\" role:end-user tags:6281234567890 tags:*81234567890 tags:vip");
        request.Query("filter[type]").Should().Be("user");
        request.Query("page[size]").Should().Be("10");
        request.Query("page[after]").Should().Be("cur0");
        result["pagination_info"]!["next_cursor"]!.GetValue<string>().Should().Be("next1");
        result["pagination_info"]!["has_more"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task SearchUsers_OrganizationId_ListsMembers()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json($$$"""{"users":[{{{Ann}}}],"meta":{"has_more":false}}"""));
        using var _ = fake;

        await UsersTools.SearchUsers(zendesk, organization_id: 7);

        var request = fake.Requests.Single();
        request.Path.Should().Be("/api/v2/organizations/7/users");
        request.Query("page[size]").Should().Be("100");
    }

    [Fact]
    public async Task SearchUsers_IncludeOrganizations_AddsMemberships_AndCapsPageSize()
    {
        var (fake, zendesk) = Build(r => r.Path switch
        {
            "/api/v2/organizations/7/users" => FakeResponse.Json($$$"""{"users":[{{{Ann}}}],"meta":{"has_more":false}}"""),
            "/api/v2/users/500/organization_memberships" => FakeResponse.Json("""{"organization_memberships":[{"id":1,"organization_id":7,"default":true},{"id":2,"organization_id":8,"default":false}],"meta":{"has_more":false}}"""),
            "/api/v2/organizations/show_many" => FakeResponse.Json("""{"organizations":[{"id":7,"name":"Acme","external_id":"a"},{"id":8,"name":"Beta","external_id":null}]}"""),
            _ => FakeResponse.NotFound(),
        });
        using var _ = fake;

        var result = Parse(await UsersTools.SearchUsers(zendesk, organization_id: 7, include_fields: ["organizations"], page_size: 100));

        fake.Requests.First().Query("page[size]").Should().Be("25");
        var orgs = result["users"]![0]!["organizations"]!.AsArray();
        orgs.Select(o => (string)o!["name"]!).Should().Equal("Acme", "Beta");
        orgs[0]!["is_default"]!.GetValue<bool>().Should().BeTrue();
        orgs[0]!["external_id"]!.GetValue<string>().Should().Be("a");
        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("two-modes")]
    [InlineData("bad-role")]
    [InlineData("bad-wildcard")]
    [InlineData("short-wildcard")]
    [InlineData("tag-with-space")]
    [InlineData("comma-external-id")]
    [InlineData("bad-include")]
    [InlineData("too-many-ids")]
    public async Task SearchUsers_RejectsBadFilters(string scenario)
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json("{}"));
        using var _ = fake;

        Func<Task> act = scenario switch
        {
            "none" => () => UsersTools.SearchUsers(zendesk),
            "two-modes" => () => UsersTools.SearchUsers(zendesk, user_ids: [1], email: "a@b.c"),
            "bad-role" => () => UsersTools.SearchUsers(zendesk, role: "owner"),
            "bad-wildcard" => () => UsersTools.SearchUsers(zendesk, tags: "ab*cd"),
            "short-wildcard" => () => UsersTools.SearchUsers(zendesk, tags: "a*"),
            "tag-with-space" => () => UsersTools.SearchUsers(zendesk, tags: "62 812"),
            "comma-external-id" => () => UsersTools.SearchUsers(zendesk, external_ids: ["a,b"]),
            "bad-include" => () => UsersTools.SearchUsers(zendesk, role: "agent", include_fields: ["groups"]),
            _ => () => UsersTools.SearchUsers(zendesk, user_ids: Enumerable.Range(1, 101).Select(i => (long)i).ToArray()),
        };

        await act.Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FindAgentByName_SearchesBothAgentRoles_AndPutsExactMatchesFirst()
    {
        var (fake, zendesk) = Build(r => r.Query("query")!.Contains("role:agent", StringComparison.Ordinal)
            ? FakeResponse.Json("""{"results":[{"id":2,"name":"John Doe Junior","email":"j2@x","role":"agent","active":true,"suspended":false},{"id":3,"name":"End User","role":"end-user"}]}""")
            : FakeResponse.Json("""{"results":[{"id":1,"name":"John Doe","email":"j@x","role":"admin","active":true,"suspended":false}]}"""));
        using var _ = fake;

        var result = Parse(await UsersTools.FindAgentByName(zendesk, "  John Doe "));

        fake.Requests.Select(r => r.Query("query")).Should().Equal(
            "type:user role:agent name:\"John Doe\"",
            "type:user role:admin name:\"John Doe\"");
        fake.Requests.Should().OnlyContain(r => r.Path == "/api/v2/search" && r.Method == HttpMethod.Get);
        result["agents"]!.AsArray().Select(a => (long)a!["id"]!).Should().Equal(1, 2);
        result["exact_match"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task FindAgentByName_RequiresAName()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json("{}"));
        using var _ = fake;

        await ((Func<Task>)(() => UsersTools.FindAgentByName(zendesk, " "))).Should().ThrowAsync<ArgumentException>();
        await ((Func<Task>)(() => UsersTools.FindAgentByName(zendesk, new string('x', 201)))).Should().ThrowAsync<ArgumentException>();
    }
}
