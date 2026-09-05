using System.Text.Json.Serialization;
using TraceTown.Traffic.Configuration.Json;

namespace TraceTown.Traffic.Configuration;

/// <summary>
/// What a component of the system is and how it behaves. The <see cref="Kind"/>
/// decides which signals it emits and, critically, whether calls to it produce a
/// server span at all — a database does not instrument itself.
/// </summary>
public sealed record ServiceConfig
{
    /// <summary>Stable identifier. Referenced by dependencies, flows and faults.</summary>
    public required string Id { get; init; }

    /// <summary>Becomes <c>service.name</c>. Defaults to <see cref="Id"/>.</summary>
    public string? Name { get; init; }

    public required ServiceKind Kind { get; init; }

    /// <summary>
    /// Neighbourhood label — team, namespace, bounded context. Emitted as
    /// <c>service.namespace</c>, which is what a viewer can group the town by.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// Becomes <c>service.version</c>. Change it mid-run through the control API
    /// to make a deployment visible in the telemetry.
    /// </summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>
    /// Replicas. Each gets its own <c>service.instance.id</c>, and requests are
    /// spread across them, so per-instance metrics differ the way real ones do.
    /// </summary>
    public int Instances { get; init; } = 1;

    /// <summary>Time spent in this service itself, excluding downstream calls.</summary>
    public LatencyConfig Latency { get; init; } = new();

    /// <summary>
    /// Chance this service fails on its own, 0..1. Separate from failures it
    /// inherits by calling something that is already broken.
    /// </summary>
    public double ErrorRate { get; init; }

    /// <summary>Extra resource attributes for this service alone.</summary>
    public IReadOnlyDictionary<string, string> Resource { get; init; }
        = new Dictionary<string, string>();

    /// <summary>What this service calls, and how often.</summary>
    public IReadOnlyList<DependencyConfig> Dependencies { get; init; } = [];

    /// <summary>Queues this service consumes from. Only meaningful for workers.</summary>
    public IReadOnlyList<ConsumerConfig> Consumes { get; init; } = [];

    /// <summary>Backend-specific detail for <c>database</c> services.</summary>
    public DatabaseConfig? Database { get; init; }

    /// <summary>Backend-specific detail for <c>cache</c> services.</summary>
    public CacheConfig? Cache { get; init; }

    /// <summary>Broker-specific detail for <c>queue</c> services.</summary>
    public QueueConfig? Queue { get; init; }

    /// <summary>Schedule for <c>cron</c> services, which generate their own load.</summary>
    public CronConfig? Cron { get; init; }
}

/// <summary>
/// The archetypes the simulator understands. These match Trace Town's service
/// kinds exactly, because that is what the telemetry is being drawn as.
/// </summary>
[JsonConverter(typeof(FlexibleEnumConverter<ServiceKind>))]
public enum ServiceKind
{
    /// <summary>Edge — ingress, load balancer, BFF. Where root spans start.</summary>
    Gateway,

    /// <summary>An instrumented HTTP service. Server span in, client spans out.</summary>
    Api,

    /// <summary>Consumes from a queue rather than serving requests.</summary>
    Worker,

    /// <summary>Relational or document store. Emits metrics and logs, never spans.</summary>
    Database,

    /// <summary>Key-value store. Same span rules as a database.</summary>
    Cache,

    /// <summary>Message broker. The producer and consumer spans belong to its clients.</summary>
    Queue,

    /// <summary>Scheduled job. Generates its own root traces on a schedule.</summary>
    Cron,

    /// <summary>Someone else's system. You see your client span and nothing more.</summary>
    External,
}

/// <summary>
/// A latency distribution. Lognormal by default because real service latency is
/// lognormal — a hard floor, a fat right tail, and a mean well above the median.
/// A normal distribution would give you a tail that is far too thin to test with.
/// </summary>
public sealed record LatencyConfig
{
    public double P50Ms { get; init; } = 10;

    public double P99Ms { get; init; } = 50;

    public LatencyDistribution Distribution { get; init; } = LatencyDistribution.Lognormal;

    /// <summary>Never return less than this. Models a fixed cost like a network hop.</summary>
    public double FloorMs { get; init; }
}

[JsonConverter(typeof(FlexibleEnumConverter<LatencyDistribution>))]
public enum LatencyDistribution
{
    /// <summary>The realistic default.</summary>
    Lognormal,

