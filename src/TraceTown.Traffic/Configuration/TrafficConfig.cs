using System.Text.Json.Serialization;
using TraceTown.Traffic.Configuration.Json;

namespace TraceTown.Traffic.Configuration;

/// <summary>
/// The whole tool, described in one file. Everything the simulator does is a
/// function of this object plus a clock and a seeded random source, which is
/// what makes a run reproducible.
/// </summary>
public sealed record TrafficConfig
{
    /// <summary>Identity shared by every simulated service in the run.</summary>
    public TownConfig Town { get; init; } = new();

    /// <summary>Where the telemetry goes.</summary>
    public ExporterConfig Exporter { get; init; } = new();

    /// <summary>How the simulation itself behaves — rates, sampling, seed.</summary>
    public SimulationConfig Simulation { get; init; } = new();

    /// <summary>The HTTP control surface. Disabled by binding it to nothing.</summary>
    public ControlConfig Control { get; init; } = new();

    /// <summary>The components of the system. Order is irrelevant.</summary>
    public IReadOnlyList<ServiceConfig> Services { get; init; } = [];

    /// <summary>
    /// The entry points that actually generate load. A service with no flow
    /// reaching it emits nothing but its own idle infrastructure metrics.
    /// </summary>
    public IReadOnlyList<FlowConfig> Flows { get; init; } = [];

    /// <summary>Named sequences of faults you can switch between at runtime.</summary>
    public IReadOnlyList<ScenarioConfig> Scenarios { get; init; } = [];
}

public sealed record TownConfig
{
    public string Name { get; init; } = "trace-town";

    /// <summary>Becomes the <c>deployment.environment.name</c> resource attribute.</summary>
    public string Environment { get; init; } = "production";

    /// <summary>Resource attributes merged onto every service in the town.</summary>
    public IReadOnlyDictionary<string, string> Resource { get; init; }
        = new Dictionary<string, string>();
}

public sealed record ExporterConfig
{
    /// <summary>OTLP endpoint. Include the scheme; the port is not assumed.</summary>
    public string Endpoint { get; init; } = "http://localhost:4318";

    /// <summary>
    /// <c>httpProtobuf</c> or <c>grpc</c>. HTTP is the default because every
    /// exporter then shares one <see cref="HttpClient"/> — with a service per
    /// simulated component, one gRPC channel each adds up fast.
    /// </summary>
    public OtlpProtocol Protocol { get; init; } = OtlpProtocol.HttpProtobuf;

    /// <summary>Headers added to every export, for backends that want a key.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Per-signal switches, for pushing traces somewhere metrics do not go.</summary>
    public bool Traces { get; init; } = true;

    public bool Metrics { get; init; } = true;

    public bool Logs { get; init; } = true;

    /// <summary>How often metric readers export. Match your backend's scrape interval.</summary>
    public int MetricIntervalMs { get; init; } = 10_000;

    public int TimeoutMs { get; init; } = 10_000;

    /// <summary>
    /// Whether to emit superseded attribute names alongside the current ones.
    /// Defaults to <see cref="SemanticConventionMode.Dup"/> because several
    /// backends still key on the old names — SigNoz builds its service map from
    /// <c>db.system</c>, and without it every database and cache vanishes from
    /// the dependency graph while the traces themselves look correct.
    /// </summary>
    public SemanticConventionMode SemanticConventions { get; init; } = SemanticConventionMode.Dup;
}

/// <summary>
/// Mirrors OpenTelemetry's own <c>OTEL_SEMCONV_STABILITY_OPT_IN</c> switch.
/// </summary>
[JsonConverter(typeof(FlexibleEnumConverter<SemanticConventionMode>))]
public enum SemanticConventionMode
{
    /// <summary>Emit the current names and the ones they replaced. The default.</summary>
    Dup,

    /// <summary>Emit only the current names. Correct, and invisible to some backends.</summary>
    Latest,
}

[JsonConverter(typeof(FlexibleEnumConverter<OtlpProtocol>))]
public enum OtlpProtocol
{
    HttpProtobuf,
    Grpc,
}

public sealed record SimulationConfig
{
    /// <summary>
    /// How often the engine wakes to generate work. Requests within a tick are
    /// spread across it, so this is scheduling granularity, not batch size.
    /// </summary>
    public int TickMs { get; init; } = 250;

    /// <summary>
    /// Fraction of requests that produce spans, 0..1. Metrics are always
    /// recorded for every request — that is the point of the split, and it is
    /// what real systems do. See docs/telemetry-model.md.
    /// </summary>
    public double TraceSampleRatio { get; init; } = 0.1;

    /// <summary>
    /// Errored requests are sampled at this ratio instead, so a 0.1% error rate
    /// is still visible in traces at a 1% head sample. Tail sampling by another
    /// name, and honest about it.
    /// </summary>
    public double ErrorTraceSampleRatio { get; init; } = 1.0;

    /// <summary>Fixed seed makes a run reproducible. Omit for a random one.</summary>
    public int? Seed { get; init; }

    /// <summary>Multiplies every flow rate. The volume knob for the whole town.</summary>
    public double RateMultiplier { get; init; } = 1.0;

    /// <summary>Guards against a dependency cycle turning into a stack overflow.</summary>
    public int MaxDepth { get; init; } = 12;

    /// <summary>Scenario started on boot. Omit to start with no faults at all.</summary>
    public string? Scenario { get; init; }

    /// <summary>Stop after this long. Omit to run until interrupted.</summary>
    public string? Duration { get; init; }
}

public sealed record ControlConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Loopback by default. The API can reshape your telemetry and has no
    /// authentication unless you set <see cref="Token"/>, so binding it to
    /// 0.0.0.0 is a deliberate act.
    /// </summary>
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 8080;

    /// <summary>When set, requests must carry <c>Authorization: Bearer {token}</c>.</summary>
    public string? Token { get; init; }
}
