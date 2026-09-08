# Control API

Change what the running generator is doing, without a restart.

```bash
curl localhost:8080/api/status                    # what is happening
curl -X POST localhost:8080/api/scenarios/cascade # switch scenario
curl localhost:8080/api/faults                    # what is currently broken

curl -X POST localhost:8080/api/faults -H 'Content-Type: application/json' \
  -d '{"target":"postgres-*","kind":"latency","multiplier":20,"ramp":"30s"}'

curl -X POST localhost:8080/api/flows/checkout/rate \
  -H 'Content-Type: application/json' -d '{"rps":200}'
```

Bound to `127.0.0.1` with no auth unless you set `control.token`. There is a
[console](console.md) on the same port with buttons for all of it.

---

The config file is the source of truth, but you rarely want to restart to change
one thing. A small HTTP API lets you switch scenario, inject a fault or change a
rate while the generator is running — for demos, for exploratory work, and for
driving it from a test.

```json
"control": { "enabled": true, "host": "127.0.0.1", "port": 8080 }
```

Disable it with `"enabled": false` or `--no-control`.

## Security

There is no authentication unless you set `control.token`. That is why it binds
to `127.0.0.1` by default, and why binding it anywhere else should be a decision
rather than an accident — anyone who can reach this API can reshape the telemetry
your dashboards are showing.

```json
"control": { "host": "0.0.0.0", "token": "a-long-random-string" }
```

```bash
curl -H 'Authorization: Bearer a-long-random-string' localhost:8080/api/status
```

`/healthz` is always unauthenticated, so a container orchestrator can probe it.
The tool warns on startup if you bind it wide open with no token.

## Endpoints

### `GET /healthz`

`{"status":"ok"}`. For readiness probes.

### `GET /api/status`

Everything at a glance: uptime, exporter settings, request and error counts,
spans emitted, the current scenario and how far through it you are, and how many
faults are active.

### `GET /api/topology`

The resolved graph — every service with its kind, group, version, replica count,
whether it emits server spans, and its dependencies — plus a flat `edges` list
including the queue hops, which cannot be inferred from the dependency lists
alone.

Useful for driving a visualisation without parsing the config yourself.

### `GET /api/scenarios`

Lists the scenarios, with a `current` flag on the one running.

### `POST /api/scenarios/{id}`

Switches scenario, clearing the previous one's faults. `404` with the list of
valid ids if there is no such scenario.

```bash
curl -X POST localhost:8080/api/scenarios/cascade
```

### `DELETE /api/scenarios/current`

Stops the running scenario and clears its faults.

### `GET /api/faults`

Everything currently wrong, with each fault's current ramp `intensity` (0..1),
age, and how long until it self-clears.

### `POST /api/faults`

Injects a fault. The body is exactly a fault object from a scenario file, so
anything you can write in a config you can inject here.

```bash
curl -X POST localhost:8080/api/faults \
  -H 'Content-Type: application/json' \
  -d '{"target":"postgres-*","kind":"latency","multiplier":20,"ramp":"30s","duration":"2m"}'
```

It runs the same validation the config file gets, and returns `400` with the
reason if the target does not exist or the parameters do not fit the kind. A typo
would otherwise apply cleanly and do nothing, which is the most confusing possible
outcome.

Injected faults have origin `manual`, so a scenario's `clear` step will not
remove them.

### `DELETE /api/faults/{id}` · `DELETE /api/faults`

Remove one fault, or all of them regardless of origin.

### `GET /api/flows`

Each flow's configured and effective rate, its profile, any traffic multiplier
currently applied to it, and its request and error counts.

### `POST /api/flows/{id}/rate`

Overrides a flow's rate until you change it again or restart.

```bash
curl -X POST localhost:8080/api/flows/checkout/rate \
  -H 'Content-Type: application/json' -d '{"rps":200}'
```

The load profile still applies on top, so a `diurnal` flow keeps its shape around
the new base rate.

### `DELETE /api/flows/{id}/rate`

Back to the configured rate.

## Driving it from a test

The API is the reason this tool is useful in CI rather than only at a desk. A
test can put the system into a known-bad state, assert that an alert fired or a
dashboard query returned what it should, and clean up:

```bash
BASE=http://localhost:8080

curl -sX POST $BASE/api/faults -H 'Content-Type: application/json' \
  -d '{"target":"postgres-orders","kind":"latency","p99Ms":5000}'

sleep 60   # let the metric window fill

# ... assert your alert fired ...

curl -sX DELETE $BASE/api/faults
```

Pair it with `--seed` for a reproducible run.
