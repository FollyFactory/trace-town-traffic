using System.Diagnostics.Metrics;
using TraceTown.Traffic.Model;

namespace TraceTown.Traffic.Emit;

/// <summary>
/// The instruments one simulated service owns. Durations are recorded in
/// seconds because that is the unit the semantic conventions specify, however
/// much the rest of this codebase thinks in milliseconds.
/// </summary>
internal sealed class Instruments : IDisposable
{
    private readonly Meter _meter;

    internal Instruments(ServiceRuntime service)
    {
        _meter = new Meter($"trace-town/{service.Id}", "1.0.0");

        HttpServerDuration = _meter.CreateHistogram<double>(
            SemConv.Http.ServerDuration, "s", "Duration of inbound HTTP requests.");

        HttpClientDuration = _meter.CreateHistogram<double>(
            SemConv.Http.ClientDuration, "s", "Duration of outbound HTTP requests.");

        DbClientDuration = _meter.CreateHistogram<double>(
            SemConv.Db.ClientDuration, "s", "Duration of database client operations.");

        MessagingPublishDuration = _meter.CreateHistogram<double>(
            SemConv.Messaging.ClientOperationDuration, "s", "Duration of messaging publish operations.");

        MessagingProcessDuration = _meter.CreateHistogram<double>(
            SemConv.Messaging.ProcessDuration, "s", "Duration of message processing.");

        SentMessages = _meter.CreateCounter<long>(
            SemConv.Messaging.SentMessages, "{message}", "Messages published.");

        ConsumedMessages = _meter.CreateCounter<long>(
            SemConv.Messaging.ConsumedMessages, "{message}", "Messages consumed.");

        RegisterProcessMetrics(service);
        RegisterInfraMetrics(service);
    }

    internal Histogram<double> HttpServerDuration { get; }

    internal Histogram<double> HttpClientDuration { get; }

    internal Histogram<double> DbClientDuration { get; }

    internal Histogram<double> MessagingPublishDuration { get; }

    internal Histogram<double> MessagingProcessDuration { get; }

    internal Counter<long> SentMessages { get; }

    internal Counter<long> ConsumedMessages { get; }

    internal string MeterName => _meter.Name;

    /// <summary>
    /// CPU and memory, per replica. Only for things running code we own — a
    /// managed database does not hand us its process metrics, its exporter does.
    /// </summary>
    private void RegisterProcessMetrics(ServiceRuntime service)
    {
        if (!service.IsInstrumentedProcess)
        {
            return;
        }

        _meter.CreateObservableGauge(
            SemConv.Infra.ProcessCpu,
            () => service.Instances.Select(i => new Measurement<double>(
                i.Cpu, new KeyValuePair<string, object?>(SemConv.Resource.ServiceInstanceId, i.Id))),
            "1",
            "Process CPU utilisation, 0..1.");

        _meter.CreateObservableGauge(
            SemConv.Infra.ProcessMemory,
            () => service.Instances.Select(i => new Measurement<long>(
                (long)i.MemoryBytes, new KeyValuePair<string, object?>(SemConv.Resource.ServiceInstanceId, i.Id))),
            "By",
            "Resident memory.");
    }

    /// <summary>
    /// The metrics a collector receiver would scrape from the component itself.
    /// This is the only telemetry a database, cache or broker genuinely produces
    /// about its own state — it is not in the trace, and it is not optional if
    /// you want to know why the queries got slow.
    /// </summary>
    private void RegisterInfraMetrics(ServiceRuntime service)
    {
        InfraState state = service.Infra;

        switch (service.Kind)
        {
            case Configuration.ServiceKind.Database:
                _meter.CreateObservableGauge(SemConv.Infra.PostgresBackends,
                    () => (long)state.ActiveConnections, "{connection}", "Open connections.");
                _meter.CreateObservableGauge(SemConv.Infra.PostgresMaxConnections,
                    () => (long)state.MaxConnections, "{connection}", "Configured connection limit.");
                _meter.CreateObservableCounter(SemConv.Infra.PostgresCommits,
                    () => state.Commits, "{transaction}", "Committed transactions.");
                _meter.CreateObservableCounter(SemConv.Infra.PostgresRollbacks,
                    () => state.Rollbacks, "{transaction}", "Rolled back transactions.");
                _meter.CreateObservableCounter(SemConv.Infra.PostgresDeadlocks,
                    () => state.Deadlocks, "{deadlock}", "Deadlocks detected.");
                _meter.CreateObservableCounter(SemConv.Infra.PostgresOperations,
                    () => state.Operations, "{operation}", "Operations executed.");
                break;

            case Configuration.ServiceKind.Cache:
                _meter.CreateObservableCounter(SemConv.Infra.RedisCommands,
                    () => state.Operations, "{command}", "Commands processed.");
                _meter.CreateObservableCounter(SemConv.Infra.RedisKeyspaceHits,
                    () => state.Hits, "{hit}", "Keyspace hits.");
                _meter.CreateObservableCounter(SemConv.Infra.RedisKeyspaceMisses,
                    () => state.Misses, "{miss}", "Keyspace misses.");
                _meter.CreateObservableCounter(SemConv.Infra.RedisEvictedKeys,
                    () => state.Evictions, "{key}", "Keys evicted under memory pressure.");
                _meter.CreateObservableGauge(SemConv.Infra.RedisMemoryUsed,
                    () => (long)state.MemoryBytes, "By", "Memory in use.");
                _meter.CreateObservableGauge(SemConv.Infra.RedisConnectedClients,
                    () => (long)state.ConnectedClients, "{client}", "Connected clients.");
                break;

            case Configuration.ServiceKind.Queue:
                _meter.CreateObservableGauge(SemConv.Infra.BrokerQueueDepth,
                    () => (long)state.Backlog, "{message}", "Messages waiting to be consumed.");
                _meter.CreateObservableGauge(SemConv.Infra.KafkaConsumerLag,
                    () => (long)state.Backlog, "{message}", "Consumer group lag.");
                _meter.CreateObservableCounter(SemConv.Infra.BrokerMessagesPublished,
                    () => state.Published, "{message}", "Messages published.");
                _meter.CreateObservableCounter(SemConv.Infra.BrokerMessagesDelivered,
                    () => state.Delivered, "{message}", "Messages delivered to consumers.");
                break;

            default:
                break;
        }
    }

    public void Dispose() => _meter.Dispose();
}
