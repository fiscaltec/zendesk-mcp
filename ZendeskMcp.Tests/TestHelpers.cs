using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace ZendeskMcp.Tests;

/// <summary>
/// Helper utilities for creating mocks and test data.
/// </summary>
/// <remarks>
/// The <c>CA2000</c> suppressions below cover the mock <c>HttpResponseMessage</c> instances, which
/// are handed to the <c>HttpClient</c> under test via Moq and so must outlive the setup call.
/// CodeQL raises its own equivalent (<c>cs/local-not-disposed</c>) and that one <b>cannot</b> be
/// suppressed here: <c>SuppressMessage</c> is a Roslyn mechanism and CodeQL never reads it, so an
/// attribute naming a <c>cs/*</c> rule id suppresses nothing. Dismiss those on the alert itself in
/// the repository's Security tab instead of adding an attribute that only looks like it works.
/// </remarks>
public static class TestHelpers
{
    /// <summary>The Zendesk subdomain every test service is configured with.</summary>
    public const string Subdomain = "acme";

    /// <summary>The origin <see cref="Subdomain"/> resolves to.</summary>
    public const string BaseUrl = "https://acme.zendesk.com";

    /// <summary>The Entra object id of the default test caller.</summary>
    public const string TestObjectId = "11111111-2222-3333-4444-555555555555";

    /// <summary>
    /// Configuration for integration hosts (WebApplicationFactory): per-user OAuth with an in-memory
    /// token store and a development client secret, so no Key Vault is needed and authentication can
    /// stay on. Use <see cref="NoAuthZendeskConfiguration"/> for a host running OAuth:NoAuth.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> ZendeskConfiguration = new Dictionary<string, string?>
    {
        ["Zendesk:Subdomain"] = Subdomain,
        ["Zendesk:OAuthClientId"] = "zendesk-test-client",
        ["Zendesk:DevelopmentOAuthClientSecret"] = "zendesk-test-secret",
    };

    /// <summary>
    /// Configuration for an OAuth:NoAuth integration host: the development API token, which the
    /// startup guard only accepts without authentication.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> NoAuthZendeskConfiguration = new Dictionary<string, string?>
    {
        ["Zendesk:Subdomain"] = Subdomain,
        ["Zendesk:DevelopmentEmail"] = "dev@example.com",
        ["Zendesk:DevelopmentApiToken"] = "zd_test_dummy",
    };

