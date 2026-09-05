using TraceTown.Traffic.Configuration;

namespace TraceTown.Traffic.Model;

/// <summary>
/// The counters an infrastructure component exposes to a scraper. These are the
/// signal a database or broker genuinely produces about itself — it does not
/// produce spans — so they are modelled separately from anything request-shaped.
/// </summary>
public sealed class InfraState
{
    private InfraState(ServiceKind kind) => Kind = kind;

    public ServiceKind Kind { get; }

    // Shared
    internal long Operations;
    internal long Errors;

    // Database
    internal long Commits;
    internal long Rollbacks;
    internal long Deadlocks;
    internal long SlowQueries;
    internal int MaxConnections = 100;
    internal double ActiveConnections;

    // Cache
    internal long Hits;
    internal long Misses;
    internal long Evictions;
    internal double MemoryBytes;
    internal double MaxMemoryBytes;
    internal int ConnectedClients = 1;

    // Queue
    internal long Published;
    internal long Delivered;
    /// <summary>Messages sitting in the broker, unconsumed. The interesting one.</summary>
    internal double Backlog;
    internal double ConsumerLagMs;
    internal int Partitions = 1;
    internal int Consumers;

    public static InfraState For(ServiceConfig config)
    {
        var state = new InfraState(config.Kind);

        if (config.Database is { } database)
        {
            state.MaxConnections = database.MaxConnections;
            state.ActiveConnections = Math.Min(4, database.MaxConnections);
        }

        if (config.Cache is { } cache)
        {
            state.MemoryBytes = cache.MemoryBytes;
            state.MaxMemoryBytes = cache.MaxMemoryBytes;
        }

        if (config.Queue is { } queue)
        {
            state.Partitions = queue.Partitions;
        }

        return state;
    }
}
