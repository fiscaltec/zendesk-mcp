using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ZendeskMcp;

/// <summary>
/// The one path every Zendesk API call takes. Each call is authorised against the caller's tier
/// (by HTTP verb, as in <see cref="ToolAuthorizer"/>), made with the caller's own Zendesk credential
/// (<see cref="ZendeskCredentialProvider"/>), and recorded in the audit trail with the record ids it
/// returned. Tools build requests and shape results; they never talk to Zendesk any other way.
/// </summary>
public partial class ZendeskService
{
    private readonly HttpClient _httpClient;
    private readonly ZendeskServerOptions _options;
    private readonly ZendeskCredentialProvider _credentials;
    private readonly ToolAuthorizer _authorizer;
    private readonly AuditLogger _audit;
    private readonly ToolCallAuditContext? _auditContext;
    private readonly ILogger<ZendeskService> _logger;
    private readonly ZendeskMetrics? _metrics;
    private readonly IMemoryCache? _cache;
    private readonly string _baseUrl;

    public ZendeskService(
        HttpClient httpClient,
        IOptions<ZendeskServerOptions> options,
        ZendeskCredentialProvider credentials,
        ToolAuthorizer authorizer,
        AuditLogger audit,
        ToolCallAuditContext? auditContext = null,
        ILogger<ZendeskService>? logger = null,
        ZendeskMetrics? metrics = null,
        IMemoryCache? cache = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _credentials = credentials;
        _authorizer = authorizer;
        _audit = audit;
        _auditContext = auditContext;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ZendeskService>.Instance;
        _metrics = metrics;
        _cache = cache;
        _baseUrl = _options.BaseUrl;
    }

    /// <summary>The validated Zendesk options, for tools that read the feature switches and limits.</summary>
    public ZendeskServerOptions Options => _options;

    /// <summary>The credential provider, for the connection tools.</summary>
    public ZendeskCredentialProvider Credentials => _credentials;

    /// <summary>The account's own origin, e.g. <c>https://fiscaltec.zendesk.com</c>.</summary>
    public string BaseUrl => _baseUrl;

    public Task<JsonNode> GetAsync(string path, IEnumerable<KeyValuePair<string, string?>>? query = null, string? brandUrl = null, CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Get, path, query, body: null, brandUrl, headers: null, cancellationToken);

    public Task<JsonNode> PostAsync(string path, JsonNode? body, IEnumerable<KeyValuePair<string, string?>>? query = null, string? brandUrl = null, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Post, path, query, body, brandUrl, headers, cancellationToken);

    public Task<JsonNode> PutAsync(string path, JsonNode? body, IEnumerable<KeyValuePair<string, string?>>? query = null, string? brandUrl = null, CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Put, path, query, body, brandUrl, headers: null, cancellationToken);

    public Task<JsonNode> DeleteAsync(string path, IEnumerable<KeyValuePair<string, string?>>? query = null, string? brandUrl = null, CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Delete, path, query, body: null, brandUrl, headers: null, cancellationToken);

    /// <summary>
    /// POSTs raw bytes (Zendesk's <c>/api/v2/uploads</c> takes the file as the body, not JSON).
    /// </summary>
    public Task<JsonNode> PostBinaryAsync(string path, IEnumerable<KeyValuePair<string, string?>>? query, byte[] content, string contentType, CancellationToken cancellationToken = default) =>
        PostBinaryAsync(path, query, new ArraySegment<byte>(content), contentType, cancellationToken);

    /// <summary>As above, for part of a buffer, so a caller need not copy what it has already read.</summary>
    public async Task<JsonNode> PostBinaryAsync(string path, IEnumerable<KeyValuePair<string, string?>>? query, ArraySegment<byte> content, string contentType, CancellationToken cancellationToken = default)
    {
        var url = BuildUrl(path, query, baseUrl: null);
        using var httpContent = new ByteArrayContent(content.Array!, content.Offset, content.Count);
        httpContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        var body = await SendAsync(HttpMethod.Post, url, httpContent, headers: null, cancellationToken);
        return ParseOrEmpty(body);
    }

