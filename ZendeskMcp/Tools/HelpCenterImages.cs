using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZendeskMcp.Tools;

/// <summary>
/// Inlines the images an article body references as base64 <c>data:</c> URIs, for
/// Get_help_center_article's <c>embed_images</c>.
/// </summary>
/// <remarks>
/// ⚠️ Every download goes through <see cref="ZendeskService.DownloadAsync"/>, which sends the caller's
/// Zendesk credential only to this account's own hosts and refuses anything else. An article body is
/// untrusted content — anyone with edit rights can point an <c>&lt;img&gt;</c> at any host — so an image
/// elsewhere is skipped and reported, never fetched, and never fetched some other way without the
/// credential either (that would make this server a request proxy for article authors).
/// </remarks>
public static partial class HelpCenterImages
{
    /// <summary>At most this many distinct images are inlined per call, so one article cannot balloon the result.</summary>
    public const int MaxImages = 25;

    /// <summary>Largest single image inlined. Bigger ones are skipped and listed, with their URL.</summary>
    public const long MaxImageBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Total image bytes inlined per call, counted at every place an image is inlined rather than once
    /// per download. A base64 copy of every occurrence sits in the result, and the result is held and
    /// serialised in memory on the single replica; without a total, an article of large images — or one
    /// image referenced thousands of times — could make one call cost gigabytes.
    /// </summary>
    public const long MaxTotalBytes = 8 * 1024 * 1024;

    // The src attribute of an <img> tag, double- or single-quoted. Group "v" is the raw attribute value.
    [GeneratedRegex("""<img\b[^>]*?\bsrc\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)')""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImgSrc();

    /// <summary>Shared state for one call, so an image used by several translations is downloaded once.</summary>
    public sealed class Session(ZendeskService zendesk, string baseUrl)
    {
        internal ZendeskService Zendesk { get; } = zendesk;
        internal Uri Base { get; } = new(baseUrl.TrimEnd('/') + "/");
        internal Dictionary<string, Inlined?> Resolved { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> InlinedSources { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> OverBudget { get; } = new(StringComparer.Ordinal);
        internal JsonArray Skipped { get; } = [];
        internal int Embedded { get; set; }
        internal int Downloaded { get; set; }
        internal long BytesEmbedded { get; set; }

        /// <summary>Images that were not inlined, each with the src and the reason.</summary>
        public JsonArray SkippedImages => Skipped;

        /// <summary>How many distinct images were inlined.</summary>
        public int EmbeddedCount => Embedded;
    }

    /// <summary>Returns <paramref name="html"/> with every image it can fetch inlined.</summary>
    public static async Task<string?> EmbedAsync(Session session, string? html, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html;
        }

        var matches = ImgSrc().Matches(html);
        if (matches.Count == 0)
        {
            return html;
        }

        // Replace only inside the src attribute value of the matched tags, so the same text elsewhere
        // in the body (a link, prose) is left alone. Each download is cached, but every occurrence is
        // charged to the byte budget: a cached image repeated a thousand times is a thousand base64
        // copies in the result. An occurrence past the budget keeps its URL.
        var builder = new StringBuilder(html.Length);
        var last = 0;
        foreach (Match match in matches)
        {
            var group = match.Groups["v"];
            builder.Append(html, last, group.Index - last);
            builder.Append(await InlineAsync(session, group.Value, cancellationToken) ?? group.Value);
            last = group.Index + group.Length;
        }

        builder.Append(html, last, html.Length - last);
        return builder.ToString();
    }

    private static async Task<string?> InlineAsync(Session session, string raw, CancellationToken cancellationToken)
    {
        if (!session.Resolved.TryGetValue(raw, out var image))
        {
            image = await FetchAsync(session, raw, cancellationToken);
            session.Resolved[raw] = image;
        }

        if (image is null)
        {
            return null;
        }

        if (session.BytesEmbedded + image.Bytes > MaxTotalBytes)
        {
            // Once per src, however many occurrences are left as URLs.
            if (session.OverBudget.Add(raw))
            {
                Skip(session, raw, $"the {MaxTotalBytes}-byte total for inlined images in one call is used up; open it from its URL instead");
            }

            return null;
        }

        session.BytesEmbedded += image.Bytes;
        if (session.InlinedSources.Add(raw))
        {
            session.Embedded++;
        }

        return image.DataUri;
    }

    private static async Task<Inlined?> FetchAsync(Session session, string raw, CancellationToken cancellationToken)
    {
        var src = WebUtility.HtmlDecode(raw).Trim();
        if (src.Length == 0 || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Uri.TryCreate(session.Base, src, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Skip(session, raw, "not an https URL");
            return null;
        }

        if (session.Downloaded >= MaxImages)
        {
            Skip(session, raw, $"more than {MaxImages} images; only the first {MaxImages} are embedded");
            return null;
        }

        DownloadedFile file;
        try
        {
            file = await session.Zendesk.DownloadAsync(uri.AbsoluteUri, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            // Refused host, or over the size limit: both are the guard working, not a failure.
            Skip(session, raw, ex.Message);
            return null;
        }
        catch (HttpRequestException ex)
        {
            Skip(session, raw, ex.Message);
            return null;
        }

        var contentType = file.ContentType;
        if (contentType is null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            Skip(session, raw, $"not an image (content type '{contentType ?? "unknown"}')");
            return null;
        }

        if (file.Content.LongLength > MaxImageBytes)
        {
            Skip(session, raw, $"larger than {MaxImageBytes} bytes; open it from its URL instead");
            return null;
        }

        session.Downloaded++;

        // The budget only grows, so an image that does not fit now never will: refuse it before
        // encoding, so its base64 copy is not built and then held in Resolved for nothing.
        if (session.BytesEmbedded + file.Content.LongLength > MaxTotalBytes)
        {
            session.OverBudget.Add(raw);
            Skip(session, raw, $"the {MaxTotalBytes}-byte total for inlined images in one call is used up; open it from its URL instead");
            return null;
        }

        return new Inlined($"data:{contentType.ToLowerInvariant()};base64,{Convert.ToBase64String(file.Content)}", file.Content.LongLength);
    }

    /// <summary>A downloaded image ready to inline, and its size in bytes before base64.</summary>
    internal sealed record Inlined(string DataUri, long Bytes);

    private static void Skip(Session session, string src, string reason) =>
        session.Skipped.Add(new JsonObject { ["src"] = src, ["reason"] = reason });
}
