# Configuration reference

One JSON file describes the whole simulated system. Comments and trailing commas
are allowed. A [JSON schema](../schema/traffic.schema.json) is provided for
editor completion — reference it from the top of your file:

```json
{ "$schema": "https://raw.githubusercontent.com/FollyFactory/trace-town-traffic/main/schema/traffic.schema.json" }
```

Check a file without running it:

```bash
trace-town-traffic my-town.json --validate
```

Validation is strict about things that would fail silently — a misspelt
dependency target is an error, not a service that mysteriously never gets
called — and advisory about things that are merely suspicious, such as a service
no flow can reach.

## Top level

| Field | Type | Default | |
|---|---|---|---|
| `town` | object | | Identity shared by every service |
| `exporter` | object | | Where telemetry goes |
| `simulation` | object | | Rates, sampling, seed |
| `control` | object | | The HTTP control API |
| `services` | array | `[]` | The components of the system |
| `flows` | array | `[]` | The entry points that generate load |
| `scenarios` | array | `[]` | Named sequences of faults |

## `town`

| Field | Type | Default | |
|---|---|---|---|
| `name` | string | `trace-town` | Emitted as `trace_town.town` |
| `environment` | string | `production` | Becomes `deployment.environment.name` |
| `resource` | object | `{}` | Resource attributes merged onto every service |

```json
"town": {
  "name": "shopfront",
  "environment": "staging",
  "resource": { "cloud.provider": "aws", "cloud.region": "eu-west-2" }
}
```

## `exporter`

| Field | Type | Default | |
|---|---|---|---|
| `endpoint` | string | `http://localhost:4318` | OTLP endpoint. Include the scheme; the port is not assumed |
| `protocol` | `httpProtobuf` \| `grpc` | `httpProtobuf` | HTTP is the default so every pipeline shares one `HttpClient` |
| `headers` | object | `{}` | Added to every export, for backends that want a key |
| `traces` | bool | `true` | |
| `metrics` | bool | `true` | |
| `logs` | bool | `true` | |
| `metricIntervalMs` | int | `10000` | Export interval. Match your backend's resolution |
| `timeoutMs` | int | `10000` | |

With `httpProtobuf` the signal paths (`/v1/traces` and so on) are appended to
the endpoint. With `grpc` the endpoint is used as given.

Turning a signal off is genuinely useful: `"metrics": false` while you are
debugging trace shapes keeps a backend from filling with data you are not
reading.

## `simulation`

| Field | Type | Default | |
|---|---|---|---|
| `tickMs` | int | `250` | How often each flow wakes to generate work |
| `traceSampleRatio` | 0..1 | `0.1` | Fraction of requests that produce spans |
| `errorTraceSampleRatio` | 0..1 | `1.0` | Sample rate for failed requests |
| `seed` | int | random | Fix it to make a run reproducible |
| `rateMultiplier` | number | `1.0` | Scales every flow. The volume knob |
| `maxDepth` | int | `12` | Recursion guard for the call graph |
| `scenario` | string | none | Scenario to start with |
| `duration` | duration | forever | Stop after this long |

**Metrics are always recorded for every request**, whatever the sample ratio.
Sampling a metric does not make it cheaper, it makes it wrong. Errors are sampled
separately and at 100% by default, because a rare failure is the entire reason
anyone opens a trace viewer.

Seeding makes each flow reproducible from its own seed. It does not make the
interleaving *between* concurrently running flows reproducible, and no
single-process design that uses real wall-clock time could.

## `control`

| Field | Type | Default | |
|---|---|---|---|
| `enabled` | bool | `true` | |
| `host` | string | `127.0.0.1` | |
| `port` | int | `8080` | |
| `token` | string | none | When set, requires `Authorization: Bearer <token>` |

Loopback by default. The API can reshape your telemetry and has no
authentication unless you set a token, so binding it elsewhere should be a
decision rather than an accident. It warns on startup if you bind it wide open
with no token.

## `services[]`

