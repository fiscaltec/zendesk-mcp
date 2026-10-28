using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Tools;

/// <summary>
/// Adding images to the Help Center media library in two steps: a pre-signed upload URL the client
/// PUTs the bytes to directly, then registering the upload as a media item.
/// </summary>
/// <remarks>
/// The image bytes never pass through this server, and this server never calls the pre-signed URL:
/// it is not a Zendesk API host, and a pre-signed URL needs no credential, so sending the agent's
/// token there would only leak it.
/// </remarks>
[McpServerToolType]
public static class HelpCenterMediaTools
{
    /// <summary>The largest image Zendesk's media library accepts, 20 MB.</summary>
    public const long MaxImageBytes = 20_971_520;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.Ordinal)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/svg+xml",
    };

    // Request_ tools are writes; Idempotent=false because each call mints a new upload slot.
    [Authorize(Policy = "zendesk:write")]
    [McpServerTool(Name = "Request_help_center_image_upload", Title = "Request Help Center image upload", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Step 1 of 2 for adding an image (for example a screenshot) to Help Center articles. Returns a pre-signed upload URL with the exact headers it needs. " +
        "The client then uploads the file's bytes straight to that URL with an HTTP PUT (for example by running the returned upload_command with curl, replacing <path> with the local file's path), so the image never passes through this server. The URL expires after about one hour. " +
        "Then call Create_help_center_image with the returned asset_upload_id. Images up to 20 MB; PNG, JPEG, GIF, WebP or SVG. " +
        "For multi-brand accounts, pass brand_url to upload to a non-default brand's media library. " + HelpCenterJson.UntrustedContentWarning)]
    public static async Task<string> RequestHelpCenterImageUpload(
        ZendeskService zendesk,
        [Description("File name the image will have in the media library, e.g. 'connect-step-2.png'. Max 255 characters.")] string filename,
        [Description("MIME type of the file: 'image/png', 'image/jpeg', 'image/gif', 'image/webp' or 'image/svg+xml'. Must match the file; it is sent again as a header on the upload.")] string content_type,
        [Description("Exact size of the file in bytes. Max 20 MB (20,971,520).")] long file_size,
        [Description(HelpCenterJson.BrandUrlDescription)] string? brand_url = null,
        CancellationToken cancellationToken = default)
    {
        var validFilename = Filename(filename);
        if (!AllowedContentTypes.Contains(content_type ?? string.Empty))
        {
            throw new ArgumentException($"content_type must be one of {string.Join(", ", AllowedContentTypes)} (got '{content_type}').");
        }

        if (file_size is < 1 or > MaxImageBytes)
        {
            throw new ArgumentException($"file_size must be between 1 and {MaxImageBytes} bytes (got {file_size}).");
        }

        // Unverified against the live API (docs-checked, not live-tested): the request body
        // ({content_type, file_size}) matches the documentation, but the documentation gives the
        // response two ways: its prose puts headers beside upload_url as an object, its example puts
        // them inside upload_url as a JSON-encoded string. Both are read, and an upload whose headers
        // cannot be read is refused rather than handed out without them (the URL is signed over them).
        var response = await zendesk.PostAsync(
            "/api/v2/guide/medias/upload_url",
            new JsonObject { ["content_type"] = content_type, ["file_size"] = file_size },
            brandUrl: brand_url,
            cancellationToken: cancellationToken);

        var upload = response["upload_url"] ?? throw new HttpRequestException("Zendesk returned no upload_url.");
        var assetUploadId = ToolJson.String(upload["asset_upload_id"]) ?? throw new HttpRequestException("Zendesk returned no asset_upload_id.");
        var url = ToolJson.String(upload["url"]) ?? throw new HttpRequestException("Zendesk returned no upload URL.");

        var headers = new JsonObject();
        var command = new StringBuilder("curl -sS -X PUT");
        var given = UploadHeaders(upload["headers"] ?? response["headers"]);
        if (given is not null)
        {
            foreach (var (name, value) in given)
            {
                var text = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? string.Empty;
                headers[name] = text;
                command.Append(" -H ").Append(ShellQuote($"{name}: {text}"));
            }
        }

        // <path> is a placeholder the client fills in; the caller's filename is deliberately not put
        // into a command line someone will run.
        command.Append(" --data-binary @\"<path>\" ").Append(ShellQuote(url));

        return new JsonObject
        {
            ["asset_upload_id"] = assetUploadId,
            ["filename"] = validFilename,
            ["upload_url"] = url,
            ["method"] = "PUT",
            ["headers"] = headers,
            ["expires"] = "about 1 hour after this call",
            ["upload_command"] = command.ToString(),
            ["next_step"] = $"Upload the file's exact bytes with an HTTP PUT to upload_url, sending every header listed (for example run upload_command with <path> replaced by the local path of '{validFilename}'). Then call Create_help_center_image with asset_upload_id '{assetUploadId}' and filename '{validFilename}'" + (string.IsNullOrWhiteSpace(brand_url) ? "." : ", and the same brand_url."),
        }.ToJsonString();
    }

    [Authorize(Policy = "zendesk:write")]
    [McpServerTool(Name = "Create_help_center_image", Title = "Create Help Center image", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Step 2 of 2: after the file has been uploaded to the URL returned by Request_help_center_image_upload, add it to the Help Center media library. " +
        "Returns the image id, its URL and a ready-made <img> tag to place in an article body with Create_help_center_article or Update_article_translation. Zendesk attaches the image to the article once the body references it. " +
        "Use the same brand_url as in step 1. " + HelpCenterJson.UntrustedContentWarning)]
    public static async Task<string> CreateHelpCenterImage(
        ZendeskService zendesk,
        [Description("The asset_upload_id returned by Request_help_center_image_upload. Max 200 characters.")] string asset_upload_id,
        [Description("File name for the image in the media library, usually the same as in step 1. Max 255 characters.")] string filename,
        [Description("Alt text for the returned <img> tag. Max 500 characters. Defaults to the file name.")] string? alt_text = null,
        [Description(HelpCenterJson.BrandUrlDescription)] string? brand_url = null,
        CancellationToken cancellationToken = default)
    {
        var uploadId = HelpCenterJson.RequiredText(asset_upload_id, "asset_upload_id", 200).Trim();
        var validFilename = Filename(filename);
        HelpCenterJson.Text(alt_text, "alt_text", 500);
        var alt = string.IsNullOrWhiteSpace(alt_text) ? validFilename : alt_text;

        // Unverified against the live API (docs-checked, not live-tested): the request body
        // {asset_upload_id, filename} and the answer {media: {id, url, name, content_type, size, ...}}
        // match the documentation.
        var response = await zendesk.PostAsync(
            "/api/v2/guide/medias",
            new JsonObject { ["asset_upload_id"] = uploadId, ["filename"] = validFilename },
            brandUrl: brand_url,
            cancellationToken: cancellationToken);

        var media = response["media"] ?? response;
        var url = ToolJson.String(media["url"]);
        var result = new JsonObject
        {
            ["id"] = media["id"]?.DeepClone(),
            ["url"] = url,
            ["filename"] = media["name"]?.DeepClone() ?? validFilename,
            ["content_type"] = media["content_type"]?.DeepClone(),
            ["size"] = media["size"]?.DeepClone(),
            ["img_tag"] = url is null ? null : $"<img src=\"{WebUtility.HtmlEncode(url)}\" alt=\"{WebUtility.HtmlEncode(alt)}\">",
        };
        return result.ToJsonString();
    }

    /// <summary>The upload headers as an object, whether Zendesk sent an object or a JSON-encoded string.</summary>
    private static JsonObject? UploadHeaders(JsonNode? headers)
    {
        switch (headers)
        {
            case null:
                return null;
            case JsonObject given:
                return given;
            case JsonValue value when value.TryGetValue<string>(out var text):
                try
                {
                    if (JsonNode.Parse(text) is JsonObject parsed)
                    {
                        return parsed;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Falls through to the refusal below.
                }

                break;
        }

        throw new HttpRequestException("Zendesk returned the upload headers in a form this server cannot read, so the upload URL cannot be used.");
    }

    private static string Filename(string? filename)
    {
        var value = HelpCenterJson.RequiredText(filename, "filename", HelpCenterJson.MaxNameLength).Trim();
        if (value.Any(char.IsControl) || value.Contains('/') || value.Contains('\\'))
        {
            throw new ArgumentException("filename must be a plain file name without path separators or control characters.");
        }

        return value;
    }

    // POSIX single-quoting: the only character that needs care inside single quotes is the quote itself.
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
