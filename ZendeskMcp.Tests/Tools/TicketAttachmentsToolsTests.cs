using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests.Tools;

public class TicketAttachmentsToolsTests
{
    private static FakeZendesk AttachmentFake(string attachment, string fileBody = "PNGDATA") =>
        TestHelpers.CreateFakeZendesk(r => r.Path switch
        {
            "/api/v2/attachments/55" => FakeResponse.Json(attachment),
            "/files/a.png" => new FakeResponse(HttpStatusCode.OK, fileBody, ContentType: "image/png"),
            "/api/v2/brands" => FakeResponse.Json("""{"brands":[{"id":1,"brand_url":"https://acme.zendesk.com","host_mapping":null}],"meta":{"has_more":false}}"""),
            _ => FakeResponse.NotFound(),
        });

    [Fact]
    public async Task GetTicketAttachment_ReturnsBase64AndImageBlock()
    {
        using var fake = AttachmentFake("""{"attachment":{"id":55,"file_name":"a.png","content_type":"image/png","size":7,"content_url":"https://acme.zendesk.com/files/a.png"}}""");
        var result = await TicketAttachmentsTools.GetTicketAttachment(TestHelpers.BuildZendeskService(fake.Client), 55);

        result.IsError.Should().NotBe(true);
        var text = JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
        text["file_name"]!.GetValue<string>().Should().Be("a.png");
        Convert.FromBase64String(text["content_base64"]!.GetValue<string>()).Should().Equal("PNGDATA"u8.ToArray());
        var image = result.Content.OfType<ImageContentBlock>().Single();
        image.MimeType.Should().Be("image/png");
        image.DecodedData.ToArray().Should().Equal("PNGDATA"u8.ToArray());
    }

    [Fact]
    public async Task GetTicketAttachment_NonImageHasNoImageBlock()
    {
        using var fake = AttachmentFake("""{"attachment":{"id":55,"file_name":"a.txt","content_type":"text/plain","size":7,"content_url":"https://acme.zendesk.com/files/a.png"}}""");
        var result = await TicketAttachmentsTools.GetTicketAttachment(TestHelpers.BuildZendeskService(fake.Client), 55);
        result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>();
    }

    [Fact]
    public async Task GetTicketAttachment_RefusesForeignHost()
    {
        using var fake = AttachmentFake("""{"attachment":{"id":55,"file_name":"a.png","content_type":"image/png","size":7,"content_url":"https://evil.example.com/files/a.png"}}""");
        var act = () => TicketAttachmentsTools.GetTicketAttachment(TestHelpers.BuildZendeskService(fake.Client), 55);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*evil.example.com*");
        fake.Requests.Should().NotContain(r => r.Url.Host == "evil.example.com");
    }

