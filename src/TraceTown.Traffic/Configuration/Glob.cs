namespace TraceTown.Traffic.Configuration;

/// <summary>
/// The one wildcard the config supports. <c>*</c> matches any run of characters,
/// so <c>*-api</c> selects every API and <c>*</c> selects everything. Deliberately
/// not a regex: fault targets are read far more often than they are written, and
/// a regex in a config file is a thing you have to decode.
/// </summary>
public static class Glob
{
    public static bool Matches(string pattern, string candidate)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(pattern, candidate, StringComparison.OrdinalIgnoreCase);
        }

        string[] segments = pattern.Split('*');
        int position = 0;

        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (segment.Length == 0)
            {
                continue;
            }

            if (i == 0)
            {
                if (!candidate.StartsWith(segment, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                position = segment.Length;
                continue;
            }

            if (i == segments.Length - 1)
            {
                // The final literal must land at the very end, and must not
                // overlap what earlier segments already consumed.
                return candidate.Length - segment.Length >= position
                    && candidate.EndsWith(segment, StringComparison.OrdinalIgnoreCase);
            }

            int found = candidate.IndexOf(segment, position, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            position = found + segment.Length;
        }

        return true;
    }

    public static bool MatchesAny(string pattern, IEnumerable<string> candidates)
        => candidates.Any(c => Matches(pattern, c));

    public static IEnumerable<string> Select(string pattern, IEnumerable<string> candidates)
        => candidates.Where(c => Matches(pattern, c));
}
