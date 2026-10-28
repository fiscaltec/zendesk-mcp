using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ZendeskMcp.Tests;

/// <summary>
/// One-shot upload URLs: the slot is the only credential a curl upload carries, so it must work
/// once, for exactly the declared size, and re-check the caller's write tier when it is used.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ZendeskUploadsTests
{
    [Fact]
    public async Task AnUpload_IsPassedToZendesk_AndAnswersWithTheUploadToken()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        var slot = await factory.IssueSlotAsync("invoice.pdf", 5, inline: false);

        var response = await client.PutAsync($"/uploads/{slot}", new ByteArrayContent("hello"u8.ToArray()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"upload_token\":\"tok-1\"");
        var upload = factory.ZendeskRequests.Single();
        upload.Method.Should().Be(HttpMethod.Post);
        upload.Path.Should().Be("/api/v2/uploads");
        upload.Query("filename").Should().Be("invoice.pdf");
        upload.Query("inline").Should().BeNull();
        upload.Body.Should().Be("hello");
    }

    [Fact]
    public async Task AnInlineImage_IsMarkedInline()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        var slot = await factory.IssueSlotAsync("shot.png", 3, inline: true);

        await client.PutAsync($"/uploads/{slot}", new ByteArrayContent([1, 2, 3]));

        factory.ZendeskRequests.Single().Query("inline").Should().Be("true");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public async Task TheWrongNumberOfBytes_IsRefused_AndNothingReachesZendesk(int sent)
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        var slot = await factory.IssueSlotAsync("invoice.pdf", 5, inline: false);

        var response = await client.PutAsync($"/uploads/{slot}", new ByteArrayContent(new byte[sent]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        factory.ZendeskRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ASlot_WorksOnce()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        var slot = await factory.IssueSlotAsync("invoice.pdf", 5, inline: false);
        (await client.PutAsync($"/uploads/{slot}", new ByteArrayContent(new byte[5]))).StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await client.PutAsync($"/uploads/{slot}", new ByteArrayContent(new byte[5]));

        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.ZendeskRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task AnUnknownSlot_IsRefused()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var response = await client.PutAsync("/uploads/guessed", new ByteArrayContent(new byte[1]));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OnePerson_CanHoldOnlyAFewOpenSlots()
    {
        var slots = new ZendeskUploadSlots(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        for (var i = 0; i < ZendeskUploadSlots.MaxOpenSlotsPerPerson; i++)
        {
            await slots.CreateAsync(TestHelpers.UserPrincipal(), "a.txt", 1, false, null);
        }

        var act = () => slots.CreateAsync(TestHelpers.UserPrincipal(), "a.txt", 1, false, null);

        await act.Should().ThrowAsync<ArgumentException>("each open slot can pin memory while a slow upload streams in");
        await slots.CreateAsync(TestHelpers.UserPrincipal(objectId: "99999999-2222-3333-4444-555555555555"), "a.txt", 1, false, null);
    }

    [Fact]
    public async Task TheSlotCarriesIdentityAndPermissionClaims_AndNothingElse()
    {
        var slots = new ZendeskUploadSlots(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        var caller = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("oid", TestHelpers.TestObjectId),
            new Claim("permissions", "zendesk:read"),
            new Claim("scope", "mcp.access"),
            new Claim("https://zendesk-mcp.example.com/permissions", "zendesk:write"),
            new Claim("name", "Not Carried"),
        ], "Test"));

        var slot = await slots.CreateAsync(caller, "a.txt", 1, inline: false, extraClaimType: "https://zendesk-mcp.example.com/permissions");
        var principal = (await slots.TakeAsync(slot))!.ToPrincipal();

        principal.Identity!.IsAuthenticated.Should().BeTrue();
        principal.Claims.Select(c => c.Type).Should().BeEquivalentTo(
            ["oid", "permissions", "scope", "https://zendesk-mcp.example.com/permissions"]);
    }

    [Fact]
    public async Task AReaderHoldingASlot_StillCannotUpload()
    {
        // The write tier is re-checked when the slot is spent. Here the slot's principal holds only
        // the read tier, so the Zendesk upload — a POST — is refused by the service's backstop.
        var slots = new ZendeskUploadSlots(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        var slot = await slots.CreateAsync(TestHelpers.UserPrincipal(permissions: "zendesk:read"), "a.txt", 1, false, null);
        var principal = (await slots.TakeAsync(slot))!.ToPrincipal();
        var authorizer = new ToolAuthorizer(
            Options.Create(new ToolAuthorizationOptions { Enabled = true }),
            Options.Create(new OAuthOptions { NoAuth = false }),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } });

        var act = () => authorizer.EnsureAuthorizedAsync(HttpMethod.Post);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly List<RecordedRequest> _requests = [];

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

        public Task<string> IssueSlotAsync(string fileName, long size, bool inline) =>
            Services.GetRequiredService<ZendeskUploadSlots>()
                .CreateAsync(TestHelpers.UserPrincipal(), fileName, size, inline, extraClaimType: null);

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>(TestHelpers.NoAuthZendeskConfiguration)
                {
                    ["OAuth:NoAuth"] = "true",
                });
            });
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<ZendeskService>().ConfigurePrimaryHttpMessageHandler(() => new StubZendesk(this));
            });
            return base.CreateHost(builder);
        }

        private sealed class StubZendesk(Factory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                lock (owner._requests)
                {
                    owner._requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body,
                        new Dictionary<string, string>()));
                }

                const string json = "{\"upload\":{\"token\":\"tok-1\",\"attachment\":{\"id\":9,\"file_name\":\"f\",\"content_type\":\"application/pdf\",\"size\":5}}}";
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
        }
    }
}
