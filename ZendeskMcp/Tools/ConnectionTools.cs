using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Tools;

/// <summary>
/// The person's own Zendesk connection: who it is, a link to make or replace it, and removing it.
/// Under the read tier because every person who can use anything here needs these first, and none
/// of them touches a Zendesk record.
/// </summary>
[McpServerToolType]
public static class ConnectionTools
{
    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "Get_zendesk_connection", Title = "Get Zendesk connection", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Show which Zendesk account the signed-in person is connected as, or a link to connect one. Every other Zendesk tool runs as this account.")]
    public static async Task<string> GetZendeskConnection(ZendeskService zendesk, CancellationToken cancellationToken = default)
    {
        var credentials = zendesk.Credentials;
        if (credentials.UsesDevelopmentToken)
        {
            return new JsonObject
            {
                ["connected"] = true,
                ["mode"] = string.IsNullOrWhiteSpace(zendesk.Options.DevelopmentAccessToken) ? "development_api_token" : "development_access_token",
                ["zendesk_email"] = zendesk.Options.DevelopmentEmail,
                ["note"] = "Local development: every call uses one shared development credential. Deployed servers use each person's own connection.",
            }.ToJsonString();
        }

        var token = await credentials.PeekAsync(cancellationToken);
        if (token is null)
        {
            return new JsonObject
            {
                ["connected"] = false,
                ["connect_url"] = await credentials.CreateConnectLinkAsync(cancellationToken),
                ["note"] = "Give the person this link to connect their Zendesk account. It works once and expires shortly.",
            }.ToJsonString();
        }

        return new JsonObject
        {
            ["connected"] = true,
            ["zendesk_user_id"] = token.ZendeskUserId,
            ["zendesk_email"] = token.ZendeskEmail,
            ["zendesk_role"] = token.ZendeskRole,
            ["connected_at"] = token.ConnectedAt,
            ["zendesk_url"] = zendesk.BaseUrl,
        }.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "Get_zendesk_connect_link", Title = "Get Zendesk connect link", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Get a fresh link for the person to connect, or reconnect, their Zendesk account — for example to switch to a different Zendesk login. The link works once and expires shortly.")]
    public static async Task<string> GetZendeskConnectLink(ZendeskService zendesk, CancellationToken cancellationToken = default)
    {
        if (zendesk.Credentials.UsesDevelopmentToken)
        {
            throw new ArgumentException("This server uses a development API token, so there is nothing to connect.");
        }

        return new JsonObject
        {
            ["connect_url"] = await zendesk.Credentials.CreateConnectLinkAsync(cancellationToken),
            ["expires_in_minutes"] = (int)zendesk.Options.ConnectLinkLifetime.TotalMinutes,
        }.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "Disconnect_zendesk", Title = "Disconnect Zendesk", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Disconnect the person's Zendesk account from this server: revokes the token at Zendesk and forgets it. Only do this when the person asks. They can reconnect at any time.")]
    public static async Task<string> DisconnectZendesk(ZendeskService zendesk, CancellationToken cancellationToken = default)
    {
        if (zendesk.Credentials.UsesDevelopmentToken)
        {
            throw new ArgumentException("This server uses a development API token, so there is nothing to disconnect.");
        }

        var removed = await zendesk.Credentials.DisconnectAsync(cancellationToken);
        return new JsonObject
        {
            ["result"] = removed ? "disconnected" : "not_connected",
        }.ToJsonString();
    }
}
