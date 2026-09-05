using System.Diagnostics;
using System.Globalization;
using TraceTown.Traffic.Configuration;
using TraceTown.Traffic.Emit;
using TraceTown.Traffic.Faults;
using TraceTown.Traffic.Model;

namespace TraceTown.Traffic.Simulation;

/// <summary>
/// Walks the topology and decides what one request did: which services it
/// touched, how long each took, what failed, and which spans that would have
/// produced. Emits nothing — the result is a plan the exporter turns into OTLP.
/// </summary>
public sealed class RequestSimulator(Topology topology, FaultBoard faults, SimulationConfig simulation)
{
    /// <summary>Cost of a network hop, before the far end does any work.</summary>
    private const double NetworkMs = 0.8;

    public SimulatedRequest Simulate(FlowConfig flow, Rng rng, DateTimeOffset now)
    {
        ServiceRuntime entry = topology[flow.Entry];
        var context = new RequestContext
        {
            FlowId = flow.Id,
            Rng = rng,
            Now = now,
            Always = flow.Always,
            Except = flow.Except,
            MaxDepth = flow.Depth ?? simulation.MaxDepth,
        };

        (string method, string route) = Operations.Http(flow.Route, entry.Id);
        ServiceInstance instance = PickInstance(entry, rng);

        var root = new SpanPlan
        {
            Emitter = entry,
            Kind = ActivityKind.Server,
            Name = $"{method} {route}",
            Instance = instance,
            Metric = SpanMetric.HttpServer,
        };

        root.Add(SemConv.Http.RequestMethod, method)
            .Add(SemConv.Http.Route, route)
            .Add(SemConv.Http.UrlPath, route)
            .Add(SemConv.Http.UrlScheme, "https")
            .Add(SemConv.Network.ServerAddress, entry.Id)
            .Add(SemConv.Network.ServerPort, 443)
            .Add(SemConv.Network.ProtocolVersion, "1.1")
            .Add("trace_town.flow", flow.Id);

        // A request rejected at the edge never reaches anything downstream.
        // Modelled separately from a server failure because a 4xx is not an
        // incident, and a town that rains every time someone sends a bad
        // request is a town nobody trusts.
        if (rng.Chance(flow.ClientErrorRate))
        {
            int status = rng.Pick<int>([400, 401, 403, 404, 422]);
            root.DurationMs = LatencySampler.Sample(entry.Config.Latency, rng) * 0.3;
            root.Failed = false;
            root.ErrorType = status.ToString(CultureInfo.InvariantCulture);
            root.Add(SemConv.Http.ResponseStatusCode, status);
            root.Logs.Add(new LogPlan(LogSeverity.Info, $"{method} {route} rejected with {status}")
                .With("http.response.status_code", status));

            return new SimulatedRequest { FlowId = flow.Id, Root = root };
        }

        root.DurationMs = Execute(entry, instance, root, context, depth: 0);
        Finish(root, entry, isServer: true, rng);
        Absolutise(root, 0);

        return new SimulatedRequest { FlowId = flow.Id, Root = root };
    }

    /// <summary>A scheduled job: a root span with no caller and no HTTP about it.</summary>
    public SimulatedRequest SimulateCron(ServiceRuntime service, Rng rng, DateTimeOffset now)
    {
        var context = new RequestContext
        {
            FlowId = $"cron:{service.Id}",
            Rng = rng,
            Now = now,
            MaxDepth = simulation.MaxDepth,
        };

        string job = service.Config.Cron?.Job ?? service.Id;
        ServiceInstance instance = service.Instances[0];

        var root = new SpanPlan
        {
            Emitter = service,
            Kind = ActivityKind.Internal,
            Name = job,
            Instance = instance,
        };

        root.Add("cron.job.name", job)
            .Add("cron.schedule", service.Config.Cron?.Every ?? "5m")
            .Add("trace_town.flow", context.FlowId);

        root.DurationMs = Execute(service, instance, root, context, depth: 0);
        Finish(root, service, isServer: true, rng);
        Absolutise(root, 0);

        return new SimulatedRequest { FlowId = context.FlowId, Root = root };
    }

    /// <summary>
    /// Rebases every span's offset from "relative to my parent" to "relative to
    /// the start of the request". The recursion has to build offsets the first
    /// way, because a span does not know where its parent sits until the parent
    /// is finished; everything that reads the tree afterwards — the exporter's
    /// timestamps, the total duration, the printer — wants the second.
    /// </summary>
    private static void Absolutise(SpanPlan plan, double parentStartMs)
    {
        plan.StartOffsetMs += parentStartMs;

        foreach (SpanPlan child in plan.Children)
        {
            Absolutise(child, plan.StartOffsetMs);
        }
    }

