using System.Net;
using FluentAssertions;

namespace ZendeskMcp.Tests;

/// <summary>
/// The per-user credential: each call carries the caller's own Zendesk token, a person with no
/// connection is handed a connect link rather than any shared credential, and an expiring token is
/// refreshed — once, even under concurrency, because Zendesk rotates the refresh token on use.
/// </summary>
public class ZendeskCredentialProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task ACall_CarriesTheCallersOwnBearerToken()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json(TestHelpers.GetSampleTicketJson()));
        var (service, _) = TestHelpers.BuildUserZendeskService(zendesk.Client, TestHelpers.SampleToken("their-token"));

        await service.GetAsync("/api/v2/tickets/101");

        zendesk.Requests.Single().Headers["Authorization"].Should().Be("Bearer their-token");
    }

    [Fact]
    public async Task APersonWithNoConnection_GetsAConnectLink_AndNoRequestIsMade()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("{}"));
        var (service, _) = TestHelpers.BuildUserZendeskService(zendesk.Client, token: null);

        var act = () => service.GetAsync("/api/v2/tickets/101");

        var thrown = await act.Should().ThrowAsync<ZendeskConnectionRequiredException>();
        thrown.Which.ConnectUrl.Should().StartWith("https://zendesk-mcp.example.com/zendesk/connect?ticket=");
        thrown.Which.Message.Should().Contain(thrown.Which.ConnectUrl, "the model must be able to hand the person the link");
        zendesk.Requests.Should().BeEmpty("nothing may be sent to Zendesk without the caller's own credential");
    }

    [Fact]
    public void TheConnectionRequiredFailure_IsSurfacedToTheClient()
    {
        ToolErrorResult.IsSurfaceable(new ZendeskConnectionRequiredException("https://x/zendesk/connect?ticket=t", "Not connected."))
            .Should().BeTrue("the message is the link the person needs; the SDK's generic error would hide it");
    }

    [Fact]
    public async Task AZendesk401_ForgetsTheToken_AndAsksThePersonToReconnect()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.WithStatus(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_token\"}"));
        var (service, store) = TestHelpers.BuildUserZendeskService(zendesk.Client, TestHelpers.SampleToken());

        var act = () => service.GetAsync("/api/v2/tickets/101");

        await act.Should().ThrowAsync<ZendeskConnectionRequiredException>();
        (await store.GetAsync(TestHelpers.TestObjectId)).Should().BeNull("a token Zendesk refuses must not be tried again");
    }

    [Fact]
    public async Task AZendesk401_WithALiveRefreshToken_RefreshesAndRetriesOnce()
    {
        var calls = 0;
        using var zendesk = TestHelpers.CreateFakeZendesk(request => request.Path switch
        {
            "/oauth/tokens" => FakeResponse.Json("{\"access_token\":\"fresh\",\"refresh_token\":\"r2\",\"expires_in\":7200}"),
            _ when request.Headers["Authorization"] == "Bearer revoked-early" => FakeResponse.WithStatus(HttpStatusCode.Unauthorized),
            _ => Interlocked.Increment(ref calls) > 0 ? FakeResponse.Json(TestHelpers.GetSampleTicketJson()) : FakeResponse.Json("{}"),
        });
        var (service, store) = TestHelpers.BuildUserZendeskService(zendesk.Client, TestHelpers.SampleToken("revoked-early", refreshToken: "r1"));

        var result = await service.GetAsync("/api/v2/tickets/101");

        result["ticket"]!["id"]!.GetValue<long>().Should().Be(101);
        (await store.GetAsync(TestHelpers.TestObjectId))!.AccessToken.Should().Be("fresh",
            "a 401 can mean an access token revoked early while the grant is still good");
    }

    [Fact]
    public async Task AZendesk401_ForATokenAlreadyReplaced_RetriesWithTheNewOne_AndForgetsNothing()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(request =>
            request.Headers["Authorization"] == "Bearer new"
                ? FakeResponse.Json(TestHelpers.GetSampleTicketJson())
                : FakeResponse.WithStatus(HttpStatusCode.Unauthorized));
        var (service, store) = TestHelpers.BuildUserZendeskService(zendesk.Client, TestHelpers.SampleToken("old", refreshToken: null));
        var header = await service.Credentials.GetAuthorizationAsync();
        await store.SaveAsync(TestHelpers.TestObjectId, TestHelpers.SampleToken("new", refreshToken: null));

        // A request that went out with the old token comes back 401 after a reconnect stored a new one.
        var retry = await service.Credentials.OnTokenRejectedAsync(header, allowRecovery: true);

        retry.Parameter.Should().Be("new");
        (await store.GetAsync(TestHelpers.TestObjectId))!.AccessToken.Should().Be("new", "the working grant must not be wiped");
    }

    [Fact]
    public async Task AnExpiringToken_IsRefreshedBeforeUse_AndTheRotatedTokenIsStored()
    {
        var time = new FixedClock(Now);
        using var zendesk = TestHelpers.CreateFakeZendesk(request => request.Path switch
        {
            "/oauth/tokens" => FakeResponse.Json("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":7200,\"token_type\":\"bearer\"}"),
            _ => FakeResponse.Json(TestHelpers.GetSampleTicketJson()),
        });
        var (service, store) = TestHelpers.BuildUserZendeskService(zendesk.Client,
            TestHelpers.SampleToken("old-access", expiresAt: Now.AddSeconds(30), refreshToken: "old-refresh"),
            timeProvider: time);

        await service.GetAsync("/api/v2/tickets/101");

        var refresh = zendesk.Requests.Single(r => r.Path == "/oauth/tokens");
        refresh.Body.Should().Contain("\"grant_type\":\"refresh_token\"").And.Contain("\"refresh_token\":\"old-refresh\"");
        zendesk.Requests.Last().Headers["Authorization"].Should().Be("Bearer new-access");

        var stored = await store.GetAsync(TestHelpers.TestObjectId);
        stored!.AccessToken.Should().Be("new-access");
        stored.RefreshToken.Should().Be("new-refresh", "Zendesk rotates refresh tokens, so the old one is spent");
        stored.ExpiresAt.Should().Be(Now.AddSeconds(7200));
        stored.ZendeskUserId.Should().Be(900001, "a refresh keeps the identity confirmed at connect time");
    }

    [Fact]
    public async Task ConcurrentCalls_RefreshOnlyOnce()
    {
        var time = new FixedClock(Now);
        var refreshes = 0;
        using var zendesk = TestHelpers.CreateFakeZendesk(request =>
        {
            if (request.Path == "/oauth/tokens")
            {
                Interlocked.Increment(ref refreshes);
                Thread.Sleep(50);
                return FakeResponse.Json("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":7200}");
            }

            return FakeResponse.Json(TestHelpers.GetSampleTicketJson());
        });
        var (service, _) = TestHelpers.BuildUserZendeskService(zendesk.Client,
            TestHelpers.SampleToken("old-access", expiresAt: Now.AddSeconds(30), refreshToken: "old-refresh"),
            timeProvider: time);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.GetAsync("/api/v2/tickets/101")));

        refreshes.Should().Be(1, "a second refresh would present the already-rotated token and fail");
    }

    [Fact]
    public async Task ARefusedRefresh_DisconnectsAndHandsBackALink()
    {
        var time = new FixedClock(Now);
        using var zendesk = TestHelpers.CreateFakeZendesk(request => request.Path == "/oauth/tokens"
            ? FakeResponse.WithStatus(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"echoes old-refresh\"}")
            : FakeResponse.Json("{}"));
        var (service, store) = TestHelpers.BuildUserZendeskService(zendesk.Client,
            TestHelpers.SampleToken("old-access", expiresAt: Now.AddSeconds(30), refreshToken: "old-refresh"),
            timeProvider: time);

        var act = () => service.GetAsync("/api/v2/tickets/101");

        var thrown = await act.Should().ThrowAsync<ZendeskConnectionRequiredException>();
        thrown.Which.Message.Should().NotContain("old-refresh", "Zendesk's error description can echo the request");
        (await store.GetAsync(TestHelpers.TestObjectId)).Should().BeNull();
    }

    [Fact]
    public async Task ANonExpiringToken_IsNeverRefreshed()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json(TestHelpers.GetSampleTicketJson()));
        var (service, _) = TestHelpers.BuildUserZendeskService(zendesk.Client, TestHelpers.SampleToken(expiresAt: null, refreshToken: null));

        await service.GetAsync("/api/v2/tickets/101");

        zendesk.Requests.Should().NotContain(r => r.Path == "/oauth/tokens");
    }

    [Fact]
    public async Task TheDevelopmentToken_AuthenticatesWithZendeskApiTokenBasicAuth()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json(TestHelpers.GetSampleTicketJson()));
        var service = TestHelpers.BuildZendeskService(zendesk.Client);

        await service.GetAsync("/api/v2/tickets/101");

        var header = zendesk.Requests.Single().Headers["Authorization"];
        header.Should().StartWith("Basic ");
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]))
            .Should().Be("dev@example.com/token:zd_test_token");
    }

    [Fact]
    public async Task TheDevelopmentAccessToken_IsSentAsABearerToken()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json(TestHelpers.GetSampleTicketJson()));
        var service = TestHelpers.BuildZendeskService(zendesk.Client, configure: o =>
        {
            o.DevelopmentApiToken = null;
            o.DevelopmentAccessToken = "zd_oauth_dev";
        });

        await service.GetAsync("/api/v2/tickets/101");

        zendesk.Requests.Single().Headers["Authorization"].Should().Be("Bearer zd_oauth_dev");
    }

    [Fact]
    public async Task Disconnect_RevokesAtZendesk_AndForgetsTheToken()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.WithStatus(HttpStatusCode.NoContent, string.Empty));
        var (service, store) = TestHelpers.BuildUserZendeskService(zendesk.Client, TestHelpers.SampleToken("to-revoke"));

        var removed = await service.Credentials.DisconnectAsync();

        removed.Should().BeTrue();
        var revoke = zendesk.Requests.Single();
        revoke.Method.Should().Be(HttpMethod.Delete);
        revoke.Path.Should().Be("/api/v2/oauth/tokens/current.json");
        revoke.Headers["Authorization"].Should().Be("Bearer to-revoke");
        (await store.GetAsync(TestHelpers.TestObjectId)).Should().BeNull();
    }

    [Fact]
    public async Task ACallerWithoutAnObjectId_IsRefused()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.Json("{}"));
        var service = TestHelpers.BuildZendeskService(zendesk.Client,
            user: new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("preferred_username", "agent@example.com")], "Test")));

        var act = () => service.GetAsync("/api/v2/tickets/101");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        zendesk.Requests.Should().BeEmpty();
    }

    // A fixed clock; this repo avoids Microsoft.Extensions.TimeProvider.Testing for one overridden
    // method, as GraphGroupPermissionResolverTests does.
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
