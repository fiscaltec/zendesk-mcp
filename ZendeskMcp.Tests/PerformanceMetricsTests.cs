using System.Net;
using System.Text.Json;
using Azure;
using Azure.Security.KeyVault.Secrets;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ZendeskMcp.Tests;

/// <summary>
/// Phase 6 of vitally-mcp#94: the counters and the upstream-call duration. The rate-limit and the Graph / OIDC
/// cache counters are tested beside their components; these are the ones without a natural home.
/// </summary>
/// <remarks>
/// The earlier "slow requests" diagnosis measured whole-request times (about 5 s per call, against about
/// 70 s of model time per tool turn). What it could not see was an unsampled breakdown of a slow call
/// into its upstream calls, which the duration here provides.
/// </remarks>
public class PerformanceMetricsTests
{
    [Fact]
    public async Task TokenStore_CountsAMissOnTheVaultFetch_AndAHitOnTheCachedRead()
    {
        using var capture = new MetricCapture();
        var secrets = new Mock<SecretClient>();
        secrets.Setup(s => s.GetSecretAsync($"zendesk-user-{TestHelpers.TestObjectId}", null, It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(
                SecretModelFactory.KeyVaultSecret(
                    new SecretProperties($"zendesk-user-{TestHelpers.TestObjectId}"),
                    value: JsonSerializer.Serialize(TestHelpers.SampleToken())),
                Mock.Of<Response>()));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new KeyVaultZendeskTokenStore(
            secrets.Object,
            cache,
            Options.Create(new ZendeskServerOptions()),
            NullLogger<KeyVaultZendeskTokenStore>.Instance,
            capture.Metrics);

        // Three calls, not two: with two, a swap of hit and miss still yields one of each and passes.
        await store.GetAsync(TestHelpers.TestObjectId);
        await store.GetAsync(TestHelpers.TestObjectId);
        await store.GetAsync(TestHelpers.TestObjectId);

        capture.Total("zendesk.cache.lookups", ("cache", "user_token"), ("result", "miss")).Should().Be(1);
        capture.Total("zendesk.cache.lookups", ("cache", "user_token"), ("result", "hit")).Should().Be(2);
    }

    [Fact]
    public async Task TokenStore_CountsNothing_OnTheDevelopmentTokenPath()
    {
        // The development API token never consults the token store, so counting a lookup would
        // inflate the rate with lookups that never happened.
        using var capture = new MetricCapture();
        var secrets = new Mock<SecretClient>(MockBehavior.Strict);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new KeyVaultZendeskTokenStore(
            secrets.Object,
            cache,
            Options.Create(new ZendeskServerOptions()),
            NullLogger<KeyVaultZendeskTokenStore>.Instance,
            capture.Metrics);
        using var client = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleTicketsJson());
        var service = TestHelpers.BuildZendeskService(client, tokenStore: store);

        await service.GetAsync("/api/v2/tickets");

        capture.Total("zendesk.cache.lookups").Should().Be(0);
    }

    [Fact]
    public async Task Pager_CountsATruncation_TaggedWithTheResourceType()
    {
        // `truncated: true` means the pager stopped at MaxAutoPageFetches and the matching total is
        // unknowable. The rate of it says whether the cap is set where users hit it.
        using var capture = new MetricCapture();
        var page = """{"tickets":[{"id":101,"subject":"Login fails"}],"meta":{"has_more":true,"after_cursor":"more"}}""";
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(page, page, page);
        using var _client = client;
        var service = TestHelpers.BuildZendeskService(client, maxAutoPageFetches: 2, metrics: capture.Metrics);

        await service.GetAllPagesAsync("/api/v2/tickets", null, "tickets", maxItems: 1000);

        capture.Total("zendesk.autopager.truncations", ("resource", "/api/v2/tickets")).Should().Be(1);
    }

    [Fact]
    public async Task Pager_TagsTheFixedResourceKind_NeverTheCallersIdInThePath()
    {
        // An organisation's tickets are paged from `organizations/{organizationId}/tickets`, so the
        // path the pager receives carries the caller's id. A metric tag stores every distinct value it
        // is given — that would be a cardinality explosion and customer text in a metrics store — so
        // the tag is the fixed first segment only, checked against an allowlist.
        using var capture = new MetricCapture();
        var page = """{"tickets":[{"id":101,"created_at":"2026-09-01T00:00:00Z"}],"meta":{"has_more":true,"after_cursor":"more"}}""";
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(page, page, page);
        using var _client = client;
        var service = TestHelpers.BuildZendeskService(client, maxAutoPageFetches: 2, metrics: capture.Metrics);

        await service.GetAllPagesAsync("/api/v2/organizations/orgsentinel/tickets", null, "tickets", maxItems: 1000);

        // The first line is the real check: it fails if the tag is the raw path. The total afterwards
        // catches any OTHER tag value carrying the id, which the allowlist should make impossible.
        capture.Total("zendesk.autopager.truncations", ("resource", "/api/v2/organizations/…")).Should().Be(1);
        capture.Total("zendesk.autopager.truncations").Should().Be(1,
            "exactly one truncation, tagged with the fixed resource kind and nothing else");
    }

    [Fact]
    public async Task Pager_CountsNoTruncation_WhenTheEndpointIsExhausted()
    {
        using var capture = new MetricCapture();
        using var client = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleTicketsJson());
        var service = TestHelpers.BuildZendeskService(client, metrics: capture.Metrics);

        await service.GetAllPagesAsync("/api/v2/tickets", null, "tickets", maxItems: 1000);

        capture.Total("zendesk.autopager.truncations").Should().Be(0);
    }

    [Fact]
    public async Task UpstreamAuditRecord_CarriesItsDuration_AsANamedProperty()
    {
        // The tool-call record has carried a duration since vitally-mcp#147; the upstream record did not, so a
        // slow composite tool (a ticket conversation, an SLA summary) could not say which of its
        // calls was slow. A NAMED
        // property, so it becomes a queryable AppEvents dimension rather than text to parse.
        var logger = new StateCapturingLogger<AuditLogger>();
        var audit = new AuditLogger(
            Options.Create(new AuditOptions { Enabled = true, IncludeReads = true }), logger);
        using var client = new HttpClient(new DelayingHandler(TimeSpan.FromMilliseconds(60)));
        var service = TestHelpers.BuildZendeskService(client, audit: audit);

        await service.GetAsync("/api/v2/organizations");

        var state = logger.States.Should().ContainSingle().Subject;
        var duration = state.Should().ContainSingle(kv => kv.Key == "AuditDurationMs").Subject.Value;
        // A real measurement, not merely a long: the unmeasured sentinel -1 is a long too, so asserting
        // the type alone passed with the duration never passed to LogAction at all.
        duration.Should().BeOfType<long>().Which.Should().BeGreaterThanOrEqualTo(50,
            "the upstream answered after 60 ms, and the record must say so");
    }

    /// <summary>Answers every request after a fixed delay, so a duration has something to measure.</summary>
    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"organizations":[]}""") };
        }
    }
}
