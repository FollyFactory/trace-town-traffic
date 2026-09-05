using TraceTown.Traffic.Configuration;

namespace TraceTown.Traffic.Model;

/// <summary>
/// A configured service, resolved and given somewhere to keep its running
/// counters. This is simulation state only — nothing here knows about OTLP.
/// </summary>
public sealed class ServiceRuntime
{
    public ServiceRuntime(ServiceConfig config, TownConfig town)
    {
        Config = config;
        Id = config.Id;
        Name = config.Name ?? config.Id;
        Kind = config.Kind;
        Group = config.Group ?? town.Name;

        Instances = new ServiceInstance[Math.Max(1, config.Instances)];
        for (int i = 0; i < Instances.Length; i++)
        {
            // A stable, boring instance id. Real orchestrators produce
            // something uglier, but a readable one is easier to follow in a
            // trace viewer and nothing depends on the shape.
            Instances[i] = new ServiceInstance($"{Id}-{i}", i);
        }

        Infra = InfraState.For(config);
    }

    public ServiceConfig Config { get; }

    public string Id { get; }

    /// <summary>Current <c>service.name</c>.</summary>
    public string Name { get; }

    public ServiceKind Kind { get; }

    public string Group { get; }

    /// <summary>
    /// Mutable so a deploy can be simulated at runtime — change it and the
    /// resource attribute changes on everything emitted afterwards.
    /// </summary>
    public string Version { get; set; } = "1.0.0";

    public ServiceInstance[] Instances { get; }

    /// <summary>Counters a collector receiver would scrape from this thing.</summary>
    public InfraState Infra { get; }

    /// <summary>
    /// True when calls to this service produce a server span. Databases, caches,
    /// brokers and third parties do not instrument themselves for us, so the
    /// only span is the caller's client span. See docs/telemetry-model.md.
    /// </summary>
    public bool EmitsServerSpans => Kind is ServiceKind.Gateway or ServiceKind.Api or ServiceKind.Worker;

    /// <summary>
    /// True when the service runs code we own and can therefore report its own
    /// process metrics and application logs.
    /// </summary>
    public bool IsInstrumentedProcess => Kind is not (ServiceKind.External or ServiceKind.Queue
        or ServiceKind.Database or ServiceKind.Cache);

    public override string ToString() => $"{Id} ({Kind})";
}

/// <summary>One replica. Requests are spread across these so per-instance metrics differ.</summary>
public sealed class ServiceInstance(string id, int index)
{
    public string Id { get; } = id;

    public int Index { get; } = index;

    /// <summary>Requests currently in flight. Drives concurrency and CPU metrics.</summary>
    internal int InFlight;

    /// <summary>Smoothed CPU utilisation, 0..1.</summary>
    public double Cpu { get; set; }

    /// <summary>Resident memory in bytes, drifting the way a real heap does.</summary>
    public double MemoryBytes { get; set; } = 128 * 1024 * 1024;
}
