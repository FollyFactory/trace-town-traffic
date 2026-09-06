# Prometheus

**Metrics**, with Grafana in front. The usual starting point.

```bash
docker compose -f deploy/prometheus.yml --profile generator up -d
```

| | |
|---|---|
| Grafana | <http://localhost:3000> — no login |
| Prometheus | <http://localhost:9090> |
| Control API | <http://localhost:8080/api/status> |

Prometheus receives OTLP directly rather than scraping, and promotes
`service.name`, `service.namespace` and `service.instance.id` to labels so you
can group by them.

Traces are not thrown away here. They feed two collector connectors —
`spanmetrics` and `servicegraph` — so a metrics-only stack still gets RED metrics
and a full dependency graph, **including the databases and brokers**, which never
emit a span of their own and can only ever appear as the far end of a call.

Three queries worth starting from:

```promql
# Request rate per service
sum by (service_name) (rate(http_server_request_duration_seconds_count[1m]))

# p99 latency per service
histogram_quantile(0.99, sum by (le, service_name) (rate(http_server_request_duration_seconds_bucket[5m])))

# Connection pool against its ceiling — this moves before latency does
postgresql_backends / postgresql_connection_max

# The dependency graph, derived from spans rather than reported by anyone
sum by (client, server) (rate(traces_service_graph_request_total[5m]))
```

That last one names each database by its `db.namespace`, so `orders`,
`payments` and `inventory` stay distinct rather than collapsing into one
`postgresql` node. Give the service graph about a minute after start-up — it
pairs client and server spans and flushes on an interval.

That last one is the argument for collecting infrastructure metrics at all. Watch
it during:

```bash
curl -X POST localhost:8080/api/scenarios/cascade
```

Stop it, and delete its data:

```bash
docker compose -f deploy/prometheus.yml --profile generator down -v
```

Prefer to run the generator on your machine instead of in the stack? Leave off
`--profile generator` and run it against the collector directly:

```bash
dotnet run --project src/TraceTown.Traffic -- examples/ecommerce.json
```

That is the faster loop while you are editing a config, since there is no image
to rebuild.
