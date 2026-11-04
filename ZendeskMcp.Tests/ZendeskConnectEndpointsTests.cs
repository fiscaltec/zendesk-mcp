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

    private static async Task<string> StartAsync(Factory factory, HttpClient client, string signInName)
    {
        var link = await factory.IssueLinkAsync(ObjectId, signInName);
        var connect = await client.GetAsync(link);
        connect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        return QueryValue(connect.Headers.Location!.ToString(), "state");
    }

    private static Task<HttpResponseMessage> GetWithCookieAsync(HttpClient client, string url, string state)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"zendesk_connect_state={state}");
        return client.SendAsync(request);
    }

    private static string QueryValue(string url, string name) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(url).Query)[name].ToString();

    private static string? CookieValue(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith("zendesk_connect_state=", StringComparison.Ordinal))?
                .Split('=', 2)[1]
            : null;

    private sealed class Factory(string zendeskEmail, string role = "agent", string name = "Agent Example") : WebApplicationFactory<Program>
    {
        private readonly List<RecordedRequest> _requests = [];

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

        public Task<string> IssueLinkAsync(string objectId, string signInName) =>
            Services.GetRequiredService<ZendeskConnectLinks>().CreateAsync(objectId, signInName, "http://localhost");

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
                services.AddHttpClient(ZendeskOAuthClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new StubZendesk(this));
            });
            return base.CreateHost(builder);
        }

        private sealed class StubZendesk(Factory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                var recorded = new RecordedRequest(request.Method, request.RequestUri!, body,
                    request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
                lock (owner._requests)
                {
                    owner._requests.Add(recorded);
                }

                var json = recorded.Path switch
                {
                    "/oauth/tokens" => "{\"access_token\":\"issued-access\",\"refresh_token\":\"issued-refresh\",\"expires_in\":7200,\"token_type\":\"bearer\"}",
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
