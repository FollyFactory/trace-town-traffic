using System.Text.Json;
using System.Text.Json.Serialization;

namespace TraceTown.Traffic.Configuration.Json;

/// <summary>
/// Reads enum values written any way a person might reasonably write them —
/// <c>randomWalk</c>, <c>random-walk</c>, <c>random_walk</c>, <c>RANDOM WALK</c>
/// — and always writes camelCase. Hand-authored config should not fail on a
/// hyphen, and the error when it genuinely is a typo lists the valid values.
/// </summary>
internal sealed class FlexibleEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<string, TEnum> Lookup = BuildLookup();
    private static readonly Dictionary<TEnum, string> Names = BuildNames();

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected a string for {typeof(TEnum).Name}, got {reader.TokenType}. " +
                $"Valid values: {string.Join(", ", Names.Values)}.");
        }

        string raw = reader.GetString() ?? string.Empty;
        if (Lookup.TryGetValue(Normalise(raw), out TEnum value))
        {
            return value;
        }

        throw new JsonException(
            $"'{raw}' is not a valid {typeof(TEnum).Name}. " +
            $"Valid values: {string.Join(", ", Names.Values)}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => writer.WriteStringValue(Names.TryGetValue(value, out string? name) ? name : value.ToString());

    /// <summary>Strips every separator and case difference so only the letters matter.</summary>
    private static string Normalise(string raw)
    {
        Span<char> buffer = stackalloc char[raw.Length];
        int length = 0;
        foreach (char c in raw)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }

    private static Dictionary<string, TEnum> BuildLookup()
    {
        Dictionary<string, TEnum> map = [];
        foreach (TEnum value in Enum.GetValues<TEnum>())
        {
            map[Normalise(value.ToString())] = value;
        }

        return map;
    }

    private static Dictionary<TEnum, string> BuildNames()
    {
        Dictionary<TEnum, string> map = [];
        foreach (TEnum value in Enum.GetValues<TEnum>())
        {
            string name = value.ToString();
            map[value] = char.ToLowerInvariant(name[0]) + name[1..];
        }

        return map;
    }
}
