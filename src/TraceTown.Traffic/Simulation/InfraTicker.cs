using TraceTown.Traffic.Configuration;
using TraceTown.Traffic.Faults;
using TraceTown.Traffic.Model;

namespace TraceTown.Traffic.Simulation;

/// <summary>
/// Keeps the infrastructure counters moving between requests: connection pools,
/// queue depth, memory, CPU.
/// </summary>
/// <remarks>
/// Concurrency is derived by Little's law — throughput multiplied by latency —
/// which is why a service that gets slower without getting busier still shows a
/// rising connection count. Getting that relationship right is most of what
/// makes infrastructure metrics worth looking at: it is the mechanism by which a
/// slow database exhausts its caller's pool.
/// </remarks>
public sealed class InfraTicker(Topology topology, FaultBoard faults)
{
    private readonly Dictionary<string, long> _lastOperations = [];
    private DateTimeOffset _lastTick = DateTimeOffset.UtcNow;

    public void Tick(DateTimeOffset now, Rng rng)
    {
        double seconds = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        if (seconds <= 0)
        {
            return;
        }

        foreach (ServiceRuntime service in topology.Services)
        {
            InfraState state = service.Infra;

            _lastOperations.TryGetValue(service.Id, out long previous);
            long current = Interlocked.Read(ref state.Operations);
            _lastOperations[service.Id] = current;

            double throughput = Math.Max(0, (current - previous) / seconds);
            double saturation = faults.Saturation(service, 0, now);
            double latencySeconds = faults.Latency(service, 0, now).P50Ms / 1000.0;

            // Little's law: how many operations are in flight at once.
            double concurrency = throughput * latencySeconds;

            UpdateProcess(service, concurrency, saturation, rng);

            switch (service.Kind)
            {
                case ServiceKind.Database:
                    UpdateDatabase(service, state, concurrency, saturation, rng);
                    break;

                case ServiceKind.Cache:
                    UpdateCache(service, state, saturation, rng);
                    break;

                case ServiceKind.Queue:
                    UpdateQueue(state, throughput);
                    break;

                default:
                    break;
            }
        }
    }

    private static void UpdateProcess(ServiceRuntime service, double concurrency, double saturation, Rng rng)
    {
        if (!service.IsInstrumentedProcess)
        {
            return;
        }

        // Eight concurrent operations per replica is taken as a full core's
        // worth of work. It is a made-up constant, but it makes CPU move with
        // load in the right direction and at a believable scale.
        double perInstance = concurrency / service.Instances.Length / 8.0;
        double target = Math.Clamp(perInstance + (saturation * 0.6), 0.01, 1.0);

        foreach (ServiceInstance instance in service.Instances)
        {
            double jittered = Math.Clamp(target * (0.85 + (rng.NextDouble() * 0.3)), 0, 1);

            // Exponential smoothing: real CPU graphs do not teleport.
            instance.Cpu += (jittered - instance.Cpu) * 0.3;

            double drift = (rng.NextDouble() - 0.45) * 4 * 1024 * 1024;
            double pressure = saturation * 64 * 1024 * 1024;
            instance.MemoryBytes = Math.Clamp(
                instance.MemoryBytes + drift + (pressure * 0.05),
                64 * 1024 * 1024,
                4L * 1024 * 1024 * 1024);
        }
    }

    private static void UpdateDatabase(
        ServiceRuntime service,
        InfraState state,
        double concurrency,
        double saturation,
        Rng rng)
    {
        DatabaseConfig config = service.Config.Database ?? new DatabaseConfig();

        // A pool holds idle connections too, so it never drops to the number
        // actually executing.
        double idle = Math.Max(2, config.MaxConnections * 0.05);
        double target = Math.Min(config.MaxConnections, idle + concurrency + (saturation * config.MaxConnections));

        state.ActiveConnections += (target - state.ActiveConnections) * 0.4;

        // Deadlocks are rare, and under contention they are not.
        if (rng.Chance(0.002 + (saturation * 0.05)))
        {
            Interlocked.Increment(ref state.Deadlocks);
        }
    }

    private static void UpdateCache(ServiceRuntime service, InfraState state, double saturation, Rng rng)
    {
        CacheConfig config = service.Config.Cache ?? new CacheConfig();

        double growth = (rng.NextDouble() * 0.004) + (saturation * 0.02);
        state.MemoryBytes = Math.Min(state.MaxMemoryBytes, state.MemoryBytes * (1 + growth));

        // Once memory is up against the limit the cache starts throwing keys
        // away, which is visible long before anyone notices the hit rate fall.
        double headroom = 1 - (state.MemoryBytes / Math.Max(1, state.MaxMemoryBytes));
        if (headroom < 0.1)
        {
            long evicted = (long)((0.1 - headroom) * 10000 * (0.5 + rng.NextDouble()));
            Interlocked.Add(ref state.Evictions, Math.Max(1, evicted));
            state.MemoryBytes *= 0.98;
        }

        state.ConnectedClients = Math.Max(1, (int)Math.Round(
            state.ConnectedClients + ((rng.NextDouble() - 0.5) * 4)));
    }

    /// <summary>
    /// Queue depth, again by Little's law: how many messages are in the broker
    /// is the arrival rate multiplied by how long each one waits. A lag fault
    /// therefore shows up as a growing backlog without anything else changing,
    /// which is exactly the shape of a worker falling behind.
    /// </summary>
    private static void UpdateQueue(InfraState state, double publishRate)
    {
        double waitSeconds = state.ConsumerLagMs / 1000.0;
        double target = publishRate * waitSeconds;
        state.Backlog += (target - state.Backlog) * 0.5;
    }
}
