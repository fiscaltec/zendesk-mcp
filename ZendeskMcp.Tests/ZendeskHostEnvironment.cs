using Microsoft.Extensions.DependencyInjection;

namespace ZendeskMcp.Tests;

/// <summary>
/// The <c>Zendesk__*</c> environment variables a composed host needs, in the environment-variable form
/// of <see cref="TestHelpers.ZendeskConfiguration"/> and <see cref="TestHelpers.NoAuthZendeskConfiguration"/>.
/// </summary>
/// <remarks>
/// Environment variables rather than <c>AddInMemoryCollection</c> for the reason the integration
/// collection exists: <c>Program.cs</c> reads <c>OAuth:NoAuth</c>, <c>Zendesk:KeyVaultUri</c> and
/// <c>Zendesk:DevelopmentApiToken</c> at composition time, before test configuration is injected, and
/// the startup guards decide on those reads. The two shapes are not interchangeable:
/// <c>StartupGuards.EnsureDevelopmentTokenIsLocal</c> refuses the development API token unless
/// <c>OAuth:NoAuth</c> is on, so an auth-on host gets the per-user OAuth client instead.
/// </remarks>
public static class ZendeskHostEnvironment
{
    /// <summary>Every variable <see cref="Set"/> may write, for a fixture's cleanup list.</summary>
    public static readonly IReadOnlyList<string> Names =
        [.. TestHelpers.ZendeskConfiguration.Keys.Concat(TestHelpers.NoAuthZendeskConfiguration.Keys)
            .Distinct().Select(ToEnvironmentName)];

    /// <summary>Sets the Zendesk configuration for a host with authentication on or off, clearing the other shape's keys.</summary>
    public static void Set(bool noAuth)
    {
        Clear();
        foreach (var (key, value) in noAuth ? TestHelpers.NoAuthZendeskConfiguration : TestHelpers.ZendeskConfiguration)
        {
            Environment.SetEnvironmentVariable(ToEnvironmentName(key), value);
        }
    }

    public static void Clear()
    {
        foreach (var name in Names)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    /// <summary>
    /// Replaces the host's token store with one where <paramref name="objectId"/> has already connected
    /// Zendesk, so an auth-on host's tools reach the (stubbed) Zendesk API rather than answering with a
    /// connect link. The caller's principal must carry that <c>oid</c>.
    /// </summary>
    public static IServiceCollection AddConnectedZendeskUser(this IServiceCollection services, string objectId)
    {
        var store = new InMemoryZendeskTokenStore();
        store.SaveAsync(objectId, TestHelpers.SampleToken()).GetAwaiter().GetResult();
        services.AddSingleton<IZendeskTokenStore>(store);
        return services;
    }

    private static string ToEnvironmentName(string key) => key.Replace(":", "__", StringComparison.Ordinal);
}
