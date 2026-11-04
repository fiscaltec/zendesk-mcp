using System.Runtime.CompilerServices;

namespace ZendeskMcp.Tests;

/// <summary>
/// Keeps the configuration a developer has exported to run the server locally — <c>OAuth__NoAuth</c>,
/// <c>Authorization__ReadOnly</c>, <c>Zendesk__DevelopmentAccessToken</c>, <c>ASPNETCORE_ENVIRONMENT</c>
/// and the rest — out of the hosts the tests compose (#8).
/// </summary>
/// <remarks>
/// <para>
/// <c>WebApplication.CreateBuilder</c> reads every environment variable, so without this a developer's
/// shell decides how <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/> hosts are
/// composed: 107 of 1,033 tests failed with the five dev-mode variables from <c>CLAUDE.md</c> set,
/// and none in CI, which sets none of them.
/// </para>
/// <para>
/// <b>Cleared once, when the test assembly loads, rather than overridden per host</b> — because the
/// override routes cannot reach a key the test does not name. Checked on 2026-11-04 against
/// <c>Microsoft.AspNetCore.Mvc.Testing</c> 10.0:
/// </para>
/// <list type="bullet">
/// <item><c>ConfigureAppConfiguration</c> runs after <c>Program.cs</c>'s top-level reads
/// (<c>noAuth</c>, <c>readOnlyMode</c>, the startup guards), so removing the environment-variable
/// sources there is too late for exactly the settings that fail.</item>
/// <item><c>ConfigureHostConfiguration</c> is applied early and does beat environment variables, but
/// its callback receives a separate configuration with no environment sources to remove, and a
/// <c>null</c> value arrives in the app as <c>""</c> — which <c>GetValue&lt;bool&gt;</c> rejects
/// (<c>Failed to convert configuration value '' at 'OAuth:NoAuth'</c>). It can replace a value, never
/// unset one.</item>
/// </list>
/// <para>
/// The objection to clearing variables in a fixture's constructor — process-wide, racing parallel
/// test classes — does not apply here: a module initialiser runs once, before any test class or
/// fixture exists, so nothing can be composing a host concurrently. It changes only this test
/// process's copy of the environment; the developer's shell, and a server they are running from it,
/// are separate processes and are untouched.
/// </para>
/// <para>
/// Tests that set these variables themselves (the fixtures in <see cref="IntegrationTestCollection"/>)
/// do so after this has run, so their own values are what their hosts see.
/// </para>
/// </remarks>
public static class AmbientConfiguration
{
    /// <summary>
    /// The configuration sections <c>Program.cs</c> reads, in environment-variable form. <c>Logging__</c>
    /// because <c>WebApplication.CreateBuilder</c> binds it (an exported
    /// <c>Logging__LogLevel__Default=Warning</c> fails eight <see cref="ToolCallAuditCompositionTests"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> SectionPrefixes =
    [
        $"{OAuthOptions.SectionName}__",
        $"{ToolAuthorizationOptions.SectionName}__",
        $"{ZendeskServerOptions.SectionName}__",
        $"{AuditOptions.SectionName}__",
        $"{ToolsListCacheOptions.SectionName}__",
        "ApplicationInsights__",
        "Logging__",
    ];

    /// <summary>
    /// Whole variable names the framework reads outside those sections: the hosting environment (the
    /// development-token guard depends on it) and <c>AllowedHosts</c> (host filtering — an exported
    /// <c>AllowedHosts=nope.example</c> fails 102 tests). <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is
    /// the Azure Monitor SDK's own environment fallback; cleared so a developer's resource can never
    /// receive test telemetry.
    /// </summary>
    public static readonly IReadOnlyList<string> ExactNames =
        ["ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "AllowedHosts", "APPLICATIONINSIGHTS_CONNECTION_STRING"];

    /// <summary>
    /// Prefixes read straight from the environment by a library rather than through configuration:
    /// the OpenTelemetry SDK's <c>OTEL_*</c> (an exported <c>OTEL_SDK_DISABLED=true</c> fails
    /// <see cref="LoggingFilterTests"/>' exporter-wiring test).
    /// </summary>
    public static readonly IReadOnlyList<string> LibraryPrefixes = ["OTEL_"];

    /// <summary>The variables this process inherited and <see cref="ClearOnLoad"/> removed, for diagnosis.</summary>
    public static IReadOnlyList<string> Cleared { get; private set; } = [];

    /// <summary>
    /// Whether <paramref name="name"/> is configuration the server reads. Case-insensitive, because
    /// .NET configuration keys are: on Linux <c>oauth__noauth</c> is a distinct variable from
    /// <c>OAuth__NoAuth</c> but binds exactly as it does. The <c>ASPNETCORE_</c> and <c>DOTNET_</c>
    /// forms are included because the host's prefixed providers map them onto the same keys.
    /// </summary>
    public static bool IsServerConfiguration(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (LibraryPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var unprefixed = StripHostPrefix(name);
        return ExactNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(n, unprefixed, StringComparison.OrdinalIgnoreCase))
            || SectionPrefixes.Any(p => unprefixed.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    [ModuleInitializer]
    internal static void ClearOnLoad()
    {
        var names = Environment.GetEnvironmentVariables().Keys.Cast<object>()
            .Select(k => (string)k)
            .Where(IsServerConfiguration)
            .ToArray();

        foreach (var name in names)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        Cleared = names;
    }

    private static string StripHostPrefix(string name)
    {
        foreach (var prefix in (ReadOnlySpan<string>)["ASPNETCORE_", "DOTNET_"])
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name[prefix.Length..];
            }
        }

        return name;
    }
}
