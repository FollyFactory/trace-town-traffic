# What actually emits telemetry

> **Do databases push traces?** No — almost never. The span you see for a query
> was created by the *caller*, not by the database. What the database itself
> contributes is metrics and logs, collected by a completely different route.

That distinction is the single most misunderstood thing about instrumenting a
distributed system, and getting it wrong produces synthetic data that teaches
people to expect things real systems will never give them. So this tool models
it strictly, and this document is the reasoning.

## One rule explains all of it

**Telemetry comes from instrumented code, and you only have instrumented code
where you own the process.**

Everything else follows. Your service imports an OpenTelemetry SDK, and its HTTP
client library, its database driver and its message-broker client all create
spans as they work. That covers your side of every call you make. It covers
nothing on the other side unless the other side is also yours.

So for any hop, ask one question: *does the far end run code I have
instrumented?*

- **Yes** — another of your services. You get a `CLIENT` span from the caller
  and a `SERVER` span from the callee. Two spans, one hop, and the gap between
  them is your network time.
- **No** — a database, a cache, a broker, someone else's API. You get the
  caller's `CLIENT` span and nothing else. One span for the whole hop, and its
  duration is everything: network, queueing, and the work itself, indivisible.

That second case is not a gap in your setup. It is the honest limit of what you
can see, and a good visualisation should show it as such.

## Component by component

| Component | Traces | Metrics | Logs |
|---|---|---|---|
| **Gateway** | Root `SERVER` span, `CLIENT` spans out | `http.server.request.duration` | Application logs, correlated |
| **API** | `SERVER` span in, `CLIENT` spans out | `http.server.*`, `http.client.*`, `db.client.*` | Application logs, correlated |
| **Worker** | `CONSUMER` span, `CLIENT` spans out | `messaging.process.duration` | Application logs, correlated |
| **Database** | **None of its own** — only the caller's `CLIENT` span | Scraped by a collector receiver | Its own log file, **uncorrelated** |
| **Cache** | **None of its own** — only the caller's `CLIENT` span | Scraped by a collector receiver | Uncorrelated |
| **Queue** | **None of its own** — `PRODUCER` and `CONSUMER` spans belong to its clients | Scraped by a collector receiver | Uncorrelated |
| **External** | **None** — only your `CLIENT` span | Nothing. It is not your system | Nothing |
| **Cron** | Root `INTERNAL` span | Whatever it calls | Application logs, correlated |

### Databases

A query produces exactly one span, created by the client library inside the
calling service:

```
checkout-api  CLIENT  "INSERT orders"   3.4ms
    db.system.name    postgresql
    db.namespace      orders
    db.operation.name INSERT
    db.collection.name orders
    db.query.text     INSERT INTO orders (id, data) VALUES ($1, $2)
    server.address    postgres-orders
    server.port       5432
```

There is no child span. Nothing inside Postgres opened a span, added attributes
and closed it, because Postgres has never heard of your trace.

Note that `db.query.text` is **parameterised**. The convention wants the shape of
the query, not the values — a statement with a customer's email address inlined
is a data leak sitting in your trace backend, readable by everyone with access
to it. This tool only ever emits placeholders.

**Can a database ever emit spans?** In narrow cases, yes, and it is worth
knowing they exist so you can recognise them as the exception. Postgres has the
third-party `pg_tracing` extension; some managed services expose query insights
that can be converted; a few distributed databases such as Cassandra and
CockroachDB have internal tracing that can be exported. None of this is default,
none is common, and none of it is what people mean when they say "we have
tracing". The overwhelmingly normal case is the one modelled here.

**What the database does give you** is its own state, and you get it a different
way: the collector scrapes it. The `postgresqlreceiver` connects to Postgres and
reads `pg_stat_*`, producing metrics like these, which this tool emits under the
same names:

| Metric | Why it matters |
|---|---|
| `postgresql.backends` | Connections in use. Rising against the limit is a pool about to exhaust |
| `postgresql.connection.max` | The ceiling the above is heading for |
| `postgresql.commits` / `postgresql.rollbacks` | Rollbacks climbing means application errors, not database errors |
| `postgresql.deadlocks` | Contention. Rare normally, not rare under load |
| `postgresql.operations` | Throughput, for deriving concurrency |

These are the metrics that tell you *why* the queries got slow, and no amount of
tracing will produce them. That is the argument for collecting both.

The database's **logs** are the third piece, and the most instructive. A slow
query line comes out of Postgres's own log file, is picked up by the collector's
`filelog` receiver, and arrives with **no `trace_id`**, because the process that
wrote it was never part of a trace. So you get:

```
service.name = postgres-orders   severity = WARN   trace_id = (none)
"duration: 812.4 ms  statement: SELECT * FROM orders WHERE id = $1"
```

You can correlate that to a trace by timestamp and statement shape, by hand, and
that is genuinely how people do it. This tool emits those logs uncorrelated on
purpose. Attaching a `trace_id` would have been easy and would have been a lie.

### Caches

Identical rules. A Redis `GET` is a `CLIENT` span from the caller with
`db.system.name=redis`, and Redis emits nothing about it. The `redisreceiver`
scrapes `INFO` for `redis.keyspace.hits`, `redis.keyspace.misses`,
`redis.memory.used` and `redis.keys.evicted`.