    [Fact]
    public async Task GetTicketAttachment_RefusesOversizedAndMalware()
    {
        using var big = AttachmentFake("""{"attachment":{"id":55,"file_name":"a.png","size":99999999999,"content_url":"https://acme.zendesk.com/files/a.png"}}""");
        await ((Func<Task>)(() => TicketAttachmentsTools.GetTicketAttachment(TestHelpers.BuildZendeskService(big.Client), 55)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*bytes*");
        big.Requests.Should().NotContain(r => r.Path == "/files/a.png");

        using var bad = AttachmentFake("""{"attachment":{"id":55,"file_name":"a.png","size":7,"malware_scan_result":"malware_found","content_url":"https://acme.zendesk.com/files/a.png"}}""");
        await ((Func<Task>)(() => TicketAttachmentsTools.GetTicketAttachment(TestHelpers.BuildZendeskService(bad.Client), 55)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*malware*");
    }

    // ---------- Request_ticket_attachment_upload ----------

    // HttpContextAccessor keeps its context in a static AsyncLocal, which BuildZendeskService also
    // sets, so tests build the service first and this context last.
    private static (IHttpContextAccessor Accessor, ZendeskUploadSlots Slots) UploadContext()
    {
        var context = new DefaultHttpContext { User = TestHelpers.UserPrincipal() };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("request-host.example.com");
        var slots = new ZendeskUploadSlots(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        return (new HttpContextAccessor { HttpContext = context }, slots);
    }

    [Fact]
    public async Task RequestUpload_IssuesOneShotSlotUnderPublicBaseUrl()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.NotFound());
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);
        var (accessor, slots) = UploadContext();

        var result = JsonNode.Parse(await TicketAttachmentsTools.RequestTicketAttachmentUpload(zendesk,
            "shot.png", 1234, accessor, slots, Options.Create(new ToolAuthorizationOptions()),
            Options.Create(new OAuthOptions { PublicBaseUrl = "https://mcp.example.com" }), inline: true))!;

        var url = result["upload_url"]!.GetValue<string>();
        url.Should().StartWith("https://mcp.example.com/uploads/");
        result["method"]!.GetValue<string>().Should().Be("PUT");
        result["max_bytes"]!.GetValue<long>().Should().Be(1234);
        result["upload_command"]!.GetValue<string>().Should().Contain(url).And.Contain("--data-binary @\"<path to file>\"");

        var slot = await slots.TakeAsync(url[(url.LastIndexOf('/') + 1)..]);
        slot.Should().NotBeNull();
        slot!.FileName.Should().Be("shot.png");
        slot.FileSize.Should().Be(1234);
        slot.Inline.Should().BeTrue();
        slot.Claims.Should().Contain(c => c.Type == "oid" && c.Value == TestHelpers.TestObjectId);
        fake.Requests.Should().BeEmpty("issuing a slot makes no Zendesk call");
    }

    [Fact]
    public async Task RequestUpload_FallsBackToRequestHost()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.NotFound());
        var zendesk = TestHelpers.BuildZendeskService(fake.Client);
        var (accessor, slots) = UploadContext();
        var result = JsonNode.Parse(await TicketAttachmentsTools.RequestTicketAttachmentUpload(zendesk,
            "notes.txt", 10, accessor, slots, Options.Create(new ToolAuthorizationOptions()), Options.Create(new OAuthOptions())))!;
        result["upload_url"]!.GetValue<string>().Should().StartWith("https://request-host.example.com/uploads/");
    }

    [Theory]
    [InlineData("../etc/passwd", 10L, false, "*bare file name*")]
    [InlineData("dir\\file.txt", 10L, false, "*bare file name*")]
    [InlineData("file.txt", 0L, false, "*file_size*")]
    [InlineData("file.txt", 999_999_999L, false, "*file_size*")]
    [InlineData("file.pdf", 10L, true, "*inline*")]
    public async Task RequestUpload_ValidatesArguments(string name, long size, bool inline, string message)
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.NotFound());
        var (accessor, slots) = UploadContext();
        var act = () => TicketAttachmentsTools.RequestTicketAttachmentUpload(TestHelpers.BuildZendeskService(fake.Client),
            name, size, accessor, slots, Options.Create(new ToolAuthorizationOptions()), Options.Create(new OAuthOptions()), inline: inline);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage(message);
    }

    [Fact]
    public async Task RequestUpload_NeedsAZendeskConnection()
    {
        using var fake = TestHelpers.CreateFakeZendesk(_ => FakeResponse.NotFound());
        var (zendesk, _) = TestHelpers.BuildUserZendeskService(fake.Client, token: null);
        var (accessor, slots) = UploadContext();
        var act = () => TicketAttachmentsTools.RequestTicketAttachmentUpload(zendesk, "a.txt", 10, accessor, slots,
            Options.Create(new ToolAuthorizationOptions()), Options.Create(new OAuthOptions()));
        await act.Should().ThrowAsync<ZendeskConnectionRequiredException>();
    }

    [Fact]
    public void RequestUpload_ServiceParametersAreNotInTheInputSchema()
    {
        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        services.AddSingleton<IDistributedCache>(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        services.AddSingleton<ZendeskUploadSlots>();
        services.AddOptions();
        services.AddScoped(_ => TestHelpers.BuildZendeskService(new HttpClient()));
        using var provider = services.BuildServiceProvider();

        var method = typeof(TicketAttachmentsTools).GetMethod(nameof(TicketAttachmentsTools.RequestTicketAttachmentUpload), BindingFlags.Public | BindingFlags.Static)!;
        var tool = McpServerTool.Create(method, target: null, new McpServerToolCreateOptions { Services = provider });
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

        properties.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["filename", "file_size", "inline"]);
    }
}
