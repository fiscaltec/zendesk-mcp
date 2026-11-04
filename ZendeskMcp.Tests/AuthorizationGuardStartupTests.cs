using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace ZendeskMcp.Tests;

/// <summary>
/// <see cref="StartupGuards.EnsureAuthorizationIsOn"/> through the real Program.cs: a guard that is
/// unit-tested but not wired in protects nothing (#12).
/// </summary>
/// <remarks>
/// Environment variables rather than <c>AddInMemoryCollection</c>: Program.cs runs the startup guards
/// before test configuration is injected (see <see cref="ZendeskHostEnvironment"/>).
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
public class AuthorizationGuardStartupTests
{
    [Fact]
    public void Host_RefusesToStart_WithTheTierChecksOff_BehindSignIn()
    {
        WithEnvironment(noAuth: false, () =>
        {
            using var factory = new Factory(Environments.Production);

            var act = () => factory.CreateClient();

            act.Should().Throw<Exception>().Which.ToString().Should().Contain("Authorization:Enabled=false is refused");
        });
    }

    [Fact]
    public void Host_Starts_WithTheTierChecksOff_UnderNoAuth()
    {
        // The control: the same setting where it is allowed, so the refusal above is the guard's.
        WithEnvironment(noAuth: true, () =>
        {
            using var factory = new Factory(Environments.Development);
            using var client = factory.CreateClient();

            client.GetAsync("/health").GetAwaiter().GetResult().IsSuccessStatusCode.Should().BeTrue();
        });
    }

    private static void WithEnvironment(bool noAuth, Action test)
    {
        Environment.SetEnvironmentVariable("OAuth__NoAuth", noAuth ? "true" : "false");
        Environment.SetEnvironmentVariable("Authorization__Enabled", "false");
        ZendeskHostEnvironment.Set(noAuth);
        try
        {
            test();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OAuth__NoAuth", null);
            Environment.SetEnvironmentVariable("Authorization__Enabled", null);
            ZendeskHostEnvironment.Clear();
        }
    }

    private sealed class Factory(string environment) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            return base.CreateHost(builder);
        }
    }
}