| Field | Type | Default | |
|---|---|---|---|
| `id` | string | **required** | Referenced by dependencies, flows and faults |
| `name` | string | `id` | Becomes `service.name` |
| `kind` | enum | **required** | See below |
| `group` | string | town name | Becomes `service.namespace` |
| `version` | string | `1.0.0` | Becomes `service.version` |
| `instances` | int | `1` | Replicas. Requests spread across them |
| `latency` | object | | Time in this service alone, excluding what it calls |
| `errorRate` | 0..1 | `0` | Chance it fails on its own |
| `resource` | object | `{}` | Extra resource attributes for this service |
| `dependencies` | array | `[]` | What it calls |
| `consumes` | array | `[]` | Queues it subscribes to |
| `database` / `cache` / `queue` / `cron` | object | | Kind-specific detail |

`kind` is `gateway`, `api`, `worker`, `database`, `cache`, `queue`, `cron` or
`external`. It decides which signals the component emits and, critically,
whether calls to it produce a server span at all —
[telemetry-model.md](telemetry-model.md) explains the rule.

`latency` is **the service's own time**, not end to end. Total request latency is
this plus everything it calls, which is how it works in a real system too.

### `latency`

| Field | Type | Default | |
|---|---|---|---|
| `p50Ms` | number | `10` | Median |
| `p99Ms` | number | `50` | 99th percentile |
| `distribution` | enum | `lognormal` | `lognormal`, `normal`, `uniform`, `constant` |
| `floorMs` | number | `0` | Never returns less than this |

Lognormal is right for service latency and the others are for when you want
something artificial: `constant` to isolate one variable in a test, `uniform`
when you want a flat spread. A `normal` distribution has a tail so thin that a
p99 fault is nearly invisible, which is exactly the wrong property here.

`floorMs` models a fixed cost — a network hop to another region, a TLS handshake.

### `dependencies[]`

| Field | Type | Default | |
|---|---|---|---|
| `target` | string | **required** | The service id being called |
| `operation` | string | derived | See below |
| `probability` | 0..1 | `1.0` | Chance the call happens at all |
| `calls` | int | `1` | How many times. Fan-out, N+1 queries |
| `callsMax` | int | | Upper bound when the count varies |
| `async` | bool | `false` | Fire and forget: the span is recorded, the caller does not wait |
| `propagates` | bool | `true` | Whether a failure downstream fails the caller |

`operation` is written the way you would say it, and is parsed for the target's
kind:

| Target kind | Write | Produces |
|---|---|---|
| `api`, `gateway`, `external` | `POST /v1/charges` | `http.request.method`, `http.route` |
| `database` | `SELECT orders` | `db.operation.name`, `db.collection.name`, a parameterised `db.query.text` |
| `cache` | `GET session` | `db.operation.name`, and hit/miss accounting on reads |
| `queue` | `orders.placed` | `messaging.destination.name` |

`probability` is how you model a cache miss path: give the database dependency
`"probability": 0.12` and it is called on 12% of requests, which *is* the miss
rate.

`propagates: false` models a fallback or a circuit breaker. The call still fails
and is still visible in the trace; the caller simply carries on. It is the
difference between a system that degrades and one that collapses.

### `consumes[]`

Only meaningful on a `worker`. The far side of a producer span.

| Field | Type | Default | |
|---|---|---|---|
| `queue` | string | **required** | The `queue` service subscribed to |
| `destination` | string | queue's default | Topic, queue or subject name |
| `lagMs` | number | `100` | Baseline wait before a message is picked up |
| `batch` | int | `1` | Messages per consumer span |
| `errorRate` | 0..1 | `0` | Chance processing fails after delivery |

`lagMs` is the dead time between publish and pick-up, and it is drawn as a gap in
the trace. It is where a system that is falling behind spends its day.

### `database`

| Field | Type | Default | |
|---|---|---|---|
| `system` | string | `postgresql` | Becomes `db.system.name` |
| `namespace` | string | service id | Becomes `db.namespace` |
| `maxConnections` | int | `100` | Pool size. Drives saturation metrics |
| `slowQueryMs` | number | `500` | Queries above this also produce a slow-query log |

