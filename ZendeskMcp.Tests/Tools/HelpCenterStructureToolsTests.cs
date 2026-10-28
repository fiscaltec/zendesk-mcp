using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

public class HelpCenterStructureToolsTests
{
    private static FakeResponse Echo(RecordedRequest r) => (r.Method.Method, r.Path) switch
    {
        ("GET", "/api/v2/brands") => FakeResponse.Json(HelpCenterArticlesToolsTests.BrandsJson),
        ("POST", "/api/v2/help_center/categories") => FakeResponse.Json("""{"category":{"id":5,"name":"Billing","locale":"en-gb","position":1}}"""),
        ("POST", "/api/v2/help_center/categories/5/sections") => FakeResponse.Json("""{"section":{"id":7,"name":"Invoices","category_id":5}}"""),
        ("PUT", "/api/v2/help_center/categories/5") => FakeResponse.Json("""{"category":{"id":5,"position":3}}"""),
        ("PUT", "/api/v2/help_center/sections/7") => FakeResponse.Json("""{"section":{"id":7,"position":3,"category_id":6}}"""),
        ("PUT", _) when r.Path.Contains("/translations/", StringComparison.Ordinal) => FakeResponse.Json("""{"translation":{"id":1,"locale":"en-gb","title":"N","body":"D"}}"""),
        _ => FakeResponse.NotFound(),
    };