    /// <summary>
    /// Runs a service's own work and everything it calls, appending spans to
    /// <paramref name="span"/>. Returns how long the service took in total.
    /// </summary>
    private double Execute(
        ServiceRuntime service,
        ServiceInstance instance,
        SpanPlan span,
        RequestContext context,
        int depth)
    {
        LatencyConfig latency = faults.Latency(service, instance.Index, context.Now);
        double self = LatencySampler.Sample(latency, context.Rng);

        // Half the service's own work before its calls and half after, so the
        // waterfall has gaps where the code actually runs rather than one solid
        // bar with children hanging off the front.
        double elapsed = self / 2;

        if (depth < context.MaxDepth)
        {
            foreach (DependencyConfig dependency in service.Config.Dependencies)
            {
                if (!context.Allows(dependency.Target)
                    || !topology.TryGet(dependency.Target, out ServiceRuntime target))
                {
                    continue;
                }

                bool forced = context.Forces(dependency.Target);
                if (!forced && !context.Rng.Chance(dependency.Probability))
                {
                    continue;
                }

                int calls = dependency.CallsMax is { } max
                    ? context.Rng.Next(dependency.Calls, max)
                    : dependency.Calls;

                for (int i = 0; i < calls; i++)
                {
                    double consumed = Call(service, instance, span, target, dependency, context, depth, elapsed);

                    // An asynchronous call is dispatched and forgotten: its span
                    // is real, but the caller does not wait for it.
                    if (!dependency.Async || target.Kind == ServiceKind.Queue)
                    {
                        elapsed += consumed;
                    }
                }
            }
        }

        // Failing early is cheaper than failing late, except when the failure is
        // a timeout, and both matter to whoever is reading the trace.
        if (!span.Failed && context.Rng.Chance(faults.ErrorRate(service, instance.Index, context.Now)))
        {
            span.Failed = true;
            span.ErrorType = faults.Saturation(service, instance.Index, context.Now) > 0.5
                ? "resource_exhausted"
                : "internal_error";
        }

        return elapsed + (self / 2);
    }

    /// <summary>
    /// One outbound call. Produces the caller's client-side span, and a server
    /// span on the far side only when the far side is something that
    /// instruments itself.
    /// </summary>
    private double Call(
        ServiceRuntime caller,
        ServiceInstance callerInstance,
        SpanPlan parent,
        ServiceRuntime target,
        DependencyConfig dependency,
        RequestContext context,
        int depth,
        double offset)
    {
        SpanPlan span = target.Kind switch
        {
            ServiceKind.Database => DatabaseCall(caller, target, dependency, context),
            ServiceKind.Cache => CacheCall(caller, target, dependency, context),
            ServiceKind.Queue => QueueCall(caller, target, dependency, context, depth),
            _ => HttpCall(caller, target, dependency, context, depth),
        };

        span.StartOffsetMs = offset;
        span.Instance = callerInstance;
        parent.Children.Add(span);

        if (span.Failed && dependency.Propagates)
        {
            parent.Failed = true;
            parent.ErrorType ??= span.ErrorType;
            parent.Logs.Add(new LogPlan(
                    LogSeverity.Error,
                    $"call to {target.Id} failed: {span.ErrorType}")
                .With("peer.service", target.Id)
                .With(SemConv.ErrorType, span.ErrorType));
        }

        return span.DurationMs;
    }

