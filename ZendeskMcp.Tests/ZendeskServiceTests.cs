using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace ZendeskMcp.Tests;

/// <summary>
/// The transport every tool shares: URL building, error surfacing, cursor paging, and the two
/// credential boundaries — a <c>brand_url</c> and an attachment URL may only ever reach this
/// account's own hosts, because the caller's Zendesk token goes wherever the request goes.
/// </summary>
public class ZendeskServiceTests
{
    private const string BrandsJson = """
        {"brands":[
          {"id":1,"name":"Main","brand_url":"https://acme.zendesk.com","host_mapping":null},
          {"id":2,"name":"Other","brand_url":"https://acme-other.zendesk.com","host_mapping":"help.other.example"}
        ],"meta":{"has_more":false}}
        """;

    [Fact]
    public async Task QueryValues_AreEscaped_AndNullsDropped()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("{}"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        await service.GetAsync("/api/v2/search/export", [new("query", "type:ticket subject:\"a&b=c\""), new("skip", null)]);

        var request = zendesk.Requests.Single();
        request.Url.GetLeftPart(UriPartial.Path).Should().Be("https://acme.zendesk.com/api/v2/search/export");
        request.Query("query").Should().Be("type:ticket subject:\"a&b=c\"");
        request.Url.Query.Should().NotContain("skip");
    }

    [Fact]
    public async Task EveryRequest_AsksForJson()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("{}"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        await service.GetAsync("/api/v2/brands");
        await service.PostAsync("/api/v2/tickets", new JsonObject());

        zendesk.Requests.Should().AllSatisfy(r => r.Headers["Accept"].Should().Be("application/json",
            "Zendesk answers 415 to a path without .json unless the request asks for JSON"));
    }

    [Fact]
    public async Task AZendeskRefusal_SurfacesItsBody()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.WithStatus(HttpStatusCode.UnprocessableEntity,
            "{\"error\":\"RecordInvalid\",\"description\":\"Record validation errors\",\"details\":{\"subject\":[\"is too long\"]}}"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        var act = () => service.PostAsync("/api/v2/tickets", new JsonObject());

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("422").And.Contain("is too long", "the model needs Zendesk's reason to correct its call");
    }

    [Fact]
    public async Task GetAllPages_FollowsTheCursor_AndMergesSideloads()
    {
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(
            "{\"tickets\":[{\"id\":1}],\"users\":[{\"id\":10}],\"meta\":{\"has_more\":true,\"after_cursor\":\"c1\"}}",
            "{\"tickets\":[{\"id\":2}],\"users\":[{\"id\":20}],\"meta\":{\"has_more\":false,\"after_cursor\":null}}");
        using var _client = client;
        var service = TestHelpers.BuildZendeskService(client);

        var paged = await service.GetAllPagesAsync("/api/v2/tickets", null, "tickets", maxItems: 100);

        paged.Items.Select(t => t["id"]!.GetValue<long>()).Should().Equal(1, 2);
        paged.HasMore.Should().BeFalse();
        paged.PagesFetched.Should().Be(2);
        ((JsonArray)paged.Sideloads["users"]!).Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAllPages_FollowsAnOffsetNextPage_WhenTheEndpointIgnoresCursors()
    {
        // permission_groups answers page[size] with offset pagination; only page and per_page are
        // taken from next_page, so a foreign host in it never receives the request.
        using var zendesk = TestHelpers.CreateFakeZendesk(r => r.Query("page") switch
        {
            null => FakeResponse.Json("{\"permission_groups\":[{\"id\":1}],\"next_page\":\"https://elsewhere.example/api/v2/guide/permission_groups.json?per_page=30&page=2\",\"count\":2}"),
            _ => FakeResponse.Json("{\"permission_groups\":[{\"id\":2}],\"next_page\":null,\"count\":2}"),
        });
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        var paged = await service.GetAllPagesAsync("/api/v2/guide/permission_groups", null, "permission_groups", maxItems: 100);

        paged.Items.Select(g => g["id"]!.GetValue<long>()).Should().Equal(1, 2);
        paged.HasMore.Should().BeFalse();
        zendesk.Requests.Should().AllSatisfy(r => r.Url.Host.Should().Be("acme.zendesk.com"));
        zendesk.Requests[1].Query("page").Should().Be("2");
        zendesk.Requests[1].Query("per_page").Should().Be("30");
        zendesk.Requests[1].Query("page[size]").Should().BeNull();
    }

    [Fact]
    public async Task GetAllPages_StopsAtThePageCap_AndReportsTheCursor()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ =>
            FakeResponse.Json("{\"tickets\":[{\"id\":1}],\"meta\":{\"has_more\":true,\"after_cursor\":\"next\"}}"));
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildZendeskService(zendesk.Client, maxAutoPageFetches: 3, auditContext: context);

