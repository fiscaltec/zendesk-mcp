# ZendeskMcp.Tests

Automated test suite for the Zendesk MCP server: xUnit v3 (on Microsoft.Testing.Platform) +
FluentAssertions + Moq + the ASP.NET Core test host, fully in-process — no Zendesk tenant, no
identity provider, no Key Vault.

```powershell
dotnet test ZendeskMcp.sln -c Debug                                  # the summary line carries the total
dotnet test ZendeskMcp.sln -c Debug --filter-class "*TicketsToolsTests"
```

No total is written here: a count in prose is only right on the day it is written.

> **Integration tests that set environment variables must join `IntegrationTestCollection`.**
> `Program.cs` reads `OAuth:NoAuth`, `Authorization:ReadOnly` and the Zendesk Key Vault and
> development-token settings at composition time — before `WebApplicationFactory` can inject
> configuration — so environment variables are the only override that works, and they are
> process-wide. `ZendeskHostEnvironment` sets and clears the `Zendesk__*` ones; set every variable
> your fixture depends on explicitly.

## Where things are tested

| Area | Classes |
|---|---|
| Per-user Zendesk OAuth | `ZendeskCredentialProviderTests`, `ZendeskConnectEndpointsTests`, `ZendeskTokenStoreTests`, `ConnectionToolsTests` |
| Transport | `ZendeskServiceTests`, `ZendeskServiceAuthorizationTests`, `ZendeskServiceAuditTests`, `AuditRecordIdsTests`, `ZendeskRateLimitHandlerTests`, `TicketSearchQueryTests` |
| Uploads | `ZendeskUploadsTests` |
| Tools | `Tools/*Tests` — one or more classes per tool area |
| Tool conventions | `ToolAnnotationCoverageTests`, `ToolAuthorizePolicyCoverageTests` (via `ToolCatalogue`) |
| Ported from vitally-mcp | OAuth proxy and discovery, RBAC and the Graph resolver, stale entitlement, discovery filtering and read-only, audit composition, failure logging, logging filters, telemetry redaction, metrics, startup guards |

`ProbeTools` are test-only tools covering every tier and naming shape, registered with the SDK's own
`WithTools` so the composition tests (audit, discovery filtering, read-only) do not depend on any one
production tool.

## Patterns

### A tool against a fake Zendesk

```csharp
using var zendesk = TestHelpers.CreateFakeZendesk(request => request.Path switch
{
    "/api/v2/tickets/101" => FakeResponse.Json(TestHelpers.GetSampleTicketJson()),
    _ => FakeResponse.NotFound(),
});
var service = TestHelpers.BuildZendeskService(zendesk.Client, configure: o => o.Features.AllowPublicReplies = true);

var result = await TicketsTools.GetTicket(service, ticket_id: 101);

zendesk.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Get && r.Path == "/api/v2/tickets/101");
```

`BuildZendeskService` uses the development API token, so no caller identity is needed.
`BuildUserZendeskService` acts for a signed-in person with a stored Zendesk token, for per-user
behaviour. Both use `TestHelpers.FieldHttpContextAccessor`, not the framework's `HttpContextAccessor`,
whose single process-wide slot would let one helper overwrite another's caller.

### Integration tests against the real composition root

Derive from `WebApplicationFactory<Program>`, override `CreateHost` with in-memory configuration
(`TestHelpers.ZendeskConfiguration` for a host with sign-in, `NoAuthZendeskConfiguration` with
`OAuth:NoAuth=true`), and replace outbound handlers in `ConfigureServices` —
`AddHttpClient<ZendeskService>().ConfigurePrimaryHttpMessageHandler(...)` for Zendesk API calls,
`AddHttpClient(ZendeskOAuthClient.HttpClientName)...` for the Zendesk OAuth endpoints.
`ZendeskConnectEndpointsTests` and `ZendeskUploadsTests` show both.

If the factory sets `OAuth:SharedClientId` it **must** call `services.UseStubDiscovery()`: startup
resolves the upstream OIDC discovery document and refuses to boot without it.

## Adding tests

1. For a new tool, add cases to the area's `Tools/*Tests.cs`: the request built (verb, path, query,
   body), the result shaped, validation messages, feature-switch refusals, and any credential boundary
   (`brand_url`, downloads).
2. For a new response shape, check `AuditRecordIdsTests` would find its ids.
3. Never relax a convention test to fit a tool; fix the tool, or add a documented exception to
   `ToolCatalogue` as `Disconnect_zendesk` has.

## Out of scope (intentional)

- Live Zendesk API calls — covered by the staging validation in `../CLAUDE.md`.
- MCP protocol-level behaviour — the `ModelContextProtocol` SDK's own coverage.
- End-to-end runs in Claude Desktop / Claude Code — manual.