    private SpanPlan HttpCall(
        ServiceRuntime caller,
        ServiceRuntime target,
        DependencyConfig dependency,
        RequestContext context,
        int depth)
    {
        (string method, string route) = Operations.Http(dependency.Operation, target.Id);

        var span = new SpanPlan
        {
            Emitter = caller,
            Kind = ActivityKind.Client,
            Name = $"{method} {route}",
            Peer = target,
            Metric = SpanMetric.HttpClient,
        };

        span.Add(SemConv.Http.RequestMethod, method)
            .Add(SemConv.Network.ServerAddress, target.Id)
            .Add(SemConv.Network.ServerPort, target.Kind == ServiceKind.External ? 443 : 8080)
            .Add(SemConv.Http.UrlFull, $"https://{target.Id}{route}");

        ServiceInstance instance = PickInstance(target, context.Rng);

        if (faults.IsDown(target, instance.Index, context.Now))
        {
            // Connection refused: fast, and with no server span, because
            // nothing was there to record one.
            span.DurationMs = 0.4 + (context.Rng.NextDouble() * 1.5);
            span.Failed = true;
            span.ErrorType = "connection_error";
            span.Add(SemConv.ErrorType, "connection_error");
            return span;
        }

        double network = NetworkMs * (0.5 + context.Rng.NextDouble());

        if (!target.EmitsServerSpans)
        {
            // A third party. We see our own client span and nothing else — the
            // building on the far side is drawn entirely from this one span.
            LatencyConfig latency = faults.Latency(target, instance.Index, context.Now);
            span.DurationMs = network + LatencySampler.Sample(latency, context.Rng);
            bool failed = context.Rng.Chance(faults.ErrorRate(target, instance.Index, context.Now));
            ApplyHttpStatus(span, failed, context.Rng);
            return span;
        }

        var server = new SpanPlan
        {
            Emitter = target,
            Kind = ActivityKind.Server,
            Name = $"{method} {route}",
            Instance = instance,
            Metric = SpanMetric.HttpServer,
            StartOffsetMs = network / 2,
        };

        server.Add(SemConv.Http.RequestMethod, method)
            .Add(SemConv.Http.Route, route)
            .Add(SemConv.Http.UrlPath, route)
            .Add(SemConv.Http.UrlScheme, "https")
            .Add(SemConv.Network.ServerAddress, target.Id)
            .Add(SemConv.Network.ClientAddress, caller.Id)
            .Add("trace_town.flow", context.FlowId);

        server.DurationMs = Execute(target, instance, server, context, depth + 1);
        Finish(server, target, isServer: true, context.Rng);

        span.Children.Add(server);
        span.DurationMs = network + server.DurationMs;
        ApplyHttpStatus(span, server.Failed, context.Rng);

        return span;
    }

    private SpanPlan DatabaseCall(
        ServiceRuntime caller,
        ServiceRuntime target,
        DependencyConfig dependency,
        RequestContext context)
    {
        (string operation, string? collection, string statement) = Operations.Db(dependency.Operation, target);
        DatabaseConfig config = target.Config.Database ?? new DatabaseConfig();

        // The span belongs to the caller. Nothing inside the database records
        // anything — see docs/telemetry-model.md.
        var span = new SpanPlan
        {
            Emitter = caller,
            Kind = ActivityKind.Client,
            Name = collection is null ? operation : $"{operation} {collection}",
            Peer = target,
            Metric = SpanMetric.DbClient,
        };

        span.Add(SemConv.Db.SystemName, config.System)
            .Add(SemConv.Db.Namespace, config.Namespace ?? target.Id)
            .Add(SemConv.Db.OperationName, operation)
            .Add(SemConv.Db.CollectionName, collection)
            .Add(SemConv.Db.QueryText, statement)
            .Add(SemConv.Network.ServerAddress, target.Id)
            .Add(SemConv.Network.ServerPort, DefaultPort(config.System));

        ServiceInstance instance = target.Instances[0];

        if (faults.IsDown(target, instance.Index, context.Now))
        {
            span.DurationMs = 0.5 + context.Rng.NextDouble();
            span.Failed = true;
            span.ErrorType = "connection_error";
            span.Add(SemConv.ErrorType, "connection_error");
            target.Infra.Errors++;
            return span;
        }

        double saturation = faults.Saturation(target, instance.Index, context.Now);
        LatencyConfig latency = faults.Latency(target, instance.Index, context.Now);
        span.DurationMs = LatencySampler.Sample(latency, context.Rng);

        bool failed = context.Rng.Chance(faults.ErrorRate(target, instance.Index, context.Now));
        if (failed)
        {
            span.Failed = true;
            // A pool that has run out looks nothing like a query that threw.
            span.ErrorType = saturation > 0.5 ? "pool_timeout" : "query_error";
            span.Add(SemConv.ErrorType, span.ErrorType);
            target.Infra.Errors++;
            Interlocked.Increment(ref target.Infra.Rollbacks);
        }
        else
        {
            span.Add(SemConv.Db.RowsAffected, context.Rng.Next(0, 50));
            Interlocked.Increment(ref target.Infra.Commits);
        }

        Interlocked.Increment(ref target.Infra.Operations);

        // The slow-query log is written by the database, about itself, with no
        // idea which trace caused it. That disconnect is real, and pretending
        // otherwise would teach the wrong lesson.
        if (span.DurationMs >= config.SlowQueryMs)
        {
            Interlocked.Increment(ref target.Infra.SlowQueries);
            span.Logs.Add(new LogPlan(
                    LogSeverity.Warn,
                    $"duration: {span.DurationMs:F1} ms  statement: {statement}")
            {
                Uncorrelated = true,
            }
                .With("db.namespace", config.Namespace ?? target.Id)
                .With("log.source", target.Id));
        }

        return span;
    }

