using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Tests;

/// <summary>One tool as its declaration states it: name, annotations and <c>[Authorize]</c> policy.</summary>
public sealed record ToolDeclaration(string Name, McpServerToolAttribute Attribute, MethodInfo Method, string? Policy);

/// <summary>
/// The tools a host registers, read by reflection from their declarations. The single source the
/// count-free invariants are computed from: the production tool set grows as tools are added, so a
/// suite that wants "every read tool" derives the set here rather than hard-coding how many there are.
/// </summary>
public static class ToolCatalogue
{
    public const string ReadPolicy = "zendesk:read";
    public const string WritePolicy = "zendesk:write";
    public const string DeletePolicy = "zendesk:delete";

    /// <summary>
    /// Read-tier tools that are deliberately <c>ReadOnly=false</c>. <c>Disconnect_zendesk</c> revokes
    /// and forgets the caller's <i>own</i> Zendesk connection: it touches no Zendesk record, and every
    /// person who can use anything here must be able to undo their own connection, so it sits in the
    /// read tier while honestly declaring that it changes state.
    /// </summary>
    public static readonly IReadOnlySet<string> ReadTierMutations = new HashSet<string>(StringComparer.Ordinal)
    {
        "Disconnect_zendesk",
    };

    /// <summary>Every tool the production server registers.</summary>
    public static IReadOnlyList<ToolDeclaration> Production() => Scan(typeof(ZendeskService).Assembly.GetTypes());

    /// <summary>The production tools plus <see cref="ProbeTools"/>, for hosts built with <see cref="ProbeToolHost.AddProbeTools"/>.</summary>
    public static IReadOnlyList<ToolDeclaration> ProductionAndProbes() =>
        [.. Production(), .. Scan([typeof(ProbeTools)])];

    /// <summary>The tools a caller holding <paramref name="permissions"/> may discover.</summary>
    public static IReadOnlyList<string> VisibleTo(IReadOnlyList<ToolDeclaration> tools, params string[] permissions) =>
        tools.Where(t => t.Policy is not null && permissions.Contains(t.Policy)).Select(t => t.Name).ToList();

    private static List<ToolDeclaration> Scan(IEnumerable<Type> types)
    {
        var tools = new List<ToolDeclaration>();
        foreach (var type in types.Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is not null)
                {
                    tools.Add(new ToolDeclaration(attr.Name ?? method.Name, attr, method, method.GetCustomAttribute<AuthorizeAttribute>()?.Policy));
                }
            }
        }

        return tools;
    }
}
