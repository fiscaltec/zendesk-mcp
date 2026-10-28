using System.Net;
using FluentAssertions;

namespace ZendeskMcp.Tests;

/// <summary>
/// Verifies that RBAC enforcement is wired into the single choke point
/// (<c>ZendeskService.SendAsync</c>) so it applies to every tool, and that the HTTP verb maps to
/// the correct permission tier.
/// </summary>
public class VitallyServiceAuthorizationTests
{
    [Fact]
    public async Task Read_Denied_WhenCallerLacksReadPermission()
    {
        var http = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = TestHelpers.BuildZendeskService(
            http, authorizer: TestHelpers.BuildEnabledAuthorizer("zendesk:write"));

        var act = () => service.GetResourcesAsync("accounts");
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*zendesk:read*");
    }

    [Fact]
    public async Task Write_Denied_WhenCallerOnlyHasRead()
    {
        var http = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleSingleAccountJson());
        var service = TestHelpers.BuildZendeskService(
            http, authorizer: TestHelpers.BuildEnabledAuthorizer("zendesk:read"));

        var act = () => service.CreateResourceAsync("accounts", "{}");
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*zendesk:write*");
    }

    [Fact]
    public async Task Delete_Denied_WhenCallerOnlyHasWrite()
    {
        var http = TestHelpers.CreateMockHttpClient("{}");
        var service = TestHelpers.BuildZendeskService(
            http, authorizer: TestHelpers.BuildEnabledAuthorizer("zendesk:read", "zendesk:write"));

        var act = () => service.DeleteResourceAsync("accounts", "account-123");
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*zendesk:delete*");
    }

    [Fact]
    public async Task Read_Allowed_WhenCallerHasReadPermission()
    {
        var http = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = TestHelpers.BuildZendeskService(
            http, authorizer: TestHelpers.BuildEnabledAuthorizer("zendesk:read"));

        var result = await service.GetResourcesAsync("accounts");
        result.Should().Contain("account-123");
    }

    [Fact]
    public async Task Delete_Allowed_WhenCallerHasDeletePermission()
    {
        var http = TestHelpers.CreateMockHttpClient("{}", HttpStatusCode.OK);
        var service = TestHelpers.BuildZendeskService(
            http, authorizer: TestHelpers.BuildEnabledAuthorizer("zendesk:delete"));

        var act = () => service.DeleteResourceAsync("accounts", "account-123");
        await act.Should().NotThrowAsync();
    }
}
