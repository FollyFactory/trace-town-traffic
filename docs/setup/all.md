# Everything at once

Jaeger, Prometheus, Loki and Grafana together, fed from one collector. For
comparing how each renders the same telemetry.

```bash
docker compose -f deploy/all.yml --profile generator up -d
```

| | |
|---|---|
| Grafana | <http://localhost:3000> — all three wired up, no login |
| Jaeger | <http://localhost:16686> |
| Prometheus | <http://localhost:9090> |
| Control API | <http://localhost:8080/api/status> |

Grafana's datasources are linked, so a log line's `trace_id` opens the trace in
Jaeger.

The collector also derives RED metrics and a dependency graph from the spans
(`spanmetrics` and `servicegraph`), so Prometheus holds both what the services
reported about themselves and what was inferred from their traces — which is
exactly the situation a real reader has to cope with.

Heavier than any single setup, and there is nothing here you cannot get from one
of the others. Start from [Jaeger](jaeger.md) or [Prometheus](prometheus.md)
unless you specifically want the comparison.

```bash
docker compose -f deploy/all.yml --profile generator down -v
```

SigNoz is not in this one — it brings its own collector and ClickHouse, and has
[its own setup](signoz.md).