        var paged = await service.GetAllPagesAsync("/api/v2/tickets", null, "tickets", maxItems: 1000);

        zendesk.Requests.Should().HaveCount(3);
        paged.HasMore.Should().BeTrue();
        paged.NextCursor.Should().Be("next");
        zendesk.Requests[1].Query("page[after]").Should().Be("next");
    }

    [Fact]
    public async Task ABrandUrl_ForOneOfTheAccountsBrands_IsUsedAsTheBase()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/brands"
            ? FakeResponse.Json(BrandsJson)
            : FakeResponse.Json("{\"articles\":[]}"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        await service.GetAsync("/api/v2/help_center/articles", brandUrl: "https://acme-other.zendesk.com/hc/en-gb");

        zendesk.Requests.Last().Url.GetLeftPart(UriPartial.Path).Should().Be("https://acme-other.zendesk.com/api/v2/help_center/articles");
    }

    [Fact]
    public async Task ABrandsHostMapping_IsTranslatedToItsZendeskHost_AndNeverSentTheCredential()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/brands"
            ? FakeResponse.Json(BrandsJson)
            : FakeResponse.Json("{\"articles\":[]}"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        await service.GetAsync("/api/v2/help_center/articles", brandUrl: "https://help.other.example/hc/en-gb");

        zendesk.Requests.Last().Url.Host.Should().Be("acme-other.zendesk.com");
        zendesk.Requests.Should().NotContain(r => r.Url.Host == "help.other.example",
            "a host mapping can name any domain; only hosts Zendesk controls receive the token");
    }

    // The live shape: a brand with a host mapping reports the mapped host as its brand_url.
    private const string MappedBrandsJson = """
        {"brands":[{"id":1,"name":"Main","subdomain":"acme","brand_url":"https://help.acme.example","host_mapping":"help.acme.example"}],"meta":{"has_more":false}}
        """;

    [Fact]
    public async Task ABrandUrl_ThatIsItsHostMapping_IsTranslatedToTheSubdomainHost()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/brands"
            ? FakeResponse.Json(MappedBrandsJson)
            : FakeResponse.Json("{\"articles\":[]}"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        await service.GetAsync("/api/v2/help_center/articles", brandUrl: "https://help.acme.example");

        zendesk.Requests.Last().Url.Host.Should().Be("acme.zendesk.com");
        zendesk.Requests.Should().NotContain(r => r.Url.Host == "help.acme.example");
    }

    [Fact]
    public async Task ADownload_FromAMappedHelpCenterHost_GoesToTheSubdomainHost()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/brands"
            ? FakeResponse.Json(MappedBrandsJson)
            : new FakeResponse(HttpStatusCode.OK, "png", ContentType: "image/png"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        await service.DownloadAsync("https://help.acme.example/hc/article_attachments/1/x.png");

        var file = zendesk.Requests.Last();
        file.Url.GetLeftPart(UriPartial.Path).Should().Be("https://acme.zendesk.com/hc/article_attachments/1/x.png");
        file.Headers.Should().ContainKey("Authorization");
        zendesk.Requests.Should().NotContain(r => r.Url.Host == "help.acme.example");
    }

    [Fact]
    public async Task ABrandWhoseUrlIsNotAZendeskHost_IsNotACredentialDestination()
    {
        const string brands = """
            {"brands":[{"id":3,"name":"Odd","brand_url":"https://odd.example","host_mapping":null}],"meta":{"has_more":false}}
            """;
        using var zendesk = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/brands" ? FakeResponse.Json(brands) : FakeResponse.Json("{}"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        var act = () => service.GetAsync("/api/v2/help_center/articles", brandUrl: "https://odd.example");

        await act.Should().ThrowAsync<ArgumentException>();
        zendesk.Requests.Should().NotContain(r => r.Url.Host == "odd.example");
    }

    [Theory]
    [InlineData("https://acme.zendesk.com/api/v2/users/me.json")]
    [InlineData("https://acme.zendesk.com//api/v2/users/me.json")]
    [InlineData("https://acme.zendesk.com/%2Fapi/v2/users/me.json")]
    [InlineData("https://acme.zendesk.com/api%2Fv2/users/me.json")]
    [InlineData("https://acme.zendesk.com/API/v2/users/me.json")]
    [InlineData("https://acme.zendesk.com/x/../api/v2/users/me.json")]
    public async Task ADownload_OfAnApiPath_IsRefused(string url)
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("{}"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        var act = () => service.DownloadAsync(url);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*API path*");
        zendesk.Requests.Should().BeEmpty("an image src in an article must not make each reader's token call the API");
    }

    [Fact]
    public async Task ADownloadRedirect_ToTheAttachmentCdn_IsFollowedWithoutTheCredential()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(r => r.Url.Host switch
        {
            "acme.zendesk.com" => new FakeResponse(HttpStatusCode.Found, string.Empty,
                new Dictionary<string, string> { ["Location"] = "https://acme.zdusercontent.com/attachment/1/a.png?token=signed" }),
            _ => new FakeResponse(HttpStatusCode.OK, "PNG", ContentType: "image/png"),
        });
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        var file = await service.DownloadAsync("https://acme.zendesk.com/attachments/token/abc/?name=a.png");

        file.ContentType.Should().Be("image/png");
        var cdn = zendesk.Requests.Single(r => r.Url.Host == "acme.zdusercontent.com");
        cdn.Headers.Should().NotContainKey("Authorization", "the CDN URL is signed and must never see the token");
    }

    [Fact]
    public async Task ADownloadRedirect_ElsewhereIsRefused()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/brands"
            ? FakeResponse.Json(BrandsJson)
            : r.Url.Host == "acme.zendesk.com"
                ? new FakeResponse(HttpStatusCode.Found, string.Empty, new Dictionary<string, string> { ["Location"] = "https://attacker.example/x" })
                : FakeResponse.Json("{}"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        var act = () => service.DownloadAsync("https://acme.zendesk.com/hc/article_attachments/1/x.png");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*redirect*");
        zendesk.Requests.Should().NotContain(r => r.Url.Host == "attacker.example");
    }

    [Theory]
    [InlineData("https://attacker.example")]
    [InlineData("https://acme.zendesk.com.attacker.example")]
    [InlineData("http://acme-other.zendesk.com")]
    [InlineData("not a url")]
    public async Task ABrandUrl_ThatIsNotTheAccounts_IsRefused_BeforeAnyRequestReachesIt(string brandUrl)
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/brands"
            ? FakeResponse.Json(BrandsJson)
            : FakeResponse.Json("{}"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        var act = () => service.GetAsync("/api/v2/help_center/articles", brandUrl: brandUrl);

        await act.Should().ThrowAsync<ArgumentException>();
        zendesk.Requests.Should().OnlyContain(r => r.Url.Host == "acme.zendesk.com",
            "the caller's token must never be sent to a host the account does not own");
    }

    [Fact]
    public async Task ADownload_FromAnotherHost_IsRefused_WithoutSendingTheToken()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/brands"
            ? FakeResponse.Json(BrandsJson)
            : FakeResponse.Json("{}"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        var act = () => service.DownloadAsync("https://files.attacker.example/a.pdf");

        await act.Should().ThrowAsync<ArgumentException>();
        zendesk.Requests.Should().NotContain(r => r.Url.Host == "files.attacker.example");
    }

    [Fact]
    public async Task ADownload_LargerThanTheLimit_IsRefused()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => new FakeResponse(HttpStatusCode.OK, new string('x', 2048), ContentType: "application/pdf"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client, configure: o => o.MaxAttachmentBytes = 1024);

        var act = () => service.DownloadAsync("https://acme.zendesk.com/attachments/token/abc/?name=a.pdf");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*1024*");
    }

    [Fact]
    public async Task TheRbacBackstop_RefusesAWriteFromAReader_BeforeAnythingIsSent()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("{}"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client,
            authorizer: TestHelpers.BuildEnabledAuthorizer("zendesk:read"));

        var act = () => service.PutAsync("/api/v2/tickets/1", new JsonObject());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        zendesk.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Paths_MustBeRooted()
    {
        var service = TestHelpers.BuildZendeskService(TestHelpers.CreateMockHttpClient("{}"));

        var act = () => service.BuildUrl("api/v2/tickets", null, null);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Segment_EscapesSeparators()
    {
        ZendeskService.Segment("en-gb/../../users").Should().Be("en-gb%2F..%2F..%2Fusers");
    }
}
