using System.Globalization;
using TraceTown.Traffic.Model;

namespace TraceTown.Traffic.Simulation;

/// <summary>
/// Turns the short strings people write in config — <c>POST /api/checkout</c>,
/// <c>SELECT orders</c>, <c>GET session</c> — into the attribute values the
/// semantic conventions want.
/// </summary>
internal static class Operations
{
    private static readonly string[] HttpMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    private static readonly string[] SqlVerbs =
        ["SELECT", "INSERT", "UPDATE", "DELETE", "UPSERT", "MERGE", "BEGIN", "COMMIT", "CALL"];

    /// <summary>Splits <c>POST /api/checkout</c> into its method and route.</summary>
    internal static (string Method, string Route) Http(string? operation, string fallbackTarget)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            return ("GET", $"/{fallbackTarget}");
        }

        string[] parts = operation.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);

        if (parts.Length == 2 && HttpMethods.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
        {
            return (parts[0].ToUpperInvariant(), Route(parts[1]));
        }

        return ("GET", Route(operation.Trim()));

        static string Route(string value) => value.StartsWith('/') ? value : $"/{value}";
    }

    /// <summary>
    /// Splits <c>SELECT orders</c> into an operation and a collection, and
    /// builds a parameterised statement for <c>db.query.text</c>. Parameterised
    /// because the convention wants the query shape, not the values — a
    /// statement with a customer's email inlined is a data leak in a trace.
    /// </summary>
    internal static (string Operation, string? Collection, string Statement) Db(
        string? operation,
        ServiceRuntime target)
    {
        string system = target.Config.Database?.System ?? "postgresql";

        if (string.IsNullOrWhiteSpace(operation))
        {
            return ("SELECT", null, $"SELECT * FROM {target.Id.Replace('-', '_')}");
        }

        string[] parts = operation.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);

        if (parts.Length == 2 && SqlVerbs.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
        {
            string verb = parts[0].ToUpperInvariant();
            string collection = parts[1];
            return (verb, collection, Statement(verb, collection, system));
        }

        // Not SQL-shaped — a Mongo command, a stored procedure name. Pass it
        // through rather than inventing a shape it does not have.
        return (operation.Trim(), null, operation.Trim());
    }

    private static string Statement(string verb, string collection, string system)
    {
        string placeholder = system is "postgresql" or "cockroachdb" ? "$1" : "?";

        return verb switch
        {
            "SELECT" => $"SELECT * FROM {collection} WHERE id = {placeholder}",
            "INSERT" => $"INSERT INTO {collection} (id, data) VALUES ({placeholder}, {placeholder})",
            "UPDATE" => $"UPDATE {collection} SET data = {placeholder} WHERE id = {placeholder}",
            "DELETE" => $"DELETE FROM {collection} WHERE id = {placeholder}",
            "UPSERT" or "MERGE" => $"INSERT INTO {collection} (id, data) VALUES ({placeholder}, {placeholder}) ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data",
            _ => $"{verb} {collection}",
        };
    }

    /// <summary>Cache commands. The key is templated, never the real one.</summary>
    internal static (string Operation, string Key) Cache(string? operation, string fallbackTarget)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            return ("GET", $"{fallbackTarget}:{{key}}");
        }

        string[] parts = operation.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (parts[0].ToUpperInvariant(), parts[1])
            : (parts[0].ToUpperInvariant(), $"{fallbackTarget}:{{key}}");
    }

    /// <summary>A message id that looks like one without being a real ULID.</summary>
    internal static string MessageId(Rng rng)
        => string.Create(
            16,
            (High: rng.NextUInt32(), Low: rng.NextUInt32()),
            static (span, state) =>
            {
                state.High.TryFormat(span[..8], out _, "x8", CultureInfo.InvariantCulture);
                state.Low.TryFormat(span[8..], out _, "x8", CultureInfo.InvariantCulture);
            });
}
