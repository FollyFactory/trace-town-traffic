using System.Text.Json;

namespace TraceTown.Traffic.Configuration;

/// <summary>
/// Reads a config file. Comments and trailing commas are allowed because these
/// files are hand-written and long-lived, and a config you cannot annotate is a
/// config nobody maintains.
/// </summary>
public static class ConfigLoader
{
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<TrafficConfig> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        string full = Resolve(path);
        await using FileStream stream = File.OpenRead(full);
        return await ReadAsync(stream, full, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the config relative to the working directory, and failing that
    /// next to the executable — which is what makes the bundled examples work
    /// inside the container without anyone having to know where they landed.
    /// </summary>
    public static string Resolve(string path)
    {
        string full = Path.GetFullPath(path);
        if (File.Exists(full))
        {
            return full;
        }

        if (!Path.IsPathRooted(path))
        {
            string beside = Path.Combine(AppContext.BaseDirectory, path);
            if (File.Exists(beside))
            {
                return beside;
            }
        }

        throw new ConfigException($"No config file at {full}.");
    }

    public static async Task<TrafficConfig> ReadAsync(
        Stream stream,
        string source,
        CancellationToken cancellationToken = default)
    {
        TrafficConfig? config;
        try
        {
            config = await JsonSerializer
                .DeserializeAsync<TrafficConfig>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // The path and line number are the whole value of this message.
            string where = ex.LineNumber is { } line
                ? $"{source}, line {line + 1}"
                : source;
            throw new ConfigException($"{where}: {ex.Message}", ex);
        }

        if (config is null)
        {
            throw new ConfigException($"{source} is empty.");
        }

        return config;
    }

    public static TrafficConfig Parse(string json, string source = "<inline>")
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return ReadAsync(stream, source).GetAwaiter().GetResult();
    }
}

public sealed class ConfigException : Exception
{
    public ConfigException(string message) : base(message) { }

    public ConfigException(string message, Exception inner) : base(message, inner) { }
}
