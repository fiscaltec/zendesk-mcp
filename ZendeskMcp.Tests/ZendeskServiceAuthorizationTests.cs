using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ZendeskMcp.Tests;

/// <summary>
/// Verifies that RBAC enforcement is wired into the single choke point
/// (<c>ZendeskService.SendAsync</c>) so it applies to every tool, and that the HTTP verb maps to
/// the correct permission tier.
/// </summary>
public class ZendeskServiceAuthorizationTests
{
    /// <summary>
    /// <see cref="TestHelpers.BuildEnabledAuthorizer"/>'s principal, held in a field rather than in
    /// <see cref="HttpContextAccessor"/>. That accessor stores its context in a process-wide
    /// AsyncLocal, so the anonymous context <see cref="TestHelpers.BuildZendeskService"/> installs for
    /// the credential provider replaces the authorizer's caller — every call would then be denied,
    /// and the "allowed" tests would fail while the "denied" ones passed for the wrong reason.
    /// </summary>
    private static ToolAuthorizer EnabledAuthorizer(params string[] permissions) =>
        new(
            Options.Create(new ToolAuthorizationOptions { Enabled = true }),
            Options.Create(new OAuthOptions { NoAuth = false }),
            new FixedHttpContextAccessor(new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    permissions.Select(p => new Claim("permissions", p)), authenticationType: "Test")),
            }));

    private sealed class FixedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => context; set { } }
    }

    [Fact]
    public async Task Read_Denied_WhenCallerLacksReadPermission()
    {
        var http = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleTicketsJson());
        var service = TestHelpers.BuildZendeskService(
            http, authorizer: EnabledAuthorizer("zendesk:write"));

        var act = () => service.GetAsync("/api/v2/tickets");
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*zendesk:read*");
    }

    [Fact]
    public async Task Write_Denied_WhenCallerOnlyHasRead()
    {
        var http = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleTicketJson());
        var service = TestHelpers.BuildZendeskService(
            http, authorizer: EnabledAuthorizer("zendesk:read"));

        var act = () => service.PostAsync("/api/v2/tickets", new JsonObject { ["ticket"] = new JsonObject() });
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*zendesk:write*");
    }

    [Fact]
    public async Task Update_Denied_WhenCallerOnlyHasRead()
    {
        // PUT is how Zendesk updates a record, so it must need the same tier as POST.
        var http = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleTicketJson());
        var service = TestHelpers.BuildZendeskService(
            http, authorizer: EnabledAuthorizer("zendesk:read"));

        var act = () => service.PutAsync("/api/v2/tickets/101", new JsonObject { ["ticket"] = new JsonObject() });
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*zendesk:write*");
    }

    [Fact]
    public async Task Delete_Denied_WhenCallerOnlyHasWrite()
    {
        var http = TestHelpers.CreateMockHttpClient("{}");
        var service = TestHelpers.BuildZendeskService(
            http, authorizer: EnabledAuthorizer("zendesk:read", "zendesk:write"));

        var act = () => service.DeleteAsync("/api/v2/tickets/123");
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*zendesk:delete*");
    }

    [Fact]
    public async Task Read_Allowed_WhenCallerHasReadPermission()
    {
        var http = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleTicketsJson());
        var service = TestHelpers.BuildZendeskService(
            http, authorizer: EnabledAuthorizer("zendesk:read"));

        var result = await service.GetAsync("/api/v2/tickets");
        result["tickets"]!.AsArray().Select(t => t!["id"]!.GetValue<long>()).Should().Contain(101);
    }

    [Fact]
    public async Task Delete_Allowed_WhenCallerHasDeletePermission()
    {
        var http = TestHelpers.CreateMockHttpClient("{}", HttpStatusCode.OK);
        var service = TestHelpers.BuildZendeskService(
            http, authorizer: EnabledAuthorizer("zendesk:delete"));

        var act = () => service.DeleteAsync("/api/v2/tickets/123");
        await act.Should().NotThrowAsync();
    }
}
