using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace ZendeskMcp.Tools;

/// <summary>
/// What a write tool's repeat guard remembers about one call: when it was made, and whether its
/// outcome is known. <see cref="Pending"/> means the write was sent and no answer came back that
/// settles it — the response was lost to a cancellation, a timeout or a server error, which is
/// exactly the case a retry comes from (#12). <see cref="InFlight"/> means that call is still
/// waiting for Zendesk on this server: an MCP client that gives up does not cancel a stateless
/// request, so the write can still land after a retry has looked for it and found nothing.
/// </summary>
internal sealed record RepeatGuardEntry(DateTimeOffset At, long? RecordId, bool Pending, bool InFlight = false);

/// <summary>
/// Shared rules for the repeat guards of <c>Create_help_center_article</c> and <c>Apply_macro</c>.
/// </summary>
/// <remarks>
/// Each guard records a pending entry <b>before</b> it sends the write, and replaces it with a
/// completed one only after Zendesk confirms it. A retry that finds a pending entry cannot know
/// whether the first attempt landed, so it looks in Zendesk for the record that attempt would have
/// made, and refuses when it cannot tell — a second public reply or a second draft is worse than a
/// refusal that names the parameter for applying or creating again on purpose.
/// </remarks>
internal static class RepeatGuard
{
    /// <summary>
    /// How far before the attempt a lookup reaches, because Zendesk's clock and this server's can
    /// differ. The cost: a matching record made in that window by another call — an <c>apply_again</c>
    /// a minute earlier, or (for articles, matched on title and locale only) a different article of the
    /// same title — is taken for this attempt's, and the retry answers <c>already_…</c> rather than
    /// writing. That errs towards not writing twice, and the answer's warning says the earlier record was found.
    /// </summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>A pending entry for a write about to be sent, and still waiting for its answer.</summary>
    public static RepeatGuardEntry InFlightNow() => new(DateTimeOffset.UtcNow, null, Pending: true, InFlight: true);

    public static RepeatGuardEntry Completed(long? recordId = null) => new(DateTimeOffset.UtcNow, recordId, Pending: false);

    /// <summary>
    /// True when the exception proves the write was not made: it was refused before it was sent
    /// (validation, a permission or feature refusal, no connection), or Zendesk answered with a 4xx.
    /// Anything else — a cancellation, a timeout, a 5xx, a transport failure — leaves the outcome
    /// unknown, and the pending entry stays.
    /// </summary>
    public static bool ProvesNotWritten(Exception ex) => ex switch
    {
        ArgumentException or UnauthorizedAccessException or ZendeskConnectionRequiredException => true,
        HttpRequestException { StatusCode: { } status } => (int)status is >= 400 and < 500 && status != HttpStatusCode.RequestTimeout,
        _ => false,
    };

    /// <summary>The refusal for a retry that arrives while the first call is still waiting for Zendesk.</summary>
    public static ArgumentException StillInFlight(string what, string again) => new(
        $"An earlier attempt to {what} was sent and is still waiting for Zendesk's answer, so it may yet land. Wait a minute and try again; this server then checks whether it did. Pass {again} true only to do it a second time on purpose.");

    /// <summary>A Zendesk timestamp, or null when it is absent or unreadable.</summary>
    public static DateTimeOffset? Timestamp(JsonNode? node) =>
        ToolJson.String(node) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
}
