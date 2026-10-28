using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

public class HelpCenterArticlesToolsTests
{
    internal const string BrandsJson = """
    {
      "brands": [
        { "id": 1, "brand_url": "https://acme.zendesk.com", "host_mapping": null },
        { "id": 2, "brand_url": "https://otherbrand.zendesk.com", "host_mapping": "support.other.example" }
      ],
      "meta": { "has_more": false, "after_cursor": null }
    }
    """;

    private const string ArticleJson = """
    {
      "article": {
        "id": 42, "title": "Reset your password", "body": "<p>Hello</p>", "locale": "en-gb", "source_locale": "en-gb",
        "section_id": 7, "author_id": 9, "draft": false, "permission_group_id": 3, "user_segment_id": 11,
        "user_segment_ids": [11, 12], "label_names": ["login"], "html_url": "https://acme.zendesk.com/hc/en-gb/articles/42",
        "updated_at": "2026-01-01T00:00:00Z"
      }
    }
    """;

    private static FakeResponse DefaultRoute(RecordedRequest r) => r.Path switch
    {
        "/api/v2/brands" => FakeResponse.Json(BrandsJson),
        "/api/v2/help_center/articles/42" or "/api/v2/help_center/en-gb/articles/42" => FakeResponse.Json(ArticleJson),
        "/api/v2/help_center/sections/7" or "/api/v2/help_center/en-gb/sections/7" => FakeResponse.Json("""{"section":{"id":7,"name":"Accounts","category_id":5}}"""),
        "/api/v2/help_center/categories/5" or "/api/v2/help_center/en-gb/categories/5" => FakeResponse.Json("""{"category":{"id":5,"name":"Getting started"}}"""),
        "/api/v2/users/9" => FakeResponse.Json("""{"user":{"id":9,"name":"Ada Author"}}"""),
        _ => FakeResponse.NotFound(),
    };

    // ---- Search_help_center_articles ----