    /// <summary>
    /// Follows Zendesk cursor pagination (<c>page[size]</c>/<c>page[after]</c>, answered with
    /// <c>meta.has_more</c> and <c>meta.after_cursor</c>) and returns the items of
    /// <paramref name="arrayProperty"/> across pages, up to <paramref name="maxItems"/> or
    /// <see cref="ZendeskServerOptions.MaxAutoPageFetches"/> pages, whichever comes first.
    /// </summary>
    /// <remarks>
    /// Some endpoints ignore <c>page[size]</c> and answer with offset pagination instead
    /// (<c>/api/v2/guide/permission_groups</c> does, 30 a page). When a page carries no <c>meta</c>
    /// but a <c>next_page</c> URL, its <c>page</c> and <c>per_page</c> values are followed; only those
    /// two integers are taken from it, never its host or path.
    /// </remarks>
    public async Task<PagedItems> GetAllPagesAsync(string path, IEnumerable<KeyValuePair<string, string?>>? query, string arrayProperty, int maxItems, string? brandUrl = null, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        var items = new List<JsonNode>();
        var baseQuery = (query ?? []).Where(kv => kv.Key is not "page[size]" and not "page[after]" and not "page" and not "per_page").ToList();
        string? after = null;
        (string Page, string PerPage)? offset = null;
        var pages = 0;
        var hasMore = false;
        var sideloads = new JsonObject();

        while (true)
        {
            var pageQuery = new List<KeyValuePair<string, string?>>(baseQuery);
            if (offset is { } next)
            {
                pageQuery.Add(new("page", next.Page));
                pageQuery.Add(new("per_page", next.PerPage));
            }
            else
            {
                pageQuery.Add(new("page[size]", pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                if (after is not null)
                {
                    pageQuery.Add(new("page[after]", after));
                }
            }

            var page = await GetAsync(path, pageQuery, brandUrl, cancellationToken);
            pages++;

            if (page[arrayProperty] is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is not null && items.Count < maxItems)
                    {
                        items.Add(item.DeepClone());
                    }
                }
            }

            // Sideloaded collections (users, organizations, groups...) arrive beside the main array on
            // every page; merge them so a caller paging tickets can still resolve every requester.
            if (page is JsonObject pageObject)
            {
                foreach (var (name, value) in pageObject)
                {
                    if (name == arrayProperty || name is "meta" or "links" || value is not JsonArray sideArray)
                    {
                        continue;
                    }

                    if (sideloads[name] is not JsonArray merged)
                    {
                        merged = new JsonArray();
                        sideloads[name] = merged;
                    }

                    foreach (var element in sideArray)
                    {
                        merged.Add(element?.DeepClone());
                    }
                }
            }

            if (page["meta"] is null && OffsetNextPage(page["next_page"]) is { } nextOffset)
            {
                offset = nextOffset;
                hasMore = true;
                after = null;
            }
            else
            {
                offset = null;
                hasMore = page["meta"]?["has_more"]?.GetValue<bool>() == true;
                after = page["meta"]?["after_cursor"]?.GetValue<string>();
            }

            if (!hasMore || (offset is null && string.IsNullOrEmpty(after)) || items.Count >= maxItems)
            {
                break;
            }

            if (pages >= _options.MaxAutoPageFetches)
            {
                _auditContext?.MarkPagerTruncated();
                _metrics?.PagerTruncated(ResourceTypeOf(BuildUrl(path, null, null)));
                break;
            }
        }

        return new PagedItems(items, hasMore ? after : null, hasMore, pages, sideloads);
    }

