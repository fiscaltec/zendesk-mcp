using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

public class OrganizationsToolsTests
{
    private const string Acme = """{"id":7,"name":"Acme","external_id":"crm-7","domain_names":["acme.test"],"tags":["enterprise"],"group_id":null,"shared_tickets":true,"shared_comments":false,"details":"","notes":"","organization_fields":{"tier":"gold"},"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-02T00:00:00Z"}""";

    private static (FakeZendesk Fake, ZendeskService Zendesk) Build(Func<RecordedRequest, FakeResponse> route, bool allowChanges = false)
    {
        var fake = TestHelpers.CreateFakeZendesk(route);
        return (fake, TestHelpers.BuildZendeskService(fake.Client, configure: o => o.Features.AllowUserAndOrganizationChanges = allowChanges));
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    // ---------------------------------------------------------------- Search_organizations

    [Fact]
    public async Task SearchOrganizations_Ids_ShapeRecords_AndReportNotFound()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json($$$"""{"organizations":[{{{Acme}}}]}"""));
        using var _ = fake;

        var result = Parse(await OrganizationsTools.SearchOrganizations(zendesk, organization_ids: [7, 8]));

        fake.Requests.Single().Path.Should().Be("/api/v2/organizations/show_many");
        fake.Requests.Single().Query("ids").Should().Be("7,8");
        var org = result["organizations"]![0]!;
        org["url"]!.GetValue<string>().Should().Be("https://acme.zendesk.com/agent/organizations/7");
        org["organization_fields"]!["tier"]!.GetValue<string>().Should().Be("gold");
        result["not_found"]!.AsArray().Select(n => (long)n!).Should().Equal(8);
    }

    [Fact]
    public async Task SearchOrganizations_ExternalIds_ReportNotFound()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json($$$"""{"organizations":[{{{Acme}}}]}"""));
        using var _ = fake;

        var result = Parse(await OrganizationsTools.SearchOrganizations(zendesk, external_ids: ["crm-7", "crm-9"]));

        fake.Requests.Single().Query("external_ids").Should().Be("crm-7,crm-9");
        result["not_found"]!.AsArray().Select(n => (string)n!).Should().Equal("crm-9");
    }

    [Theory]
    [InlineData(null, "/api/v2/organizations/search")]
    [InlineData("prefix", "/api/v2/organizations/autocomplete")]
    public async Task SearchOrganizations_Name_UsesExactSearchOrAutocomplete(string? match, string path)
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json($$$"""{"organizations":[{{{Acme}}}]}"""));
        using var _ = fake;

        await OrganizationsTools.SearchOrganizations(zendesk, name: " Ac ", match: match);

        fake.Requests.Single().Path.Should().Be(path);
        fake.Requests.Single().Query("name").Should().Be("Ac");
    }

    [Fact]
    public async Task SearchOrganizations_Prefix_PagesAutocompleteByOffset()
    {
        // Live, autocomplete refuses page[size]/page[after] and pages by number.
        var (fake, zendesk) = Build(r => r.Query("page[size]") is not null
            ? FakeResponse.WithStatus(System.Net.HttpStatusCode.BadRequest, """{"error":{"title":"Invalid attribute","message":"page must be an integer"}}""")
            : FakeResponse.Json(r.Query("page") == "2"
                ? $$$"""{"organizations":[{{{Acme}}}],"next_page":null,"previous_page":"x","count":2}"""
                : $$$"""{"organizations":[{{{Acme}}}],"next_page":"https://acme.zendesk.com/api/v2/organizations/autocomplete.json?name=Ac&page=2&per_page=1","previous_page":null,"count":2}"""));
        using var _ = fake;

        var first = JsonNode.Parse(await OrganizationsTools.SearchOrganizations(zendesk, name: "Ac", match: "prefix", page_size: 1))!;
        var next = first["pagination_info"]!["next_cursor"]!.GetValue<string>();
        var second = JsonNode.Parse(await OrganizationsTools.SearchOrganizations(zendesk, name: "Ac", match: "prefix", page_size: 1, after_cursor: next))!;

        first["pagination_info"]!["has_more"]!.GetValue<bool>().Should().BeTrue();
        second["pagination_info"]!["has_more"]!.GetValue<bool>().Should().BeFalse();
        fake.Requests[0].Query("per_page").Should().Be("1");
        fake.Requests[1].Query("page").Should().Be("2");
    }

    [Fact]
    public async Task SearchOrganizations_Tags_UseExportSearch()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json($$$"""{"results":[{{{Acme}}}],"meta":{"has_more":false}}"""));
        using var _ = fake;

        await OrganizationsTools.SearchOrganizations(zendesk, tags: "Enterprise,ent*");

        var request = fake.Requests.Single();
        request.Path.Should().Be("/api/v2/search/export");
        request.Query("query").Should().Be("type:organization tags:enterprise tags:ent*");
        request.Query("filter[type]").Should().Be("organization");
    }

    [Fact]
    public async Task SearchOrganizations_NoFilter_ListsAll_WithPagination()
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json($$$"""{"organizations":[{{{Acme}}}],"meta":{"has_more":true,"after_cursor":"c2"}}"""));
        using var _ = fake;

        var result = Parse(await OrganizationsTools.SearchOrganizations(zendesk, page_size: 5, after_cursor: "c1"));

        var request = fake.Requests.Single();
        request.Path.Should().Be("/api/v2/organizations");
        request.Query("page[size]").Should().Be("5");
        request.Query("page[after]").Should().Be("c1");
        result["pagination_info"]!["next_cursor"]!.GetValue<string>().Should().Be("c2");
    }

    [Theory]
    [InlineData("two-filters")]
    [InlineData("short-prefix")]
    [InlineData("match-without-name")]
    [InlineData("bad-match")]
    public async Task SearchOrganizations_RejectsBadFilters(string scenario)
    {
        var (fake, zendesk) = Build(_ => FakeResponse.Json("{}"));
        using var _ = fake;

        Func<Task> act = scenario switch
        {
            "two-filters" => () => OrganizationsTools.SearchOrganizations(zendesk, organization_ids: [1], name: "Acme"),
            "short-prefix" => () => OrganizationsTools.SearchOrganizations(zendesk, name: "A", match: "prefix"),
            "match-without-name" => () => OrganizationsTools.SearchOrganizations(zendesk, match: "prefix"),
            _ => () => OrganizationsTools.SearchOrganizations(zendesk, name: "Acme", match: "fuzzy"),
        };

        await act.Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Add_user_to_organization

    private const string EndUser = """{"user":{"id":500,"name":"Ann","role":"end-user"}}""";

    private static Func<RecordedRequest, FakeResponse> Routes(string memberships, Func<RecordedRequest, FakeResponse>? post = null, bool? multipleOrganizations = false) => r => (r.Method.Method, r.Path) switch
    {
        ("GET", "/api/v2/account/settings") when multipleOrganizations is { } multiple =>
            FakeResponse.Json("{\"settings\":{\"user\":{\"multiple_organizations\":" + (multiple ? "true" : "false") + "}}}"),
        ("GET", "/api/v2/users/500") => FakeResponse.Json(EndUser),
        ("GET", "/api/v2/organizations/7") => FakeResponse.Json($$$"""{"organization":{{{Acme}}}}"""),
        ("GET", "/api/v2/organizations/3") => FakeResponse.Json("""{"organization":{"id":3,"name":"Old Co"}}"""),
        ("GET", "/api/v2/users/500/organization_memberships") => FakeResponse.Json(memberships),
        ("POST", "/api/v2/organization_memberships") => post?.Invoke(r) ?? FakeResponse.Json("""{"organization_membership":{"id":900,"user_id":500,"organization_id":7,"default":false}}"""),
        ("PUT", "/api/v2/users/500") => FakeResponse.Json(EndUser),
        ("PUT", _) => FakeResponse.Json("{}"),
        _ => FakeResponse.NotFound(),
    };

    [Fact]
    public async Task AddUserToOrganization_IsRefusedWhenTheSwitchIsOff()
    {
        var (fake, zendesk) = Build(Routes("""{"organization_memberships":[]}"""));
        using var _ = fake;

        var act = () => OrganizationsTools.AddUserToOrganization(zendesk, 500, 7);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*AllowUserAndOrganizationChanges*");
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AddUserToOrganization_Creates_AndWarnsThatSharedTicketsBecomeVisible()
    {
        var (fake, zendesk) = Build(Routes("""{"organization_memberships":[{"id":1,"organization_id":3,"default":true}],"meta":{"has_more":false}}""", multipleOrganizations: true), allowChanges: true);
        using var _ = fake;

        var result = Parse(await OrganizationsTools.AddUserToOrganization(zendesk, 500, 7));

        result["result"]!.GetValue<string>().Should().Be("created");
        result["membership_id"]!.GetValue<long>().Should().Be(900);
        result["is_default"]!.GetValue<bool>().Should().BeFalse();
        result["warnings"]!.AsArray().Select(w => (string)w!).Should().Contain(w => w.Contains("shares its tickets"));
        var body = Parse(fake.Requests.Single(r => r.Method == HttpMethod.Post).Body!)["organization_membership"]!;
        body.ToJsonString().Should().Be("""{"user_id":500,"organization_id":7,"default":false}""");
    }

    [Fact]
    public async Task AddUserToOrganization_AlreadyMember_WritesNothing_UnlessMakingDefault()
    {
        var memberships = """{"organization_memberships":[{"id":55,"organization_id":7,"default":false}],"meta":{"has_more":false}}""";
        var (fake, zendesk) = Build(Routes(memberships), allowChanges: true);
        using var _ = fake;

        var result = Parse(await OrganizationsTools.AddUserToOrganization(zendesk, 500, 7));
        result["result"]!.GetValue<string>().Should().Be("already_member");
        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);

        var withDefault = Parse(await OrganizationsTools.AddUserToOrganization(zendesk, 500, 7, make_default: true));
        withDefault["is_default"]!.GetValue<bool>().Should().BeTrue();
        fake.Requests.Single(r => r.Method == HttpMethod.Put).Path.Should().Be("/api/v2/users/500/organization_memberships/55/make_default");
    }

    [Fact]
    public async Task AddUserToOrganization_SingleOrganizationAccount_RefusesWithoutReplaceExisting_BeforeWriting()
    {
        // Live, Zendesk accepts the second membership and silently drops the first, so the refusal has
        // to come before any write.
        var memberships = """{"organization_memberships":[{"id":1,"organization_id":3,"default":true}],"meta":{"has_more":false}}""";
        var (fake, zendesk) = Build(Routes(memberships, multipleOrganizations: false), allowChanges: true);
        using var _ = fake;

        var act = () => OrganizationsTools.AddUserToOrganization(zendesk, 500, 7);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*replace_existing*");
        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "nothing may be written without replace_existing");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task AddUserToOrganization_UnreadableAccountSetting_RefusesEvenWithReplaceExisting(bool? replaceExisting)
    {
        // The settings route answers 404 here, so whether users may hold several organisations is
        // unknown. Guessing either way could move someone or report a move that did not happen.
        var memberships = """{"organization_memberships":[{"id":1,"organization_id":3,"default":true}],"meta":{"has_more":false}}""";
        var (fake, zendesk) = Build(Routes(memberships, multipleOrganizations: null), allowChanges: true);
        using var _ = fake;

        var act = () => OrganizationsTools.AddUserToOrganization(zendesk, 500, 7, replace_existing: replaceExisting);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*could not be read*");
        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "nothing may be written while the account's rule is unknown");
    }

    [Fact]
    public async Task AddUserToOrganization_ReplaceExisting_MovesTheUserWithAPut_NeverADelete()
    {
        var calls = 0;
        var before = """{"organization_memberships":[{"id":1,"organization_id":3,"default":true}],"meta":{"has_more":false}}""";
        var after = """{"organization_memberships":[{"id":2,"organization_id":7,"default":true}],"meta":{"has_more":false}}""";
        var (fake, zendesk) = Build(r => r.Path == "/api/v2/users/500/organization_memberships"
                ? FakeResponse.Json(calls++ == 0 ? before : after)
                : Routes(before)(r),
            allowChanges: true);
        using var _ = fake;

        var result = Parse(await OrganizationsTools.AddUserToOrganization(zendesk, 500, 7, replace_existing: true));

        result["result"]!.GetValue<string>().Should().Be("moved");
        result["membership_id"]!.GetValue<long>().Should().Be(2);
        result["previous_organization"]!["name"]!.GetValue<string>().Should().Be("Old Co");
        var put = fake.Requests.Single(r => r.Method == HttpMethod.Put);
        put.Path.Should().Be("/api/v2/users/500");
        Parse(put.Body!).ToJsonString().Should().Be("""{"user":{"organization_id":7}}""");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
    }
}