    /// <summary>
    /// Creates a mock HttpClient that returns the specified JSON response.
    /// The HttpResponseMessage and HttpClient created here are owned by the test method
    /// that calls this helper; lifetime is bounded by the test method's duration.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "Test mock — HttpResponseMessage is returned via Moq to the consuming HttpClient; lifetime is bounded by the test run.")]
    public static HttpClient CreateMockHttpClient(string jsonResponse, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(jsonResponse)
            });

        return new HttpClient(mockHttpMessageHandler.Object);
    }

    /// <summary>
    /// Mock HttpClient that returns the supplied bodies in order, one per successive request.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "Test mock — see CreateMockHttpClient.")]
    public static (HttpClient client, Mock<HttpMessageHandler> handler) CreateMockHttpClientPaged(params string[] pages)
    {
        var mock = new Mock<HttpMessageHandler>();
        var seq = mock.Protected().SetupSequence<Task<HttpResponseMessage>>(
            "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
        foreach (var page in pages)
        {
            seq = seq.ReturnsAsync(new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent(page) });
        }
        return (new HttpClient(mock.Object), mock);
    }

    /// <summary>
    /// Creates a mock HttpClient that allows verification of the request URL.
    /// The HttpResponseMessage and HttpClient are owned by the test method.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "Test mock — see CreateMockHttpClient.")]
    public static (HttpClient client, Mock<HttpMessageHandler> handler) CreateMockHttpClientWithHandler(
        string jsonResponse,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(jsonResponse)
            });

        var client = new HttpClient(mockHttpMessageHandler.Object);
        return (client, mockHttpMessageHandler);
    }

    /// <summary>
    /// A fake Zendesk: answers each request from <paramref name="route"/> and records every request
    /// (method, absolute URL, body, headers) for assertions. The usual way to test a tool — route by
    /// <see cref="RecordedRequest.Path"/>, return a canned body, then assert on what was sent.
    /// </summary>
    public static FakeZendesk CreateFakeZendesk(Func<RecordedRequest, FakeResponse> route) => new(route);

    /// <summary>
    /// Builds a ZendeskService for tests. Defaults to the development API token, which needs no signed-in
    /// caller and no token store, so tests about URLs and result shapes stay free of identity set-up.
    /// Pass <paramref name="user"/> (or use <see cref="BuildUserZendeskService"/>) to exercise per-user OAuth.
    /// </summary>
    public static ZendeskService BuildZendeskService(
        HttpClient httpClient,
        ToolAuthorizer? authorizer = null,
        AuditLogger? audit = null,
        ToolCallAuditContext? auditContext = null,
        int maxAutoPageFetches = 10,
        Microsoft.Extensions.Logging.ILogger<ZendeskService>? logger = null,
        ZendeskMetrics? metrics = null,
        Action<ZendeskServerOptions>? configure = null,
        IZendeskTokenStore? tokenStore = null,
        ClaimsPrincipal? user = null,
        IMemoryCache? cache = null,
        TimeProvider? timeProvider = null,
        IHttpClientFactory? oauthHttpClientFactory = null)
    {
        var serverOptions = new ZendeskServerOptions
        {
            Subdomain = Subdomain,
            MaxAutoPageFetches = maxAutoPageFetches,
        };

        if (user is null)
        {
            serverOptions.DevelopmentEmail = "dev@example.com";
            serverOptions.DevelopmentApiToken = "zd_test_token";
        }
        else
        {
            serverOptions.OAuthClientId = "zendesk-test-client";
            serverOptions.DevelopmentOAuthClientSecret = "zendesk-test-secret";
        }

        configure?.Invoke(serverOptions);
        serverOptions.Validate();
        var options = Options.Create(serverOptions);

        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal(new ClaimsIdentity()) },
        };
        accessor.HttpContext.Request.Scheme = "https";
        accessor.HttpContext.Request.Host = new HostString("zendesk-mcp.example.com");

        cache ??= new MemoryCache(new MemoryCacheOptions());
        var oauthOptions = Options.Create(new OAuthOptions { NoAuth = user is null, PublicBaseUrl = "https://zendesk-mcp.example.com" });
        var oauthClient = new ZendeskOAuthClient(
            oauthHttpClientFactory ?? new SingleClientFactory(httpClient),
            options, cache, NullLogger<ZendeskOAuthClient>.Instance, secretClient: null, timeProvider);
        var links = new ZendeskConnectLinks(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), options);
        var credentials = new ZendeskCredentialProvider(
            options, oauthOptions, tokenStore ?? new InMemoryZendeskTokenStore(), oauthClient, links,
            NullLogger<ZendeskCredentialProvider>.Instance, accessor, timeProvider);

        // Default to a disabled authorizer so the bulk of the tests (which exercise URL/shape
        // behaviour, not access control) don't need to set up claims. Enforcement is covered
        // explicitly by ToolAuthorizerTests and the dedicated ZendeskService authorisation tests.
        authorizer ??= new ToolAuthorizer(
            Options.Create(new ToolAuthorizationOptions { Enabled = false }),
            Options.Create(new OAuthOptions { NoAuth = true }));
        audit ??= new AuditLogger(
            Options.Create(new AuditOptions { Enabled = false }),
            NullLogger<AuditLogger>.Instance);
        return new ZendeskService(httpClient, options, credentials, authorizer, audit, auditContext, logger, metrics, cache);
    }

    /// <summary>
    /// A ZendeskService acting for a signed-in person (<see cref="TestObjectId"/>) whose Zendesk token is
    /// already stored, for tests of per-user behaviour. Returns the store so a test can inspect it.
    /// </summary>
    public static (ZendeskService Service, InMemoryZendeskTokenStore Store) BuildUserZendeskService(
        HttpClient httpClient,
        ZendeskUserToken? token = null,
        string signInName = "agent@example.com",
        Action<ZendeskServerOptions>? configure = null,
        TimeProvider? timeProvider = null,
        IHttpClientFactory? oauthHttpClientFactory = null)
    {
        var store = new InMemoryZendeskTokenStore();
        if (token is not null)
        {
            store.SaveAsync(TestObjectId, token).GetAwaiter().GetResult();
        }

        var service = BuildZendeskService(httpClient, configure: configure, tokenStore: store,
            user: UserPrincipal(TestObjectId, signInName), timeProvider: timeProvider,
            oauthHttpClientFactory: oauthHttpClientFactory);
        return (service, store);
    }

    /// <summary>A signed-in Entra principal with an object id and a sign-in name.</summary>
    public static ClaimsPrincipal UserPrincipal(string objectId = TestObjectId, string signInName = "agent@example.com", params string[] permissions) =>
        new(new ClaimsIdentity(
            new[] { new Claim("oid", objectId), new Claim("preferred_username", signInName) }
                .Concat(permissions.Select(p => new Claim("permissions", p))),
            authenticationType: "Test"));

    /// <summary>A stored Zendesk grant for tests.</summary>
    public static ZendeskUserToken SampleToken(string accessToken = "zd_access", DateTimeOffset? expiresAt = null, string? refreshToken = "zd_refresh") =>
        new(accessToken, refreshToken, expiresAt, null, 900001, "agent@example.com", "agent", DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Builds a <see cref="ToolAuthorizer"/> with enforcement enabled, backed by an
    /// <see cref="IHttpContextAccessor"/> whose principal carries the supplied permissions.
    /// </summary>
    public static ToolAuthorizer BuildEnabledAuthorizer(params string[] permissions)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    permissions.Select(p => new Claim("permissions", p)),
                    authenticationType: "Test"))
        };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        return new ToolAuthorizer(
            Options.Create(new ToolAuthorizationOptions { Enabled = true }),
            Options.Create(new OAuthOptions { NoAuth = false }),
            accessor);
    }

    /// <summary>
    /// Builds a bare <see cref="ToolAuthorizer"/> with RBAC either on or off and no
    /// <see cref="IHttpContextAccessor"/>. For callers that pass the principal in explicitly (the
    /// authorisation policy handler resolves it from the policy context, not from the ambient HTTP context).
    /// </summary>
    public static ToolAuthorizer BuildToolAuthorizer(bool enabled)
    {
        return new ToolAuthorizer(
            Options.Create(new ToolAuthorizationOptions { Enabled = enabled }),
            Options.Create(new OAuthOptions { NoAuth = false }));
    }

    /// <summary>Sample ticket list in Zendesk's shape, with a sideloaded user.</summary>
    public static string GetSampleTicketsJson() => """
    {
      "tickets": [
        { "id": 101, "subject": "Login fails", "status": "open", "priority": "high", "requester_id": 501, "organization_id": 601, "created_at": "2026-01-02T09:00:00Z" },
        { "id": 102, "subject": "Invoice query", "status": "pending", "priority": "normal", "requester_id": 502, "organization_id": null, "created_at": "2026-01-03T09:00:00Z" }
      ],
      "users": [ { "id": 501, "name": "Alice Example", "email": "alice@example.com" } ],
      "meta": { "has_more": false, "after_cursor": null },
      "links": { "next": null }
    }
    """;

    /// <summary>Sample single ticket in Zendesk's shape.</summary>
    public static string GetSampleTicketJson() => """
    {
      "ticket": { "id": 101, "subject": "Login fails", "description": "Cannot sign in", "status": "open", "priority": "high", "type": "incident", "requester_id": 501, "assignee_id": 701, "organization_id": 601, "tags": ["login"], "created_at": "2026-01-02T09:00:00Z", "updated_at": "2026-01-02T10:00:00Z" }
    }
    """;

    /// <summary>Empty search results in Zendesk's export shape.</summary>
    public static string GetEmptyResultsJson() => """
    {
      "results": [],
      "meta": { "has_more": false, "after_cursor": null }
    }
    """;

    /// <summary>An IHttpClientFactory handing out one client, for the OAuth client in tests.</summary>
    public sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