    /// <summary>The <c>page</c> and <c>per_page</c> of an offset-paginated <c>next_page</c> URL, or null.</summary>
    private static (string Page, string PerPage)? OffsetNextPage(JsonNode? nextPage)
    {
        if (nextPage is not JsonValue value || !value.TryGetValue<string>(out var url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var parameters = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return int.TryParse(parameters["page"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var page)
               && int.TryParse(parameters["per_page"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var perPage)
            ? (page.ToString(System.Globalization.CultureInfo.InvariantCulture), perPage.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>
    /// Downloads a file from Zendesk — an attachment's <c>content_url</c>, or an image in an article.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three rules keep a URL taken from ticket or article content from carrying the caller's token
    /// anywhere it should not go:
    /// </para>
    /// <list type="number">
    ///   <item>The host must be the account's own or one of its brands'. A brand's <i>host mapping</i>
    ///     (a custom domain an admin points at the Help Center) is accepted as an address but never
    ///     sent the credential: the request is rewritten to that brand's <c>{subdomain}.zendesk.com</c>
    ///     host, the only hosts Zendesk itself controls. A mapping can name any domain, and a stale or
    ///     malicious one would otherwise receive every reader's token.</item>
    ///   <item>No <c>/api/</c> path: this fetches files, and an image <c>src</c> in an article must not
    ///     be able to make each reader's token issue API calls.</item>
    ///   <item>Redirects are followed here, not by the handler (which is built with
    ///     <c>AllowAutoRedirect = false</c>): at most three, https only, to an account host (credential
    ///     re-sent) or Zendesk's attachment CDN <c>*.zdusercontent.com</c> (no credential — its URLs are
    ///     signed). Anything else is refused.</item>
    /// </list>
    /// </remarks>
    public async Task<DownloadedFile> DownloadAsync(string absoluteUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The file URL is not an absolute https URL.");
        }

        var target = await CredentialTargetAsync(uri, cancellationToken)
            ?? throw new ArgumentException($"Refusing to download from '{uri.Host}': it is not this Zendesk account's host.");

        await AuthorizeAsync(HttpMethod.Get, absoluteUrl, cancellationToken);
        var authorization = await _credentials.GetAuthorizationAsync(cancellationToken);

        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target.Uri);
            if (target.SendCredential)
            {
                request.Headers.Authorization = authorization;
            }

            var started = Stopwatch.GetTimestamp();
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            _audit.LogAction(HttpMethod.Get, target.Uri.GetLeftPart(UriPartial.Path), (int)response.StatusCode, _auditContext?.CorrelationId, Stopwatch.GetElapsedTime(started));

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                if (hop >= 3)
                {
                    throw new HttpRequestException("Zendesk redirected the download too many times.");
                }

                var next = location.IsAbsoluteUri ? location : new Uri(target.Uri, location);
                target = await RedirectTargetAsync(next, cancellationToken)
                    ?? throw new ArgumentException($"Refusing to follow a redirect to '{next.Host}': it is neither this Zendesk account's host nor its attachment CDN.");
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Zendesk returned {(int)response.StatusCode} {response.ReasonPhrase} for the file.",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            if (response.Content.Headers.ContentLength is { } length && length > _options.MaxAttachmentBytes)
            {
                throw new ArgumentException($"The file is {length} bytes; the limit is {_options.MaxAttachmentBytes}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > _options.MaxAttachmentBytes)
                {
                    throw new ArgumentException($"The file is larger than the {_options.MaxAttachmentBytes}-byte limit.");
                }
            }

            return new DownloadedFile(buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
        }
    }

    /// <summary>
    /// Resolves an optional <c>brand_url</c> argument to a base URL to call, accepting only the
    /// account's own brands. Null or empty means the default brand.
    /// </summary>
    /// <remarks>
    /// ⚠️ This is a credential boundary, not input tidying. The base URL decides where the caller's
    /// Zendesk token is sent, and a <c>brand_url</c> can come from a model that has just read untrusted
    /// ticket or article content. Anything that is not a host <c>/api/v2/brands</c> names is refused,
    /// and a brand's host mapping is translated to that brand's <c>{subdomain}.zendesk.com</c> host —
    /// the credential only ever goes to a host Zendesk controls (see <see cref="DownloadAsync"/>).
    /// </remarks>
    public async Task<string?> ResolveBrandBaseUrlAsync(string? brandUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(brandUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(brandUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"brand_url must be an https URL such as 'https://{_options.Subdomain}.zendesk.com' (got '{brandUrl}').");
        }

        var host = await CanonicalHostAsync(uri.Host, cancellationToken)
            ?? throw new ArgumentException($"brand_url '{uri.Host}' is not one of this Zendesk account's brands. Use List_brands to see them.");
        return $"https://{host}";
    }

    /// <summary>Builds an absolute URL from an <c>/api/v2/...</c> path and query, escaping every query value.</summary>
    public string BuildUrl(string path, IEnumerable<KeyValuePair<string, string?>>? query, string? baseUrl)
    {
        if (!path.StartsWith('/'))
        {
            throw new ArgumentException("Zendesk paths must start with '/'.", nameof(path));
        }

        var url = (baseUrl ?? _baseUrl) + path;
        var pairs = (query ?? [])
            .Where(kv => kv.Value is not null)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}")
            .ToList();
        return pairs.Count == 0 ? url : $"{url}?{string.Join("&", pairs)}";
    }

    /// <summary>Escapes one caller-supplied path segment (a locale, a key) so it cannot change the path.</summary>
    public static string Segment(string value) => Uri.EscapeDataString(value);

    private async Task<JsonNode> SendJsonAsync(HttpMethod method, string path, IEnumerable<KeyValuePair<string, string?>>? query, JsonNode? body, string? brandUrl, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        var baseUrl = await ResolveBrandBaseUrlAsync(brandUrl, cancellationToken);
        var url = BuildUrl(path, query, baseUrl);
        using var content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var json = await SendAsync(method, url, content, headers, cancellationToken);
        return ParseOrEmpty(json);
    }

    private async Task AuthorizeAsync(HttpMethod method, string url, CancellationToken cancellationToken)
    {
        // Server-side RBAC backstop: every Zendesk call passes through here, so checking the caller's
        // permission against the HTTP verb covers every tool in one place, including the ones whose
        // [Authorize] attribute names the wrong tier by mistake. A denied attempt is itself worth
        // recording in the audit trail.
        try
        {
            await _authorizer.EnsureAuthorizedAsync(method, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            _audit.LogDenied(method, url, _auditContext?.CorrelationId);
            throw;
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string url, HttpContent? content, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(method, url, cancellationToken);

        var authorization = await _credentials.GetAuthorizationAsync(cancellationToken);
        HttpResponseMessage response;
        string body;
        for (var attempt = 1; ; attempt++)
        {
            // A fresh message per attempt (a request message cannot be sent twice); the content is
            // buffered and HttpClient leaves it undisposed, so it can ride the retry. Disposing a
            // message disposes its content, so the content is detached before each message goes.
            using var request = new DetachingRequest(method, url);
            if (content is not null)
            {
                request.Content = content;
            }

            if (headers is not null)
            {
                foreach (var (name, value) in headers)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            request.Headers.Authorization = authorization;

            // Zendesk refuses a path without a .json suffix with 415 unless the request asks for JSON.
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            // Timed from send to body read, so the upstream record carries the duration that lets a slow
            // composite tool be decomposed into its calls. That includes any 429 waits inside the
            // rate-limit handler, deliberately: they are part of what the caller waited for.
            var started = Stopwatch.GetTimestamp();
            response = await _httpClient.SendAsync(request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(started);

            _audit.LogAction(method, url, (int)response.StatusCode, _auditContext?.CorrelationId, elapsed);

            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                break;
            }

            response.Dispose();

            // The caller's token was refused. The provider decides what that means for THIS token —
            // it may already have been replaced by a refresh or a reconnect, or a refresh may rescue it —
            // and either hands back a credential worth one retry or throws with a connect link.
            authorization = await _credentials.OnTokenRejectedAsync(authorization, allowRecovery: attempt == 1, cancellationToken);
        }

        using var _ = response;

        if (!response.IsSuccessStatusCode)
        {
            // Status and resource TYPE only, never the body or the rest of the path: Zendesk's error
            // bodies can quote the record that failed validation, and paths carry caller text. The
            // correlation id joins this line to the audit records, which is where the detail lives.
            LogBestEffort(() => _logger.Log(ToolCallFailureLog.LevelFor(response.StatusCode),
                "Zendesk upstream call failed: {Method} {ResourceType} returned {StatusCode} correlation={CorrelationId}",
                method.Method, ResourceTypeOf(url), (int)response.StatusCode,
                _auditContext?.CorrelationId ?? "none"));

            // Zendesk explains a refusal in the body ({"error":"RecordInvalid","details":{...}}), and
            // that explanation is what the model needs to correct its next call.
            var bodySnippet = string.IsNullOrWhiteSpace(body) ? "(empty body)" : Truncate(body, 1024);
            throw new HttpRequestException(
                $"Zendesk returned {(int)response.StatusCode} {response.ReasonPhrase}. Body: {bodySnippet}",
                inner: null,
                statusCode: response.StatusCode);
        }

        // Capture the records touched HERE, from the raw body, because tools reshape it afterwards and
        // a narrowed result may no longer carry the ids.
        try
        {
            var touched = AuditRecordIds.Extract(body);
            if (touched.IdsRecorded == 0)
            {
                touched = AuditRecordIds.FromMutationUrl(method, url);
            }

            _auditContext?.RecordUpstream(touched);
        }
        catch (Exception)
        {
            // The audit sidecar must never be the reason a call fails.
        }

        return body;
    }

    /// <summary>
    /// The host a request for <paramref name="host"/> may carry the caller's credential to: the account's
    /// own host, a brand's <c>{subdomain}.zendesk.com</c> host, or — for a brand's host mapping — that
    /// brand's <c>{subdomain}.zendesk.com</c> host instead. Null for anything else.
    /// </summary>
    private async Task<string?> CanonicalHostAsync(string host, CancellationToken cancellationToken)
    {
        var ownHost = new Uri(_baseUrl).Host;
        if (string.Equals(host, ownHost, StringComparison.OrdinalIgnoreCase))
        {
            return ownHost;
        }

        var brandHosts = await GetBrandHostsAsync(cancellationToken);
        return brandHosts.TryGetValue(host, out var canonical) ? canonical : null;
    }

    /// <summary>
    /// A request message whose disposal leaves its content alone, so the same buffered body can be
    /// sent again after a 401 (a plain <see cref="HttpRequestMessage"/> disposes its content, and the
    /// retry would throw <see cref="ObjectDisposedException"/>).
    /// </summary>
    private sealed class DetachingRequest(HttpMethod method, string url) : HttpRequestMessage(method, url)
    {
        protected override void Dispose(bool disposing)
        {
            Content = null;
            base.Dispose(disposing);
        }
    }

    // True for any spelling of an API path a server might route as one: "/api/…", "//api/…",
    // "/%2Fapi/…" and "/api%2F…". Uri already resolves dot segments and unescapes "%61pi".
    internal static bool IsApiPath(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath).Replace('\\', '/');
        while (path.Contains("//", StringComparison.Ordinal))
        {
            path = path.Replace("//", "/", StringComparison.Ordinal);
        }

        return path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<DownloadTarget?> CredentialTargetAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (IsApiPath(uri))
        {
            throw new ArgumentException("Refusing to download an API path; only files can be downloaded.");
        }

        var canonical = await CanonicalHostAsync(uri.Host, cancellationToken);
        if (canonical is null)
        {
            return null;
        }

        var rewritten = new UriBuilder(uri) { Host = canonical, Port = -1 }.Uri;
        return new DownloadTarget(rewritten, SendCredential: true);
    }

    private async Task<DownloadTarget?> RedirectTargetAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        if (uri.Host.EndsWith(".zdusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return new DownloadTarget(uri, SendCredential: false);
        }

        return await CredentialTargetAsync(uri, cancellationToken);
    }

    private sealed record DownloadTarget(Uri Uri, bool SendCredential);

    // Every host the account's brands answer on, mapped to the host the credential may go to: a brand's
    // own {subdomain}.zendesk.com maps to itself, and its host mapping maps to that same
    // {subdomain}.zendesk.com. Only *.zendesk.com hosts are ever credential destinations. Cached for ten
    // minutes across callers, because the list is account configuration rather than anything personal.
    // Live, a brand with a host mapping reports that mapped host as its brand_url, so the Zendesk host
    // comes from the brand's subdomain, and brand_url is only an alias for it.
    private async Task<Dictionary<string, string>> GetBrandHostsAsync(CancellationToken cancellationToken)
    {
        const string cacheKey = "zendesk-brand-hosts";
        if (_cache is not null && _cache.TryGetValue<Dictionary<string, string>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var brands = await GetAllPagesAsync("/api/v2/brands", null, "brands", maxItems: 1000, cancellationToken: cancellationToken);
        foreach (var brand in brands.Items)
        {
            var brandUrl = brand?["brand_url"] is JsonValue v && v.TryGetValue<string>(out var u) ? u : null;
            var brandHost = Uri.TryCreate(brandUrl, UriKind.Absolute, out var parsed) ? parsed.Host : null;
            var subdomain = brand?["subdomain"] is JsonValue s && s.TryGetValue<string>(out var sub) ? sub : null;
            var zendeskHost = subdomain is not null && SubdomainPattern().IsMatch(subdomain)
                ? $"{subdomain}.zendesk.com"
                : brandHost is not null && brandHost.EndsWith(".zendesk.com", StringComparison.OrdinalIgnoreCase) ? brandHost : null;
            if (zendeskHost is null)
            {
                continue;
            }

            hosts[zendeskHost] = zendeskHost;
            if (brandHost is not null)
            {
                hosts[brandHost] = zendeskHost;
            }

            var mapping = brand?["host_mapping"] is JsonValue m && m.TryGetValue<string>(out var mapped) ? mapped : null;
            if (!string.IsNullOrWhiteSpace(mapping))
            {
                hosts[mapping.Trim()] = zendeskHost;
            }
        }

        _cache?.Set(cacheKey, hosts, TimeSpan.FromMinutes(10));
        return hosts;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SubdomainPattern();

    // The first path segments this code builds, and so the only ones safe to put on the console.
    private static readonly HashSet<string> KnownResourceTypes = new(StringComparer.Ordinal)
    {
        "attachments", "brands", "custom_statuses", "dynamic_content", "groups", "guide", "help_center",
        "incremental", "macros", "oauth", "organization_memberships", "organizations", "satisfaction_ratings",
        "search", "slas", "ticket_fields", "ticket_forms", "ticket_metrics", "tickets", "triggers", "uploads",
        "users",
    };

    // `/api/v2/tickets/<id>/comments` → `/api/v2/tickets/…`. Only the first segment after /api/v2/ is
    // fixed by this code rather than by a caller, so it is the only one safe to log to the console.
    // Read from the RAW string and checked against the allowlist, never from a parsed Uri (which would
    // normalise dot-segments first); anything else reads `unrecognised`, failing closed.
    private string ResourceTypeOf(string url)
    {
        var marker = url.IndexOf("/api/v2/", StringComparison.Ordinal);
        if (marker < 0)
        {
            return "/api/v2/(unrecognised)";
        }

        var rest = url.AsSpan(marker + "/api/v2/".Length);
        var end = rest.IndexOfAny('/', '?', '\\');
        var segment = (end < 0 ? rest : rest[..end]).ToString();
        if (segment.EndsWith(".json", StringComparison.Ordinal))
        {
            segment = segment[..^5];
        }

        if (!KnownResourceTypes.Contains(segment))
        {
            return "/api/v2/(unrecognised)";
        }

        return end >= 0 && rest[end] == '/' ? $"/api/v2/{segment}/…" : $"/api/v2/{segment}";
    }

    private static JsonNode ParseOrEmpty(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json) ?? new JsonObject();
        }
        catch (JsonException)
        {
            throw new HttpRequestException("Zendesk returned a response that is not JSON.");
        }
    }

    // A failure log must never change what the call returns: it sits just before the exception that
    // carries Zendesk's explanation to the client.
    private static void LogBestEffort(Action write)
    {
        try
        {
            write();
        }
        catch (Exception)
        {
            // Deliberately ignored.
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…(truncated)";
}

/// <summary>Items gathered across pages, with the cursor to continue from when the pager stopped early.</summary>
public sealed record PagedItems(IReadOnlyList<JsonNode> Items, string? NextCursor, bool HasMore, int PagesFetched, JsonObject Sideloads);

/// <summary>A downloaded file.</summary>
public sealed record DownloadedFile(byte[] Content, string? ContentType);
