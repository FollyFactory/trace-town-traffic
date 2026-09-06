# Scenarios

A timed sequence of things going wrong. This is the reason the tool exists.

```bash
trace-town-traffic examples/ecommerce.json --scenario cascade
curl -X POST localhost:8080/api/scenarios/cascade    # or switch it live
```

| Fault | Effect |
|---|---|
| `latency` | Slower. `multiplier`, or absolute `p50Ms`/`p99Ms` |
| `errors` | Fails some of the time. `errorRate` |
| `outage` | Refuses connections — fails *fast*, unlike a timeout |
| `traffic` | More or less load. Targets a flow: `"flow:checkout"` |
| `queueLag` | Messages wait longer. Backlog grows |
| `saturation` | Exhausts its own resources — visible in infra metrics first |

Add `ramp` to ease one in, `coverage` to hit only some replicas, `duration` to
self-clear. `target` takes globs.

---

A scenario is a timed sequence of things going wrong. Generating steady traffic
is easy and not very interesting; being able to reproduce a specific incident, on
demand, in seconds, is the reason this tool exists.

```bash
trace-town-traffic examples/ecommerce.json --scenario cascade
trace-town-traffic examples/ecommerce.json --list-scenarios

# or switch at runtime, without a restart
curl -X POST localhost:8080/api/scenarios/cascade
```

Switching scenario clears whatever the previous one left behind, so you never end
up with two incidents overlapping by accident.

## Shape

```json
{
  "id": "cascade",
  "description": "A third party slows and the pressure walks upstream.",
  "loop": false,
  "steps": [
    {
      "at": "30s",
      "note": "Stripe latency climbs",
      "faults": [
        { "target": "stripe", "kind": "latency", "p99Ms": 9000, "ramp": "60s",
          "because": "upstream provider degradation" }
      ]
    },
    { "at": "6m", "note": "Recovered", "clear": true }
  ]
}
```

| Field | | |
|---|---|---|
| `id` | string | Referenced by `--scenario` and the control API |
| `description` | string | Shown by `--list-scenarios` |
| `loop` | bool | Restart from the top once the last step's time passes |
| `steps[].at` | duration | Offset from the start. Steps run in time order regardless of how they are written |
| `steps[].note` | string | Logged when the step fires, so a run narrates itself |
| `steps[].clear` | bool | Remove every fault *this scenario* has applied |
| `steps[].faults` | array | Faults to apply |

`clear` only removes this scenario's own faults. Anything you injected by hand
through the control API survives, which is usually what you want when you are
poking at a running system.

## Faults

| Field | | |
|---|---|---|
| `target` | A service id, `flow:<id>` for a flow, or a glob against either |
| `kind` | `latency`, `errors`, `outage`, `traffic`, `queueLag`, `saturation` |
| `multiplier` | Scales the current value |
| `p50Ms`, `p99Ms` | Absolute latency, overriding the service's own |
| `errorRate` | Absolute error rate, 0..1 |
| `lagMs` | Queue wait time |
| `coverage` | Fraction of replicas affected, 0..1 |
| `ramp` | Ease the fault in over this long |
| `duration` | Self-clear after this long |
| `because` | Free-text reason, logged when it fires |

Faults **stack**. A service can be both slow and erroring without either
definition knowing about the other.

### `latency`

```json
{ "target": "postgres-orders", "kind": "latency", "multiplier": 20 }
{ "target": "stripe", "kind": "latency", "p50Ms": 2500, "p99Ms": 9000 }
```

`multiplier` scales both percentiles. `p50Ms`/`p99Ms` set them outright. With
neither, the service simply gets noticeably slower.

### `errors`

```json
{ "target": "checkout-api", "kind": "errors", "errorRate": 0.35 }
```

Server-side failures — 5xx, and the span status set to Error. Distinct from a
flow's `clientErrorRate`, which produces 4xx and is nobody's incident.

### `outage`

```json
{ "target": "postgres-orders", "kind": "outage", "because": "failover" }
```

The service refuses connections. Calls fail in **under a millisecond** with
`error.type=connection_error` and no server span, because nothing was there to
record one.