/// <summary>A request the fake Zendesk received.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Url, string? Body, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>The path without the query, e.g. <c>/api/v2/tickets/101</c>.</summary>
    public string Path => Url.AbsolutePath;

    /// <summary>A decoded query value, or null.</summary>
    public string? Query(string name) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(Url.Query).TryGetValue(name, out var values)
            ? values.ToString()
            : null;
}

/// <summary>What the fake Zendesk answers.</summary>
public sealed record FakeResponse(HttpStatusCode Status, string Body, IReadOnlyDictionary<string, string>? Headers = null, string ContentType = "application/json")
{
    public static FakeResponse Json(string body) => new(HttpStatusCode.OK, body);
    public static FakeResponse WithStatus(HttpStatusCode status, string body = "{}") => new(status, body);
    public static FakeResponse NotFound() => new(HttpStatusCode.NotFound, "{\"error\":\"RecordNotFound\",\"description\":\"Not found\"}");
}

/// <summary>A routed, recording fake of the Zendesk API. Dispose it with the test.</summary>
public sealed class FakeZendesk : IDisposable
{
    private readonly List<RecordedRequest> _requests = [];
    private readonly object _gate = new();

    public FakeZendesk(Func<RecordedRequest, FakeResponse> route)
    {
        Client = new HttpClient(new Handler(this, route));
    }

    public HttpClient Client { get; }

    /// <summary>Every request received, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    public void Dispose() => Client.Dispose();

    private sealed class Handler(FakeZendesk owner, Func<RecordedRequest, FakeResponse> route) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var recorded = new RecordedRequest(request.Method, request.RequestUri!, body, headers);
            lock (owner._gate)
            {
                owner._requests.Add(recorded);
            }

            var answer = route(recorded);
            var response = new HttpResponseMessage(answer.Status)
            {
                Content = new StringContent(answer.Body, Encoding.UTF8, answer.ContentType),
            };
            foreach (var (name, value) in answer.Headers ?? new Dictionary<string, string>())
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        }
    }
}
