# Setups

One command each. Every setup runs an OpenTelemetry collector on `4318`, the
generator against it, and a backend to look at the results in.

| | Signals | Start |
|---|---|---|
| **[Jaeger](jaeger.md)** | traces | `docker compose -f deploy/jaeger.yml --profile generator up -d` |
| **[Prometheus](prometheus.md)** | metrics | `docker compose -f deploy/prometheus.yml --profile generator up -d` |
| **[Loki](loki.md)** | logs | `docker compose -f deploy/loki.yml --profile generator up -d` |
| **[Tempo](tempo.md)** | traces | `docker compose -f deploy/tempo.yml --profile generator up -d` |
| **[SigNoz](signoz.md)** | all three | `docker compose -f deploy/signoz.yml --profile generator up -d` |
| **[Everything](all.md)** | all three | `docker compose -f deploy/all.yml --profile generator up -d` |

New to this? Start with **Jaeger** — two containers, and traces are the signal
that shows the shape of the system.

## Things that apply to all of them

**Run one at a time.** They all bind `4318`, `8080` and often `3000`, so bring
one down before starting another.

**`--profile generator` is what starts the generator.** Leave it off to run only
the backend, then point the generator at it yourself:

```bash
dotnet run --project src/TraceTown.Traffic -- examples/ecommerce.json
```

**`down -v` deletes the backend's data**, which is usually what you want between
experiments.

**Signals a backend does not take are discarded** at the collector, so Jaeger
receiving metrics is not an error — nothing is logged and nothing fails.

## Already have a backend?

Skip all of this:

```bash
dotnet run --project src/TraceTown.Traffic -- examples/ecommerce.json \
  --endpoint https://otlp.example.com
```
