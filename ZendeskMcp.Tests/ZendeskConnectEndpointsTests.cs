using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ZendeskMcp.Tests;

/// <summary>
/// The browser half of a person's Zendesk connection, end to end through the real host: a link
/// issued to one Entra identity, the redirect to Zendesk with PKCE, and the callback that stores a
/// token only when the Zendesk login is that same person.
/// </summary>
/// <remarks>
/// The refusal cases are the security property. A connect link opened by someone else — forwarded,
/// leaked, or planted — would otherwise attach <i>their</i> Zendesk account to the link owner's
/// identity, so every later call by the owner would act as them.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
public class ZendeskConnectEndpointsTests
{
    private const string ObjectId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    [Fact]
    public async Task TheFullFlow_StoresTheTokenAgainstTheLinksOwner()
    {
        using var factory = new Factory(zendeskEmail: "Agent@Example.com");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var link = await factory.IssueLinkAsync(ObjectId, "agent@example.com");

        var connect = await client.GetAsync(link);

        connect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = connect.Headers.Location!.ToString();
        location.Should().StartWith("https://acme.zendesk.com/oauth/authorizations/new?");
        location.Should().Contain("client_id=zendesk-test-client")
            .And.Contain("code_challenge_method=S256")
            .And.Contain("redirect_uri=" + Uri.EscapeDataString("http://localhost/zendesk/callback"));
        var state = QueryValue(location, "state");
        CookieValue(connect).Should().Be(state, "the state is pinned to this browser");

        var callback = await GetWithCookieAsync(client, $"/zendesk/callback?code=the-code&state={state}", state);

        callback.StatusCode.Should().Be(HttpStatusCode.OK);
        (await callback.Content.ReadAsStringAsync()).Should().Contain("Zendesk connected");
        var stored = await factory.Store.GetAsync(ObjectId);
        stored!.AccessToken.Should().Be("issued-access");
        stored.ZendeskUserId.Should().Be(4242);

        var exchange = factory.ZendeskRequests.Single(r => r.Path == "/oauth/tokens");
        exchange.Body.Should().Contain("\"grant_type\":\"authorization_code\"")
            .And.Contain("\"code\":\"the-code\"")
            .And.Contain("\"client_secret\":\"zendesk-test-secret\"")
            .And.Contain("\"code_verifier\":", "PKCE binds the code to this attempt");
    }