`system` also picks a default `server.port` — 5432 for postgresql, 3306 for
mysql, 27017 for mongodb, and so on.

### `cache`

| Field | Type | Default | |
|---|---|---|---|
| `system` | string | `redis` | |
| `hitRate` | 0..1 | `0.95` | Drives `redis.keyspace.hits` / `misses` |
| `memoryBytes` | int | 512 MiB | Starting memory in use |
| `maxMemoryBytes` | int | 1 GiB | Evictions begin as memory nears this |

### `queue`

| Field | Type | Default | |
|---|---|---|---|
| `system` | string | `kafka` | Becomes `messaging.system` |
| `destination` | string | service id | Default destination for publishers |
| `partitions` | int | `3` | Reported as `messaging.destination.partition.id` |

### `cron`

| Field | Type | Default | |
|---|---|---|---|
| `every` | duration | `5m` | How often the job fires |
| `jitter` | duration | none | Random offset, so jobs do not align perfectly |
| `job` | string | service id | Root span name |

## `flows[]`

Flows are the only thing that generates load. A service no flow reaches emits
nothing but idle infrastructure metrics.

| Field | Type | Default | |
|---|---|---|---|
| `id` | string | **required** | |
| `entry` | string | **required** | The service requests arrive at |
| `route` | string | `/{entry}` | e.g. `POST /api/checkout`. Becomes the root span name |
| `rps` | number | `1` | Requests per second at a profile value of 1.0 |
| `profile` | object | constant | How the rate varies over time |
| `clientErrorRate` | 0..1 | `0` | 4xx rejections at the edge |
| `always` | string[] | `[]` | Services this flow always reaches, ignoring probabilities |
| `except` | string[] | `[]` | Services this flow never calls, at any depth |
| `depth` | int | `maxDepth` | How far down the graph it reaches. `0` is the entry point alone |

`clientErrorRate` produces 4xx responses that are **not counted as failures**. A
malformed request is the caller's problem, not an incident, and a system that
alarms every time someone sends one is a system nobody trusts.

### Making one flow differ from another

Services declare an average call graph; a flow is a specific journey through it.
Two lists reconcile the two, and both accept globs:

- **`always`** forces a branch regardless of its `probability`. A checkout flow
  lists `checkout-api` and therefore always goes there, while a browse flow over
  the same gateway does not.
- **`except`** prunes a branch at any depth.

```json
{ "id": "checkout", "entry": "edge-gateway", "route": "POST /api/checkout",
  "always": ["checkout-api"], "except": ["search-api", "catalog-api"] }
```

`depth: 0` is how you write a health check: the entry point and nothing below it.

### `profile`

| Field | Type | Default | |
|---|---|---|---|
| `kind` | enum | `constant` | `constant`, `diurnal`, `sine`, `burst`, `ramp`, `randomWalk` |
| `amplitude` | 0..1 | `0.3` | How far the rate swings either side of the base |
| `period` | duration | `10m` | Cycle length for `sine`, `burst`, `ramp` |
| `duty` | 0..1 | `0.1` | Fraction of the period a `burst` spends elevated |
| `peak` | number | `3.0` | Peak multiplier for `burst`, end multiplier for `ramp` |
| `jitter` | number | `0.05` | Gaussian noise, as a fraction of the rate |

`diurnal` runs a 24-hour cycle in **local time**, trough around 04:00 and peak
around 15:00, because that is when the humans generating the traffic are awake.
Two flows on the same profile peak together, the way real traffic does.

`burst` eases in and out of its spike rather than stepping, since no real traffic
pattern steps.

## `scenarios[]`

See [scenarios.md](scenarios.md).

## Durations

Anywhere a duration is accepted: `500ms`, `30s`, `5m`, `1h`, `2m30s`, `1h30m`. A
bare number is seconds. Trailing junk is rejected rather than silently ignored —
`30x` is an error, not thirty seconds.

## Globs

`target`, `always` and `except` accept one wildcard, `*`, matching any run of
characters: `*-api`, `postgres-*`, `*`. Deliberately not a regex — these are read
far more often than they are written, and a regex in a config file is a thing you
have to decode.
