using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZendeskMcp;

namespace ZendeskMcp.Tests;

public class ToolAuthorizerTests
{
    private const string AdminGroup = "70b48a20-d4b1-47dc-a132-21bc99272a86";
    private const string ReaderGroup = "71451cc9-f5df-44ee-8ed1-3acc41a911eb";
    private const string CallerOid = "675ebdda-7590-4d79-8ec3-a2d17ab029ba";

    private sealed class StubResolver(IReadOnlySet<string>? result, bool servedStale = false) : IGroupPermissionResolver
    {
        public string? LastObjectId { get; private set; }
        public Task<ResolvedPermissions?> TryResolvePermissionsAsync(string userObjectId, CancellationToken cancellationToken = default)
        {
            LastObjectId = userObjectId;
            return Task.FromResult(result is null
                ? null
                : servedStale
                    ? ResolvedPermissions.Retained(result, TimeSpan.FromSeconds(90))
                    : ResolvedPermissions.Confirmed(result, TimeSpan.Zero));
        }
    }

    private static ToolAuthorizer Build(
        bool enabled = true,
        bool noAuth = false,
        ClaimsPrincipal? user = null,
        ToolAuthorizationOptions? options = null,
        IGroupPermissionResolver? resolver = null,
        ILogger<ToolAuthorizer>? logger = null,
        ToolCallAuditContext? auditContext = null)
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = user is null ? null : new DefaultHttpContext { User = user }
        };
        return new ToolAuthorizer(
            Options.Create(options ?? new ToolAuthorizationOptions { Enabled = enabled }),
            Options.Create(new OAuthOptions { NoAuth = noAuth }),
            accessor,
            resolver,
            logger,
            auditContext);
    }

    private static ClaimsPrincipal UserWithPermissions(params string[] permissions) =>
        new(new ClaimsIdentity(permissions.Select(p => new Claim("permissions", p)), "Test"));

    private static ClaimsPrincipal UserWithScope(string scope) =>
        new(new ClaimsIdentity(new[] { new Claim("scope", scope) }, "Test"));

    /// <summary>
    /// A principal carrying the <c>oid</c> claim, which is the only thing
    /// <see cref="CallerIdentity"/> reads — a subject is never parsed for an object id (vitally-mcp#156).
    /// </summary>
    private static ClaimsPrincipal UserWithOid(string oid, params string[] permissions)
    {
        var claims = new List<Claim> { new("oid", oid) };
        claims.AddRange(permissions.Select(p => new Claim("permissions", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static ToolAuthorizationOptions LiveOptions() => new()
    {
        Enabled = true,
        LiveGroupCheck = true,
        ReaderGroupId = ReaderGroup,
        AdminGroupId = AdminGroup
    };

    [Theory]
    [InlineData("GET", "zendesk:read")]
    [InlineData("POST", "zendesk:write")]
    [InlineData("PUT", "zendesk:write")]
    [InlineData("PATCH", "zendesk:write")]
    [InlineData("DELETE", "zendesk:delete")]
    public void RequiredPermission_MapsVerbToTier(string method, string expected)
    {
        Build().RequiredPermission(new HttpMethod(method)).Should().Be(expected);
    }

    [Fact]
    public void HasPermission_True_FromPermissionsClaim()
    {
        ToolAuthorizer.HasPermission(UserWithPermissions("zendesk:read", "zendesk:write"), "zendesk:write")
            .Should().BeTrue();
    }

    [Fact]
    public void HasPermission_True_FromSpaceDelimitedScope()
    {
        ToolAuthorizer.HasPermission(UserWithScope("openid zendesk:read zendesk:delete"), "zendesk:delete")
            .Should().BeTrue();
    }

    [Fact]
    public void HasPermission_False_WhenAbsent()
    {
        ToolAuthorizer.HasPermission(UserWithPermissions("zendesk:read"), "zendesk:delete").Should().BeFalse();
    }

    [Fact]
    public void HasPermission_True_FromCustomNamespacedClaim()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("https://vitally.fiscaltec.com/permissions", "zendesk:write") }, "Test"));
        ToolAuthorizer.HasPermission(user, "zendesk:write", "https://vitally.fiscaltec.com/permissions").Should().BeTrue();
    }

    [Fact]
    public void HasPermission_IgnoresCustomClaim_WhenNotConfigured()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("https://vitally.fiscaltec.com/permissions", "zendesk:write") }, "Test"));
        ToolAuthorizer.HasPermission(user, "zendesk:write", customClaimType: null).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureAuthorized_Allows_WhenPermissionPresent()
    {
        var authorizer = Build(user: UserWithPermissions("zendesk:write"));
        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Post)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureAuthorized_Throws_WhenPermissionMissing()
    {
        var authorizer = Build(user: UserWithPermissions("zendesk:read"));
        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete))
            .Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*zendesk:delete*");
    }

    [Fact]
    public async Task EnsureAuthorized_Throws_WhenNoAuthenticatedUser()
    {
        var authorizer = Build(user: null);
        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Get))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task EnsureAuthorized_NoOp_WhenDisabled()
    {
        var authorizer = Build(enabled: false, user: null);
        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureAuthorized_NoOp_WhenNoAuthDevMode()
    {
        var authorizer = Build(noAuth: true, user: null);
        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete)).Should().NotThrowAsync();
    }

    // ---- Live group check ----

    [Fact]
    public async Task LiveCheck_Allows_WhenLiveGroupsGrantPermission()
    {
        var resolver = new StubResolver(new HashSet<string> { "zendesk:read", "zendesk:write", "zendesk:delete" });
        var authorizer = Build(user: UserWithOid(CallerOid), options: LiveOptions(), resolver: resolver);

        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete)).Should().NotThrowAsync();
        resolver.LastObjectId.Should().Be(CallerOid, "the oid claim is what the resolver is keyed on");
    }

    [Fact]
    public async Task LiveCheck_Engages_WhenTheObjectIdArrivesUnderItsMappedClaimType()
    {
        // Regression: JwtBearer's default inbound claim mapping can present `oid` under its
        // WS-Federation URI instead of the short name. Which one arrives is a property of that
        // mapping rather than of the token, so reading only the short name finds nothing in
        // production while passing every test that mints it.
        var resolver = new StubResolver(new HashSet<string> { "zendesk:read", "zendesk:write", "zendesk:delete" });
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", CallerOid) }, "Test"));
        var authorizer = Build(user: user, options: LiveOptions(), resolver: resolver);

        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete)).Should().NotThrowAsync();
        resolver.LastObjectId.Should().Be(CallerOid);
    }

    [Fact]
    public async Task LiveCheck_Denies_WhenLiveGroupsLackPermission()
    {
        // Live membership says read-only — must override a stale token claim that still has delete.
        var resolver = new StubResolver(new HashSet<string> { "zendesk:read" });
        var authorizer = Build(user: UserWithOid(CallerOid, "zendesk:delete"), options: LiveOptions(), resolver: resolver);

        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete))
            .Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*zendesk:delete*");
    }

    [Fact]
    public async Task LiveCheck_Denies_WhenResolverUnavailable_EvenThoughTheTokenClaimWouldGrantIt()
    {
        // The claim tier that used to sit beneath the stale cache is gone (vitally-mcp#108). The resolver
        // returning null means it had neither a fresh Graph result nor a usable stale one, and
        // there is nothing below that any more — so this must deny despite a token claim that
        // plainly grants delete. Asserting *with* the claim present is the whole point: a version
        // that still consulted it would pass a test written without one.
        var resolver = new StubResolver(null);
        var authorizer = Build(user: UserWithOid(CallerOid, "zendesk:delete"), options: LiveOptions(), resolver: resolver);

        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete))
            .Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*zendesk:delete*");
    }

    [Fact]
    public async Task LiveCheck_Denies_WhenNoObjectIdCanBeDetermined_EvenThoughTheTokenClaimWouldGrantIt()
    {
        // Same fail-closed rule for the other way out of the live path. An Entra token always
        // carries `oid`, so a principal without one is a malformed token rather than an un-entitled
        // user — and before vitally-mcp#108 it fell through to the claim, which here would allow. The subject
        // is deliberately present and deliberately not consulted (vitally-mcp#156).
        var resolver = new StubResolver(new HashSet<string> { "zendesk:delete" });
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("sub", "not-a-guid"), new Claim("permissions", "zendesk:delete") }, "Test"));
        var authorizer = Build(user: user, options: LiveOptions(), resolver: resolver);

        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        resolver.LastObjectId.Should().BeNull("the resolver must not be called with a subject that is not an object id");
    }

    [Fact]
    public async Task LiveCheck_Denies_WhenNoResolverIsRegistered_RatherThanFallingBackToTheClaim()
    {
        // The mode is chosen by the flag alone. Testing the resolver for null alongside it — which
        // is how this was first written — hands a miswired container back to the token claim, a
        // silent revert to the posture the cutover removed, by the one route nobody is watching.
        var authorizer = Build(user: UserWithOid(CallerOid, "zendesk:delete"), options: LiveOptions(), resolver: null);

        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Theory]
    [InlineData(false)]  // resolver present, returns null  -> the "nothing usable" line
    [InlineData(true)]   // resolver absent entirely        -> the "no resolver registered" line
    public async Task LiveCheck_LogsItsDenialReasonOncePerRequest_NotOncePerEvaluation(bool noResolver)
    {
        // ToolAuthorizer is scoped, so one instance is one HTTP request — and a single tools/list
        // evaluates authorisation once per tool (dozens of them). Without the guard, one malformed token
        // or one Graph outage buries the single line that explains the failure under a copy per tool of
        // itself, in exactly the logs someone is reading because authorisation is failing.
        var logger = new CapturingLogger<ToolAuthorizer>();
        var authorizer = Build(
            user: UserWithOid(CallerOid),
            options: LiveOptions(),
            resolver: noResolver ? null : new StubResolver(null),
            logger: logger);

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete, HttpMethod.Get })
        {
            await authorizer.Invoking(a => a.EnsureAuthorizedAsync(method))
                .Should().ThrowAsync<UnauthorizedAccessException>();
        }

        logger.Entries.Should().ContainSingle().Which.Message
            .Should().Contain("this request",
                "the message describes the request's outcome, not one tool's tier — naming a permission "
                + "would make a single line read as if only that tier were refused");
    }

    [Fact]
    public async Task ClaimTier_StillResolves_WhenTheLiveCheckIsOff()
    {
        // Removing the fall-through must not remove the mode. With LiveGroupCheck off the token
        // claim is the entire resolution rather than a fallback beneath Graph, and a deployment
        // configured that way has to keep working.
        var authorizer = Build(user: UserWithOid(CallerOid, "zendesk:delete"), resolver: new StubResolver(null));

        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete)).Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task ReadOnly_DeniesMutatingVerbs_EvenWhenAuthDisabled(string method)
    {
        // ReadOnly is independent of Enabled — denies writes even with RBAC off.
        var authorizer = Build(options: new ToolAuthorizationOptions { Enabled = false, ReadOnly = true });

        Func<Task> act = () => authorizer.EnsureAuthorizedAsync(new HttpMethod(method));

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*read-only*");
    }

    [Fact]
    public async Task ReadOnly_AllowsGet()
    {
        var authorizer = Build(options: new ToolAuthorizationOptions { Enabled = false, ReadOnly = true });

        Func<Task> act = () => authorizer.EnsureAuthorizedAsync(HttpMethod.Get);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReadOnly_DeniesWrite_EvenInNoAuthDev()
    {
        var authorizer = Build(noAuth: true, options: new ToolAuthorizationOptions { Enabled = false, ReadOnly = true });

        Func<Task> act = () => authorizer.EnsureAuthorizedAsync(HttpMethod.Delete);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*read-only*");
    }

    [Fact]
    public async Task ReadOnlyFalse_DoesNotBlockWrites()
    {
        // Default ReadOnly=false: with Enabled=false the write passes (unchanged behaviour).
        var authorizer = Build(options: new ToolAuthorizationOptions { Enabled = false, ReadOnly = false });

        Func<Task> act = () => authorizer.EnsureAuthorizedAsync(HttpMethod.Post);

        await act.Should().NotThrowAsync();
    }

    // ---- Shared resolution surface for the policy handler (Task 4) ----

    [Fact]
    public async Task HasEffectivePermissionAsync_IsPublicAndHonoursTokenClaim()
    {
        var authorizer = Build(options: new ToolAuthorizationOptions { Enabled = true });
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("permissions", "zendesk:read")], "test"));

        (await authorizer.HasEffectivePermissionAsync(user, "zendesk:read")).Should().BeTrue();
        (await authorizer.HasEffectivePermissionAsync(user, "zendesk:write")).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false, true)]   // RBAC disabled => bypassed
    [InlineData(false, true, true)]   // NoAuth dev mode => bypassed
    [InlineData(false, false, false)] // both on => enforced
    public async Task IsAuthorizationBypassedAsync_ReflectsEnabledAndNoAuth(bool disabled, bool noAuth, bool expected)
    {
        var authorizer = Build(enabled: !disabled, noAuth: noAuth);

        (await authorizer.IsAuthorizationBypassedAsync()).Should().Be(expected);
    }

    [Fact]
    public async Task HasEffectivePermissionAsync_RecordsTheResolvedTier_IntoTheAuditContext()
    {
        // The audit record's tier must be the one the DECISION was made against. Re-deriving it in
        // the filter would mean a second Graph lookup that could answer differently, producing a
        // record that disagrees with the decision it claims to document — and, because entitlement
        // is resolved live, nothing could later tell which was right.
        var resolver = new StubResolver(new HashSet<string> { "zendesk:read", "zendesk:write" });
        var context = new ToolCallAuditContext();
        var authorizer = Build(
            options: new ToolAuthorizationOptions { Enabled = true, LiveGroupCheck = true },
            resolver: resolver,
            auditContext: context);

        await authorizer.HasEffectivePermissionAsync(UserWithOid("675ebdda-7590-4d79-8ec3-a2d17ab029ba"), "zendesk:read");

        context.Summarise().PermissionTier.Should().Be("zendesk:read,zendesk:write");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HasEffectivePermissionAsync_RecordsWhetherTheTierWasServedStale(bool servedStale)
    {
        // vitally-mcp#161. The resolver is the only component that knows whether it served the retained copy,
        // and the audit record must carry its answer — both ways round. `false` is recorded only
        // because the resolver reported it: that is a checked claim, unlike the default it replaces.
        var resolver = new StubResolver(new HashSet<string> { "zendesk:read" }, servedStale);
        var context = new ToolCallAuditContext();
        var authorizer = Build(
            options: new ToolAuthorizationOptions { Enabled = true, LiveGroupCheck = true },
            resolver: resolver,
            auditContext: context);

        await authorizer.HasEffectivePermissionAsync(UserWithOid(CallerOid), "zendesk:read");

        context.Summarise().TierServedStale.Should().Be(servedStale);
    }

    [Fact]
    public async Task HasEffectivePermissionAsync_StillDenies_WhenTheResolverReturnsNothing_AndRecordsNoTier()
    {
        // The widened return must not have softened the fail-closed rule: null is still a denial,
        // and a denial resolved no tier, so the record must not claim one — stale or otherwise.
        var context = new ToolCallAuditContext();
        var authorizer = Build(
            options: new ToolAuthorizationOptions { Enabled = true, LiveGroupCheck = true },
            resolver: new StubResolver(null),
            auditContext: context);

        var allowed = await authorizer.HasEffectivePermissionAsync(UserWithOid(CallerOid), "zendesk:read");

        allowed.Should().BeFalse();
        var summary = context.Summarise();
        summary.PermissionTier.Should().Be("unresolved");
        summary.TierServedStale.Should().BeNull("nothing was resolved, so nothing was checked");
    }

    [Fact]
    public async Task HasEffectivePermissionAsync_LeavesStalenessUnknown_OnTheClaimPath()
    {
        // The claim path has no retained copy and no Graph lookup, so "was it fresh?" has no answer
        // there. Recording false would assert a check that never happened.
        var context = new ToolCallAuditContext();
        var authorizer = Build(
            options: new ToolAuthorizationOptions { Enabled = true, LiveGroupCheck = false },
            auditContext: context);

        await authorizer.HasEffectivePermissionAsync(UserWithPermissions("zendesk:read"), "zendesk:read");

        context.Summarise().TierServedStale.Should().BeNull();
    }

    [Fact]
    public async Task HasEffectivePermissionAsync_RecordsTheTier_OnTheClaimPathToo()
    {
        // The claim path is inert on every deployed target, but it is the supported local-dev mode
        // and the record promises "the tier the caller resolved to". Leaving it "unresolved" there
        // would make a developer reading their own audit output conclude the field is broken.
        var context = new ToolCallAuditContext();
        var authorizer = Build(
            options: new ToolAuthorizationOptions { Enabled = true, LiveGroupCheck = false },
            auditContext: context);

        await authorizer.HasEffectivePermissionAsync(
            UserWithPermissions("zendesk:read", "zendesk:write"), "zendesk:read");

        context.Summarise().PermissionTier.Should().Be("zendesk:read,zendesk:write");
    }

    [Fact]
    public async Task HasEffectivePermissionAsync_RecordsTheTier_WhenThePermissionNamesAreConfigured()
    {
        // ToolAuthorizationOptions lets the three permission names be configured, and only validates
        // that they are non-empty — so a deployment may legitimately use names with no `zendesk:`
        // prefix. Filtering the recorded tier on that prefix meant HasPermission would authorise the
        // call while the audit record claimed the caller held nothing.
        var context = new ToolCallAuditContext();
        var authorizer = Build(
            options: new ToolAuthorizationOptions
            {
                Enabled = true,
                LiveGroupCheck = false,
                ReadPermission = "read",
                WritePermission = "write",
                DeletePermission = "delete",
            },
            auditContext: context);

        var allowed = await authorizer.HasEffectivePermissionAsync(
            UserWithPermissions("read", "write"), "read");

        allowed.Should().BeTrue("the configured name is what authorises");
        context.Summarise().PermissionTier.Should().Be("read,write",
            "and the record must say what authorised it, not 'none'");
    }
}
