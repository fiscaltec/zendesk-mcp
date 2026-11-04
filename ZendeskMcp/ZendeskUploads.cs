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
/// bits, usable once, for fifteen minutes, for exactly the declared number of bytes, and at most
/// <see cref="MaxOpenSlotsPerPerson"/> open per person. It records the caller's Entra object id and the
/// permission claims they presented, and the PUT re-runs the <b>write</b>-tier check at upload time.
/// With <c>Authorization:LiveGroupCheck</c> on (every deployed target) that check reads the person's
/// group membership from Graph afresh, so someone removed from the editor group in between cannot use a
/// slot issued before; with it off, it can only re-read the claims copied into the slot.
/// </para>
/// <para>
/// Backed by <see cref="IDistributedCache"/> for the same single-replica reason as
/// <see cref="ZendeskConnectLinks"/>.
/// </para>
/// </remarks>
public sealed class ZendeskUploadSlots(IDistributedCache cache)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Slots one person may hold, unused or with an upload in flight. Each PUT can hold up to the
    /// file-size limit in memory while it streams in, so without a cap one write-tier caller could
    /// request many slots and pin the single replica's memory with slow uploads. A finished upload
    /// stops counting at once.
    /// </summary>
    public const int MaxOpenSlotsPerPerson = 5;

    private const string Prefix = "zendesk-upload:";

    // Each issued slot's reservation, by slot id, and each person's reservations, by owner. A slot
    // counts against its owner's quota from issue until its upload finishes (Release) or, never used,
    // until it expires — so an upload in flight still counts, because that is what holds a buffer, and
    // a finished one stops counting at once. The reservation is also the one-use gate: TryTake is a
    // compare-and-swap, so two concurrent PUTs cannot both spend a slot, which a read of the cache
    // followed by a remove could not promise. Process-local, like the slots themselves (single replica).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Reservation> _reservations = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<Reservation>> _issued = new(StringComparer.Ordinal);

    // The claims a slot carries forward: identity for the audit record and the Zendesk token lookup,
    // and the token-claim permissions for deployments that resolve tiers from claims rather than Graph.
    private static readonly HashSet<string> CarriedClaimTypes = new(StringComparer.Ordinal)
    {
        "oid", "http://schemas.microsoft.com/identity/claims/objectidentifier",
        "permissions", "scope", "preferred_username", "upn",
    };

    public async Task<string> CreateAsync(ClaimsPrincipal caller, string fileName, long fileSize, bool inline, string? extraClaimType, CancellationToken cancellationToken = default)
    {
        var owner = CallerIdentity.TryGetObjectId(caller) ?? "unknown";
        var slot = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var reservation = new Reservation(slot, owner, DateTimeOffset.UtcNow);

        var issued = _issued.GetOrAdd(owner, _ => []);
        lock (issued)
        {
            // Drop the slots that expired unused. Only an open one can expire: TryExpire loses to a
            // concurrent TryTake, and a slot whose upload is in flight keeps counting until Release.
            foreach (var stale in issued.Where(r => reservation.IssuedAt - r.IssuedAt > Lifetime && r.TryExpire()).ToList())
            {
                issued.Remove(stale);
                _reservations.TryRemove(stale.Slot, out _);
            }

            if (issued.Count >= MaxOpenSlotsPerPerson)
            {
                throw new ArgumentException(
                    $"You already have {MaxOpenSlotsPerPerson} upload links open or uploading. Use or let them expire (15 minutes) before asking for another.");
            }

            issued.Add(reservation);
            _reservations[slot] = reservation;
        }

        try
        {
            var claims = caller.Claims
                .Where(c => CarriedClaimTypes.Contains(c.Type) || (extraClaimType is not null && c.Type == extraClaimType))
                .Select(c => new SlotClaim(c.Type, c.Value))
                .ToList();
            var record = new UploadSlot(fileName, fileSize, inline, claims);
            await cache.SetAsync(Prefix + slot, JsonSerializer.SerializeToUtf8Bytes(record),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime }, cancellationToken);
        }
        catch
        {
            // A slot nobody was given must not hold one of the person's places for fifteen minutes.
            Release(slot);
            throw;
        }

        return slot;
    }

    /// <summary>
    /// Spends a slot. Returns null for an unknown, expired or already used one. A slot returned here
    /// keeps counting against its owner's quota until <see cref="Release"/> is called for it, which the
    /// upload endpoint does when the upload finishes, however it finishes.
    /// </summary>
    public async Task<UploadSlot?> TakeAsync(string slot, CancellationToken cancellationToken = default)
    {
        // The one-use gate, before anything is read: of two concurrent PUTs for one slot, exactly one
        // gets past here, so the other neither uploads as the owner nor allocates a second buffer.
        if (!_reservations.TryGetValue(slot, out var reservation) || !reservation.TryTake())
        {
            return null;
        }

        UploadSlot? upload = null;
        try
        {
            var payload = await cache.GetAsync(Prefix + slot, cancellationToken);
            if (payload is not null)
            {
                await cache.RemoveAsync(Prefix + slot, cancellationToken);
                upload = JsonSerializer.Deserialize<UploadSlot>(payload);
            }
        }
        catch (JsonException)
        {
            // Unreadable: treated as unknown, below.
        }
        finally
        {
            // Nothing to upload (expired in the cache, unreadable, or the read failed): no upload will
            // follow to release it, so release it here.
            if (upload is null)
            {
                Release(slot);
            }
        }

        return upload;
    }

    /// <summary>
    /// Returns a slot's place in its owner's quota. Idempotent, and harmless for an unknown slot.
    /// </summary>
    public void Release(string slot)
    {
        if (!_reservations.TryRemove(slot, out var reservation))
        {
            return;
        }

        if (_issued.TryGetValue(reservation.Owner, out var issued))
        {
            lock (issued)
            {
                issued.Remove(reservation);
            }
        }
    }

    private sealed class Reservation(string slot, string owner, DateTimeOffset issuedAt)
    {
        private const int Open = 0, Taken = 1, Expired = 2;
        private int _state = Open;

        public string Slot { get; } = slot;
        public string Owner { get; } = owner;
        public DateTimeOffset IssuedAt { get; } = issuedAt;

        public bool TryTake() => Interlocked.CompareExchange(ref _state, Taken, Open) == Open;

        public bool TryExpire() => Interlocked.CompareExchange(ref _state, Expired, Open) == Open;
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

            // The slot holds one of its owner's places while the bytes stream in, and gives it back
            // however the upload ends — refused, failed, aborted or done.
            try
            {
                return await UploadAsync(upload, ctx);
            }
            finally
            {
                slots.Release(slot);
            }
        }).DisableAntiforgery();
    }

    private static async Task<IResult> UploadAsync(ZendeskUploadSlots.UploadSlot upload, HttpContext ctx)
    {
        if (ctx.Request.ContentLength is { } declared && declared != upload.FileSize)
        {
            return Results.Json(new { error = $"Expected exactly {upload.FileSize} bytes, but the request declares {declared}." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Read at most one byte more than promised, into a buffer that grows with what actually
        // arrives — allocating the declared size up front would let a slow, never-finished upload pin
        // the whole amount.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length <= upload.FileSize
               && (read = await ctx.Request.Body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, upload.FileSize + 1 - buffer.Length)), ctx.RequestAborted)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        var total = buffer.Length;
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
                new ArraySegment<byte>(buffer.GetBuffer(), 0, (int)total), "application/binary", ctx.RequestAborted);
            var uploadNode = result["upload"];
            if (uploadNode?["token"] is null)
            {
                // A 2xx without a token would read as success and hand back nothing to attach.
                return Results.Json(new { error = "Zendesk accepted the upload but returned no upload token; request a new slot and try again." },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var attachment = uploadNode["attachment"];
            return Results.Json(new JsonObject
            {
                ["upload_token"] = uploadNode["token"]!.DeepClone(),
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
    }
}
