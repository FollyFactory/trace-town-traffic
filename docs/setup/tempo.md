# Tempo

**Traces**, in Grafana's trace store, with Grafana in front.

```bash
docker compose -f deploy/tempo.yml --profile generator up -d
```

| | |
|---|---|
| Grafana | <http://localhost:3000> — no login |
| Tempo | <http://localhost:3200> |
| Control API | <http://localhost:8080/api/status> |

In Grafana, **Explore** → Tempo → **Search**, and filter by service name. Or
query TraceQL directly:

```traceql
{ resource.service.name = "checkout-api" && duration > 500ms }
{ span.db.system.name = "postgresql" && duration > 100ms }
```

Pick Tempo over Jaeger if you already run Grafana, or if you want TraceQL. Jaeger
is the smaller thing to stand up.

Stop it, and delete its data:

```bash
docker compose -f deploy/tempo.yml --profile generator down -v
```

Prefer to run the generator on your machine instead of in the stack? Leave off
`--profile generator` and run it against the collector directly:

```bash
dotnet run --project src/TraceTown.Traffic -- examples/ecommerce.json
```

That is the faster loop while you are editing a config, since there is no image
to rebuild.
