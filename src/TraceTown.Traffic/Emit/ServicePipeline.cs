using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TraceTown.Traffic.Configuration;
using TraceTown.Traffic.Model;

namespace TraceTown.Traffic.Emit;

/// <summary>
/// A full OpenTelemetry SDK pipeline standing in for one simulated service:
/// its own resource, its own <see cref="ActivitySource"/>, its own meter and
/// its own logger, exporting over OTLP exactly as the real service would.
/// </summary>
/// <remarks>
/// One pipeline per service, not per replica. A provider per replica would be
/// more faithful — <c>service.instance.id</c> belongs in the resource — but each
/// provider runs its own export thread, and a fifty-service town with three
/// replicas each would spend more time scheduling threads than simulating
/// anything. Instance id is therefore a metric and span attribute here. It is
/// the one place this tool knowingly departs from what a real deployment looks
/// like, and it is called out in docs/architecture.md.
/// </remarks>
internal sealed class ServicePipeline : IDisposable
{
    private readonly TracerProvider? _tracer;
    private readonly MeterProvider? _meter;
    private readonly ILoggerFactory? _loggerFactory;

    internal ServicePipeline(ServiceRuntime service, TrafficConfig config, HttpClient shared)
    {
        Service = service;
        Source = new ActivitySource($"trace-town/{service.Id}", "1.0.0");
        Instruments = new Instruments(service);

        ResourceBuilder resource = BuildResource(service, config);
        ExporterConfig exporter = config.Exporter;

        if (exporter.Traces)
        {
            _tracer = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resource)
                .AddSource(Source.Name)

                // The simulator decides what is sampled, long before anything
                // reaches the SDK, so the SDK itself records everything it is
                // given. Sampling twice would silently halve the rate.
                .SetSampler(new AlwaysOnSampler())
                .AddOtlpExporter(options => Configure(options, exporter, shared, "v1/traces"))
                .Build();
        }

        if (exporter.Metrics)
        {
            _meter = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resource)
                .AddMeter(Instruments.MeterName)
                .AddView(SemConv.Http.ServerDuration, DurationBuckets())
                .AddView(SemConv.Http.ClientDuration, DurationBuckets())
                .AddView(SemConv.Db.ClientDuration, DurationBuckets())
                .AddView(SemConv.Messaging.ProcessDuration, DurationBuckets())
                .AddOtlpExporter((options, reader) =>
                {
                    Configure(options, exporter, shared, "v1/metrics");
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                        exporter.MetricIntervalMs;
                })
                .Build();
        }

        if (exporter.Logs)
        {
            _loggerFactory = LoggerFactory.Create(builder => builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddOpenTelemetry(options =>
                {
                    options.SetResourceBuilder(resource);
                    options.IncludeScopes = true;
                    options.IncludeFormattedMessage = true;
                    options.ParseStateValues = true;
                    options.AddOtlpExporter(o => Configure(o, exporter, shared, "v1/logs"));
                }));

            Logger = _loggerFactory.CreateLogger(service.Name);
        }

        Logger ??= Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    internal ServiceRuntime Service { get; }

    internal ActivitySource Source { get; }

    internal Instruments Instruments { get; }

    internal ILogger Logger { get; }

    /// <summary>
    /// The bucket boundaries the HTTP semantic conventions recommend. Without
    /// them the SDK's defaults top out too low to see a four-second dependency,
    /// which is exactly the thing you are usually looking for.
    /// </summary>
    private static ExplicitBucketHistogramConfiguration DurationBuckets() => new()
    {
        Boundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10],
    };

    private static ResourceBuilder BuildResource(ServiceRuntime service, TrafficConfig config)
    {
        Dictionary<string, object> attributes = new(StringComparer.Ordinal)
        {
            [SemConv.Resource.DeploymentEnvironment] = config.Town.Environment,
            ["trace_town.kind"] = service.Kind.ToString().ToLowerInvariant(),
            ["trace_town.town"] = config.Town.Name,
            ["trace_town.simulated"] = true,
        };

        if (config.Exporter.SemanticConventions == SemanticConventionMode.Dup)
        {
            foreach ((string current, string superseded) in SemConv.RenamedResourceAttributes)
            {
                if (attributes.TryGetValue(current, out object? value))
                {
                    attributes[superseded] = value;
                }
            }
        }

        foreach ((string key, string value) in config.Town.Resource)
        {
            attributes[key] = value;
        }

        foreach ((string key, string value) in service.Config.Resource)
        {
            attributes[key] = value;
        }

        return ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: service.Name,
                serviceNamespace: service.Group,
                serviceVersion: service.Version,
                autoGenerateServiceInstanceId: false)
            .AddAttributes(attributes!);
    }

    private static void Configure(
        OtlpExporterOptions options,
        ExporterConfig config,
        HttpClient shared,
        string httpPath)
    {
        bool http = config.Protocol == OtlpProtocol.HttpProtobuf;

        options.Protocol = http ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
        options.Endpoint = http
            ? new Uri(new Uri(config.Endpoint.TrimEnd('/') + "/"), httpPath)
            : new Uri(config.Endpoint);

        options.TimeoutMilliseconds = config.TimeoutMs;

        if (config.Headers.Count > 0)
        {
            options.Headers = string.Join(",", config.Headers.Select(h => $"{h.Key}={h.Value}"));
        }

        // Every pipeline shares one HttpClient. With a provider per service per
        // signal, a client each would mean a connection pool each.
        if (http)
        {
            options.HttpClientFactory = () => shared;
        }
    }

    /// <summary>Drains buffered telemetry. Called on shutdown, before disposal.</summary>
    internal void ForceFlush(int timeoutMs)
    {
        _tracer?.ForceFlush(timeoutMs);
        _meter?.ForceFlush(timeoutMs);
    }

    public void Dispose()
    {
        _tracer?.Dispose();
        _meter?.Dispose();
        _loggerFactory?.Dispose();
        Instruments.Dispose();
        Source.Dispose();
    }
}
