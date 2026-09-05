using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TraceTown.Traffic.Configuration;
using TraceTown.Traffic.Model;

namespace TraceTown.Traffic.Emit;

/// <summary>
/// Turns a simulated request into real telemetry.
/// </summary>
/// <remarks>
/// Two rules govern everything here. Metrics are recorded for every request,
/// sampled or not, because a sampled metric is simply a wrong number. Spans are
/// recorded only for sampled requests, because that is what sampling is.
/// <para>
/// Timestamps run backwards from the moment the request completed, so a trace
/// is constructed whole rather than held open while the simulation waits. That
/// is also what lets an asynchronous consumer span sit correctly in the past
/// relative to the producer that queued it.
/// </para>
/// </remarks>
public sealed class TelemetryEmitter : IDisposable
{
    private readonly Dictionary<string, ServicePipeline> _pipelines;
    private readonly HttpClient _http;

    public TelemetryEmitter(Topology topology, TrafficConfig config)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(config.Exporter.TimeoutMs) };
        _pipelines = topology.Services.ToDictionary(
            s => s.Id,
            s => new ServicePipeline(s, config, _http),
            StringComparer.Ordinal);
    }

    private long _spansEmitted;
    private long _requestsEmitted;

    /// <summary>Total spans handed to the SDK since start-up.</summary>
    public long SpansEmitted => Interlocked.Read(ref _spansEmitted);

    /// <summary>Requests simulated, including the ones never traced.</summary>
    public long RequestsEmitted => Interlocked.Read(ref _requestsEmitted);

    public void Emit(SimulatedRequest request)
    {
        Interlocked.Increment(ref _requestsEmitted);

        double total = request.Root.Descend().Max(s => s.EndOffsetMs);
        DateTimeOffset origin = request.CompletedAt - TimeSpan.FromMilliseconds(total);

        Walk(request.Root, default, origin, request.Sampled);
    }

    /// <summary>
    /// Records one span and everything below it. The activity is created before
    /// the children so they can be parented to it, and stopped after, so
    /// <see cref="Activity.Current"/> is correct for any log written inside.
    /// </summary>
    private void Walk(SpanPlan plan, ActivityContext parent, DateTimeOffset origin, bool sampled)
    {
        ServicePipeline pipeline = _pipelines[plan.Emitter.Id];

        RecordMetrics(plan, pipeline);

        Activity? activity = null;
        if (sampled)
        {
            // StartOffsetMs is relative to the start of the whole request, not
            // to the parent span — see RequestSimulator.Absolutise.
            DateTimeOffset start = origin + TimeSpan.FromMilliseconds(plan.StartOffsetMs);

            activity = pipeline.Source.StartActivity(
                plan.Name,
                plan.Kind,
                parent,
                tags: null,
                links: null,
                startTime: start);

            if (activity is not null)
            {
                foreach ((string key, object? value) in plan.Attributes)
                {
                    activity.SetTag(key, value);
                }

                if (plan.Instance is { } instance)
                {
                    activity.SetTag(SemConv.Resource.ServiceInstanceId, instance.Id);
                }

                activity.SetStatus(
                    plan.Failed ? ActivityStatusCode.Error : ActivityStatusCode.Unset,
                    plan.Failed ? plan.StatusDescription ?? plan.ErrorType : null);

                Interlocked.Increment(ref _spansEmitted);
            }
        }

        ActivityContext childParent = activity?.Context ?? parent;

        foreach (SpanPlan child in plan.Children)
        {
            Walk(child, childParent, origin, sampled);
        }

        WriteLogs(plan, pipeline, sampled, origin);

        if (activity is not null)
        {
            activity.SetEndTime((origin + TimeSpan.FromMilliseconds(plan.EndOffsetMs)).UtcDateTime);
            activity.Stop();
        }
    }

    private static void RecordMetrics(SpanPlan plan, ServicePipeline pipeline)
    {
        if (plan.Metric == SpanMetric.None)
        {
            return;
        }

        double seconds = plan.DurationMs / 1000.0;
        Instruments instruments = pipeline.Instruments;

        if (plan.Metric is SpanMetric.HttpServer or SpanMetric.MessagingProcess)
        {
            Interlocked.Increment(ref plan.Emitter.Infra.Operations);
        }

        switch (plan.Metric)
        {
            case SpanMetric.HttpServer:
                instruments.HttpServerDuration.Record(seconds, [
                    Tag(plan, SemConv.Http.RequestMethod),
                    Tag(plan, SemConv.Http.Route),
                    Tag(plan, SemConv.Http.ResponseStatusCode),
                    ErrorTag(plan),
                    InstanceTag(plan),
                ]);
                break;

            case SpanMetric.HttpClient:
                instruments.HttpClientDuration.Record(seconds, [
                    Tag(plan, SemConv.Http.RequestMethod),
                    Tag(plan, SemConv.Network.ServerAddress),
                    Tag(plan, SemConv.Http.ResponseStatusCode),
                    ErrorTag(plan),
                ]);
                break;

            case SpanMetric.DbClient:
                instruments.DbClientDuration.Record(seconds, [
                    Tag(plan, SemConv.Db.SystemName),
                    Tag(plan, SemConv.Db.Namespace),
                    Tag(plan, SemConv.Db.OperationName),
                    Tag(plan, SemConv.Db.CollectionName),
                    Tag(plan, SemConv.Network.ServerAddress),
                    ErrorTag(plan),
                ]);
                break;

            case SpanMetric.MessagingPublish:
                instruments.MessagingPublishDuration.Record(seconds, [
                    Tag(plan, SemConv.Messaging.System),
                    Tag(plan, SemConv.Messaging.DestinationName),
                    Tag(plan, SemConv.Messaging.OperationName),
                    ErrorTag(plan),
                ]);

                if (!plan.Failed)
                {
                    instruments.SentMessages.Add(1, [
                        Tag(plan, SemConv.Messaging.System),
                        Tag(plan, SemConv.Messaging.DestinationName),
                    ]);
                }

                break;

            case SpanMetric.MessagingProcess:
                instruments.MessagingProcessDuration.Record(seconds, [
                    Tag(plan, SemConv.Messaging.System),
                    Tag(plan, SemConv.Messaging.DestinationName),
                    Tag(plan, SemConv.Messaging.ConsumerGroupName),
                    ErrorTag(plan),
                    InstanceTag(plan),
                ]);

                instruments.ConsumedMessages.Add(1, [
                    Tag(plan, SemConv.Messaging.System),
                    Tag(plan, SemConv.Messaging.DestinationName),
                    Tag(plan, SemConv.Messaging.ConsumerGroupName),
                ]);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Writes the log records a span produced. Errors always go out; anything
    /// quieter only when the request was sampled, on the grounds that an
    /// unsampled info line is noise nobody will ever correlate to anything.
    /// </summary>
    private void WriteLogs(SpanPlan plan, ServicePipeline pipeline, bool sampled, DateTimeOffset origin)
    {
        if (plan.Logs.Count == 0)
        {
            return;
        }

        foreach (LogPlan log in plan.Logs)
        {
            if (log.Severity < LogSeverity.Error && !sampled)
            {
                continue;
            }

            // An infrastructure log is written by the component itself, which
            // has no idea a trace is happening. Emitting it under the caller's
            // span would be a convenient lie.
            ServicePipeline writer = log.Uncorrelated && plan.Peer is { } peer
                ? _pipelines[peer.Id]
                : pipeline;

            LogLevel level = Level(log.Severity);
            if (!writer.Logger.IsEnabled(level))
            {
                continue;
            }

            Activity? current = Activity.Current;
            if (log.Uncorrelated)
            {
                Activity.Current = null;
            }

            try
            {
                List<KeyValuePair<string, object?>> attributes = [.. log.Attributes];
                if (!log.Uncorrelated && plan.Instance is { } instance)
                {
                    attributes.Add(new KeyValuePair<string, object?>(
                        SemConv.Resource.ServiceInstanceId, instance.Id));
                }

                if (writer.Logger.IsEnabled(level))
                {
                    writer.Logger.Log(
                        level,
                        default,
                        new LogState(log.Message, attributes),
                        exception: null,
                        static (state, _) => state.ToString());
                }
            }
            finally
            {
                Activity.Current = current;
            }
        }
    }

    /// <summary>Drains buffered telemetry without tearing the pipelines down.</summary>
    public void Flush(TimeSpan timeout)
    {
        foreach (ServicePipeline pipeline in _pipelines.Values)
        {
            pipeline.ForceFlush((int)timeout.TotalMilliseconds);
        }
    }

    private static KeyValuePair<string, object?> Tag(SpanPlan plan, string key)
    {
        foreach ((string name, object? value) in plan.Attributes)
        {
            if (name == key)
            {
                return new KeyValuePair<string, object?>(key, value);
            }
        }

        return new KeyValuePair<string, object?>(key, null);
    }

    private static KeyValuePair<string, object?> ErrorTag(SpanPlan plan)
        => new(SemConv.ErrorType, plan.ErrorType);

    private static KeyValuePair<string, object?> InstanceTag(SpanPlan plan)
        => new(SemConv.Resource.ServiceInstanceId, plan.Instance?.Id);

    private static LogLevel Level(LogSeverity severity) => severity switch
    {
        LogSeverity.Debug => LogLevel.Debug,
        LogSeverity.Info => LogLevel.Information,
        LogSeverity.Warn => LogLevel.Warning,
        _ => LogLevel.Error,
    };

    public void Dispose()
    {
        foreach (ServicePipeline pipeline in _pipelines.Values)
        {
            pipeline.Dispose();
        }

        _pipelines.Clear();
        _http.Dispose();
    }
}
