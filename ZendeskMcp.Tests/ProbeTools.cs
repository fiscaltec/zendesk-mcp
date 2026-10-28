using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Tests;

/// <summary>
/// Test-only tools, one per shape the composed-host tests need: a paged read, a read that validates
/// caller text, a composite read that absorbs a failed sub-call, and a write and a delete on each side
/// of the naming convention. Registered into a test host with <see cref="ProbeToolHost.AddProbeTools"/>.
/// </summary>
/// <remarks>
/// <para>
/// They exist because the host-level suites (audit, logging, discovery filtering, read-only mode)
/// prove properties of the <i>pipeline</i> — the scoped audit context, the error-surfacing filter, the
/// <c>[Authorize]</c> checkpoint — and need a tool that reaches Zendesk through
/// <see cref="ZendeskService"/> at a known tier to do it. The production tools are being written in
/// parallel and change shape, so pinning these suites to one of them would make every tool change a
/// pipeline-test change. Each probe does only what a real tool does: build a <c>/api/v2/...</c> path,
/// call <see cref="ZendeskService"/>, and shape the result.
/// </para>
/// <para>
/// ⚠️ Not in the production assembly, so the reflection sweeps over production tools
/// (<see cref="ToolCatalogue"/>) never see them — and must not: they would let a sweep pass on a tool
/// nobody ships. They are added to <see cref="ToolCatalogue"/> only where a test composes a host that
/// registers them.
/// </para>
/// </remarks>
[McpServerToolType]
public static class ProbeTools
{
    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "List_probe_tickets", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Test probe: pages /api/v2/tickets.")]
    public static async Task<string> ListProbeTickets(ZendeskService zendesk, string? nameContains = null, CancellationToken cancellationToken = default)
    {
        var query = nameContains is null ? null : new Dictionary<string, string?> { ["subject"] = nameContains };
        var page = await zendesk.GetAllPagesAsync("/api/v2/tickets", query, "tickets", maxItems: 1000, cancellationToken: cancellationToken);
        return new JsonObject
        {
            ["tickets"] = new JsonArray([.. page.Items.Select(i => i.DeepClone())]),
            ["truncated"] = page.HasMore,
        }.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "Search_probe_tickets", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Test probe: validates created_after, then searches.")]
    public static async Task<string> SearchProbeTickets(ZendeskService zendesk, string createdAfter, CancellationToken cancellationToken = default)
    {
        // The way real tools reject bad input: an ArgumentException quoting what the caller sent, which
        // the error-surfacing filter hands back to the model.
        if (!DateTimeOffset.TryParse(createdAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var after))
        {
            throw new ArgumentException($"created_after must be ISO-8601 (got '{createdAfter}').");
        }

        var result = await zendesk.GetAsync("/api/v2/search/export",
            new Dictionary<string, string?> { ["query"] = $"type:ticket created>{after:yyyy-MM-dd}", ["filter[type]"] = "ticket" },
            cancellationToken: cancellationToken);
        return result.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "Get_probe_ticket_summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Test probe: a ticket plus its comments, absorbing a failed comments call.")]
    public static async Task<string> GetProbeTicketSummary(ZendeskService zendesk, long ticketId, CancellationToken cancellationToken = default)
    {
        var ticket = await zendesk.GetAsync($"/api/v2/tickets/{ticketId}", cancellationToken: cancellationToken);
        JsonNode comments;
        try
        {
            comments = await zendesk.GetAsync($"/api/v2/tickets/{ticketId}/comments", cancellationToken: cancellationToken);
        }
        catch (HttpRequestException)
        {
            // The composite-tool pattern: one failed section becomes an error inside a successful
            // result, so the tool-call failure log never fires for it.
            comments = new JsonObject { ["error"] = "comments unavailable" };
        }

        return new JsonObject { ["ticket"] = ticket.DeepClone(), ["comments"] = comments.DeepClone() }.ToJsonString();
    }

    [Authorize(Policy = "zendesk:write")]
    [McpServerTool(Name = "Create_probe_ticket", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Test probe: POST /api/v2/tickets.")]
    public static async Task<string> CreateProbeTicket(ZendeskService zendesk, string subject, CancellationToken cancellationToken = default) =>
        (await zendesk.PostAsync("/api/v2/tickets", new JsonObject { ["ticket"] = new JsonObject { ["subject"] = subject } },
            cancellationToken: cancellationToken)).ToJsonString();

    [Authorize(Policy = "zendesk:write")]
    [McpServerTool(Name = "Update_probe_ticket", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Test probe: PUT /api/v2/tickets/{id}.")]
    public static async Task<string> UpdateProbeTicket(ZendeskService zendesk, long ticketId, string subject, CancellationToken cancellationToken = default) =>
        (await zendesk.PutAsync($"/api/v2/tickets/{ticketId}", new JsonObject { ["ticket"] = new JsonObject { ["subject"] = subject } },
            cancellationToken: cancellationToken)).ToJsonString();

    /// <summary>A write outside the Create_/Update_ prefixes, as <c>Apply_macro</c> is.</summary>
    [Authorize(Policy = "zendesk:write")]
    [McpServerTool(Name = "Apply_probe_macro", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Test probe: PUT /api/v2/tickets/{id} with a macro's changes.")]
    public static async Task<string> ApplyProbeMacro(ZendeskService zendesk, long ticketId, CancellationToken cancellationToken = default) =>
        (await zendesk.PutAsync($"/api/v2/tickets/{ticketId}", new JsonObject { ["ticket"] = new JsonObject() },
            cancellationToken: cancellationToken)).ToJsonString();

    [Authorize(Policy = "zendesk:delete")]
    [McpServerTool(Name = "Delete_probe_ticket", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Test probe: DELETE /api/v2/tickets/{id}.")]
    public static async Task<string> DeleteProbeTicket(ZendeskService zendesk, long ticketId, CancellationToken cancellationToken = default) =>
        (await zendesk.DeleteAsync($"/api/v2/tickets/{ticketId}", cancellationToken: cancellationToken)).ToJsonString();

    /// <summary>A delete outside the Delete_ prefix.</summary>
    [Authorize(Policy = "zendesk:delete")]
    [McpServerTool(Name = "Remove_probe_ticket_tags", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Test probe: DELETE /api/v2/tickets/{id}/tags.")]
    public static async Task<string> RemoveProbeTicketTags(ZendeskService zendesk, long ticketId, CancellationToken cancellationToken = default) =>
        (await zendesk.DeleteAsync($"/api/v2/tickets/{ticketId}/tags", cancellationToken: cancellationToken)).ToJsonString();
}

/// <summary>Registers <see cref="ProbeTools"/> into a composed host.</summary>
public static class ProbeToolHost
{
    /// <summary>
    /// Adds the probe tools alongside the production ones. Goes through the SDK's own
    /// <c>WithTools(types)</c> on the host's existing service collection, so the tools are registered
    /// exactly as <c>WithToolsFromAssembly()</c> registers the production ones — same DI-parameter
    /// binding, same <c>[Authorize]</c> metadata for the authorisation filters, and the same
    /// <see cref="KnownToolNames"/> membership — without calling <c>AddMcpServer()</c> a second time.
    /// </summary>
    public static IServiceCollection AddProbeTools(this IServiceCollection services)
    {
        new Builder(services).WithTools([typeof(ProbeTools)]);
        return services;
    }

    private sealed class Builder(IServiceCollection services) : IMcpServerBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
