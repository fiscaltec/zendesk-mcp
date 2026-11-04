using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ZendeskMcp.Tests;

/// <summary>
/// <see cref="ZendeskOAuthClient.RevokeGrantAsync"/>: a discarded grant is dead at Zendesk only when its
/// refresh token is, so a 401 to revoking an access token is not taken as proof while a refresh token
/// could still renew it (#12).
/// </summary>
public sealed class ZendeskOAuthClientRevokeGrantTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private const string Revoke = "/api/v2/oauth/tokens/current.json";
    private const string TokenEndpoint = "/oauth/tokens";
    private static readonly DateTimeOffset Expired = DateTimeOffset.UtcNow.AddHours(-1);

    [Fact]
    public async Task AnExpiredGrant_IsRefreshed_AndTheRenewedTokenRevoked()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(r => r.Path == TokenEndpoint
            ? FakeResponse.Json("{\"access_token\":\"renewed\",\"refresh_token\":\"r2\",\"expires_in\":7200}")
            : FakeResponse.WithStatus(HttpStatusCode.NoContent, string.Empty));

        (await Build(zendesk.Client).RevokeGrantAsync(TestHelpers.SampleToken("old", Expired, "r1"), default)).Should().BeTrue();

        zendesk.Requests.Should().ContainSingle(r => r.Path == Revoke).Which.Headers["Authorization"].Should().Be("Bearer renewed");
    }

    [Fact]
    public async Task ARefreshZendeskRefuses_MeansTheGrantIsAlreadyDead()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(r => r.Path == TokenEndpoint
            ? FakeResponse.WithStatus(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")
            : FakeResponse.WithStatus(HttpStatusCode.NoContent, string.Empty));

        (await Build(zendesk.Client).RevokeGrantAsync(TestHelpers.SampleToken("old", Expired, "r1"), default)).Should().BeTrue();

        zendesk.Requests.Should().NotContain(r => r.Path == Revoke);
    }

    [Fact]
    public async Task ATransientRefreshFailure_IsNotRevoked_AndDoesNotFallBackToTheExpiredToken()
    {
        // The expired token's 401 would read as a revocation while the refresh token stayed live.
        using var zendesk = TestHelpers.CreateFakeZendesk(r => r.Path == TokenEndpoint
            ? FakeResponse.WithStatus(HttpStatusCode.ServiceUnavailable, "{\"error\":\"temporarily_unavailable\"}")
            : FakeResponse.WithStatus(HttpStatusCode.Unauthorized));

        (await Build(zendesk.Client).RevokeGrantAsync(TestHelpers.SampleToken("old", Expired, "r1"), default)).Should().BeFalse();

        zendesk.Requests.Should().NotContain(r => r.Path == Revoke);
    }

    [Fact]
    public async Task A401ForAnUnexpiredTokenWithARefreshToken_RefreshesAndRevokesOnce()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(r => r switch
        {
            { Path: TokenEndpoint } => FakeResponse.Json("{\"access_token\":\"renewed\",\"refresh_token\":\"r2\",\"expires_in\":7200}"),
            _ when r.Headers["Authorization"] == "Bearer refused" => FakeResponse.WithStatus(HttpStatusCode.Unauthorized),
            _ => FakeResponse.WithStatus(HttpStatusCode.NoContent, string.Empty),
        });

        (await Build(zendesk.Client).RevokeGrantAsync(TestHelpers.SampleToken("refused", expiresAt: null, refreshToken: "r1"), default))
            .Should().BeTrue();

        zendesk.Requests.Where(r => r.Path == Revoke).Select(r => r.Headers["Authorization"])
            .Should().Equal("Bearer refused", "Bearer renewed");
    }

    [Fact]
    public async Task A401WithNoRefreshToken_CountsAsRevoked()
    {
        using var zendesk = TestHelpers.CreateFakeZendesk(_ => FakeResponse.WithStatus(HttpStatusCode.Unauthorized));

        (await Build(zendesk.Client).RevokeGrantAsync(TestHelpers.SampleToken("refused", refreshToken: null), default)).Should().BeTrue();

        zendesk.Requests.Should().ContainSingle();
    }

    private ZendeskOAuthClient Build(HttpClient client) =>
        new(new TestHelpers.SingleClientFactory(client),
            Options.Create(new ZendeskServerOptions
            {
                Subdomain = TestHelpers.Subdomain,
                OAuthClientId = "zendesk-test-client",
                DevelopmentOAuthClientSecret = "zendesk-test-secret",
            }),
            _cache, NullLogger<ZendeskOAuthClient>.Instance);

    public void Dispose() => _cache.Dispose();
}
