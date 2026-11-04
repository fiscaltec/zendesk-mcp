using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using ZendeskMcp.Tools;

namespace ZendeskMcp.Tests;

public class ConnectionToolsTests
{
    [Fact]
    public async Task AConnectedPerson_SeesWhoTheyAreConnectedAs()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("{}"));
        var (service, _) = TestHelpers.BuildUserZendeskService(zendesk.Client, TestHelpers.SampleToken());

        var result = JsonNode.Parse(await ConnectionTools.GetZendeskConnection(service))!;

        result["connected"]!.GetValue<bool>().Should().BeTrue();
        result["zendesk_email"]!.GetValue<string>().Should().Be("agent@example.com");
        result.ToJsonString().Should().NotContain("zd_access", "the token itself is never shown");
        zendesk.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task APersonWithoutAConnection_IsGivenALink()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("{}"));
        var (service, _) = TestHelpers.BuildUserZendeskService(zendesk.Client, token: null);

        var result = JsonNode.Parse(await ConnectionTools.GetZendeskConnection(service))!;

        result["connected"]!.GetValue<bool>().Should().BeFalse();
        result["connect_url"]!.GetValue<string>().Should().StartWith("https://zendesk-mcp.example.com/zendesk/connect?ticket=");
    }

    [Fact]
    public async Task Disconnect_ReportsWhetherThereWasAConnection()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.WithStatus(HttpStatusCode.NoContent, string.Empty));
        var (service, _) = TestHelpers.BuildUserZendeskService(zendesk.Client, TestHelpers.SampleToken());

        JsonNode.Parse(await ConnectionTools.DisconnectZendesk(service))!["result"]!.GetValue<string>().Should().Be("disconnected");
        JsonNode.Parse(await ConnectionTools.DisconnectZendesk(service))!["result"]!.GetValue<string>().Should().Be("not_connected");
    }

    [Fact]
    public async Task TheDevelopmentToken_HasNothingToConnectOrDisconnect()
    {
        var service = TestHelpers.BuildZendeskService(TestHelpers.CreateMockHttpClient("{}"));

        JsonNode.Parse(await ConnectionTools.GetZendeskConnection(service))!["mode"]!.GetValue<string>().Should().Be("development_api_token");
        await FluentActions.Awaiting(() => ConnectionTools.GetZendeskConnectLink(service)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => ConnectionTools.DisconnectZendesk(service)).Should().ThrowAsync<ArgumentException>();
    }
}
