# SigNoz

**Traces, metrics and logs**, in one place. This is what
[Trace Town](https://github.com/FollyFactory/trace-town) reads from.

```bash
docker compose -f deploy/signoz.yml --profile generator up -d
```

| | |
|---|---|
| SigNoz | <http://localhost:3301> |
| Control API | <http://localhost:8080/api/status> |

> **Create an account at <http://localhost:3301> first.** Until you do, SigNoz
> pushes a `nop` pipeline to its own ingester: port 4317 never opens, the
> collector logs `connection refused`, and every container reports healthy. It
> is a one-time step and nothing about the symptoms points at it.

The heaviest setup here — ClickHouse, a keeper, Postgres and two collectors —
so give it a minute on first start while migrations run.

## If nothing appears

Check setup actually completed:

```bash
curl -s localhost:3301/api/v1/version    # "setupCompleted": true
```

Then see what has been stored, without going through the UI:

```bash
docker exec signoz-telemetrystore-clickhouse-0-0 clickhouse-client --query "
  SELECT 'traces' s, count() n FROM signoz_traces.distributed_signoz_index_v3
  UNION ALL SELECT 'logs',    count() FROM signoz_logs.distributed_logs_v2
  UNION ALL SELECT 'metrics', count() FROM signoz_metrics.distributed_samples_v4
  FORMAT TSV"
```

A low log count is expected, not a fault — only errors and sampled requests are
logged.

## Two things to know

**Databases appear under their system name.** SigNoz builds its service map from
`db.system`, so five simulated Postgres services arrive as one `postgresql` node.
That is SigNoz's model.

The generator emits `db.system` alongside the current `db.system.name` for
exactly this reason — see
[telemetry-model.md](../telemetry-model.md#both-names-are-emitted-on-purpose).
Set `exporter.semanticConventions` to `latest` and every database disappears from
the service map.

**This is a testing stack.** `deploy/config/signoz/` is a pinned snapshot of what
[Foundry](https://signoz.io/docs/install/docker/) generates. SigNoz deprecated
their own Compose manifests in its favour; use Foundry to run SigNoz for real.

Stop it, and delete its data:

```bash
docker compose -f deploy/signoz.yml --profile generator down -v
```
