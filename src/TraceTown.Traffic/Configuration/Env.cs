using System.Globalization;

namespace TraceTown.Traffic.Configuration;

/// <summary>
/// Environment variable overrides, applied between the config file and the
/// command line. Containers configure things this way, and baking an endpoint
/// into an image is how you end up with a staging generator writing into
/// production.
/// </summary>
/// <remarks>
/// Precedence is file, then environment, then command line: the more specific
/// and the more immediate the source, the later it wins.
/// <para>
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is honoured too, since anything running
/// beside real instrumented services will already have it set.
/// </para>
/// </remarks>
public static class Env
{
    public const string Prefix = "TRAFFIC_";

    public static TrafficConfig Apply(TrafficConfig config, Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;

        ExporterConfig exporter = config.Exporter;
        SimulationConfig simulation = config.Simulation;
        ControlConfig control = config.Control;
        TownConfig town = config.Town;

        if (String(read, "TRAFFIC_ENDPOINT") is { } endpoint)
        {
            exporter = exporter with { Endpoint = endpoint };
        }
        else if (String(read, "OTEL_EXPORTER_OTLP_ENDPOINT") is { } otel)
        {
            exporter = exporter with { Endpoint = otel };
        }

        if (String(read, "TRAFFIC_PROTOCOL") is { } protocol)
        {
            exporter = exporter with
            {
                Protocol = protocol.Contains("grpc", StringComparison.OrdinalIgnoreCase)
                    ? OtlpProtocol.Grpc
                    : OtlpProtocol.HttpProtobuf,
            };
        }

        if (String(read, "TRAFFIC_ENVIRONMENT") is { } environment)
        {
            town = town with { Environment = environment };
        }

        if (String(read, "TRAFFIC_TOWN") is { } name)
        {
            town = town with { Name = name };
        }

        if (String(read, "TRAFFIC_SCENARIO") is { } scenario)
        {
            simulation = simulation with { Scenario = scenario };
        }

        if (String(read, "TRAFFIC_DURATION") is { } duration)
        {
            simulation = simulation with { Duration = duration };
        }

        if (Number(read, "TRAFFIC_RATE") is { } rate)
        {
            simulation = simulation with { RateMultiplier = rate };
        }

        if (Number(read, "TRAFFIC_SAMPLE") is { } sample)
        {
            simulation = simulation with { TraceSampleRatio = sample };
        }

        if (Number(read, "TRAFFIC_SEED") is { } seed)
        {
            simulation = simulation with { Seed = (int)seed };
        }

        if (String(read, "TRAFFIC_CONTROL_HOST") is { } host)
        {
            control = control with { Host = host };
        }

        if (Number(read, "TRAFFIC_CONTROL_PORT") is { } port)
        {
            control = control with { Port = (int)port };
        }

        if (String(read, "TRAFFIC_CONTROL_TOKEN") is { } token)
        {
            control = control with { Token = token };
        }

        if (String(read, "TRAFFIC_CONTROL_ENABLED") is { } enabled)
        {
            control = control with { Enabled = IsTruthy(enabled) };
        }

        return config with
        {
            Town = town,
            Exporter = exporter,
            Simulation = simulation,
            Control = control,
        };
    }

    private static string? String(Func<string, string?> read, string key)
    {
        string? value = read(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static double? Number(Func<string, string?> read, string key)
    {
        string? raw = String(read, key);
        if (raw is null)
        {
            return null;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new ConfigException($"{key}='{raw}' is not a number.");
    }

    private static bool IsTruthy(string value)
        => value is "1" or "true" or "TRUE" or "True" or "yes" or "on";
}