    [Fact]
    public async Task Search_BuildsUnifiedSearchRequestAndShapesArticles()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("""
        {
          "results": [
            { "id": 42, "type": "article", "title": "Reset", "locale": "en-gb", "html_url": "https://support.other.example/hc/en-gb/articles/42", "updated_at": "2026-01-01T00:00:00Z", "snippet": "…reset…", "body": "<p>secret</p>" },
            { "id": 99, "type": "post", "title": "Community post" }
          ],
          "meta": { "has_more": true, "after_cursor": "abc" }
        }
        """));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterArticlesTools.SearchHelpCenterArticles(zendesk, "en-GB, en-us", query: "reset password", brand_ids: "1,2", category_ids: "5", section_ids: "7", page_size: 25, after_cursor: "cur1"))!;

        var request = fake.Requests.Single();
        request.Method.Should().Be(HttpMethod.Get);
        request.Path.Should().Be("/api/v2/guide/search");
        request.Query("filter[locales]").Should().Be("en-gb,en-us");
        request.Query("query").Should().Be("reset password");
        request.Query("filter[brand_ids]").Should().Be("1,2");
        request.Query("filter[category_ids]").Should().Be("5");
        request.Query("filter[section_ids]").Should().Be("7");
        request.Query("page[size]").Should().Be("25");
        request.Query("page[after]").Should().Be("cur1");

        var articles = json["articles"]!.AsArray();
        articles.Should().ContainSingle();
        articles[0]!["brand_url"]!.GetValue<string>().Should().Be("https://support.other.example");
        articles[0]!["snippet"]!.GetValue<string>().Should().Be("…reset…");
        articles[0]!.AsObject().ContainsKey("body").Should().BeFalse("search never returns the body");
        json["pagination_info"]!["has_more"]!.GetValue<bool>().Should().BeTrue();
        json["pagination_info"]!["next_cursor"]!.GetValue<string>().Should().Be("abc");
        json["pagination_info"]!["page_size"]!.GetValue<int>().Should().Be(25);
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("en-gb", "1,abc", null)]
    [InlineData("not a locale", null, null)]
    [InlineData("en-gb", null, 0)]
    public async Task Search_RejectsInvalidInputWithoutCallingZendesk(string locales, string? brandIds, int? pageSize)
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("{}"));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => HelpCenterArticlesTools.SearchHelpCenterArticles(zendesk, locales, brand_ids: brandIds, page_size: pageSize);

        await act.Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_RejectsOverlongQuery()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("{}"));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => HelpCenterArticlesTools.SearchHelpCenterArticles(zendesk, "en-gb", query: new string('x', 501));

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*query*500*");
    }

    // ---- Get_help_center_article ----

    [Fact]
    public async Task Get_ReturnsBodyNamesAndEveryUserSegment()
    {
        using var fake = TestHelpers.CreateFakeZendesk(DefaultRoute);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterArticlesTools.GetHelpCenterArticle(zendesk, 42, locale: "en-GB"))!;

        fake.Requests.Select(r => r.Path).Should().Contain(["/api/v2/help_center/en-gb/articles/42", "/api/v2/help_center/en-gb/sections/7", "/api/v2/help_center/en-gb/categories/5", "/api/v2/users/9"]);
        fake.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
        json["body"]!.GetValue<string>().Should().Be("<p>Hello</p>");
        json["section_name"]!.GetValue<string>().Should().Be("Accounts");
        json["category_name"]!.GetValue<string>().Should().Be("Getting started");
        json["author_name"]!.GetValue<string>().Should().Be("Ada Author");
        json["user_segment_ids"]!.AsArray().Select(n => n!.GetValue<long>()).Should().Equal(11, 12);
        json["user_segment_id"]!.GetValue<long>().Should().Be(11);
        json["permission_group_id"]!.GetValue<long>().Should().Be(3);
        json["draft"]!.GetValue<bool>().Should().BeFalse();
        json["label_names"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task Get_WithoutLocale_UsesLocalelessPathAndFallsBackToSingleSegment()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/help_center/articles/42"
            ? FakeResponse.Json("""{"article":{"id":42,"title":"T","body":"b","user_segment_id":null,"section_id":7}}""")
            : FakeResponse.NotFound());
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterArticlesTools.GetHelpCenterArticle(zendesk, 42))!;

        json["user_segment_ids"]!.AsArray().Should().BeEmpty("no segment means everyone can view it");
        json["section_name"].Should().BeNull("a section the agent cannot see leaves the name empty rather than failing");
        json["category_name"].Should().BeNull();
    }

    [Fact]
    public async Task Get_AllLocales_FetchesTranslations()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/help_center/articles/42/translations"
            ? FakeResponse.Json("""{"translations":[{"id":1,"locale":"en-gb","title":"A","body":"<p>a</p>","draft":false},{"id":2,"locale":"fr","title":"B","body":"<p>b</p>","draft":true}],"meta":{"has_more":false}}""")
            : DefaultRoute(r));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterArticlesTools.GetHelpCenterArticle(zendesk, 42, locale: "all"))!;

        fake.Requests.Should().Contain(r => r.Path == "/api/v2/help_center/articles/42");
        json["translations"]!.AsArray().Select(t => t!["locale"]!.GetValue<string>()).Should().Equal("en-gb", "fr");
        json["translations"]![1]!["body"]!.GetValue<string>().Should().Be("<p>b</p>");
    }

    [Fact]
    public async Task Get_ForeignBrandUrl_IsRefusedWithoutAnyRequestToTheForeignHost()
    {
        using var fake = TestHelpers.CreateFakeZendesk(DefaultRoute);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => HelpCenterArticlesTools.GetHelpCenterArticle(zendesk, 42, brand_url: "https://attacker.example");

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*not one of this Zendesk account's brands*");
        fake.Requests.Should().NotContain(r => r.Url.Host == "attacker.example");
        fake.Requests.Should().OnlyContain(r => r.Url.Host == "acme.zendesk.com" && r.Path == "/api/v2/brands");
    }

    [Fact]
    public async Task Get_AccountBrandUrl_SendsHelpCenterCallsToThatBrand()
    {
        using var fake = TestHelpers.CreateFakeZendesk(DefaultRoute);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await HelpCenterArticlesTools.GetHelpCenterArticle(zendesk, 42, brand_url: "https://support.other.example/hc");

        // A host mapping is accepted as an address but the calls go to that brand's own zendesk.com
        // host: a mapping can name any domain, so it never receives the agent's token.
        fake.Requests.Should().Contain(r => r.Url.Host == "otherbrand.zendesk.com" && r.Path == "/api/v2/help_center/articles/42");
        fake.Requests.Should().Contain(r => r.Url.Host == "otherbrand.zendesk.com" && r.Path == "/api/v2/help_center/sections/7");
        fake.Requests.Should().NotContain(r => r.Url.Host == "support.other.example");
    }

    [Fact]
    public async Task Get_EmbedImages_InlinesAccountImagesAndSkipsForeignOnes()
    {
        const string body = """<p><img src="/hc/article_attachments/1.png" alt="a"><img src='https://evil.example/track.gif'><img src="data:image/png;base64,AAAA"></p>""";
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/help_center/articles/42" => FakeResponse.Json(new JsonObject { ["article"] = new JsonObject { ["id"] = 42, ["body"] = body } }.ToJsonString()),
            "/hc/article_attachments/1.png" => new FakeResponse(HttpStatusCode.OK, "PNGDATA", ContentType: "image/png"),
            _ => DefaultRoute(r),
        });
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterArticlesTools.GetHelpCenterArticle(zendesk, 42, embed_images: true))!;

        var html = json["body"]!.GetValue<string>();
        html.Should().Contain($"src=\"data:image/png;base64,{Convert.ToBase64String("PNGDATA"u8.ToArray())}\"");
        html.Should().Contain("https://evil.example/track.gif", "a refused image is left as it was");
        html.Should().Contain("data:image/png;base64,AAAA");
        json["images_embedded"]!.GetValue<int>().Should().Be(1);
        json["images_skipped"]!.AsArray().Should().ContainSingle().Which!["src"]!.GetValue<string>().Should().Be("https://evil.example/track.gif");
        fake.Requests.Should().NotContain(r => r.Url.Host == "evil.example", "the agent's token must never go to a host outside the account");
        fake.Requests.Should().Contain(r => r.Url.Host == "acme.zendesk.com" && r.Path == "/hc/article_attachments/1.png");
    }

    [Fact]
    public async Task Get_RejectsNonPositiveId()
    {
        using var fake = TestHelpers.CreateFakeZendesk(DefaultRoute);
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await FluentActions.Awaiting(() => HelpCenterArticlesTools.GetHelpCenterArticle(zendesk, 0)).Should().ThrowAsync<ArgumentException>().WithMessage("*article_id*");
    }

    // ---- Create_help_center_article ----

    private static Func<RecordedRequest, FakeResponse> CreateRoute(string permissionGroups) => r => (r.Method.Method, r.Path) switch
    {
        ("GET", "/api/v2/brands") => FakeResponse.Json(BrandsJson),
        ("GET", "/api/v2/guide/permission_groups") => FakeResponse.Json(permissionGroups),
        ("POST", "/api/v2/help_center/en-gb/sections/7/articles") => FakeResponse.Json("""{"article":{"id":500,"title":"New","locale":"en-gb","section_id":7,"draft":true,"permission_group_id":3,"user_segment_id":null,"html_url":"https://acme.zendesk.com/hc/en-gb/articles/500"}}"""),
        _ => FakeResponse.NotFound(),
    };

    private const string OneGroup = """{"permission_groups":[{"id":3,"name":"Admins"}],"meta":{"has_more":false}}""";
    private const string TwoGroups = """{"permission_groups":[{"id":3,"name":"Admins"},{"id":4,"name":"Writers"}],"meta":{"has_more":false}}""";

    [Fact]
    public async Task Create_PostsDraftWithSinglePermissionGroupAndExplicitNullSegment()
    {
        using var fake = TestHelpers.CreateFakeZendesk(CreateRoute(OneGroup));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterArticlesTools.CreateHelpCenterArticle(zendesk, 7, "en-GB", "New", body: "<p>Body</p>", label_names: ["a", "b"], position: 2))!;

        var post = fake.Requests.Single(r => r.Method == HttpMethod.Post);
        var article = JsonNode.Parse(post.Body!)!["article"]!.AsObject();
        article["title"]!.GetValue<string>().Should().Be("New");
        article["locale"]!.GetValue<string>().Should().Be("en-gb");
        article["draft"]!.GetValue<bool>().Should().BeTrue();
        article["permission_group_id"]!.GetValue<long>().Should().Be(3);
        article.ContainsKey("user_segment_id").Should().BeTrue();
        article["user_segment_id"].Should().BeNull();
        article["body"]!.GetValue<string>().Should().Be("<p>Body</p>");
        article["label_names"]!.AsArray().Should().HaveCount(2);
        article["position"]!.GetValue<int>().Should().Be(2);
        json["id"]!.GetValue<long>().Should().Be(500);
        json["user_segment_ids"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithSeveralPermissionGroups_RefusesAndListsThem()
    {
        using var fake = TestHelpers.CreateFakeZendesk(CreateRoute(TwoGroups));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => HelpCenterArticlesTools.CreateHelpCenterArticle(zendesk, 7, "en-gb", "New");

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*3 (Admins)*4 (Writers)*");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Create_WithExplicitPermissionGroup_DoesNotLookThemUp()
    {
        using var fake = TestHelpers.CreateFakeZendesk(CreateRoute(TwoGroups));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await HelpCenterArticlesTools.CreateHelpCenterArticle(zendesk, 7, "en-gb", "New", permission_group_id: 4, user_segment_id: 11);

        fake.Requests.Should().NotContain(r => r.Path == "/api/v2/guide/permission_groups");
        var article = JsonNode.Parse(fake.Requests.Single(r => r.Method == HttpMethod.Post).Body!)!["article"]!;
        article["permission_group_id"]!.GetValue<long>().Should().Be(4);
        article["user_segment_id"]!.GetValue<long>().Should().Be(11);
    }

    [Fact]
    public async Task Create_Publishing_IsRefusedUnlessAllowed()
    {
        using var fake = TestHelpers.CreateFakeZendesk(CreateRoute(OneGroup));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => HelpCenterArticlesTools.CreateHelpCenterArticle(zendesk, 7, "en-gb", "New", draft: false);

        (await act.Should().ThrowAsync<UnauthorizedAccessException>()).WithMessage("*AllowArticlePublishing*");
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_Publishing_IsSentWhenAllowed()
    {
        using var fake = TestHelpers.CreateFakeZendesk(CreateRoute(OneGroup));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client, configure: o => o.Features.AllowArticlePublishing = true);

        await HelpCenterArticlesTools.CreateHelpCenterArticle(zendesk, 7, "en-gb", "New", draft: false);

        JsonNode.Parse(fake.Requests.Single(r => r.Method == HttpMethod.Post).Body!)!["article"]!["draft"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Create_ValidatesLimits()
    {
        using var fake = TestHelpers.CreateFakeZendesk(CreateRoute(OneGroup));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await FluentActions.Awaiting(() => HelpCenterArticlesTools.CreateHelpCenterArticle(zendesk, 7, "en-gb", "T", label_names: Enumerable.Range(0, 21).Select(i => $"l{i}").ToArray()))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*label_names*20*");
        await FluentActions.Awaiting(() => HelpCenterArticlesTools.CreateHelpCenterArticle(zendesk, 7, "en-gb", "T", label_names: [new string('x', 101)]))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*100 characters*");
        await FluentActions.Awaiting(() => HelpCenterArticlesTools.CreateHelpCenterArticle(zendesk, 7, "en-gb", new string('x', 501)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*title*");
        await FluentActions.Awaiting(() => HelpCenterArticlesTools.CreateHelpCenterArticle(zendesk, 7, "en-gb", "T", body: new string('x', 100_001)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*body*");
        await FluentActions.Awaiting(() => HelpCenterArticlesTools.CreateHelpCenterArticle(zendesk, 7, "en-gb", "T", position: -1))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*position*");
        fake.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ForeignBrandUrl_IsRefusedBeforeAnyWrite()
    {
        using var fake = TestHelpers.CreateFakeZendesk(CreateRoute(OneGroup));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => HelpCenterArticlesTools.CreateHelpCenterArticle(zendesk, 7, "en-gb", "New", permission_group_id: 3, brand_url: "https://acme.zendesk.com.attacker.example");

        await act.Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
        fake.Requests.Should().NotContain(r => r.Url.Host != "acme.zendesk.com");
    }

    // ---- Update_article_translation ----

    [Fact]
    public async Task UpdateTranslation_PutsDraftByDefault()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Method == HttpMethod.Put
            ? FakeResponse.Json("""{"translation":{"id":1,"locale":"fr","title":"T","draft":true}}""")
            : FakeResponse.NotFound());
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterArticlesTools.UpdateArticleTranslation(zendesk, 42, "fr", title: "T", body: "<p>x</p>"))!;

        var put = fake.Requests.Single();
        put.Path.Should().Be("/api/v2/help_center/articles/42/translations/fr");
        var translation = JsonNode.Parse(put.Body!)!["translation"]!.AsObject();
        translation["title"]!.GetValue<string>().Should().Be("T");
        translation["body"]!.GetValue<string>().Should().Be("<p>x</p>");
        translation["draft"]!.GetValue<bool>().Should().BeTrue();
        json["created"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task UpdateTranslation_MissingTranslation_IsCreatedWhenUpserting()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Method == HttpMethod.Post
            ? FakeResponse.Json("""{"translation":{"id":2,"locale":"de","title":"T","draft":true}}""")
            : FakeResponse.NotFound());
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterArticlesTools.UpdateArticleTranslation(zendesk, 42, "de", title: "T"))!;

        var post = fake.Requests.Single(r => r.Method == HttpMethod.Post);
        post.Path.Should().Be("/api/v2/help_center/articles/42/translations");
        var translation = JsonNode.Parse(post.Body!)!["translation"]!;
        translation["locale"]!.GetValue<string>().Should().Be("de");
        translation["title"]!.GetValue<string>().Should().Be("T");
        translation["draft"]!.GetValue<bool>().Should().BeTrue();
        json["created"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task UpdateTranslation_MissingTranslation_WithoutUpsert_Errors()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.NotFound());
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var act = () => HelpCenterArticlesTools.UpdateArticleTranslation(zendesk, 42, "de", title: "T", upsert: false);

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*upsert*");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task UpdateTranslation_ValidatesInputAndPublishingSwitch()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("{}"));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await FluentActions.Awaiting(() => HelpCenterArticlesTools.UpdateArticleTranslation(zendesk, 42, "fr"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*title or body*");
        await FluentActions.Awaiting(() => HelpCenterArticlesTools.UpdateArticleTranslation(zendesk, 42, "fr", body: "x", draft: false))
            .Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*AllowArticlePublishing*");
        await FluentActions.Awaiting(() => HelpCenterArticlesTools.UpdateArticleTranslation(zendesk, 42, "../x", body: "x"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*locale*");
        fake.Requests.Should().BeEmpty();
    }

    // ---- Export_help_center_articles ----

    [Fact]
    public async Task Export_SectionTakesPriorityAndPagingPassesThrough()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("""
        {"articles":[{"id":1,"title":"A","body":"<p>a</p>","section_id":7,"draft":false,"user_segment_id":11,"label_names":[]}],"meta":{"has_more":true,"after_cursor":"n2"}}
        """));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterArticlesTools.ExportHelpCenterArticles(zendesk, locale: "en-us", section_id: 7, category_id: 5, page_size: 10, after_cursor: "n1"))!;

        var request = fake.Requests.Single();
        request.Path.Should().Be("/api/v2/help_center/en-us/sections/7/articles");
        request.Query("page[size]").Should().Be("10");
        request.Query("page[after]").Should().Be("n1");
        json["articles"]![0]!["body"]!.GetValue<string>().Should().Be("<p>a</p>");
        json["articles"]![0]!["user_segment_ids"]!.AsArray().Select(n => n!.GetValue<long>()).Should().Equal(11);
        json["pagination_info"]!["next_cursor"]!.GetValue<string>().Should().Be("n2");
    }

    [Theory]
    [InlineData(null, 5L, "/api/v2/help_center/categories/5/articles")]
    [InlineData(null, null, "/api/v2/help_center/articles")]
    public async Task Export_PicksPath(long? sectionId, long? categoryId, string expected)
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("""{"articles":[],"meta":{"has_more":false}}"""));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await HelpCenterArticlesTools.ExportHelpCenterArticles(zendesk, section_id: sectionId, category_id: categoryId);

        fake.Requests.Single().Path.Should().Be(expected);
    }
}
