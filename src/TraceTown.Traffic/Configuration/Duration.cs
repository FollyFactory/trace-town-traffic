using System.Globalization;
using System.Text.RegularExpressions;

namespace TraceTown.Traffic.Configuration;

/// <summary>
/// Parses the short duration strings the config uses — <c>500ms</c>, <c>30s</c>,
/// <c>2m30s</c>, <c>1h</c>. A bare number is seconds, because in a file full of
/// timings that is what everyone means.
/// </summary>
public static partial class Duration
{
    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(ms|s|m|h|d)", RegexOptions.IgnoreCase)]
    private static partial Regex PartPattern();

    public static TimeSpan Parse(string? text, TimeSpan fallback = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        string trimmed = text.Trim();

        // A bare number is seconds.
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        MatchCollection matches = PartPattern().Matches(trimmed);
        if (matches.Count == 0)
        {
            throw new FormatException(
                $"'{text}' is not a duration. Expected something like '500ms', '30s', '2m30s' or '1h'.");
        }

        // Reject trailing junk rather than silently ignoring it: '30x' should
        // not quietly become 30 seconds.
        int covered = matches.Sum(m => m.Length);
        string stripped = WhitespacePattern().Replace(trimmed, string.Empty);
        if (covered != stripped.Length)
        {
            throw new FormatException(
                $"'{text}' is not a duration — '{stripped}' has characters outside the recognised units.");
        }

        TimeSpan total = TimeSpan.Zero;
        foreach (Match match in matches)
        {
            double value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            total += match.Groups[2].Value.ToLowerInvariant() switch
            {
                "ms" => TimeSpan.FromMilliseconds(value),
                "s" => TimeSpan.FromSeconds(value),
                "m" => TimeSpan.FromMinutes(value),
                "h" => TimeSpan.FromHours(value),
                "d" => TimeSpan.FromDays(value),
                _ => TimeSpan.Zero,
            };
        }

        return total;
    }

    public static bool TryParse(string? text, out TimeSpan value)
    {
        try
        {
            value = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            value = default;
            return false;
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