That speed is the whole point. A service that is down and a service that is
merely struggling produce completely different traces — one is a wall of
sub-millisecond failures, the other a wall of slow ones — and any tool you build
should be able to tell them apart.

### `traffic`

```json
{ "target": "flow:checkout", "kind": "traffic", "multiplier": 12, "ramp": "90s" }
```

Must target a flow, hence the `flow:` prefix; validation rejects it otherwise.
Nothing is broken here — there is simply a great deal more of it, which is its
own kind of incident.

### `queueLag`

```json
{ "target": "orders-topic", "kind": "queueLag", "lagMs": 45000, "ramp": "90s" }
```

Messages wait longer before being picked up. The consumer span moves later in the
trace, and the broker's queue depth grows to match — by Little's law, depth is
arrival rate times wait time, so a worker falling behind produces a growing
backlog without the publish rate changing at all.

Can target the queue or the consumer; targeting the queue affects every consumer
of it.

### `saturation`

```json
{ "target": "payments-api", "kind": "saturation", "ramp": "45s" }
```

The component exhausts its own resources. Connection pools fill, CPU climbs,
memory rises, latency follows, and past about 70% it starts shedding load
outright with `error.type=resource_exhausted`.

This is the fault that shows up in **infrastructure metrics before it shows up in
latency**, which is the entire argument for collecting them. Watch
`postgresql.backends` climb toward `postgresql.connection.max` while the p99 is
still fine.

## Ramps

```json
{ "target": "stripe", "kind": "latency", "p99Ms": 9000, "ramp": "60s" }
```

Without `ramp` a fault applies instantly. With it, the effect is eased in
linearly over the given time. A dependency that degrades over ninety seconds and
one that falls over instantly are different incidents, and only one of them is
what usually happens.

## Partial coverage

```json
{ "target": "checkout-api", "kind": "errors", "errorRate": 0.6, "coverage": 0.34 }
```

Applies to a third of the replicas. This is what a bad deploy looks like:
errors, but not everywhere, and only from some `service.instance.id` values. It
is a genuinely hard case for alerting, because the aggregate error rate barely
moves while a third of your users have a terrible time.

## Self-clearing faults

```json
{ "target": "orders-topic", "kind": "queueLag", "lagMs": 15000, "duration": "2m" }
```

Useful for the tail of a recovery, where the fix has landed but the backlog is
still draining.

## Globs

```json
{ "target": "*-api",      "kind": "latency", "multiplier": 3 }
{ "target": "postgres-*", "kind": "outage" }
{ "target": "*",          "kind": "latency", "multiplier": 2 }
```

A pattern matching nothing is a warning, not an error — you might be sharing
scenarios across configs — but it does tell you the fault will do nothing.

## The bundled scenarios

`examples/ecommerce.json` ships seven. The first four match Trace Town's mock
feed, so the real thing and the simulator show the same stories.

| | |
|---|---|
| `calm` | Everything healthy. The baseline the others deviate from |
| `degrading` | One service quietly gets worse. Should read as "something is wrong over there", not a general alarm |
| `cascade` | A third party slows, its caller saturates, checkout sheds load, the queue backs up. Loops |
| `recovery` | Starts broken and mends, with the backlog draining after the fix |
| `bad-deploy` | A release reaches a third of the fleet and only that third misbehaves |
| `black-friday` | Nothing is broken. There is 8–12× more of it, until the cache starts evicting |
| `database-outage` | A database vanishes. Refused connections, not slow ones |

## Writing your own

Start from a real incident. The scenarios that are worth having are the ones you
have actually lived through, and the useful question is not "what broke" but
"what did the graphs look like on the way there".

A few things worth doing:

- **Give every step a `note`.** It is logged when the step fires, so the run
  narrates itself and you are not left guessing what phase you are watching.
- **Ramp anything that would ramp in reality.** Instant faults are for outages.
- **Model the cause, not the symptom.** In `cascade` the only thing genuinely
  broken is Stripe; everything downstream follows from that, which is what makes
  it a useful test of whether your tooling can find the origin.
- **Include a recovery.** Half the value of an incident scenario is watching
  things go back to normal, and repair-and-reopen paths are the least tested part
  of most dashboards.
- **Check it with `--dry-run` and `--validate`** before a long run.
