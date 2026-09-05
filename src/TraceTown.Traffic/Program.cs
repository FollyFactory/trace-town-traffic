using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TraceTown.Traffic.Configuration;
using TraceTown.Traffic.Control;
using TraceTown.Traffic.Diagnostics;
using TraceTown.Traffic.Faults;
using TraceTown.Traffic.Model;
using TraceTown.Traffic.Simulation;

CliOptions options = Cli.Parse(args);

if (options.ShowHelp)
{
    Console.WriteLine(Cli.Usage);
    return 0;
}

if (options.ShowVersion)
{
    Console.WriteLine(Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev");
    return 0;
}

if (options.Errors.Count > 0)
{
    foreach (string error in options.Errors)
    {
        Console.Error.WriteLine($"error: {error}");
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine("Run with --help for usage.");
    return 2;
}

TrafficConfig config;
try
{
    config = Cli.Apply(Env.Apply(await ConfigLoader.LoadAsync(options.ConfigPath)), options);
}
catch (ConfigException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

ValidationResult validation = ConfigValidator.Validate(config);

foreach (string error in validation.Errors)
{
    Console.Error.WriteLine($"error: {error}");
}

foreach (string warning in validation.Warnings)
{
    Console.Error.WriteLine($"warning: {warning}");
}

if (!validation.IsValid)
{
    return 2;
}

if (options.ValidateOnly)
{
    Console.WriteLine(
        $"{options.ConfigPath}: valid — {config.Services.Count} service(s), " +
        $"{config.Flows.Count} flow(s), {config.Scenarios.Count} scenario(s), " +
        $"{validation.Warnings.Count} warning(s)");
    return 0;
}

if (options.PrintConfig)
{
    Console.WriteLine(JsonSerializer.Serialize(config, ConfigLoader.SerializerOptions));
    return 0;
}

if (options.ListScenarios)
{
    if (config.Scenarios.Count == 0)
    {
        Console.WriteLine("No scenarios defined.");
        return 0;
    }

    foreach (ScenarioConfig scenario in config.Scenarios)
    {
        Console.WriteLine($"{scenario.Id,-16} {scenario.Steps.Count,2} step(s)  {scenario.Description}");
    }

    return 0;
}

if (options.DryRun)
{
    RunDryRun(config);
    return 0;
}

// ── Live run ────────────────────────────────────────────────────────────────

using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(options.Quiet ? LogLevel.Error : LogLevel.Information)
    .AddSimpleConsole(console =>
    {
        console.SingleLine = true;
        console.TimestampFormat = "HH:mm:ss ";
    }));

ILogger logger = loggerFactory.CreateLogger("trace-town-traffic");
await using var engine = new Engine(config, loggerFactory);

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    lifetime.Cancel();
};

if (Duration.Parse(config.Simulation.Duration) is { Ticks: > 0 } runFor)
{
    logger.DurationReached(runFor);
    lifetime.CancelAfter(runFor);
}

await engine.StartAsync(lifetime.Token);

if (!config.Control.Enabled)
{
    await WaitForShutdownAsync(lifetime.Token);
    return 0;
}

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(options.Quiet ? LogLevel.Error : LogLevel.Warning);
string controlUrl = $"http://{config.Control.Host}:{config.Control.Port}";
builder.WebHost.UseUrls(controlUrl);
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    json.SerializerOptions.PropertyNameCaseInsensitive = true;
});

WebApplication app = builder.Build();

// A single shared secret, checked before anything else. Not much of an auth
// story, which is exactly why the default binding is loopback.
if (config.Control.Token is { Length: > 0 } token)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/healthz"))
        {
            await next();
            return;
        }

        string? header = context.Request.Headers.Authorization;
        if (header != $"Bearer {token}")
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized." });
            return;
        }

        await next();
    });
}
else if (config.Control.Host is not ("127.0.0.1" or "localhost" or "::1"))
{
    logger.ControlApiUnprotected(config.Control.Host);
}

app.MapControlApi(engine);
logger.ControlApiListening(controlUrl);

await app.RunAsync(lifetime.Token);
return 0;

static async Task WaitForShutdownAsync(CancellationToken token)
{
    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }
    catch (OperationCanceledException)
    {
        // Expected: Ctrl-C, or the configured duration elapsing.
    }
}

/// <summary>
/// Simulates one request per flow and prints the span tree, without exporting
/// anything. The fastest way to find out whether a config says what you meant.
/// </summary>
static void RunDryRun(TrafficConfig config)
{
    using ILoggerFactory quiet = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
    var topology = new Topology(config);
    var faults = new FaultBoard(topology);
    var simulator = new RequestSimulator(topology, faults, config.Simulation);
    var rng = new Rng(config.Simulation.Seed ?? 1);

    Console.WriteLine($"Dry run of {config.Town.Name} — nothing is exported.");
    Console.WriteLine();

    foreach (FlowConfig flow in config.Flows)
    {
        Console.WriteLine(TracePrinter.Render(simulator.Simulate(flow, rng, DateTimeOffset.UtcNow)));
    }

    foreach (ServiceRuntime cron in topology.Services.Where(s => s.Kind == ServiceKind.Cron))
    {
        Console.WriteLine(TracePrinter.Render(simulator.SimulateCron(cron, rng, DateTimeOffset.UtcNow)));
    }

    Console.WriteLine(
        "A client span with no child is a call to something that does not instrument itself — " +
        "a database, a cache, a broker, a third party. See docs/telemetry-model.md.");
}