    [Fact]
    public async Task ListCategories_BuildsLocalisedPathWithSortingAndPaging()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("""
        {"categories":[{"id":5,"name":"Billing","description":"d","locale":"en-gb","position":0,"html_url":"u","url":"api"}],"meta":{"has_more":true,"after_cursor":"c2"}}
        """));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterStructureTools.ListHelpCenterCategories(zendesk, locale: "en-gb", sort_by: "updated_at", sort_order: "desc", page_size: 50, after_cursor: "c1"))!;

        var request = fake.Requests.Single();
        request.Path.Should().Be("/api/v2/help_center/en-gb/categories");
        request.Query("sort_by").Should().Be("updated_at");
        request.Query("sort_order").Should().Be("desc");
        request.Query("page[size]").Should().Be("50");
        request.Query("page[after]").Should().Be("c1");
        json["categories"]![0]!["name"]!.GetValue<string>().Should().Be("Billing");
        json["categories"]![0]!.AsObject().ContainsKey("url").Should().BeFalse();
        json["pagination_info"]!["next_cursor"]!.GetValue<string>().Should().Be("c2");
    }

    [Fact]
    public async Task ListCategories_RejectsUnknownSort()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("{}"));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await FluentActions.Awaiting(() => HelpCenterStructureTools.ListHelpCenterCategories(zendesk, sort_by: "title"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*sort_by*");
        await FluentActions.Awaiting(() => HelpCenterStructureTools.ListHelpCenterCategories(zendesk, sort_order: "up"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*sort_order*");
    }

    [Fact]
    public async Task ListCategories_ForeignBrandUrl_IsRefusedWithoutContactingIt()
    {
        using var fake = TestHelpers.CreateFakeZendesk(Echo);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await FluentActions.Awaiting(() => HelpCenterStructureTools.ListHelpCenterCategories(zendesk, brand_url: "https://evil.example"))
            .Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().OnlyContain(r => r.Url.Host == "acme.zendesk.com" && r.Path == "/api/v2/brands");
    }

    [Fact]
    public async Task CreateCategory_PostsBody()
    {
        using var fake = TestHelpers.CreateFakeZendesk(Echo);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterStructureTools.CreateHelpCenterCategory(zendesk, "Billing", "en-GB", description: "All about bills", position: 1, brand_url: "https://otherbrand.zendesk.com"))!;

        var post = fake.Requests.Single(r => r.Method == HttpMethod.Post);
        post.Url.Host.Should().Be("otherbrand.zendesk.com");
        var category = JsonNode.Parse(post.Body!)!["category"]!;
        category["name"]!.GetValue<string>().Should().Be("Billing");
        category["locale"]!.GetValue<string>().Should().Be("en-gb");
        category["description"]!.GetValue<string>().Should().Be("All about bills");
        category["position"]!.GetValue<int>().Should().Be(1);
        json["id"]!.GetValue<long>().Should().Be(5);
    }

    [Fact]
    public async Task CreateCategory_ValidatesLimits()
    {
        using var fake = TestHelpers.CreateFakeZendesk(Echo);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await FluentActions.Awaiting(() => HelpCenterStructureTools.CreateHelpCenterCategory(zendesk, new string('x', 256), "en-gb"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*name*255*");
        await FluentActions.Awaiting(() => HelpCenterStructureTools.CreateHelpCenterCategory(zendesk, "N", "en-gb", description: new string('x', 10_001)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*description*");
        await FluentActions.Awaiting(() => HelpCenterStructureTools.CreateHelpCenterCategory(zendesk, "N", ""))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*locale*");
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateCategory_PositionAndTranslationAreSeparatePuts()
    {
        using var fake = TestHelpers.CreateFakeZendesk(Echo);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterStructureTools.UpdateHelpCenterCategory(zendesk, 5, position: 3, name: "N", description: "D", locale: "en-gb"))!;

        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Put);
        var positionPut = fake.Requests.Single(r => r.Path == "/api/v2/help_center/categories/5");
        JsonNode.Parse(positionPut.Body!)!["category"]!.AsObject().Select(p => p.Key).Should().Equal("position");
        var translationPut = fake.Requests.Single(r => r.Path == "/api/v2/help_center/categories/5/translations/en-gb");
        var translation = JsonNode.Parse(translationPut.Body!)!["translation"]!;
        translation["title"]!.GetValue<string>().Should().Be("N");
        translation["body"]!.GetValue<string>().Should().Be("D");
        json["category"]!["position"]!.GetValue<int>().Should().Be(3);
        json["translation"]!["title"]!.GetValue<string>().Should().Be("N");
    }

    [Fact]
    public async Task UpdateCategory_ValidatesArguments()
    {
        using var fake = TestHelpers.CreateFakeZendesk(Echo);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await FluentActions.Awaiting(() => HelpCenterStructureTools.UpdateHelpCenterCategory(zendesk, 5))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*at least one*");
        await FluentActions.Awaiting(() => HelpCenterStructureTools.UpdateHelpCenterCategory(zendesk, 5, name: "N"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*locale is required*");
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ListSections_ByCategoryNeedsLocale()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("""{"sections":[{"id":7,"name":"Invoices","category_id":5}],"meta":{"has_more":false}}"""));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await FluentActions.Awaiting(() => HelpCenterStructureTools.ListHelpCenterSections(zendesk, category_id: 5))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*locale*");
        fake.Requests.Should().BeEmpty();

        var json = JsonNode.Parse(await HelpCenterStructureTools.ListHelpCenterSections(zendesk, locale: "fr", category_id: 5))!;
        fake.Requests.Single().Path.Should().Be("/api/v2/help_center/fr/categories/5/sections");
        json["sections"]![0]!["category_id"]!.GetValue<long>().Should().Be(5);
    }

    [Fact]
    public async Task ListSections_WithoutFiltersUsesSectionsPath()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("""{"sections":[],"meta":{"has_more":false}}"""));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await HelpCenterStructureTools.ListHelpCenterSections(zendesk);

        fake.Requests.Single().Path.Should().Be("/api/v2/help_center/sections");
    }

    [Fact]
    public async Task CreateSection_PostsIntoCategory()
    {
        using var fake = TestHelpers.CreateFakeZendesk(Echo);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterStructureTools.CreateHelpCenterSection(zendesk, 5, "Invoices", "en-gb", position: 0))!;

        var section = JsonNode.Parse(fake.Requests.Single().Body!)!["section"]!;
        section["name"]!.GetValue<string>().Should().Be("Invoices");
        section["locale"]!.GetValue<string>().Should().Be("en-gb");
        section["position"]!.GetValue<int>().Should().Be(0);
        json["id"]!.GetValue<long>().Should().Be(7);
    }

    [Fact]
    public async Task UpdateSection_MovesAndRenames()
    {
        using var fake = TestHelpers.CreateFakeZendesk(Echo);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterStructureTools.UpdateHelpCenterSection(zendesk, 7, position: 3, category_id: 6, description: "D", locale: "en-gb"))!;

        var sectionPut = JsonNode.Parse(fake.Requests.Single(r => r.Path == "/api/v2/help_center/sections/7").Body!)!["section"]!;
        sectionPut["position"]!.GetValue<int>().Should().Be(3);
        sectionPut["category_id"]!.GetValue<long>().Should().Be(6);
        var translation = JsonNode.Parse(fake.Requests.Single(r => r.Path == "/api/v2/help_center/sections/7/translations/en-gb").Body!)!["translation"]!.AsObject();
        translation.Select(p => p.Key).Should().Equal("body");
        json["section"]!["category_id"]!.GetValue<long>().Should().Be(6);
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task UpdateSection_RequiresAChange()
    {
        using var fake = TestHelpers.CreateFakeZendesk(Echo);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await FluentActions.Awaiting(() => HelpCenterStructureTools.UpdateHelpCenterSection(zendesk, 7))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*at least one*");
    }

    [Fact]
    public async Task ListUserSegments_FollowsAllPages()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Query("page[after]") is null
            ? FakeResponse.Json("""{"user_segments":[{"id":1,"name":"Signed-in users","user_type":"signed_in_users","built_in":true,"group_ids":[],"organization_ids":[],"tags":[],"or_tags":[]}],"meta":{"has_more":true,"after_cursor":"p2"}}""")
            : FakeResponse.Json("""{"user_segments":[{"id":2,"name":"Agents","user_type":"staff","built_in":true}],"meta":{"has_more":false}}"""));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterStructureTools.ListUserSegments(zendesk))!;

        fake.Requests.Should().HaveCount(2).And.OnlyContain(r => r.Path == "/api/v2/help_center/user_segments");
        json["user_segments"]!.AsArray().Select(s => s!["id"]!.GetValue<long>()).Should().Equal(1, 2);
        json["user_segments"]![0]!["user_type"]!.GetValue<string>().Should().Be("signed_in_users");
        json["truncated"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task ListPermissionGroups_ShapesGroups()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("""{"permission_groups":[{"id":3,"name":"Admins","edit":[1],"publish":[2],"built_in":true,"url":"x"}],"meta":{"has_more":false}}"""));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterStructureTools.ListPermissionGroups(zendesk))!;

        fake.Requests.Single().Path.Should().Be("/api/v2/guide/permission_groups");
        var group = json["permission_groups"]![0]!.AsObject();
        group["edit"]!.AsArray().Should().HaveCount(1);
        group["publish"]!.AsArray().Should().HaveCount(1);
        group.ContainsKey("url").Should().BeFalse();
    }
}
