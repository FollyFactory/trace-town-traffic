using TraceTown.Traffic.Configuration;

namespace TraceTown.Traffic.Model;

/// <summary>
/// The resolved system: services indexed by id, and the queue subscriptions
/// turned round so a publisher can find its consumers. Built once at start-up
/// and swapped wholesale when the config is reloaded.
/// </summary>
public sealed class Topology
{
    private readonly Dictionary<string, ServiceRuntime> _services;
    private readonly Dictionary<string, List<Subscription>> _subscriptions;

    public Topology(TrafficConfig config)
    {
        Config = config;
        _services = config.Services.ToDictionary(
            s => s.Id,
            s => new ServiceRuntime(s, config.Town),
            StringComparer.Ordinal);

        foreach (ServiceRuntime service in _services.Values)
        {
            service.Version = service.Config.Version;
        }

        _subscriptions = [];
        foreach (ServiceRuntime consumer in _services.Values)
        {
            foreach (ConsumerConfig subscription in consumer.Config.Consumes)
            {
                if (!_services.TryGetValue(subscription.Queue, out ServiceRuntime? queue))
                {
                    continue;
                }

                if (!_subscriptions.TryGetValue(subscription.Queue, out List<Subscription>? list))
                {
                    list = [];
                    _subscriptions[subscription.Queue] = list;
                }

                list.Add(new Subscription(consumer, queue, subscription));
                queue.Infra.Consumers++;
            }
        }

        Flows = config.Flows;
    }

    public TrafficConfig Config { get; }

    public IReadOnlyList<FlowConfig> Flows { get; }

    public IReadOnlyCollection<ServiceRuntime> Services => _services.Values;

    public IReadOnlyCollection<string> ServiceIds => _services.Keys;

    public ServiceRuntime this[string id] => _services[id];

    public bool TryGet(string id, out ServiceRuntime service) => _services.TryGetValue(id, out service!);

    /// <summary>Everything that consumes from the given queue.</summary>
    public IReadOnlyList<Subscription> ConsumersOf(string queueId)
        => _subscriptions.TryGetValue(queueId, out List<Subscription>? list) ? list : [];
}

/// <summary>A worker's subscription to a queue — the far side of a producer span.</summary>
public sealed record Subscription(ServiceRuntime Consumer, ServiceRuntime Queue, ConsumerConfig Config)
{
    public string Destination => Config.Destination
        ?? Queue.Config.Queue?.Destination
        ?? Queue.Id;

    /// <summary>
    /// Consumer group name. Derived from the worker rather than configured,
    /// because one worker consuming one queue only ever has one group and
    /// making people name it is a tax.
    /// </summary>
    public string Group => $"{Consumer.Id}-group";
}