    private SpanPlan CacheCall(
        ServiceRuntime caller,
        ServiceRuntime target,
        DependencyConfig dependency,
        RequestContext context)
    {
        (string operation, string key) = Operations.Cache(dependency.Operation, target.Id);
        CacheConfig config = target.Config.Cache ?? new CacheConfig();

        var span = new SpanPlan
        {
            Emitter = caller,
            Kind = ActivityKind.Client,
            Name = $"{operation} {key}",
            Peer = target,
            Metric = SpanMetric.DbClient,
        };

        span.Add(SemConv.Db.SystemName, config.System)
            .Add(SemConv.Db.OperationName, operation)
            .Add(SemConv.Network.ServerAddress, target.Id)
            .Add(SemConv.Network.ServerPort, DefaultPort(config.System));

        ServiceInstance instance = target.Instances[0];

        if (faults.IsDown(target, instance.Index, context.Now))
        {
            span.DurationMs = 0.3 + (context.Rng.NextDouble() * 0.5);
            span.Failed = true;
            span.ErrorType = "connection_error";
            span.Add(SemConv.ErrorType, "connection_error");
            return span;
        }

        LatencyConfig latency = faults.Latency(target, instance.Index, context.Now);
        span.DurationMs = LatencySampler.Sample(latency, context.Rng);

        bool isRead = operation is "GET" or "MGET" or "EXISTS" or "HGET";
        if (isRead)
        {
            bool hit = context.Rng.Chance(config.HitRate);
            span.Add("cache.hit", hit);
            if (hit)
            {
                Interlocked.Increment(ref target.Infra.Hits);
            }
            else
            {
                Interlocked.Increment(ref target.Infra.Misses);
            }
        }

        Interlocked.Increment(ref target.Infra.Operations);

        if (context.Rng.Chance(faults.ErrorRate(target, instance.Index, context.Now)))
        {
            span.Failed = true;
            span.ErrorType = "cache_error";
            span.Add(SemConv.ErrorType, "cache_error");
        }

        return span;
    }

    /// <summary>
    /// Publishing to a broker, and everything the message goes on to cause. The
    /// producer span is short — it is a network write — but the consumer span
    /// underneath it starts after the queue lag and can run long after the
    /// original request has returned to its caller.
    /// </summary>
    private SpanPlan QueueCall(
        ServiceRuntime caller,
        ServiceRuntime target,
        DependencyConfig dependency,
        RequestContext context,
        int depth)
    {
        QueueConfig config = target.Config.Queue ?? new QueueConfig();
        string destination = dependency.Operation ?? config.Destination ?? target.Id;
        int partition = context.Rng.Next(0, Math.Max(0, config.Partitions - 1));

        var span = new SpanPlan
        {
            Emitter = caller,
            Kind = ActivityKind.Producer,
            Name = $"send {destination}",
            Peer = target,
            Metric = SpanMetric.MessagingPublish,
        };

        span.Add(SemConv.Messaging.System, config.System)
            .Add(SemConv.Messaging.OperationName, "send")
            .Add(SemConv.Messaging.OperationType, "send")
            .Add(SemConv.Messaging.DestinationName, destination)
            .Add(SemConv.Messaging.DestinationPartitionId, partition.ToString(CultureInfo.InvariantCulture))
            .Add(SemConv.Messaging.MessageId, Operations.MessageId(context.Rng))
            .Add(SemConv.Messaging.MessageBodySize, context.Rng.Next(200, 8000))
            .Add(SemConv.Network.ServerAddress, target.Id);

        if (faults.IsDown(target, 0, context.Now))
        {
            span.DurationMs = 0.5 + context.Rng.NextDouble();
            span.Failed = true;
            span.ErrorType = "connection_error";
            span.Add(SemConv.ErrorType, "connection_error");
            return span;
        }

        LatencyConfig publishLatency = faults.Latency(target, 0, context.Now);
        span.DurationMs = LatencySampler.Sample(publishLatency, context.Rng);
        Interlocked.Increment(ref target.Infra.Published);

        if (depth >= context.MaxDepth)
        {
            return span;
        }

        foreach (Subscription subscription in topology.ConsumersOf(target.Id))
        {
            if (!context.Allows(subscription.Consumer.Id))
            {
                continue;
            }

            SpanPlan consumer = ConsumeMessage(subscription, destination, partition, context, depth);

            // ConsumeMessage has already set the queue lag; the message cannot
            // be picked up before the publish that put it there has finished.
            consumer.StartOffsetMs += span.DurationMs;
            span.Children.Add(consumer);
        }

        return span;
    }