Eviction is the one worth watching: when memory approaches the limit, keys start
being thrown away, the hit rate falls, and the load lands on the database behind
the cache. You can see that coming in the metrics well before it shows up in
anyone's latency. The `black-friday` scenario in the bundled example does exactly
this.

### Message brokers — the interesting case

A queue is the one place where the far side of a hop *is* instrumented, just not
at the same time. Two spans, from two different services:

```
checkout-api  PRODUCER  "send orders.placed"      3.4ms   at t=201ms
order-worker  CONSUMER  "process orders.placed"  55.2ms   at t=325ms
```

Both are in the **same trace**, and the reason is that trace context travels
inside the message. The producer injects `traceparent` into the message headers;
the consumer extracts it and starts its span as a child. Without that
propagation the two halves are separate traces and the causal link is gone —
which is exactly what happens when a broker client is not properly instrumented,
and it is one of the most common gaps in a real system.

The 120ms between the producer finishing and the consumer starting is **queue
lag**, and it is dead time that belongs to neither service. Being able to see it
is most of the reason to trace across a broker at all: a system that is falling
behind spends its day in that gap, and no per-service latency metric will show
it to you.

Note also that the consumer span *outlives its parent trace's root*. The HTTP
request returned to the user at 246ms; the consumer ran from 325ms to 380ms.
That is not an error — it is what asynchronous means, and any backend worth using
handles spans arriving after the root has been reported.

The broker's own state comes, again, from a scraper: consumer group lag, queue
depth, messages published and delivered. Queue depth follows Little's law —
arrival rate multiplied by wait time — which is why a worker that slows down
produces a growing backlog without the publish rate changing at all.

**One thing this tool does not model:** batch consumers that pull messages from
several different traces at once. Real batch consumers use **span links** rather
than parent-child, because parenting a batch to any single one of its messages
would misrepresent the causality. Here, `messaging.batch.message_count` is
emitted but every consumer span has a single parent.

### Third parties

You see your `CLIENT` span and nothing else, ever. Stripe is not going to send
spans into your backend. The whole building is drawn from one span's duration and
status, which is why a third party that degrades is so much harder to diagnose
than one of your own services — and why the `cascade` scenario starts there.

## Sampling: why metrics are not sampled

Traces are sampled; metrics are not. This tool follows that split exactly, and it
matters more than it sounds.

- **Metrics are recorded for every single request**, sampled or not. A sampled
  metric is not a cheaper metric, it is a wrong number. Your request rate, error
  rate and latency percentiles must be computed over everything.
- **Spans are recorded for a fraction of requests** — 10% by default. Traces are
  expensive and highly redundant; a hundred healthy checkouts tell you nothing a
  single one did not.
- **Failed requests are sampled at 100% by default**
  (`errorTraceSampleRatio`). A rare error is the entire reason anyone opens a
  trace viewer, and at a 1% sample rate you would essentially never catch one.
  Real systems achieve this with tail sampling in the collector; this tool
  decides at generation time, which reaches the same place by a shorter road.

Logs split the same way: **errors are always emitted**, quieter levels only for
sampled requests.

The practical consequence is that your metric dashboards and your trace search
will disagree about how many requests there were, and they are both right.

## Where a signal comes from, in one diagram

```
                    ┌──────────────────────────────────────┐
  your code ───────▶│  OpenTelemetry SDK in your process   │──▶ OTLP ──▶ collector
                    │  spans · metrics · correlated logs   │
                    └──────────────────────────────────────┘

                    ┌──────────────────────────────────────┐
  Postgres ────────▶│  collector receiver, scraping it     │──▶         collector
  Redis             │  postgresqlreceiver, redisreceiver   │
  Kafka             │  metrics only, no spans              │
                    └──────────────────────────────────────┘

                    ┌──────────────────────────────────────┐
  log files ───────▶│  collector filelog receiver          │──▶         collector
                    │  no trace_id — nothing to attach it  │
                    └──────────────────────────────────────┘
```

`trace-town-traffic` produces all three, over OTLP, from one process. What it
does not do is *run* a collector receiver — the infrastructure metrics are
emitted directly under the names a receiver would have used, so a dashboard
written against a real Postgres works unchanged against a simulated one.

## Conventions this tool follows

Attribute names track the current OpenTelemetry semantic conventions, including
the ones that changed recently. A generator that emits last year's names teaches
people to write queries that will break:

| Used here | Superseded |
|---|---|
| `db.system.name` | `db.system` |
| `db.namespace` | `db.name` |
| `db.query.text` | `db.statement` |
| `db.operation.name` | `db.operation` |
| `deployment.environment.name` | `deployment.environment` |
| `error.type` | *(new; use it, it is low-cardinality and queryable)* |
| `server.address` / `server.port` | `net.peer.name` / `net.peer.port` |

They are all pinned in one file,
[`src/TraceTown.Traffic/Emit/SemConv.cs`](../src/TraceTown.Traffic/Emit/SemConv.cs),
so there is one place to check them against the spec and one place to change.
