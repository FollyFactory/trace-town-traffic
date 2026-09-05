# How it works

One process, one config file, real OpenTelemetry SDKs. The design has one
organising idea: **simulation and emission are separate**. The simulator decides
what happened; the emitter turns that into OTLP. Neither knows much about the
other, which is why both can be tested without a backend.

```
traffic.json ─▶ TrafficConfig ─▶ Topology ────────┐
                                                  ├─▶ RequestSimulator ─▶ SpanPlan tree
               scenarios ─▶ ScenarioRunner ─▶ FaultBoard ─┘                    │
                                                                               ▼
                                                                      TelemetryEmitter
                                                                               │
                                          one OTel SDK pipeline per service ───┤
                                                                               ▼
                                                                            OTLP out
```

## The pieces

| | |
|---|---|
| `Configuration/` | The JSON model, loader and validator. Nothing here knows the simulation exists |
| `Model/Topology` | Services resolved and indexed, with queue subscriptions turned round so a publisher can find its consumers |
| `Model/SpanPlan` | A span the simulation decided happened, before anything is exported |
| `Simulation/RequestSimulator` | Walks the graph and produces the `SpanPlan` tree for one request |
| `Faults/FaultBoard` | Every fault in force, and the answers the simulator needs from them |
| `Faults/ScenarioRunner` | Fires scenario steps as their times come round |
| `Simulation/InfraTicker` | Advances infrastructure counters between requests |
| `Emit/TelemetryEmitter` | Turns a `SpanPlan` tree into spans, metrics and logs |
| `Emit/ServicePipeline` | One service's OTel SDK: resource, `ActivitySource`, meter, logger |
| `Control/` | The HTTP API, the CLI, and the dry-run printer |

## A request, end to end

1. A flow's loop wakes every `tickMs` and asks its load profile for a
   multiplier. Arrivals are drawn from a **Poisson distribution** rather than
   spaced evenly, because evenly spaced arrivals produce percentiles no real
   system has ever had.
2. `RequestSimulator` walks from the entry point. At each service it samples that
   service's own latency, then recurses into its dependencies, accumulating
   elapsed time. Half the service's own work happens before its calls and half
   after, so the waterfall has gaps where the code actually runs.
3. Each call produces a client-side span, and a server span **only if the far
   side is something that instruments itself**. That single condition is what
   makes the output honest — see [telemetry-model.md](telemetry-model.md).
4. Offsets are built relative to each parent, then rebased in one pass so every
   span is relative to the start of the request.
5. A sampling decision is made *after* simulating, so failures can be sampled
   harder than successes.
6. `TelemetryEmitter` walks the tree once: metrics for every span, actual
   `Activity` objects only if sampled.

## Time runs backwards

The whole trace is constructed at once and **timestamped to have just finished**,
rather than being held open while the simulation waits out its own durations.

That is not only cheaper — it is what makes asynchronous work expressible at all.
A consumer span that runs 400ms after the producer would otherwise be a span in
the future, which cannot be emitted. Backdating the request so it *ends* now puts
the whole causal chain, including the async tail, in the past where it belongs.

A consequence worth knowing: a trace's root span reports a duration shorter than
the trace's full time span, because the async continuation outlives it. That is
correct, it is what real async traces look like, and any backend worth using
handles it.

## One pipeline per service

Each simulated service gets its own `TracerProvider`, `MeterProvider` and
`ILoggerFactory`, with its own resource and its own `ActivitySource`, exporting
over OTLP exactly as the real service would. Nothing is hand-rolled: the OTLP
bytes come from the real SDK, so they are spec-correct by construction.

Cross-service parenting works the way it does across processes — the parent's
`ActivityContext` is passed explicitly, and the trace id carries over even though
the two spans are created by different providers.

**The one deliberate departure:** `service.instance.id` is a span and metric
attribute here, not a resource attribute. Strictly it belongs in the resource,
but that would mean a provider per replica, and each provider runs its own export
thread. A fifty-service town with three replicas each would spend more time
scheduling threads than simulating anything. Per-instance metric breakdowns still
work, which is what the attribute is mostly for.

All HTTP exporters share one `HttpClient`, which is why `httpProtobuf` is the
default protocol — with gRPC each provider opens its own channel.

## Sampling

Metrics are recorded for **every** request. Spans only for sampled ones. Errors
are sampled separately, at 100% by default, because a rare failure is the entire
reason anyone opens a trace viewer. Logs follow the same split: errors always,
quieter levels only when sampled.

Real systems get the error-biased behaviour from tail sampling in the collector.
This tool decides at generation time, which reaches the same place by a shorter
road.

## Where the numbers come from

Two pieces of queueing theory do most of the work of making the metrics move
together the way real ones do.

**Little's law** — concurrency equals throughput times latency — derives
connection-pool usage, in-flight request counts and CPU from the request rate and
the current latency. This is what makes a slow database exhaust its caller's
connection pool without anyone wiring the two together: the latency goes up, the
concurrency follows, the pool fills.

The same law gives queue depth: arrival rate times wait time. A worker that falls
behind produces a growing backlog with no change in publish rate at all.

**Lognormal latency**, because that is what service latency is — a hard floor, a
median well below the mean, and a long right tail. Sampling from a normal
distribution gives a tail so thin that a p99 fault barely registers, which makes
for a simulator you cannot test anything with. The distribution is fitted exactly
to the configured p50 and p99.

## Determinism

`simulation.seed` gives each flow its own seeded generator, so a given flow's
sequence of requests is reproducible. It does **not** make the interleaving
between concurrently running flows reproducible, and no design that uses real
wall-clock time across several threads could. If you need exact reproducibility,
run one flow.

## What it deliberately does not model

Worth knowing before you build something on top of it:

- **Real work.** No bytes move, no business logic runs. Latency is drawn from a
  distribution rather than caused by anything.
- **Backpressure.** Load does not feed back into latency on its own; a service
  under 100× traffic gets slower only if you say so with a `saturation` fault.
  Real self-reinforcing collapse is emergent, and modelling it would trade a lot
  of predictability for some realism.
- **Retries and circuit breakers.** `propagates: false` approximates a fallback,
  but there are no retry storms, and a retry storm is a genuinely interesting
  failure mode.
- **Batch consumer span links.** Real batch consumers link to several producer
  spans rather than parenting to one. `messaging.batch.message_count` is emitted
  but every consumer span here has a single parent.
- **Partial trace loss.** Everything sampled is exported. Real collectors drop
  spans under pressure, and a trace with holes in it looks quite different.
- **Clock skew.** All timestamps come from one clock. Cross-host skew produces
  some of the most confusing traces you will ever debug, and it is not here.
- **Collector receivers.** Infrastructure metrics are emitted directly under the
  names a `postgresqlreceiver` or `redisreceiver` would have used, rather than
  being scraped from anything. The names match so dashboards transfer.

## Performance

The limit is the number of pipelines, not the request rate. Each service costs
three providers and their export threads; a twenty-service town is comfortable, a
two-hundred-service one would want the pipelines pooled by group instead.

At default settings — 21 services, ~90 rps of flows, 10% trace sampling — it
uses a fraction of a core. Sampling is the main lever: `--sample 1.0` multiplies
span volume tenfold without changing a single metric, which is exactly the point
of the split.

If a flow cannot keep up with its configured rate, it says so rather than quietly
emitting less traffic than you asked for and letting you draw conclusions from
it.
