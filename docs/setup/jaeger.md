# Jaeger

**Traces.** The quickest setup here — two containers, up in seconds.

```bash
docker compose -f deploy/jaeger.yml --profile generator up -d
```

| | |
|---|---|
| Jaeger | <http://localhost:16686> |
| Control API | <http://localhost:8080/api/status> |

Pick `edge-gateway`, **Find Traces**, open a `POST /api/checkout`. It has around
26 spans across 7 services, including the hop through Kafka into `order-worker`.

Then make something go wrong:

```bash
curl -X POST localhost:8080/api/scenarios/cascade
```

Stop it, and delete its data:

```bash
docker compose -f deploy/jaeger.yml --profile generator down -v
```

Prefer to run the generator on your machine instead of in the stack? Leave off
`--profile generator` and run it against the collector directly:

```bash
dotnet run --project src/TraceTown.Traffic -- examples/ecommerce.json
```

That is the faster loop while you are editing a config, since there is no image
to rebuild.
