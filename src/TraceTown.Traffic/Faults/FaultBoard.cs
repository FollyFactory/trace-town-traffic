using TraceTown.Traffic.Configuration;
using TraceTown.Traffic.Model;

namespace TraceTown.Traffic.Faults;

/// <summary>
/// Every fault currently in force, and the answers the simulator needs from
/// them. Reads happen on every simulated call from several threads at once, so
/// the index is rebuilt and swapped whole rather than locked per read.
/// </summary>
public sealed class FaultBoard(Topology topology)
{
    private readonly Lock _gate = new();
    private readonly List<ActiveFault> _faults = [];
    private volatile Index _index = Index.Empty;

    public event Action<ActiveFault, bool>? Changed;

    public IReadOnlyList<ActiveFault> Snapshot()
    {
        lock (_gate)
        {
            return [.. _faults];
        }
    }

    public ActiveFault Add(FaultConfig config, string origin, DateTimeOffset now)
    {
        var fault = new ActiveFault(config, origin, now);
        lock (_gate)
        {
            _faults.Add(fault);
            Rebuild();
        }

        Changed?.Invoke(fault, true);
        return fault;
    }

    public bool Remove(string id)
    {
        ActiveFault? removed;
        lock (_gate)
        {
            removed = _faults.FirstOrDefault(f => f.Id == id);
            if (removed is null)
            {
                return false;
            }

            _faults.Remove(removed);
            Rebuild();
        }

        Changed?.Invoke(removed, false);
        return true;
    }

    /// <summary>Clears everything, or everything from one origin.</summary>
    public int Clear(string? origin = null)
    {
        List<ActiveFault> removed;
        lock (_gate)
        {
            removed = origin is null
                ? [.. _faults]
                : [.. _faults.Where(f => f.Origin == origin)];

            foreach (ActiveFault fault in removed)
            {
                _faults.Remove(fault);
            }

            Rebuild();
        }

        foreach (ActiveFault fault in removed)
        {
            Changed?.Invoke(fault, false);
        }

        return removed.Count;
    }

    /// <summary>Drops faults that have outlived their <c>duration</c>.</summary>
    public void Expire(DateTimeOffset now)
    {
        List<ActiveFault> expired;
        lock (_gate)
        {
            expired = [.. _faults.Where(f => f.HasExpired(now))];
            if (expired.Count == 0)
            {
                return;
            }

            foreach (ActiveFault fault in expired)
            {
                _faults.Remove(fault);
            }

            Rebuild();
        }

        foreach (ActiveFault fault in expired)
        {
            Changed?.Invoke(fault, false);
        }
    }

    /// <summary>
    /// Latency after faults. Multipliers compose, absolute settings win, and a
    /// ramp eases between the base value and the target.
    /// </summary>
    public LatencyConfig Latency(ServiceRuntime service, int instanceIndex, DateTimeOffset now)
    {
        ActiveFault[] faults = _index.For(service.Id);
        if (faults.Length == 0)
        {
            return service.Config.Latency;
        }

        LatencyConfig latency = service.Config.Latency;
        double p50 = latency.P50Ms;
        double p99 = latency.P99Ms;

        foreach (ActiveFault fault in faults)
        {
            if (fault.Config.Kind != FaultKind.Latency
                || !fault.Covers(instanceIndex, service.Instances.Length))
            {
                continue;
            }

            double intensity = fault.Intensity(now);

            if (fault.Config.Multiplier is { } multiplier)
            {
                double scale = 1.0 + ((multiplier - 1.0) * intensity);
                p50 *= scale;
                p99 *= scale;
            }

            if (fault.Config.P50Ms is { } targetP50)
            {
                p50 += (targetP50 - p50) * intensity;
            }

            if (fault.Config.P99Ms is { } targetP99)
            {
                p99 += (targetP99 - p99) * intensity;
            }

            // A latency fault with no parameters means "noticeably slower".
            if (fault.Config is { Multiplier: null, P50Ms: null, P99Ms: null })
            {
                double scale = 1.0 + (4.0 * intensity);
                p50 *= scale;
                p99 *= scale;
            }
        }

        // Saturation drags latency up on its own — a pool at its limit makes
        // callers queue, which is latency they can see.
        double saturation = Saturation(service, instanceIndex, now);
        if (saturation > 0)
        {
            double scale = 1.0 + (saturation * saturation * 9.0);
            p50 *= scale;
            p99 *= scale;
        }

        return latency with { P50Ms = p50, P99Ms = Math.Max(p50, p99) };
    }

