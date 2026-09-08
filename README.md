# trace-town-traffic

Synthetic OpenTelemetry traffic for a whole distributed system, described in one
JSON file.

Point it at an OTLP endpoint and it produces the traces, metrics and logs a real
system would — APIs with correct client and server spans, brokers with trace
context carried through the messages, databases and caches that emit metrics and
slow-query logs but **no spans of their own**, third parties you can only see
from outside. Then break it on purpose and watch what your dashboards do.

```bash
docker compose -f deploy/jaeger.yml --profile generator up -d
```

Open <http://localhost:16686>. A 21-service system, running.

It was built to feed [Trace Town](https://github.com/FollyFactory/trace-town),
which draws a running system as an isometric town. It needs nothing from it —
anything that speaks OTLP will do.

## Pick a setup

| | Signals | |
|---|---|---|
| **[Jaeger](docs/setup/jaeger.md)** | traces | Smallest. Start here |
| **[Prometheus](docs/setup/prometheus.md)** | metrics | With Grafana |
| **[Loki](docs/setup/loki.md)** | logs | With Grafana |
| **[Tempo](docs/setup/tempo.md)** | traces | With Grafana |
| **[SigNoz](docs/setup/signoz.md)** | all three | What Trace Town reads |
| **[Application Insights](docs/setup/appinsights.md)** | all three | Azure. Costs money |
| **[Everything](docs/setup/all.md)** | all three | For comparing them |

One command each — see **[docs/setup](docs/setup/README.md)**. Already have a
backend? `--endpoint https://otlp.example.com` and skip all of it.

## What it models

| Kind | What it emits |
|---|---|
| `gateway` | Root server span, client spans out |
| `api` | Server span in, client spans out, HTTP and DB metrics |
| `worker` | Consumer span, process duration metrics |
| `database` | **No spans.** Scraper-style metrics, slow-query logs |
| `cache` | **No spans.** Hit/miss, memory, eviction metrics |
| `queue` | **No spans.** Depth, lag, publish/deliver counters |
| `cron` | Root span on a schedule |
| `external` | Your client span, and nothing more |

**Databases do not push traces.** The span for a query is created by the caller's
client library; the database contributes metrics and logs, the logs carrying no
`trace_id` because it has never heard of your trace. Brokers are the exception —
producer and consumer spans with context riding inside the message, which is the
only reason an async hop stays in one trace.

That distinction is the whole design:
**[telemetry-model.md](docs/telemetry-model.md)**.

## See a trace without a backend

```bash
dotnet run --project src/TraceTown.Traffic -- examples/ecommerce.json --dry-run
```

```
flow: checkout   total: 469.8ms   spans: 26   outcome: ok
POST /api/checkout                        SERVER   edge-gateway   469.8ms
└─ POST /orders                           CLIENT   web-bff        423.7ms
   └─ POST /orders                        SERVER   checkout-api   423.0ms
      ├─ GET session                      CLIENT   checkout-api     0.5ms   → redis-sessions (no server span)
      ├─ INSERT orders                    CLIENT   checkout-api     0.7ms   → postgres-orders (no server span)
      ├─ POST /v1/charges                 CLIENT   payments-api   283.8ms   → stripe (no server span)
      └─ send orders.placed               PRODUCER checkout-api     5.6ms   → orders-topic (no server span)
         └─ process orders.placed         CONSUMER order-worker     87.1ms
```

Every `(no server span)` is a call to something that does not instrument itself.

## Describe your own system

A service says what it is and what it calls. A flow says where requests come in
and how many. That is the model.

```json
{
  "services": [
    {
      "id": "checkout-api",
      "kind": "api",
      "latency": { "p50Ms": 20, "p99Ms": 120 },
      "errorRate": 0.002,
      "dependencies": [
        { "target": "postgres-orders", "operation": "INSERT orders" },
        { "target": "stripe", "operation": "POST /v1/charges" },
        { "target": "orders-topic", "operation": "orders.placed" }
      ]
    },
    { "id": "order-worker",    "kind": "worker",   "consumes": [{ "queue": "orders-topic" }] },
    { "id": "orders-topic",    "kind": "queue",    "queue": { "system": "kafka" } },
    { "id": "postgres-orders", "kind": "database", "database": { "system": "postgresql" } },
    { "id": "stripe",          "kind": "external", "latency": { "p50Ms": 120, "p99Ms": 600 } }
  ],
  "flows": [
    { "id": "checkout", "entry": "checkout-api", "route": "POST /api/checkout", "rps": 6 }
  ]
}
```

Comments and trailing commas are allowed. There is a
[JSON schema](schema/traffic.schema.json) for editor completion, and
`--validate` checks a file without running it.

Every field: **[configuration.md](docs/configuration.md)**.

## Break it on purpose

```bash
curl -X POST localhost:8080/api/scenarios/cascade
```

A third party slows, its caller saturates, checkout sheds load, the queue backs
up, and then it recovers. Six fault kinds — `latency`, `errors`, `outage`,
`traffic`, `queueLag`, `saturation` — with ramps, partial replica coverage and
glob targets, composed into timed scenarios.

**[scenarios.md](docs/scenarios.md)** · **[control-api.md](docs/control-api.md)**

## Docs

| | |
|---|---|
| [setup/](docs/setup/README.md) | One command per backend |
| [telemetry-model.md](docs/telemetry-model.md) | What emits what, and why databases do not emit spans |
| [configuration.md](docs/configuration.md) | Every field in the config file |
| [scenarios.md](docs/scenarios.md) | Fault kinds, and writing your own |
| [control-api.md](docs/control-api.md) | Changing things at runtime |
| [architecture.md](docs/architecture.md) | How it works, and what it does not model |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Building and testing |

## Command line

```
trace-town-traffic [config.json] [options]

  -e, --endpoint <url>     OTLP endpoint
  -s, --scenario <id>      Start with this scenario running
  -d, --duration <dur>     Stop after this long
  -r, --rate <multiplier>  Scale every flow
      --sample <ratio>     Trace sample ratio, 0..1
      --seed <n>           Reproducible run
      --validate           Check the config and exit
      --dry-run            Print one trace per flow and exit
```

`TRAFFIC_ENDPOINT`, `TRAFFIC_SCENARIO`, `TRAFFIC_RATE` and friends override the
file; the flags override those. `OTEL_EXPORTER_OTLP_ENDPOINT` is honoured too.

## Licence

[Apache 2.0](LICENSE).
