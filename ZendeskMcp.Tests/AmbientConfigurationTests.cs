using FluentAssertions;
using Xunit;

namespace ZendeskMcp.Tests;

/// <summary>
/// Which inherited variables <see cref="AmbientConfiguration"/> keeps out of the test hosts (#8).
/// Pure: these never touch the process environment, so they need no collection.
/// </summary>
public class AmbientConfigurationTests
{
    [Theory]
    // The five dev-mode variables CLAUDE.md tells a developer to export.
    [InlineData("OAuth__NoAuth")]
    [InlineData("Authorization__ReadOnly")]
    [InlineData("Zendesk__DevelopmentAccessToken")]
    [InlineData("Zendesk__Subdomain")]
    [InlineData("ASPNETCORE_ENVIRONMENT")]
    // Every other section Program.cs reads, nested keys included.
    [InlineData("Audit__IncludeReads")]
    [InlineData("ToolsListCache__TimeToLive")]
    [InlineData("ApplicationInsights__ConnectionString")]
    [InlineData("Logging__LogLevel__Default")]
    [InlineData("Zendesk__Features__AllowPublicReplies")]
    [InlineData("DOTNET_ENVIRONMENT")]
    [InlineData("APPLICATIONINSIGHTS_CONNECTION_STRING")]
    // Case-insensitive: configuration keys are, and on Linux these are distinct variables.
    [InlineData("oauth__noauth")]
    [InlineData("ZENDESK__SUBDOMAIN")]
    // The host's prefixed providers map these onto the same keys.
    [InlineData("ASPNETCORE_OAuth__NoAuth")]
    [InlineData("DOTNET_Zendesk__KeyVaultUri")]
    public void ServerConfiguration_IsCleared(string name) =>
        AmbientConfiguration.IsServerConfiguration(name).Should().BeTrue();

    [Theory]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("HTTPS_PROXY")]
    [InlineData("DOTNET_ROOT")]
    [InlineData("ASPNETCORE_URLS")]
    // A prefix only counts with its separator: these are not the OAuth or Zendesk sections.
    [InlineData("OAuthToken")]
    [InlineData("ZENDESK_API_TOKEN")]
    [InlineData("Zendesk_Subdomain")]
    public void UnrelatedVariables_AreLeftAlone(string name) =>
        AmbientConfiguration.IsServerConfiguration(name).Should().BeFalse();
}