    private SpanPlan ConsumeMessage(
        Subscription subscription,
        string destination,
        int partition,
        RequestContext context,
        int depth)
    {
        ServiceRuntime consumer = subscription.Consumer;
        ServiceInstance instance = PickInstance(consumer, context.Rng);

        double lag = faults.QueueLagMs(subscription.Queue, consumer, subscription.Config.LagMs, context.Now);
        subscription.Queue.Infra.ConsumerLagMs = lag;

        var span = new SpanPlan
        {
            Emitter = consumer,
            Kind = ActivityKind.Consumer,
            Name = $"process {destination}",
            Instance = instance,
            Metric = SpanMetric.MessagingProcess,

            // The gap between publish and pick-up is the queue lag, and drawing
            // it as dead time in the trace is the point: it is where a backed-up
            // system spends its day.
            StartOffsetMs = lag,
        };

        span.Add(SemConv.Messaging.System, subscription.Queue.Config.Queue?.System ?? "kafka")
            .Add(SemConv.Messaging.OperationName, "process")
            .Add(SemConv.Messaging.OperationType, "process")
            .Add(SemConv.Messaging.DestinationName, destination)
            .Add(SemConv.Messaging.DestinationPartitionId, partition.ToString(CultureInfo.InvariantCulture))
            .Add(SemConv.Messaging.ConsumerGroupName, subscription.Group)
            .Add("messaging.message.queue_time_ms", Math.Round(lag, 2))
            .Add("trace_town.flow", context.FlowId);

        if (subscription.Config.Batch > 1)
        {
            span.Add(SemConv.Messaging.BatchMessageCount, subscription.Config.Batch);
        }

        span.DurationMs = Execute(consumer, instance, span, context, depth + 1);
        Interlocked.Increment(ref subscription.Queue.Infra.Delivered);

        if (context.Rng.Chance(subscription.Config.ErrorRate))
        {
            span.Failed = true;
            span.ErrorType = "processing_error";
        }

        Finish(span, consumer, isServer: true, context.Rng);
        return span;
    }

    /// <summary>Applies the status code and error attributes a client span ends with.</summary>
    private static void ApplyHttpStatus(SpanPlan span, bool failed, Rng rng)
    {
        int status = failed ? rng.Pick<int>([500, 502, 503, 504]) : 200;
        span.Add(SemConv.Http.ResponseStatusCode, status);

        if (failed)
        {
            span.Failed = true;
            span.ErrorType = status.ToString(CultureInfo.InvariantCulture);
            span.Add(SemConv.ErrorType, span.ErrorType);
        }
    }

    /// <summary>Stamps the closing attributes and the error log on a server span.</summary>
    private static void Finish(SpanPlan span, ServiceRuntime service, bool isServer, Rng rng)
    {
        if (!isServer || span.Kind == ActivityKind.Consumer)
        {
            if (span.Failed)
            {
                span.Add(SemConv.ErrorType, span.ErrorType ?? "error");
                span.Logs.Add(new LogPlan(LogSeverity.Error, $"{span.Name} failed: {span.ErrorType}")
                    .With(SemConv.ErrorType, span.ErrorType));
            }

            return;
        }

        int status = span.Failed ? rng.Pick<int>([500, 500, 503]) : 200;
        span.Add(SemConv.Http.ResponseStatusCode, status);
        span.Add(SemConv.Http.ResponseBodySize, rng.Next(120, 20000));

        if (span.Failed)
        {
            span.ErrorType ??= status.ToString(CultureInfo.InvariantCulture);
            span.Add(SemConv.ErrorType, span.ErrorType);
            span.Logs.Add(new LogPlan(LogSeverity.Error, $"unhandled failure serving {span.Name}")
                .With(SemConv.ErrorType, span.ErrorType)
                .With("service.name", service.Name));
        }
    }

    private static ServiceInstance PickInstance(ServiceRuntime service, Rng rng)
        => service.Instances.Length == 1
            ? service.Instances[0]
            : service.Instances[rng.Next(0, service.Instances.Length - 1)];

    private static int DefaultPort(string system) => system switch
    {
        "postgresql" => 5432,
        "mysql" or "mariadb" => 3306,
        "mongodb" => 27017,
        "redis" or "valkey" => 6379,
        "memcached" => 11211,
        "cassandra" => 9042,
        "elasticsearch" => 9200,
        "clickhouse" => 8123,
        _ => 0,
    };
}
