# Loki

**Logs**, with Grafana in front.

```bash
docker compose -f deploy/loki.yml --profile generator up -d
```

| | |
|---|---|
| Grafana | <http://localhost:3000> — no login |
| Control API | <http://localhost:8080/api/status> |

In Grafana, **Explore** → Loki → `{service_name=~".+"}`.

Two things to look for:

```logql
# Application logs — these carry trace_id
{service_name="checkout-api"} | json

# A database talking about itself — no trace_id, because Postgres
# has never heard of your trace
{service_name=~"postgres.*"}
```

That difference is the point, and it is explained in
[telemetry-model.md](../telemetry-model.md).

Logs are the quietest signal here: only errors and sampled requests produce one,
so expect far fewer lines than traces. For more of them, run the generator with
`--sample 1.0`.

Stop it, and delete its data:

```bash
docker compose -f deploy/loki.yml --profile generator down -v
```

Prefer to run the generator on your machine instead of in the stack? Leave off
`--profile generator` and run it against the collector directly:

```bash
dotnet run --project src/TraceTown.Traffic -- examples/ecommerce.json
```

That is the faster loop while you are editing a config, since there is no image
to rebuild.
