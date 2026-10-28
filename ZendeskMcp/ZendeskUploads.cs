using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Distributed;

namespace ZendeskMcp;

/// <summary>
/// One-shot upload URLs for attaching a local file to a ticket comment. A tool call issues a slot;
/// the client then PUTs the file's bytes to <c>/uploads/{slot}</c> (for example with curl), and the
/// server passes them to Zendesk's <c>/api/v2/uploads</c> as the person who asked, returning the
/// upload token to put on the comment. The bytes are streamed through, never stored.
/// </summary>
/// <remarks>
/// <para>
/// The PUT carries no bearer token — curl has none — so the slot itself is the credential: 256 random
/// bits, usable once, for fifteen minutes, for exactly the declared number of bytes. It records the
/// caller's Entra object id and the permission claims they presented, and the PUT re-runs the
/// <b>write</b>-tier check against those at upload time (live Graph lookup included), so a person
/// removed from the editor group in between cannot use a slot issued before.
/// </para>
/// <para>
/// Backed by <see cref="IDistributedCache"/> for the same single-replica reason as
/// <see cref="ZendeskConnectLinks"/>.
/// </para>
/// </remarks>
public sealed class ZendeskUploadSlots(IDistributedCache cache)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private const string Prefix = "zendesk-upload:";

    // The claims a slot carries forward: identity for the audit record and the Zendesk token lookup,
    // and the token-claim permissions for deployments that resolve tiers from claims rather than Graph.
    private static readonly HashSet<string> CarriedClaimTypes = new(StringComparer.Ordinal)
    {
        "oid", "http://schemas.microsoft.com/identity/claims/objectidentifier",
        "permissions", "scope", "preferred_username", "upn",
    };

    public async Task<string> CreateAsync(ClaimsPrincipal caller, string fileName, long fileSize, bool inline, string? extraClaimType, CancellationToken cancellationToken = default)
    {
        var slot = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var claims = caller.Claims
            .Where(c => CarriedClaimTypes.Contains(c.Type) || (extraClaimType is not null && c.Type == extraClaimType))
            .Select(c => new SlotClaim(c.Type, c.Value))
            .ToList();
        var record = new UploadSlot(fileName, fileSize, inline, claims);
        await cache.SetAsync(Prefix + slot, JsonSerializer.SerializeToUtf8Bytes(record),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime }, cancellationToken);
        return slot;
    }

    /// <summary>Spends a slot. Returns null for an unknown, expired or already used one.</summary>
    public async Task<UploadSlot?> TakeAsync(string slot, CancellationToken cancellationToken = default)
    {
        var payload = await cache.GetAsync(Prefix + slot, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        await cache.RemoveAsync(Prefix + slot, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<UploadSlot>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public sealed record SlotClaim(string Type, string Value);

    public sealed record UploadSlot(string FileName, long FileSize, bool Inline, IReadOnlyList<SlotClaim> Claims)
    {
        /// <summary>The caller as they were when the slot was issued, for the authorisation re-check and the audit record.</summary>
        public ClaimsPrincipal ToPrincipal() =>
            new(new ClaimsIdentity(Claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "upload-slot"));
    }
}

public static class ZendeskUploadEndpoints
{
    public const string Path = "/uploads";

    public static void MapZendeskUploads(this WebApplication app)
    {
        app.MapPut($"{Path}/{{slot}}", async (string slot, HttpContext ctx, ZendeskUploadSlots slots) =>
        {
            var upload = await slots.TakeAsync(slot, ctx.RequestAborted);
            if (upload is null)
            {
                return Results.Json(new { error = "This upload link is unknown, expired or already used. Ask for a new one." },
                    statusCode: StatusCodes.Status404NotFound);
            }

            if (ctx.Request.ContentLength is { } declared && declared != upload.FileSize)
            {
                return Results.Json(new { error = $"Expected exactly {upload.FileSize} bytes, but the request declares {declared}." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Read at most one byte more than promised, so an oversized body is detected without
            // buffering all of it.
            var buffer = new byte[upload.FileSize + 1];
            var total = 0;
            int read;
            while (total < buffer.Length
                   && (read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ctx.RequestAborted)) > 0)
            {
                total += read;
            }

            if (total != upload.FileSize)
            {
                return Results.Json(new { error = $"Expected exactly {upload.FileSize} bytes, received {(total > upload.FileSize ? "more" : total.ToString(System.Globalization.CultureInfo.InvariantCulture))}." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Act as the person who asked for the slot: ToolAuthorizer, the credential provider and the
            // audit logger all read the caller from HttpContext.User, so setting it here — before the
            // scoped services are resolved — makes this request indistinguishable from their tool call.
            ctx.User = upload.ToPrincipal();
            var zendesk = ctx.RequestServices.GetRequiredService<ZendeskService>();

            try
            {
                var query = new List<KeyValuePair<string, string?>> { new("filename", upload.FileName) };
                if (upload.Inline)
                {
                    query.Add(new("inline", "true"));
                }

                var result = await zendesk.PostBinaryAsync("/api/v2/uploads", query,
                    buffer.AsSpan(0, total).ToArray(), "application/binary", ctx.RequestAborted);
                var uploadNode = result["upload"];
                var attachment = uploadNode?["attachment"];
                return Results.Json(new JsonObject
                {
                    ["upload_token"] = uploadNode?["token"]?.DeepClone(),
                    ["file_name"] = attachment?["file_name"]?.DeepClone(),
                    ["content_type"] = attachment?["content_type"]?.DeepClone(),
                    ["size"] = attachment?["size"]?.DeepClone(),
                    ["inline"] = upload.Inline,
                    ["next_step"] = "Pass upload_token in upload_tokens to Update_ticket or Create_ticket, with a comment, within 60 minutes.",
                });
            }
            catch (Exception ex) when (ToolErrorResult.IsSurfaceable(ex))
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        }).DisableAntiforgery();
    }
}
