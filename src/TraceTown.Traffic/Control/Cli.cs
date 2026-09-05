using System.Globalization;
using TraceTown.Traffic.Configuration;

namespace TraceTown.Traffic.Control;

/// <summary>Command-line options. Every one of them overrides the config file.</summary>
public sealed record CliOptions
{
    public string ConfigPath { get; init; } = "traffic.json";

    public string? Endpoint { get; init; }

    public string? Scenario { get; init; }

    public string? RunDuration { get; init; }

    public double? RateMultiplier { get; init; }

    public double? SampleRatio { get; init; }

    public int? Seed { get; init; }

    public int? Port { get; init; }

    public bool NoControl { get; init; }

    public bool ValidateOnly { get; init; }

    public bool DryRun { get; init; }

    public bool ListScenarios { get; init; }

    public bool PrintConfig { get; init; }

    public bool Quiet { get; init; }

    public bool ShowHelp { get; init; }

    public bool ShowVersion { get; init; }

    public List<string> Errors { get; } = [];
}

public static class Cli
{
    public const string Usage = """
        trace-town-traffic — synthetic OpenTelemetry traffic for a whole distributed system

        USAGE
          trace-town-traffic [config.json] [options]

        OPTIONS
          -c, --config <path>      Config file. Defaults to ./traffic.json, or the
                                   first positional argument.
          -e, --endpoint <url>     Override exporter.endpoint.
          -s, --scenario <id>      Start with this scenario running.
          -d, --duration <dur>     Stop after this long (30s, 5m, 1h).
          -r, --rate <multiplier>  Scale every flow's rate.
              --sample <ratio>     Override the trace sample ratio, 0..1.
              --seed <n>           Fix the random seed for a reproducible run.
          -p, --port <n>           Control API port.
              --no-control         Do not start the control API at all.
              --validate           Check the config and exit.
              --dry-run            Print one trace per flow and exit. Exports nothing.
              --list-scenarios     List the scenarios in the config and exit.
              --print-config       Print the resolved config as JSON and exit.
          -q, --quiet              Errors only.
          -h, --help               This.
          -v, --version            Version.

        EXAMPLES
          trace-town-traffic examples/ecommerce.json
          trace-town-traffic examples/ecommerce.json --scenario cascade --duration 10m
          trace-town-traffic examples/ecommerce.json --dry-run
          curl -X POST localhost:8080/api/scenarios/cascade
        """;

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        string? positional = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "-h" or "--help":
                    options = options with { ShowHelp = true };
                    break;

                case "-v" or "--version":
                    options = options with { ShowVersion = true };
                    break;

                case "--validate":
                    options = options with { ValidateOnly = true };
                    break;

                case "--dry-run":
                    options = options with { DryRun = true };
                    break;

                case "--list-scenarios":
                    options = options with { ListScenarios = true };
                    break;

                case "--print-config":
                    options = options with { PrintConfig = true };
                    break;

                case "--no-control":
                    options = options with { NoControl = true };
                    break;

                case "-q" or "--quiet":
                    options = options with { Quiet = true };
                    break;

                case "-c" or "--config":
                    options = options with { ConfigPath = Next(args, ref i, arg, options) ?? options.ConfigPath };
                    break;

                case "-e" or "--endpoint":
                    options = options with { Endpoint = Next(args, ref i, arg, options) };
                    break;

                case "-s" or "--scenario":
                    options = options with { Scenario = Next(args, ref i, arg, options) };
                    break;

                case "-d" or "--duration":
                    options = options with { RunDuration = Next(args, ref i, arg, options) };
                    break;

                case "-r" or "--rate":
                    options = options with { RateMultiplier = Number(args, ref i, arg, options) };
                    break;

                case "--sample":
                    options = options with { SampleRatio = Number(args, ref i, arg, options) };
                    break;

                case "--seed":
                    options = options with { Seed = (int?)Number(args, ref i, arg, options) };
                    break;

                case "-p" or "--port":
                    options = options with { Port = (int?)Number(args, ref i, arg, options) };
                    break;

                default:
                    if (arg.StartsWith('-'))
                    {
                        options.Errors.Add($"Unknown option '{arg}'.");
                    }
                    else if (positional is null)
                    {
                        positional = arg;
                    }
                    else
                    {
                        options.Errors.Add($"Unexpected argument '{arg}'.");
                    }

                    break;
            }
        }

        if (positional is not null)
        {
            options = options with { ConfigPath = positional };
        }

        if (options.RunDuration is { } duration && !Duration.TryParse(duration, out _))
        {
            options.Errors.Add($"--duration '{duration}' is not a duration.");
        }

        return options;
    }

    /// <summary>Applies the overrides on top of a loaded config.</summary>
    public static TrafficConfig Apply(TrafficConfig config, CliOptions options)
    {
        ExporterConfig exporter = options.Endpoint is { } endpoint
            ? config.Exporter with { Endpoint = endpoint }
            : config.Exporter;

        SimulationConfig simulation = config.Simulation;
        if (options.Scenario is { } scenario)
        {
            simulation = simulation with { Scenario = scenario };
        }

        if (options.RunDuration is { } duration)
        {
            simulation = simulation with { Duration = duration };
        }

        if (options.RateMultiplier is { } rate)
        {
            simulation = simulation with { RateMultiplier = rate };
        }

        if (options.SampleRatio is { } sample)
        {
            simulation = simulation with { TraceSampleRatio = sample };
        }

        if (options.Seed is { } seed)
        {
            simulation = simulation with { Seed = seed };
        }

        ControlConfig control = config.Control;
        if (options.Port is { } port)
        {
            control = control with { Port = port };
        }

        if (options.NoControl)
        {
            control = control with { Enabled = false };
        }

        return config with { Exporter = exporter, Simulation = simulation, Control = control };
    }

    private static string? Next(string[] args, ref int index, string flag, CliOptions options)
    {
        if (index + 1 >= args.Length)
        {
            options.Errors.Add($"{flag} needs a value.");
            return null;
        }

        return args[++index];
    }

    private static double? Number(string[] args, ref int index, string flag, CliOptions options)
    {
        string? raw = Next(args, ref index, flag, options);
        if (raw is null)
        {
            return null;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            options.Errors.Add($"{flag} expects a number, got '{raw}'.");
            return null;
        }

        return value;
    }
}
