using System.Net;
using Microsoft.Extensions.Logging;

namespace ZendeskMcp;

/// <summary>
/// DelegatingHandler that makes the HTTP pipeline rate-limit-aware against the Zendesk API:
///   - Logs a warning when the X-Rate-Limit-Remaining header drops below LowRemainingThreshold.
///   - On HTTP 429 Too Many Requests, waits for the Retry-After hint (or an X-RateLimit-Reset
///     timestamp, kept for any endpoint that sends one), capped at MaxRetryDelay, and retries up to
///     MaxRetries times.
/// Zendesk's limit is per account and per plan (from 200 to 2500 requests a minute), shared by every
/// integration on the account — so this server running hot can starve other integrations too, which
/// is why the low-remaining warning matters as much as the retry.
/// </summary>
public class ZendeskRateLimitHandler : DelegatingHandler
{
    public int MaxRetries { get; set; } = 3;
    public int LowRemainingThreshold { get; set; } = 50;
    public TimeSpan FallbackRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(60);

    private readonly ILogger<ZendeskRateLimitHandler>? _logger;
    private readonly TimeProvider _timeProvider;

    public ZendeskRateLimitHandler(
        ILogger<ZendeskRateLimitHandler>? logger = null,
        TimeProvider? timeProvider = null,
        ZendeskMetrics? metrics = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _metrics = metrics;
    }

    // Counted as well as logged (vitally-mcp#94): a counter is a pre-aggregated series rather than text to parse.
    // It fires only once the limit HAS been hit, so it says how often the server exceeds the account's
    // budget; how close it runs is X-Rate-Limit-Remaining, which is only logged.
    private readonly ZendeskMetrics? _metrics;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken);

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                WarnIfNearingLimit(response);
                return response;
            }

            if (attempt >= MaxRetries)
            {
                _metrics?.RateLimitExhausted();
                _logger?.LogWarning(
                    "Zendesk rate limit exceeded ({StatusCode}); {MaxRetries} retries exhausted, returning 429 to caller.",
                    (int)response.StatusCode, MaxRetries);
                return response;
            }

            var delay = GetRetryDelay(response);
            _logger?.LogWarning(
                "Zendesk rate limited (429); retrying in {DelayMs}ms (attempt {Attempt}/{MaxRetries}).",
                delay.TotalMilliseconds, attempt + 1, MaxRetries);

            response.Dispose();
            await Task.Delay(delay, cancellationToken);

            // Counted only once the backoff has completed and the retry is actually about to be sent: a
            // timeout or a cancelled caller during the wait sends nothing, and would otherwise overstate
            // how often the budget was exceeded (Copilot on vitally-mcp#182).
            _metrics?.RateLimitRetry();
        }
    }

    private void WarnIfNearingLimit(HttpResponseMessage response)
    {
        if (_logger is null)
        {
            return;
        }

        if ((response.Headers.TryGetValues("X-Rate-Limit-Remaining", out var values)
                || response.Headers.TryGetValues("X-RateLimit-Remaining", out values))
            && int.TryParse(values.FirstOrDefault(), out var remaining)
            && remaining < LowRemainingThreshold)
        {
            _logger.LogWarning(
                "Zendesk rate limit nearing exhaustion: {Remaining} requests remaining in current window.",
                remaining);
        }
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response)
    {
        // Retry-After: relative seconds
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return Cap(delta);
        }

        // Retry-After: HTTP date
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var wait = date - _timeProvider.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                return Cap(wait);
            }
        }

        // X-RateLimit-Reset: Unix timestamp (seconds)
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
            && long.TryParse(resetValues.FirstOrDefault(), out var resetUnix))
        {
            var resetTime = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
            var wait = resetTime - _timeProvider.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                return Cap(wait);
            }
        }

        return FallbackRetryDelay;
    }

    private TimeSpan Cap(TimeSpan value) => value > MaxRetryDelay ? MaxRetryDelay : value;
}
