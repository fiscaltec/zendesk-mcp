using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

public class HelpCenterMediaToolsTests
{
    private const string UploadUrlJson = """
    {"upload_url":{"asset_upload_id":"up-1","url":"https://uploads.example-storage.com/abc?sig=x'y","headers":{"Content-Type":"image/png","Content-Disposition":"attachment; filename=\"a.png\""}}}
    """;

    [Fact]
    public async Task RequestUpload_PostsSizeAndTypeAndReturnsCurlCommand()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json(UploadUrlJson));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterMediaTools.RequestHelpCenterImageUpload(zendesk, "step-2.png", "image/png", 1234))!;

        var post = fake.Requests.Single();
        post.Method.Should().Be(HttpMethod.Post);
        post.Path.Should().Be("/api/v2/guide/medias/upload_url");
        var body = JsonNode.Parse(post.Body!)!;
        body["content_type"]!.GetValue<string>().Should().Be("image/png");
        body["file_size"]!.GetValue<long>().Should().Be(1234);

        json["asset_upload_id"]!.GetValue<string>().Should().Be("up-1");
        json["method"]!.GetValue<string>().Should().Be("PUT");
        json["headers"]!["Content-Type"]!.GetValue<string>().Should().Be("image/png");
        var command = json["upload_command"]!.GetValue<string>();
        command.Should().StartWith("curl -sS -X PUT -H 'Content-Type: image/png'");
        command.Should().Contain("--data-binary @\"<path>\"");
        command.Should().EndWith("'https://uploads.example-storage.com/abc?sig=x'\\''y'", "a quote in the URL must not break out of the shell quoting");
        json["next_step"]!.GetValue<string>().Should().Contain("Create_help_center_image");
        fake.Requests.Should().NotContain(r => r.Url.Host == "uploads.example-storage.com", "the server never calls the pre-signed URL");
    }

    [Theory]
    [InlineData("a.png", "image/bmp", 10L)]
    [InlineData("a.png", "image/png", 0L)]
    [InlineData("a.png", "image/png", 20_971_521L)]
    [InlineData("../a.png", "image/png", 10L)]
    [InlineData("", "image/png", 10L)]
    public async Task RequestUpload_ValidatesInput(string filename, string contentType, long size)
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json(UploadUrlJson));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await FluentActions.Awaiting(() => HelpCenterMediaTools.RequestHelpCenterImageUpload(zendesk, filename, contentType, size))
            .Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().BeEmpty();
    }

    [Theory]
    // The two forms Zendesk's documentation gives: headers beside upload_url, and inside it as a JSON
    // string. The live API sends the second.
    [InlineData("""{"headers":{"Content-Type":"image/png","X-Amz-Server-Side-Encryption":"AES256"},"upload_url":{"url":"https://uploads.example-storage.com/abc","asset_upload_id":"up-1"}}""")]
    [InlineData("""{"upload_url":{"asset_upload_id":"up-1","headers":"{\"Content-Type\":\"image/png\",\"X-Amz-Server-Side-Encryption\":\"AES256\"}","url":"https://uploads.example-storage.com/abc"}}""")]
    public async Task RequestUpload_ReadsEitherDocumentedHeaderForm(string response)
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json(response));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterMediaTools.RequestHelpCenterImageUpload(zendesk, "a.png", "image/png", 10))!;

        json["headers"]!["X-Amz-Server-Side-Encryption"]!.GetValue<string>().Should().Be("AES256");
        json["upload_command"]!.GetValue<string>().Should().Contain("-H 'X-Amz-Server-Side-Encryption: AES256'");
    }

    [Fact]
    public async Task RequestUpload_UnreadableHeaders_AreRefused()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("""{"upload_url":{"asset_upload_id":"up-1","headers":"not json","url":"https://uploads.example-storage.com/abc"}}"""));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await FluentActions.Awaiting(() => HelpCenterMediaTools.RequestHelpCenterImageUpload(zendesk, "a.png", "image/png", 10))
            .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task RequestUpload_ForeignBrandUrl_IsRefused()
    {
        using var fake = TestHelpers.CreateFakeZendesk(r => r.Path == "/api/v2/brands" ? FakeResponse.Json(HelpCenterArticlesToolsTests.BrandsJson) : FakeResponse.Json(UploadUrlJson));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await FluentActions.Awaiting(() => HelpCenterMediaTools.RequestHelpCenterImageUpload(zendesk, "a.png", "image/png", 10, brand_url: "https://evil.example"))
            .Should().ThrowAsync<ArgumentException>();
        fake.Requests.Should().OnlyContain(r => r.Path == "/api/v2/brands" && r.Url.Host == "acme.zendesk.com");
    }

    [Fact]
    public async Task CreateImage_RegistersUploadAndEncodesImgTag()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("""{"media":{"id":77,"url":"https://acme.zendesk.com/guide-media/a.png?x=1&y=2","name":"a.png","content_type":"image/png","size":10}}"""));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterMediaTools.CreateHelpCenterImage(zendesk, "up-1", "a.png", alt_text: "Say \"hi\" <now>"))!;

        var post = fake.Requests.Single();
        post.Path.Should().Be("/api/v2/guide/medias");
        var body = JsonNode.Parse(post.Body!)!;
        body["asset_upload_id"]!.GetValue<string>().Should().Be("up-1");
        body["filename"]!.GetValue<string>().Should().Be("a.png");
        json["id"]!.GetValue<long>().Should().Be(77);
        json["img_tag"]!.GetValue<string>().Should().Be("<img src=\"https://acme.zendesk.com/guide-media/a.png?x=1&amp;y=2\" alt=\"Say &quot;hi&quot; &lt;now&gt;\">");
    }

    [Fact]
    public async Task CreateImage_AltTextDefaultsToFilename()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("""{"media":{"id":77,"url":"https://acme.zendesk.com/m.png"}}"""));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        var json = JsonNode.Parse(await HelpCenterMediaTools.CreateHelpCenterImage(zendesk, "up-1", "shot.png"))!;

        json["img_tag"]!.GetValue<string>().Should().Contain("alt=\"shot.png\"");
    }

    [Fact]
    public async Task CreateImage_ValidatesLengths()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("{}"));
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);

        await FluentActions.Awaiting(() => HelpCenterMediaTools.CreateHelpCenterImage(zendesk, new string('x', 201), "a.png"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*asset_upload_id*");
        await FluentActions.Awaiting(() => HelpCenterMediaTools.CreateHelpCenterImage(zendesk, "up", "a.png", alt_text: new string('x', 501)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*alt_text*");
        fake.Requests.Should().BeEmpty();
    }
}
