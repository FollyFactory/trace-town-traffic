# Console

A control panel for the running generator, served by the generator itself.

```bash
docker compose -f deploy/jaeger.yml --profile generator up -d
```

Open **<http://localhost:8080>**.

No build step, no npm, no CDN — one HTML file embedded in the binary, so it
works in a container and on a machine with no internet.

## What is on it

**Live activity** — a feed of what has just been generated. Click a trace to open
its span tree: a waterfall with the service, span kind and duration of every
span, and `(no server span)` against every call to something that does not
instrument itself.

Opening a trace freezes the feed so it does not scroll away under you. **Freeze**
holds it manually; **Errors only** hides everything that succeeded.

**Scenarios** — one click each. The running one is highlighted and shows when its
next step fires.

**Simulation** — rate multiplier across every flow, trace sample ratio, and a
pause that stops generation without tearing anything down, so you can set a
state up before releasing traffic into it.

**Flows** — a rate slider per flow, with its error count and any traffic fault
currently applied.

**Services** — every service with `slow`, `fail` and `down` buttons, for breaking
one without writing a fault by hand.

**Inject a fault** — the full form: any target (globs work), any kind, with ramp,
duration and replica coverage.

**Active faults** — what is broken, how far a ramp has got, and how long until it
clears itself. Each has an ✕.

## Notes

The console is the API with buttons on it — everything it does is in
[control-api.md](control-api.md), so anything you can click you can script.

It polls: activity every second, everything else every 2.5s. Nothing streams, so
a dropped connection recovers on its own.

Requests per second is computed from deltas between polls, so it settles a couple
of seconds after start-up.

**It has no authentication.** Neither does the API under it — that is why both
bind to `127.0.0.1` unless you change `control.host`. Set `control.token` and the
API requires it, though the console does not yet prompt for one, so use it with
`--no-control` or a proxy in front rather than exposing it.

Turn it off with `--no-control`, or move it with `--port`.
