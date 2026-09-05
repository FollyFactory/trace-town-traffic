# trace-town-traffic

Synthetic OpenTelemetry traffic for a whole distributed system, described in one
JSON file.

Point it at an OTLP endpoint and it produces the traces, metrics and logs a real
system would: gateways and APIs with correct client and server spans, message
brokers with trace context carried through the messages, databases and caches
that emit metrics and slow-query logs but **no spans of their own**, third
parties you can only see from the outside, and cron jobs that fire on a
schedule. Then you break it on purpose — a dependency that slows, a bad deploy
to a third of the fleet, a queue that backs up — and watch what your dashboards
do about it.

It was built to feed [Trace Town](https://github.com/FollyFactory/trace-town),
which draws a running system as an isometric town. It has no dependency on it.
Anything that speaks OTLP will do.

```bash
docker compose -f deploy/docker-compose.yml up -d
dotnet run --project src/TraceTown.Traffic -- examples/ecommerce.json
```

Then open Grafana at <http://localhost:3000> or Jaeger at
<http://localhost:16686>. Within a minute there is a twenty-one service system
in there, misbehaving in whatever way you tell it to.

## Contents

- [Why not just run real services?](#why-not-just-run-real-services)
- [What it models](#what-it-models)
- [Quick start](#quick-start)
- [Running the generator inside the stack](#running-the-generator-inside-the-stack)
- [SigNoz](#signoz)
- [Seeing a trace without a backend](#seeing-a-trace-without-a-backend)
- [The config file](#the-config-file)
- [Scenarios](#scenarios)
- [Driving it at runtime](#driving-it-at-runtime)
- [Command line](#command-line)
- [Troubleshooting](#troubleshooting)
- [Docs](#docs)

## Why not just run real services?

Because you cannot ask a real Postgres for a four-second p99 at 14:02 and get
it. The entire point is control: to reproduce a specific failure, on demand, as
many times as you need, in seconds rather than by waiting for it to happen in
production.

The trade is honesty about what is real. This tool does not execute business
logic or move bytes. What it does do is produce telemetry that is structurally
correct — right span kinds, right semantic conventions, right propagation, right
signal from the right component — so that anything you build against it will
work against the real thing.
[docs/telemetry-model.md](docs/telemetry-model.md) is the argument for why that
distinction is the whole game.

## What it models

| Kind | Building block | What it emits |
|---|---|---|
| `gateway` | Ingress, load balancer, BFF | Root server span, client spans out |
| `api` | An instrumented HTTP service | Server span in, client spans out, HTTP and DB metrics |
| `worker` | Queue consumer | Consumer span, process duration metrics |
| `database` | Postgres, MySQL, Mongo, Elasticsearch… | **No spans.** Scraper-style metrics, slow-query logs |
| `cache` | Redis, Memcached… | **No spans.** Hit/miss, memory, eviction metrics |
| `queue` | Kafka, RabbitMQ, SQS, NATS… | **No spans.** Depth, lag, publish/deliver counters |
| `cron` | Scheduled job | Root span on a schedule |
| `external` | Stripe, SendGrid, anyone else's API | Your client span, and nothing more |

Those are exactly Trace Town's service kinds, because that is what the telemetry
gets drawn as.

**Answering the obvious question:** databases do not push traces. The span for a
query is created by the caller's client library; the database contributes
*metrics* (scraped by the collector) and *logs* (from its own log file, with no
`trace_id` on them, because it has never heard of your trace). Message brokers
are the interesting exception — producer and consumer spans on either side, with
trace context riding inside the message, which is the only reason an async hop
stays connected. That is all set out properly in
[docs/telemetry-model.md](docs/telemetry-model.md).

## Quick start

Needs [.NET 10](https://dotnet.microsoft.com/download) and, for the bundled
backends, Docker.

```bash
# Collector + Jaeger + Prometheus + Loki + Grafana, wired together
docker compose -f deploy/docker-compose.yml up -d

# Twenty-one services, four flows, seven scenarios
dotnet run --project src/TraceTown.Traffic -- examples/ecommerce.json
```

| | |
|---|---|
| Grafana | <http://localhost:3000> — datasources provisioned, no login |
| Jaeger | <http://localhost:16686> |
| Prometheus | <http://localhost:9090> |
| Control API | <http://localhost:8080/api/status> |
| SigNoz | <http://localhost:3301> — with the SigNoz overlay, see below |

Already have a backend? Skip the compose file:

```bash
dotnet run --project src/TraceTown.Traffic -- examples/ecommerce.json \
  --endpoint https://otlp.example.com
```

## Running the generator inside the stack

The generator is in the compose file behind a profile, so it is off unless you
ask for it:

```bash
docker compose -f deploy/docker-compose.yml --profile generator up -d
```

That builds the image, points it at `http://collector:4318`, and publishes the
control API on `localhost:8080` — so everything in
[Driving it at runtime](#driving-it-at-runtime) works the same as when you run it
on the host.

To change what it runs, override the command:

```yaml
# deploy/docker-compose.override.yml
services:
  generator:
    command: ["examples/ecommerce.json", "--endpoint", "http://collector:4318",
              "--scenario", "cascade", "--rate", "3"]
```

Or use environment variables — `TRAFFIC_SCENARIO`, `TRAFFIC_RATE`,
`TRAFFIC_DURATION` and the rest are listed under [Command line](#command-line).

Running it on the host against `localhost:4318` is the more convenient loop while
you are editing a config, since there is no image to rebuild. The profile is for
when you want the whole thing to come up with one command.

Standalone, without compose:

```bash
docker build -t trace-town-traffic .
docker run --rm -e TRAFFIC_ENDPOINT=http://collector:4318 trace-town-traffic
```

## SigNoz

[Trace Town](https://github.com/FollyFactory/trace-town)'s adapter reads from
SigNoz, so there is an overlay that adds it to the stack. Export `COMPOSE_FILE`
once and every later `docker compose` command picks up both files:

```bash
export COMPOSE_FILE=deploy/docker-compose.yml:deploy/docker-compose.signoz.yml

docker compose up -d                      # the stack, with SigNoz
docker compose --profile generator up -d  # and the generator alongside it
docker compose logs -f collector
```

> **Use `COMPOSE_FILE`, or pass both `-f` flags on every single command.**
> Running `docker compose -f deploy/docker-compose.yml … up -d` with only the
> base file recreates the collector *without* the SigNoz exporter. SigNoz keeps
> running and every container stays healthy — telemetry simply stops arriving.
> This is the most likely reason for an empty SigNoz, and
> [Troubleshooting](#nothing-is-arriving-in-signoz) has the one-liner that
> confirms it.

SigNoz lands on <http://localhost:3301>, and Grafana, Jaeger and Prometheus carry
on working — the collector fans the same telemetry out to all of them. **Nothing
about how you run the generator changes**: it still exports to one endpoint and
the collector does the rest.

> **Create an account at <http://localhost:3301> before expecting any data.**
> Until SigNoz's first-run setup is complete, its opamp server pushes a `nop`
> pipeline to its own ingester — port 4317 never opens, the collector logs
> `connection refused`, and every container looks healthy. It is a one-time step
> and nothing about the symptoms points at it.

SigNoz deprecated their own Compose manifests in favour of
[Foundry](https://signoz.io/docs/install/docker/), which is what you should use
to run SigNoz for real. What is in `deploy/signoz/` is a pinned snapshot of
Foundry's output, trimmed for local use — a testing stack, not a supported
deployment. Its service names are load-bearing, because the vendored configs
address each other by hostname.

SigNoz's ingester is deliberately not published on 4317/4318: the collector
already owns those on the host, and one ingest endpoint is less to explain.

## Seeing a trace without a backend

`--dry-run` simulates one request per flow and prints the span tree. Nothing is
exported. It is the fastest way to find out whether a config says what you meant:

```bash
dotnet run --project src/TraceTown.Traffic -- examples/ecommerce.json --dry-run
```

```
flow: checkout   total: 469.8ms   spans: 26   outcome: ok
POST /api/checkout                              SERVER   edge-gateway     469.8ms
└─ POST /graphql                                CLIENT   edge-gateway     468.5ms
   └─ POST /graphql                             SERVER   web-bff          467.9ms
      ├─ POST /orders                           CLIENT   web-bff          423.7ms
      │  └─ POST /orders                        SERVER   checkout-api     423.0ms
      │     ├─ GET session                      CLIENT   checkout-api       0.5ms   → redis-sessions (no server span)
      │     ├─ INSERT orders                    CLIENT   checkout-api       0.7ms   → postgres-orders (no server span)
      │     ├─ POST /charges                    CLIENT   checkout-api     314.6ms
      │     │  └─ POST /charges                 SERVER   payments-api     314.1ms
      │     │     └─ POST /v1/charges           CLIENT   payments-api     283.8ms   → stripe (no server span)
      │     └─ send orders.placed               PRODUCER checkout-api       5.6ms   → orders-topic (no server span)
      │        └─ process orders.placed         CONSUMER order-worker      87.1ms
      │           └─ send email.order-confirmation PRODUCER order-worker    1.0ms   → email-queue (no server span)
      │              └─ process email.order-confirmation CONSUMER email-worker 109.0ms
```

Every `(no server span)` is a call to something that does not instrument itself.

`--validate` checks a config without running it, and is worth putting in CI.

## The config file

A service says what it is and what it calls. A flow says where requests come in
and how many. That is the whole model.

```json
{
  "town": { "name": "shopfront", "environment": "production" },
  "exporter": { "endpoint": "http://localhost:4318" },

  "services": [
    {
      "id": "checkout-api",
      "kind": "api",
      "instances": 3,
      "latency": { "p50Ms": 20, "p99Ms": 120 },
      "errorRate": 0.002,
      "dependencies": [
        { "target": "redis-sessions", "operation": "GET session" },
        { "target": "postgres-orders", "operation": "INSERT orders" },
        { "target": "stripe", "operation": "POST /v1/charges" },
        { "target": "orders-topic", "operation": "orders.placed" }
      ]
    },
    {
      "id": "order-worker",
      "kind": "worker",
      "consumes": [ { "queue": "orders-topic", "lagMs": 120 } ]
    },
    { "id": "orders-topic",   "kind": "queue",    "queue": { "system": "kafka" } },
    { "id": "postgres-orders","kind": "database", "database": { "system": "postgresql" } },
    { "id": "redis-sessions", "kind": "cache",    "cache": { "hitRate": 0.98 } },
    { "id": "stripe",         "kind": "external", "latency": { "p50Ms": 120, "p99Ms": 600 } }
  ],

  "flows": [
    {
      "id": "checkout",
      "entry": "checkout-api",
      "route": "POST /api/checkout",
      "rps": 6,
      "profile": { "kind": "diurnal", "amplitude": 0.6 }
    }
  ]
}
```

Comments and trailing commas are allowed, because these files are hand-written
and long-lived. There is a JSON schema at
[`schema/traffic.schema.json`](schema/traffic.schema.json) for editor
completion — reference it with `"$schema"` as the examples do.

Latency is **lognormal** by default, because real service latency is: a hard
floor, a median well below the mean, and a long right tail. Sampling from a
normal distribution gives a tail so thin that a p99 fault barely registers.

Every field is documented in [docs/configuration.md](docs/configuration.md).

## Scenarios

A scenario is a timed sequence of things going wrong. This is the reason the
tool exists.

```json
{
  "id": "cascade",
  "description": "A third party slows, its caller backs up, pressure walks upstream.",
  "steps": [
    { "at": "30s", "note": "Stripe latency climbs",
      "faults": [ { "target": "stripe", "kind": "latency", "p99Ms": 9000, "ramp": "60s" } ] },

    { "at": "2m", "note": "payments-api saturates waiting on it",
      "faults": [ { "target": "payments-api", "kind": "saturation", "ramp": "45s" } ] },

    { "at": "3m30s", "note": "checkout sheds load and the queue backs up",
      "faults": [
        { "target": "checkout-api", "kind": "errors", "errorRate": 0.35 },
        { "target": "orders-topic", "kind": "queueLag", "lagMs": 45000, "ramp": "90s" }
      ] },

    { "at": "6m", "note": "Recovered", "clear": true }
  ]
}
```

Six fault kinds, and they stack:

| Kind | Effect |
|---|---|
| `latency` | Slower. `multiplier`, or absolute `p50Ms`/`p99Ms` |
| `errors` | Fails some of the time. `errorRate` |
| `outage` | Refuses connections — fails *fast*, which reads very differently from a timeout |
| `traffic` | More or less load. Targets a flow: `"flow:checkout"` |
| `queueLag` | Messages wait longer. Backlog grows by Little's law |
| `saturation` | Exhausts its own resources — visible in infra metrics before latency |

`ramp` eases a fault in over time, because a dependency that degrades over ninety
seconds and one that falls over instantly are different incidents. `coverage`
applies a fault to only a fraction of the replicas, which is what a bad deploy
looks like. `duration` makes it clear itself. `target` accepts globs: `*-api`,
`postgres-*`.

The bundled example ships `calm`, `degrading`, `cascade`, `recovery`,
`bad-deploy`, `black-friday` and `database-outage`. More in
[docs/scenarios.md](docs/scenarios.md).

## Driving it at runtime

The JSON file is the source of truth, but you rarely want to restart to change
one thing. A small HTTP API is bound to loopback by default:

```bash
curl -X POST localhost:8080/api/scenarios/cascade      # switch scenario
curl localhost:8080/api/status                         # what is happening
curl localhost:8080/api/faults                         # what is currently broken

# Break something ad hoc — globs work, and it validates before applying
curl -X POST localhost:8080/api/faults -H 'Content-Type: application/json' \
  -d '{"target":"postgres-*","kind":"latency","multiplier":20,"ramp":"30s","duration":"2m"}'

# Turn the volume up on one flow
curl -X POST localhost:8080/api/flows/checkout/rate \
  -H 'Content-Type: application/json' -d '{"rps":200}'
```

It has no authentication unless you set `control.token`, which is why it binds to
`127.0.0.1` and why binding it anywhere else is a deliberate act. Full reference
in [docs/control-api.md](docs/control-api.md).

## Command line

```
trace-town-traffic [config.json] [options]

  -e, --endpoint <url>     Override the OTLP endpoint
  -s, --scenario <id>      Start with this scenario running
  -d, --duration <dur>     Stop after this long (30s, 5m, 1h)
  -r, --rate <multiplier>  Scale every flow's rate
      --sample <ratio>     Trace sample ratio, 0..1
      --seed <n>           Fix the seed for a reproducible run
  -p, --port <n>           Control API port
      --no-control         Do not start the control API
      --validate           Check the config and exit
      --dry-run            Print one trace per flow and exit
      --list-scenarios     List the scenarios and exit
      --print-config       Print the resolved config and exit
```

Environment variables override the file and are overridden by the flags:
`TRAFFIC_ENDPOINT`, `TRAFFIC_SCENARIO`, `TRAFFIC_RATE`, `TRAFFIC_DURATION`,
`TRAFFIC_SEED`, `TRAFFIC_CONTROL_HOST`, `TRAFFIC_CONTROL_PORT`,
`TRAFFIC_CONTROL_TOKEN`. `OTEL_EXPORTER_OTLP_ENDPOINT` is honoured too, since
anything running beside real instrumented services will already have it set.

## Troubleshooting

### Nothing is arriving in SigNoz

Two causes account for almost all of it, and neither announces itself — every
container reports healthy in both cases.

**1. The collector is running without the SigNoz exporter.** Check what config it
actually loaded:

```bash
docker inspect trace-town-traffic-collector-1 --format '{{json .Config.Cmd}}'
```

```
["--config=/etc/otel/config.yaml","--config=/etc/otel/signoz.yaml"]   ✅
["--config=/etc/otel/config.yaml"]                                    ❌ overlay dropped
```

If the second, a compose command ran with only the base `-f` file and recreated
the collector from it. Bring it back with both files, or set `COMPOSE_FILE` as
above.

**2. SigNoz's first-run setup is not complete.**

```bash
curl -s localhost:3301/api/v1/version
```

`"setupCompleted": false` means no account exists yet. Until one does, SigNoz's
opamp server pushes a `nop` pipeline to its own ingester: port 4317 never opens
and the collector logs `connection refused`. Create an account at
<http://localhost:3301> and the pipeline comes up within about ten seconds.

**Then confirm data is actually landing**, without going through the UI or its
auth:

```bash
docker exec signoz-telemetrystore-clickhouse-0-0 clickhouse-client --query "
  SELECT 'traces' s, count() n FROM signoz_traces.distributed_signoz_index_v3
  UNION ALL SELECT 'logs',    count() FROM signoz_logs.distributed_logs_v2
  UNION ALL SELECT 'metrics', count() FROM signoz_metrics.distributed_samples_v4
  FORMAT TSV"
```

Logs lag well behind traces — they are only emitted for errors and for sampled
requests, so a low count there is expected rather than a fault.

### SigNoz shows the services but no databases, caches or queues

Expected until you are on a build that emits both attribute spellings — SigNoz
classifies a span as a database call from `db.system`, which the conventions
renamed to `db.system.name`. The generator emits both by default
(`exporter.semanticConventions: "dup"`); if you have set it to `latest`, that is
why.

Note that SigNoz names infrastructure nodes after the *system* rather than the
instance, so five simulated Postgres services collapse into one `postgresql`
node in its dependency graph. That is SigNoz's model, not something the
generator controls.

### Nothing is arriving anywhere

Check the generator is actually producing, and where it thinks it is sending:

```bash
curl -s localhost:8080/api/status
```

`counts.requests` climbing with `counts.spans` at zero means sampling, not a
broken pipeline — the default only traces 10% of requests. Run with
`--sample 1.0` while debugging.

## Docs

| | |
|---|---|
| [telemetry-model.md](docs/telemetry-model.md) | What emits what, and why databases do not emit spans |
| [configuration.md](docs/configuration.md) | Every field in the config file |
| [scenarios.md](docs/scenarios.md) | Fault kinds, and writing your own scenarios |
| [control-api.md](docs/control-api.md) | The HTTP API |
| [architecture.md](docs/architecture.md) | How it works inside, and what it deliberately does not model |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Building, testing, and the conventions |

## Licence

[Apache 2.0](LICENSE).