    /// <summary>Error rate after faults, 0..1.</summary>
    public double ErrorRate(ServiceRuntime service, int instanceIndex, DateTimeOffset now)
    {
        ActiveFault[] faults = _index.For(service.Id);
        double rate = service.Config.ErrorRate;

        foreach (ActiveFault fault in faults)
        {
            if (!fault.Covers(instanceIndex, service.Instances.Length))
            {
                continue;
            }

            double intensity = fault.Intensity(now);

            switch (fault.Config.Kind)
            {
                case FaultKind.Outage:
                    rate = Math.Max(rate, intensity);
                    break;

                case FaultKind.Errors:
                    double target = fault.Config.ErrorRate ?? 0.5;
                    rate += (target - rate) * intensity;
                    break;

                case FaultKind.Saturation:
                    // Saturation sheds load before it fails outright.
                    double level = fault.Intensity(now);
                    rate = Math.Max(rate, Math.Max(0, level - 0.7) * 1.5);
                    break;

                default:
                    break;
            }
        }

        return Math.Clamp(rate, 0.0, 1.0);
    }

    /// <summary>
    /// True when the service is refusing connections. Distinct from a high error
    /// rate: a refused connection fails in a millisecond, and that difference is
    /// exactly what tells you whether something is down or merely struggling.
    /// </summary>
    public bool IsDown(ServiceRuntime service, int instanceIndex, DateTimeOffset now)
    {
        foreach (ActiveFault fault in _index.For(service.Id))
        {
            if (fault.Config.Kind == FaultKind.Outage
                && fault.Covers(instanceIndex, service.Instances.Length)
                && fault.Intensity(now) >= 1.0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Extra delay before a queued message is picked up.</summary>
    public double QueueLagMs(ServiceRuntime queue, ServiceRuntime consumer, double baseLagMs, DateTimeOffset now)
    {
        double lag = baseLagMs;

        foreach (string id in (string[])[queue.Id, consumer.Id])
        {
            foreach (ActiveFault fault in _index.For(id))
            {
                if (fault.Config.Kind != FaultKind.QueueLag)
                {
                    continue;
                }

                double intensity = fault.Intensity(now);
                double target = fault.Config.LagMs ?? (baseLagMs * 50);
                lag += (target - baseLagMs) * intensity;

                if (fault.Config.Multiplier is { } multiplier)
                {
                    lag *= 1.0 + ((multiplier - 1.0) * intensity);
                }
            }
        }

        return Math.Max(0, lag);
    }

    /// <summary>How much a flow's rate is being scaled, 1.0 for untouched.</summary>
    public double TrafficMultiplier(string flowId, DateTimeOffset now)
    {
        double multiplier = 1.0;

        foreach (ActiveFault fault in _index.ForFlow(flowId))
        {
            if (fault.Config.Kind != FaultKind.Traffic)
            {
                continue;
            }

            double intensity = fault.Intensity(now);
            double target = fault.Config.Multiplier ?? 3.0;
            multiplier *= 1.0 + ((target - 1.0) * intensity);
        }

        return Math.Max(0, multiplier);
    }

    /// <summary>How exhausted the service's own resources are, 0..1.</summary>
    public double Saturation(ServiceRuntime service, int instanceIndex, DateTimeOffset now)
    {
        double level = 0;

        foreach (ActiveFault fault in _index.For(service.Id))
        {
            if (fault.Config.Kind == FaultKind.Saturation
                && fault.Covers(instanceIndex, service.Instances.Length))
            {
                level = Math.Max(level, fault.Intensity(now) * (fault.Config.Multiplier ?? 1.0));
            }
        }

        return Math.Clamp(level, 0.0, 1.0);
    }

    public bool HasAny => _index.Any;

    /// <summary>Rebuilds the read index. Called under <see cref="_gate"/>.</summary>
    private void Rebuild()
    {
        Dictionary<string, List<ActiveFault>> services = [];
        Dictionary<string, List<ActiveFault>> flows = [];

        foreach (ActiveFault fault in _faults)
        {
            string target = fault.Config.Target;
            bool isFlow = target.StartsWith("flow:", StringComparison.Ordinal);
            string bare = isFlow ? target["flow:".Length..] : target;

            Dictionary<string, List<ActiveFault>> map = isFlow ? flows : services;
            IEnumerable<string> candidates = isFlow
                ? topology.Flows.Select(f => f.Id)
                : topology.ServiceIds;

            foreach (string id in Glob.Select(bare, candidates))
            {
                if (!map.TryGetValue(id, out List<ActiveFault>? list))
                {
                    list = [];
                    map[id] = list;
                }

                list.Add(fault);
            }
        }

        _index = new Index(
            services.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal),
            flows.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal));
    }

    /// <summary>Immutable so readers never take a lock.</summary>
    private sealed class Index(
        Dictionary<string, ActiveFault[]> services,
        Dictionary<string, ActiveFault[]> flows)
    {
        public static Index Empty { get; } = new([], []);

        public bool Any => services.Count > 0 || flows.Count > 0;

        public ActiveFault[] For(string serviceId)
            => services.TryGetValue(serviceId, out ActiveFault[]? list) ? list : [];

        public ActiveFault[] ForFlow(string flowId)
            => flows.TryGetValue(flowId, out ActiveFault[]? list) ? list : [];
    }
}