    [Fact]
    public async Task AnIdentityCheckThatTimesOut_IsRevoked_AndNotStored()
    {
        // HttpClient.Timeout raises TaskCanceledException with the request's own token untouched.
        // The new grant was neither checked nor stored, so it must be revoked, not left live (#13).
        using var factory = new Factory(zendeskEmail: "agent@example.com")
        {
            StallPath = "/api/v2/users/me.json",
            OAuthTimeout = TimeSpan.FromMilliseconds(200),
        };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var state = await StartAsync(factory, client, "agent@example.com");

        var callback = await GetWithCookieAsync(client, $"/zendesk/callback?code=c&state={state}", state);

        callback.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await factory.Store.GetAsync(ObjectId, TestContext.Current.CancellationToken)).Should().BeNull();
        factory.Revocations.Should().Equal("issued-access");
    }

    [Fact]
    public async Task ABrowserThatGivesUpDuringTheIdentityCheck_StillHasTheNewGrantRevoked()
    {
        // The request's own cancellation must still propagate, but cleanup cannot use the token that
        // has just been cancelled: it gets a bounded token of its own.
        using var factory = new Factory(zendeskEmail: "agent@example.com") { StallPath = "/api/v2/users/me.json" };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var state = await StartAsync(factory, client, "agent@example.com");

        using var abandon = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/zendesk/callback?code=c&state={state}");
        request.Headers.Add("Cookie", $"zendesk_connect_state={state}");
        var callback = client.SendAsync(request, abandon.Token);
        await WaitUntilAsync(() => factory.ZendeskRequests.Any(r => r.Path == "/api/v2/users/me.json"));
        await abandon.CancelAsync();
        await FluentActions.Awaiting(() => callback).Should().ThrowAsync<OperationCanceledException>();

        await WaitUntilAsync(() => factory.Revocations.Count > 0);
        factory.Revocations.Should().Equal("issued-access");
        (await factory.Store.GetAsync(ObjectId, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task ABrowserThatGivesUpDuringTheCodeExchange_StillHasTheIssuedGrantRevoked()
    {
        // The exchange runs on a token of its own, so a grant Zendesk issues reaches UnkeptGrant even
        // when the browser has gone, and is revoked rather than lost while live (#13).
        using var factory = new Factory(zendeskEmail: "agent@example.com") { SlowPath = ("/oauth/tokens", TimeSpan.FromMilliseconds(400)) };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var state = await StartAsync(factory, client, "agent@example.com");

        using var abandon = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/zendesk/callback?code=c&state={state}");
        request.Headers.Add("Cookie", $"zendesk_connect_state={state}");
        var callback = client.SendAsync(request, abandon.Token);
        await WaitUntilAsync(() => factory.ZendeskRequests.Any(r => r.Path == "/oauth/tokens"));
        await abandon.CancelAsync();
        await FluentActions.Awaiting(() => callback).Should().ThrowAsync<OperationCanceledException>();

        await WaitUntilAsync(() => factory.Revocations.Count > 0);
        factory.Revocations.Should().Equal("issued-access");
        (await factory.Store.GetAsync(ObjectId, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task AReconnectWhoseStoreFailsAfterTheWriteLanded_RevokesBothGrants_AndForgetsTheConnection()
    {
        // SaveAsync has overwritten the previous grant before it fails. Revoking only the new grant
        // would leave the previous one neither stored nor revoked, and the store holding a revoked
        // token (#13).
        using var factory = new Factory(zendeskEmail: "agent@example.com");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await factory.Store.SaveAsync(ObjectId, TestHelpers.SampleToken("previous-access"), TestContext.Current.CancellationToken);
        var state = await StartAsync(factory, client, "agent@example.com");
        factory.AfterSave = () => throw new InvalidOperationException("the vault write reported a failure after landing");

        var callback = await GetWithCookieAsync(client, $"/zendesk/callback?code=c&state={state}", state);

        callback.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        factory.Revocations.Should().BeEquivalentTo(["issued-access", "previous-access"]);
        (await factory.Store.GetAsync(ObjectId, TestContext.Current.CancellationToken)).Should().BeNull("the store must not keep a revoked grant");
    }

    [Fact]
    public async Task AReconnectWhoseBrowserLeavesAfterTheStore_StillRevokesTheReplacedGrant()
    {
        // The replaced grant is revoked after the new one is kept. On the request's own token, a
        // browser that left by then would cancel that revocation and leave the old grant live; it runs
        // on a cleanup token instead (#13).
        using var factory = new Factory(zendeskEmail: "agent@example.com");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await factory.Store.SaveAsync(ObjectId, TestHelpers.SampleToken("previous-access"), TestContext.Current.CancellationToken);
        var state = await StartAsync(factory, client, "agent@example.com");
        using var browser = new CancellationTokenSource();
        factory.AfterSave = browser.Cancel;

        await FluentActions.Awaiting(() => GetWithCookieAsync(client, $"/zendesk/callback?code=c&state={state}", state, browser.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        factory.AfterSave = null;

        await WaitUntilAsync(() => factory.Revocations.Contains("previous-access"));
        factory.Revocations.Should().Equal(["previous-access"], "the replaced grant is revoked, and the kept one is not");
        (await factory.Store.GetAsync(ObjectId, TestContext.Current.CancellationToken))!.AccessToken.Should().Be("issued-access");
    }

    [Fact]
    public async Task AReconnect_OverAnExpiredGrant_RefreshesItFirst_SoTheRevocationReachesALiveGrant()
    {
        // Revoking an expired access token answers 401, which reads as revoked while the refresh
        // token, and so the old grant, stays live (#12) — and an expired grant is the usual reason
        // to reconnect.
        using var factory = new Factory(zendeskEmail: "agent@example.com");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await factory.Store.SaveAsync(ObjectId, new ZendeskUserToken("old-access", "old-refresh", DateTimeOffset.UtcNow.AddHours(-1), null,
            4242, "agent@example.com", "agent", DateTimeOffset.UtcNow.AddDays(-7)));
        var state = await StartAsync(factory, client, "agent@example.com");

        var callback = await GetWithCookieAsync(client, $"/zendesk/callback?code=the-code&state={state}", state);

        callback.StatusCode.Should().Be(HttpStatusCode.OK);
        (await factory.Store.GetAsync(ObjectId))!.AccessToken.Should().Be("issued-access");
        factory.ZendeskRequests.Should().ContainSingle(r => r.Path == "/oauth/tokens" && r.Body!.Contains("\"refresh_token\":\"old-refresh\""));
        factory.ZendeskRequests.Should().ContainSingle(r => r.Method == HttpMethod.Delete)
            .Which.Headers["Authorization"].Should().Be("Bearer renewed-old", "the renewed old grant is the one to kill, never the new one");
    }

    [Fact]
    public async Task AReconnect_OverAnUnreadableGrant_StoresTheNewOne_AndRevokesWhatCanBeRead()
    {
        var unreadable = new UnreadableTokenStore(new ZendeskStoredTokenUnreadableException(
            new ZendeskUserToken("readable-old", null, null, null, 0, string.Empty, string.Empty, DateTimeOffset.MinValue)));
        using var factory = new Factory(zendeskEmail: "agent@example.com", store: unreadable);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var state = await StartAsync(factory, client, "agent@example.com");

        var callback = await GetWithCookieAsync(client, $"/zendesk/callback?code=the-code&state={state}", state);

        callback.StatusCode.Should().Be(HttpStatusCode.OK, "a damaged old grant must not stop the person reconnecting");
        (await unreadable.GetAsync(ObjectId))!.AccessToken.Should().Be("issued-access");
        factory.ZendeskRequests.Should().ContainSingle(r => r.Method == HttpMethod.Delete)
            .Which.Headers["Authorization"].Should().Be("Bearer readable-old");
    }

    [Fact]
    public async Task ADifferentZendeskLogin_IsRefused_RevokedAndNotStored()
    {
        using var factory = new Factory(zendeskEmail: "someone.else@example.com");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var state = await StartAsync(factory, client, "agent@example.com");

        var callback = await GetWithCookieAsync(client, $"/zendesk/callback?code=c&state={state}", state);

        callback.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await factory.Store.GetAsync(ObjectId)).Should().BeNull();
        factory.ZendeskRequests.Should().Contain(r => r.Method == HttpMethod.Delete && r.Path == "/api/v2/oauth/tokens/current.json",
            "a token for the wrong person must not outlive the refusal");
    }

    [Fact]
    public async Task AnEndUserLogin_IsRefused()
    {
        using var factory = new Factory(zendeskEmail: "agent@example.com", role: "end-user");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var state = await StartAsync(factory, client, "agent@example.com");

        var callback = await GetWithCookieAsync(client, $"/zendesk/callback?code=c&state={state}", state);

        callback.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await factory.Store.GetAsync(ObjectId)).Should().BeNull();
    }

    [Fact]
    public async Task ACallbackWithoutTheBrowsersStateCookie_IsRefused()
    {
        using var factory = new Factory(zendeskEmail: "agent@example.com");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var state = await StartAsync(factory, client, "agent@example.com");

        var callback = await client.GetAsync($"/zendesk/callback?code=c&state={state}");

        callback.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a code completed in another browser is login CSRF");
        factory.ZendeskRequests.Should().NotContain(r => r.Path == "/oauth/tokens", "the code is never exchanged");
        (await factory.Store.GetAsync(ObjectId)).Should().BeNull();
    }

    [Fact]
    public async Task AnUnknownLink_IsRefusedWithoutRedirecting()
    {
        using var factory = new Factory(zendeskEmail: "agent@example.com");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var connect = await client.GetAsync("/zendesk/connect?ticket=not-a-real-ticket");

        connect.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await connect.Content.ReadAsStringAsync()).Should().Contain("expired");
    }

    [Fact]
    public async Task ALink_WorksOnlyOnce()
    {
        using var factory = new Factory(zendeskEmail: "agent@example.com");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var link = await factory.IssueLinkAsync(ObjectId, "agent@example.com");
        var first = await client.GetAsync(link);
        var state = QueryValue(first.Headers.Location!.ToString(), "state");
        (await GetWithCookieAsync(client, $"/zendesk/callback?code=c&state={state}", state)).StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await client.GetAsync(link);

        again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ASecondAttemptFromASpentLink_IsRefusedAndRevoked()
    {
        // One link opened in two browsers starts two attempts. Once the first stores a grant and
        // spends the link, the second must not store another (#13).
        using var factory = new Factory(zendeskEmail: "agent@example.com");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var link = await factory.IssueLinkAsync(ObjectId, "agent@example.com");
        var firstState = QueryValue((await client.GetAsync(link)).Headers.Location!.ToString(), "state");
        var secondState = QueryValue((await client.GetAsync(link)).Headers.Location!.ToString(), "state");
        (await GetWithCookieAsync(client, $"/zendesk/callback?code=c1&state={firstState}", firstState))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await GetWithCookieAsync(client, $"/zendesk/callback?code=c2&state={secondState}", secondState);

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await second.Content.ReadAsStringAsync()).Should().Contain("already been used");
        (await factory.Store.GetAsync(ObjectId))!.AccessToken.Should().Be("issued-access", "the first connection stands");
        factory.Revocations.Should().Equal(["issued-access-2"], "the refused grant is revoked, and the stored one is not");
    }

    [Fact]
    public async Task ASecondAttemptFromASpentLink_DoesNotReconnectAfterADisconnect()
    {
        using var factory = new Factory(zendeskEmail: "agent@example.com");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var link = await factory.IssueLinkAsync(ObjectId, "agent@example.com");
        var firstState = QueryValue((await client.GetAsync(link)).Headers.Location!.ToString(), "state");
        var secondState = QueryValue((await client.GetAsync(link)).Headers.Location!.ToString(), "state");
        (await GetWithCookieAsync(client, $"/zendesk/callback?code=c1&state={firstState}", firstState))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await factory.Store.RemoveAsync(ObjectId);

        var second = await GetWithCookieAsync(client, $"/zendesk/callback?code=c2&state={secondState}", secondState);

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await factory.Store.GetAsync(ObjectId)).Should().BeNull("disconnecting must stay disconnected");
        factory.Revocations.Should().Equal(["issued-access-2"]);
    }

    [Fact]
    public async Task ABrowserThatGivesUpAfterTheStore_StillSpendsTheLink()
    {
        // Once the grant is stored, the link must be spent too, whatever the browser does: an unspent
        // link would let a second attempt reconnect someone who has since disconnected (#13).
        using var factory = new Factory(zendeskEmail: "agent@example.com");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var link = await factory.IssueLinkAsync(ObjectId, "agent@example.com");
        var firstState = QueryValue((await client.GetAsync(link)).Headers.Location!.ToString(), "state");
        var secondState = QueryValue((await client.GetAsync(link)).Headers.Location!.ToString(), "state");
        using var browser = new CancellationTokenSource();
        factory.AfterSave = browser.Cancel;

        await FluentActions.Awaiting(() => GetWithCookieAsync(client, $"/zendesk/callback?code=c1&state={firstState}", firstState, browser.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        factory.AfterSave = null;
        (await factory.Store.GetAsync(ObjectId))!.AccessToken.Should().Be("issued-access", "the store finished before the browser gave up");
        factory.Revocations.Should().NotContain("issued-access", "a grant that was stored and its link spent is kept, not revoked as abandoned");
        await factory.Store.RemoveAsync(ObjectId);

        var second = await GetWithCookieAsync(client, $"/zendesk/callback?code=c2&state={secondState}", secondState);

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the first callback spent the link before the cancellation could stop it");
        (await factory.Store.GetAsync(ObjectId)).Should().BeNull("a disconnect must stay a disconnect");
    }

    [Fact]
    public async Task AStateUsedOnce_CannotCompleteASecondTime()
    {
        using var factory = new Factory(zendeskEmail: "agent@example.com");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var state = await StartAsync(factory, client, "agent@example.com");
        (await GetWithCookieAsync(client, $"/zendesk/callback?code=c&state={state}", state)).StatusCode.Should().Be(HttpStatusCode.OK);

        var replay = await GetWithCookieAsync(client, $"/zendesk/callback?code=c&state={state}", state);

        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheResultPage_EncodesWhatZendeskReturned()
    {
        using var factory = new Factory(zendeskEmail: "agent@example.com", name: "<script>alert(1)</script>");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var state = await StartAsync(factory, client, "agent@example.com");

        var callback = await GetWithCookieAsync(client, $"/zendesk/callback?code=c&state={state}", state);

        var html = await callback.Content.ReadAsStringAsync();
        html.Should().NotContain("<script>alert(1)</script>").And.Contain("&lt;script&gt;");
    }

    [Theory]
    [InlineData("agent", "agent@example.com", "AGENT@example.com", true, true)]
    [InlineData("admin", "agent@example.com", "agent@example.com", true, true)]
    [InlineData("agent", "agent@example.com", "other@example.com", true, false)]
    [InlineData("agent", "agent@example.com", null, true, false)]
    [InlineData("end-user", "agent@example.com", "agent@example.com", true, false)]
    [InlineData("agent", "agent@example.com", "other@example.com", false, true)]
    public void Refusal_FollowsRoleAndEmailMatch(string role, string zendeskEmail, string? signInName, bool requireMatch, bool allowed)
    {
        var refusal = ZendeskConnectEndpoints.Refusal(new ZendeskIdentity(1, "A", zendeskEmail, role), signInName, requireMatch);

        (refusal is null).Should().Be(allowed);
    }

    // Polls for up to five seconds: the server finishes its cleanup after the client has given up.
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<string> StartAsync(Factory factory, HttpClient client, string signInName)
    {
        var link = await factory.IssueLinkAsync(ObjectId, signInName);
        var connect = await client.GetAsync(link);
        connect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return QueryValue(connect.Headers.Location!.ToString(), "state");
    }

    private static async Task<HttpResponseMessage> GetWithCookieAsync(HttpClient client, string url, string state, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"zendesk_connect_state={state}");
        return await client.SendAsync(request, cancellationToken);
    }

    private static string QueryValue(string url, string name) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(url).Query)[name].ToString();

    private static string? CookieValue(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith("zendesk_connect_state=", StringComparison.Ordinal))?
                .Split('=', 2)[1]
            : null;

    private sealed class Factory(string zendeskEmail, string role = "agent", string name = "Agent Example", IZendeskTokenStore? store = null) : WebApplicationFactory<Program>
    {
        private readonly List<RecordedRequest> _requests = [];
        private int _exchanges;

        private string ZendeskEmail { get; } = zendeskEmail;
        private string Role { get; } = role;
        private string Name { get; } = name;

        public IReadOnlyList<RecordedRequest> ZendeskRequests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToList();
                }
            }
        }

        public IZendeskTokenStore Store => Services.GetRequiredService<IZendeskTokenStore>();

        /// <summary>Runs after each store save, before the save returns, as if the browser acted then.</summary>
        public Action? AfterSave { get; set; }

        /// <summary>The access tokens revoked at Zendesk, in order.</summary>
        public IReadOnlyList<string> Revocations => ZendeskRequests
            .Where(r => r.Method == HttpMethod.Delete && r.Path == "/api/v2/oauth/tokens/current.json")
            .Select(r => r.Headers["Authorization"]["Bearer ".Length..])
            .ToList();

        public Task<string> IssueLinkAsync(string objectId, string signInName) =>
            Services.GetRequiredService<ZendeskConnectLinks>().CreateAsync(objectId, signInName, "http://localhost");

        /// <summary>A path the stub never answers: the request ends only when it is cancelled or times out.</summary>
        public string? StallPath { get; init; }

        /// <summary>A path the stub answers only after a delay, giving up if the request is cancelled first.</summary>
        public (string Path, TimeSpan Delay)? SlowPath { get; init; }

        /// <summary>Overrides the OAuth client's 15 s timeout, so a test can make a stalled call time out.</summary>
        public TimeSpan? OAuthTimeout { get; init; }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>(TestHelpers.ZendeskConfiguration)
                {
                    ["OAuth:NoAuth"] = "true",
                });
            });
            builder.ConfigureServices(services =>
            {
                // A store given by the test wins; otherwise the in-memory one, with an AfterSave hook.
                services.AddSingleton(store ?? new NotifyingStore(this));
                var oauthClient = services.AddHttpClient(ZendeskOAuthClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new StubZendesk(this));
                if (OAuthTimeout is { } timeout)
                {
                    oauthClient.ConfigureHttpClient(c => c.Timeout = timeout);
                }
            });
            return base.CreateHost(builder);
        }

        // The in-memory store, with a hook after each save that honours the caller's token afterwards,
        // as a real store's later awaits would: a cancelled token there throws.
        private sealed class NotifyingStore(Factory owner) : IZendeskTokenStore
        {
            private readonly InMemoryZendeskTokenStore _inner = new();

            public Task<ZendeskUserToken?> GetAsync(string objectId, CancellationToken cancellationToken = default) =>
                _inner.GetAsync(objectId, cancellationToken);

            public async Task SaveAsync(string objectId, ZendeskUserToken token, CancellationToken cancellationToken = default)
            {
                await _inner.SaveAsync(objectId, token, cancellationToken);
                owner.AfterSave?.Invoke();
                await Task.Delay(50, CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
            }

            public Task RemoveAsync(string objectId, CancellationToken cancellationToken = default) =>
                _inner.RemoveAsync(objectId, cancellationToken);
        }

        private sealed class StubZendesk(Factory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                // A request whose token is already cancelled never reaches Zendesk, so it is not
                // recorded: Revocations must mean revocations that were actually sent.
                cancellationToken.ThrowIfCancellationRequested();
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var recorded = new RecordedRequest(request.Method, request.RequestUri!, body,
                    request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
                lock (owner._requests)
                {
                    owner._requests.Add(recorded);
                }

                if (recorded.Path == owner.StallPath)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                if (owner.SlowPath is { } slow && recorded.Path == slow.Path)
                {
                    await Task.Delay(slow.Delay, cancellationToken);
                }

                var json = recorded.Path switch
                {
                    "/oauth/tokens" when body!.Contains("\"grant_type\":\"refresh_token\"", StringComparison.Ordinal) =>
                        "{\"access_token\":\"renewed-old\",\"refresh_token\":\"renewed-old-refresh\",\"expires_in\":7200,\"token_type\":\"bearer\"}",
                    // The first exchange issues "issued-access", later ones "issued-access-2" and so on,
                    // so a test can tell which grant was stored and which was revoked.
                    "/oauth/tokens" => Interlocked.Increment(ref owner._exchanges) switch
                    {
                        1 => "{\"access_token\":\"issued-access\",\"refresh_token\":\"issued-refresh\",\"expires_in\":7200,\"token_type\":\"bearer\"}",
                        var n => $"{{\"access_token\":\"issued-access-{n}\",\"refresh_token\":\"issued-refresh-{n}\",\"expires_in\":7200,\"token_type\":\"bearer\"}}",
                    },
                    "/api/v2/users/me.json" => System.Text.Json.JsonSerializer.Serialize(new
                    {
                        user = new { id = 4242, name = owner.Name, email = owner.ZendeskEmail, role = owner.Role },
                    }),
                    _ => "{}",
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
        }
    }
}
