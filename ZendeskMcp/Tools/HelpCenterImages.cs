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

    // The src attribute of an <img> tag, double- or single-quoted. Group "v" is the raw attribute value.
    [GeneratedRegex("""<img\b[^>]*?\bsrc\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)')""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImgSrc();

    /// <summary>Shared state for one call, so an image used by several translations is downloaded once.</summary>
    public sealed class Session(ZendeskService zendesk, string baseUrl)
    {
        internal ZendeskService Zendesk { get; } = zendesk;
        internal Uri Base { get; } = new(baseUrl.TrimEnd('/') + "/");
        internal Dictionary<string, string?> Resolved { get; } = new(StringComparer.Ordinal);
        internal JsonArray Skipped { get; } = [];
        internal int Embedded { get; set; }

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

        foreach (var raw in matches.Select(m => m.Groups["v"].Value).Distinct(StringComparer.Ordinal))
        {
            if (!session.Resolved.ContainsKey(raw))
            {
                session.Resolved[raw] = await FetchAsync(session, raw, cancellationToken);
            }
        }

        // Replace only inside the src attribute value of the matched tags, so the same text elsewhere
        // in the body (a link, prose) is left alone.
        var builder = new StringBuilder(html.Length);
        var last = 0;
        foreach (Match match in matches)
        {
            var group = match.Groups["v"];
            builder.Append(html, last, group.Index - last);
            builder.Append(session.Resolved.GetValueOrDefault(group.Value) ?? group.Value);
            last = group.Index + group.Length;
        }

        builder.Append(html, last, html.Length - last);
        return builder.ToString();
    }

    private static async Task<string?> FetchAsync(Session session, string raw, CancellationToken cancellationToken)
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

        if (session.Embedded >= MaxImages)
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

        session.Embedded++;
        return $"data:{contentType.ToLowerInvariant()};base64,{Convert.ToBase64String(file.Content)}";
    }

    private static void Skip(Session session, string src, string reason) =>
        session.Skipped.Add(new JsonObject { ["src"] = src, ["reason"] = reason });
}