    /// <summary>Symmetric. Useful when you want a boring, predictable shape.</summary>
    Normal,

    /// <summary>Every value equally likely between the floor and p99.</summary>
    Uniform,

    /// <summary>Always exactly p50. For isolating one variable in a test.</summary>
    Constant,
}

/// <summary>An outbound call from one service to another.</summary>
public sealed record DependencyConfig
{
    /// <summary>The <see cref="ServiceConfig.Id"/> being called.</summary>
    public required string Target { get; init; }

    /// <summary>
    /// What the call is. A route for HTTP (<c>GET /v1/charges</c>), a statement
    /// for a database (<c>SELECT orders</c>), a command for a cache
    /// (<c>GET session</c>). Defaults to something sensible for the target kind.
    /// </summary>
    public string? Operation { get; init; }

    /// <summary>Chance the call happens at all, 0..1. A cache miss path is 0.05.</summary>
    public double Probability { get; init; } = 1.0;

    /// <summary>Lower bound on how many times it is called. Fan-out, N+1 queries.</summary>
    public int Calls { get; init; } = 1;

    /// <summary>Upper bound, when the call count varies per request.</summary>
    public int? CallsMax { get; init; }

    /// <summary>
    /// Fire-and-forget: the span is recorded but its duration does not count
    /// towards the caller's. Ignored for queues, which are async by nature.
    /// </summary>
    public bool Async { get; init; }

    /// <summary>
    /// Whether a failure downstream fails this service too. False models a
    /// fallback or a circuit breaker, and is how you build a system that
    /// degrades instead of collapsing.
    /// </summary>
    public bool Propagates { get; init; } = true;
}

/// <summary>A queue subscription. The other half of a producer span.</summary>
public sealed record ConsumerConfig
{
    /// <summary>The <c>queue</c> service being consumed from.</summary>
    public required string Queue { get; init; }

    /// <summary>Destination name within the broker — topic, queue, subject.</summary>
    public string? Destination { get; init; }

    /// <summary>Baseline time a message waits before being picked up.</summary>
    public double LagMs { get; init; } = 100;

    /// <summary>Messages handled per consumer span. Above 1 the span uses links.</summary>
    public int Batch { get; init; } = 1;

    /// <summary>Chance processing fails after successful delivery.</summary>
    public double ErrorRate { get; init; }
}

public sealed record DatabaseConfig
{
    /// <summary>Becomes <c>db.system.name</c>: postgresql, mysql, mongodb, redis…</summary>
    public string System { get; init; } = "postgresql";

    /// <summary>Becomes <c>db.namespace</c> — the database or keyspace name.</summary>
    public string? Namespace { get; init; }

    /// <summary>Connection pool size. Drives the saturation metrics.</summary>
    public int MaxConnections { get; init; } = 100;

    /// <summary>Queries slower than this also produce a slow-query log record.</summary>
    public double SlowQueryMs { get; init; } = 500;
}

public sealed record CacheConfig
{
    public string System { get; init; } = "redis";

    /// <summary>Fraction of reads that hit, 0..1. Drives hit/miss metrics.</summary>
    public double HitRate { get; init; } = 0.95;

    /// <summary>Bytes reported as used. Eviction warnings fire as it approaches the max.</summary>
    public long MemoryBytes { get; init; } = 512L * 1024 * 1024;

    public long MaxMemoryBytes { get; init; } = 1024L * 1024 * 1024;
}

public sealed record QueueConfig
{
    /// <summary>Becomes <c>messaging.system</c>: kafka, rabbitmq, sqs, nats…</summary>
    public string System { get; init; } = "kafka";

    /// <summary>Default destination when a producer does not name one.</summary>
    public string? Destination { get; init; }

    /// <summary>Partitions or shards. Reported on the broker's metrics.</summary>
    public int Partitions { get; init; } = 3;
}

public sealed record CronConfig
{
    /// <summary>How often the job fires — <c>5m</c>, <c>1h</c>, <c>30s</c>.</summary>
    public string Every { get; init; } = "5m";

    /// <summary>Random offset added to each firing so jobs do not align perfectly.</summary>
    public string? Jitter { get; init; }

    /// <summary>Name of the job, used for the root span. Defaults to the service id.</summary>
    public string? Job { get; init; }
}
