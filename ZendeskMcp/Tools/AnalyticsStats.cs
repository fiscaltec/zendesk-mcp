using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ZendeskMcp.Tools;

/// <summary>
/// The pure arithmetic behind the analytics tools: medians, percentiles, averages and rates, putting
/// moments into days, weeks and months in a timezone, and CSV encoding. Nothing here calls Zendesk, so
/// every figure the tools report can be checked by a unit test on its own.
/// </summary>
public static class AnalyticsStats
{
    /// <summary>
    /// <c>{n, median, avg, p90}</c> for a set of minute values, with nulls when there are none.
    /// </summary>
    /// <remarks>
    /// The median is the conventional one (the mean of the two middle values for an even count). p90
    /// uses the nearest-rank method: the value at rank ⌈0.9 × n⌉ of the sorted values, so it is always a
    /// value that actually occurred — the same method Zendesk Explore's percentiles are usually
    /// compared against, and one that gives a stable answer for small n rather than interpolating.
    /// </remarks>
    public static JsonObject Summary(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
        {
            return new JsonObject { ["n"] = 0, ["median"] = null, ["avg"] = null, ["p90"] = null };
        }

        return new JsonObject
        {
            ["n"] = values.Count,
            ["median"] = Round(Median(values)!.Value),
            ["avg"] = Round(values.Average()),
            ["p90"] = Round(Percentile(values, 90)!.Value),
        };
    }

    /// <summary>The median (mean of the two middle values for an even count), or null when empty.</summary>
    public static double? Median(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    /// <summary>The nearest-rank percentile: the sorted value at rank ⌈p/100 × n⌉ (1-based). Null when empty.</summary>
    public static double? Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }

        if (percentile is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be in (0, 100].");
        }

        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    /// <summary>A percentage to one decimal place, or null when the denominator is zero (not 0%, which would be a claim).</summary>
    public static double? RatePct(long numerator, long denominator) =>
        denominator <= 0 ? null : Round(100.0 * numerator / denominator);

    /// <summary>Rounds to one decimal place, half away from zero, so reports read the same everywhere.</summary>
    public static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Resolves an IANA timezone name (as Zendesk's <c>iana_time_zone</c>), falling back to UTC when it
    /// is missing or unknown to this host.
    /// </summary>
    public static TimeZoneInfo ResolveTimeZone(string? ianaName)
    {
        if (!string.IsNullOrWhiteSpace(ianaName))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(ianaName);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Fall through to UTC; the tool reports the zone it actually used.
            }
        }

        return TimeZoneInfo.Utc;
    }

    /// <summary>The calendar day a moment falls on in <paramref name="zone"/>.</summary>
    public static DateOnly LocalDay(DateTimeOffset moment, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, zone).DateTime);

    /// <summary>The Monday starting the (Monday–Sunday) week that <paramref name="day"/> is in.</summary>
    public static DateOnly WeekStart(DateOnly day)
    {
        var offset = ((int)day.DayOfWeek + 6) % 7; // Monday = 0 … Sunday = 6
        return day.AddDays(-offset);
    }

    /// <summary>Bucket key for a moment: <c>yyyy-MM-dd</c> for day, the Monday's date for week, <c>yyyy-MM</c> for month.</summary>
    public static string BucketKey(DateTimeOffset moment, TimeZoneInfo zone, string bucket)
    {
        var day = LocalDay(moment, zone);
        return bucket switch
        {
            "day" => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "week" => WeekStart(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "month" => day.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            _ => throw new ArgumentException($"Unknown bucket '{bucket}'.", nameof(bucket)),
        };
    }

    /// <summary>A human label for a week or month bucket key.</summary>
    public static string BucketName(string key, string bucket) => bucket switch
    {
        "week" => $"Week of {key}",
        "month" => DateOnly.ParseExact(key + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("MMMM yyyy", CultureInfo.InvariantCulture),
        _ => key,
    };

    /// <summary>Today's date in <paramref name="zone"/>.</summary>
    public static DateOnly Today(TimeZoneInfo zone, DateTimeOffset now) => LocalDay(now, zone);

    /// <summary>Every day from <paramref name="first"/> to <paramref name="last"/> inclusive.</summary>
    public static IEnumerable<DateOnly> Days(DateOnly first, DateOnly last)
    {
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    /// <summary>Parses an ISO moment from a Zendesk field; null when absent or malformed.</summary>
    public static DateTimeOffset? Moment(JsonNode? node) =>
        ToolJson.String(node) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
            ? moment.ToUniversalTime()
            : null;

    /// <summary>Formats a moment as Zendesk does (UTC, seconds, trailing Z).</summary>
    public static string Iso(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Reads an integer node as a long; see <see cref="ToolJson.Long"/>.</summary>
    public static long? Long(JsonNode? node) => ToolJson.Long(node);

    /// <summary>Reads a number node as a double, whatever numeric type Zendesk sent.</summary>
    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        return value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>One CSV field, quoted when it holds a comma, quote, or line break (RFC 4180).</summary>
    public static string CsvField(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            JsonNode node => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString(),
            _ => value.ToString() ?? string.Empty,
        };

        // A leading =, +, - or @ would be read as a formula by a spreadsheet the CSV is opened in, and
        // these values (subjects, comments) are customer-written text. Prefixing an apostrophe keeps
        // the text visible without letting it execute.
        if (text.Length > 0 && text[0] is '=' or '+' or '@' or '\t' or '\r'
            || (text.Length > 1 && text[0] == '-' && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }

    /// <summary>One CSV line (no line ending).</summary>
    public static string CsvLine(IEnumerable<object?> values) => string.Join(',', values.Select(CsvField));

    /// <summary>A whole CSV document: header then rows, CRLF line endings.</summary>
    public static string Csv(IReadOnlyList<string> columns, IEnumerable<IEnumerable<object?>> rows)
    {
        var builder = new StringBuilder();
        builder.Append(CsvLine(columns)).Append("\r\n");
        foreach (var row in rows)
        {
            builder.Append(CsvLine(row)).Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>The <c>{columns, rows}</c> table shape the analytics tools return, each row one CSV line.</summary>
    public static JsonObject Table(IReadOnlyList<string> columns, IEnumerable<IEnumerable<object?>> rows) => new()
    {
        ["columns"] = new JsonArray(columns.Select(c => (JsonNode?)c).ToArray()),
        ["rows"] = new JsonArray(rows.Select(r => (JsonNode?)CsvLine(r)).ToArray()),
    };
}
