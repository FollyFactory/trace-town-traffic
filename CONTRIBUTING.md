# Contributing

Thanks for looking. Issues and pull requests are welcome.

## Building and testing

Needs [.NET 10](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet run --project tests/TraceTown.Traffic.Tests
```

**Run the tests with `dotnet run`, not `dotnet test`.** The test project is
xUnit v3 on Microsoft.Testing.Platform, and the .NET 10 SDK's `dotnet test`
server protocol currently fails to discover tests in it — it reports "Zero tests
ran" while the same binary runs all of them correctly on its own. `dotnet run`
is the MTP-native invocation and is what CI uses.

Useful while working on the simulation:

```bash
# Print one trace per flow. Exports nothing, needs no backend.
dotnet run --project src/TraceTown.Traffic -- examples/ecommerce.json --dry-run

# Check a config without running it
dotnet run --project src/TraceTown.Traffic -- examples/ecommerce.json --validate

# A backend to look at the results in — one command each, see docs/setup
docker compose -f deploy/jaeger.yml --profile generator up -d
```

## Conventions

- **Warnings are errors.** A telemetry generator that emits subtly wrong
  attributes is worse than one that does not build.
- **Semantic conventions live in one file**,
  [`Emit/SemConv.cs`](src/TraceTown.Traffic/Emit/SemConv.cs). Never inline an
  attribute name. When a convention moves on there should be one place to change.
- **Comments explain why, not what.** The code says what it does. A comment earns
  its place by explaining a decision, a trade-off, or something that would
  otherwise look like a mistake.
- **Simulation and emission stay separate.** `RequestSimulator` decides what
  happened and produces a `SpanPlan` tree; `TelemetryEmitter` turns that into
  OTLP. Anything that needs both belongs in neither.
- Style is enforced by [`.editorconfig`](.editorconfig). `dotnet format` before
  pushing.

## Adding things

**A service kind** touches `ServiceKind`, `ServiceRuntime.EmitsServerSpans`, a
branch in `RequestSimulator.Call`, and probably `Instruments`. The question to
answer first is: *does the far side of a call to it run instrumented code?* If
not, it produces a client span and nothing else.

**A fault kind** touches `FaultKind`, `FaultBoard`, and validation in
`ConfigValidator.ValidateFault`. Add it to a scenario in
`examples/ecommerce.json` too — a test asserts every fault kind is demonstrated
there.

**A metric** goes in `Instruments`, with its name in `SemConv`. Match the name a
real collector receiver would produce, so dashboards written against the real
thing work against the simulator.

Anything touching the config file also needs
[`schema/traffic.schema.json`](schema/traffic.schema.json) and
[`docs/configuration.md`](docs/configuration.md) updating. They are part of the
change, not a follow-up.

**A backend** gets its own `deploy/<name>.yml` and `deploy/config/collector-<name>.yaml`,
plus a short page in [`docs/setup/`](docs/setup/README.md) linked from the table
in that folder's README and in the main one. Keep the file self-contained: one
`-f` flag, one command, no overlays. Signals the backend does not take go to the
`nop` exporter rather than erroring back at the generator — except traces where
a metrics store is present, which feed the `spanmetrics` and `servicegraph`
connectors instead.

## Tests

Cover the things that would otherwise be wrong silently. The suite is heaviest
around span structure — which spans exist, who emits them, how they nest — and
around fault arithmetic, because both produce output that looks perfectly
plausible when it is wrong.

The bundled examples are tested too: they must load, validate without warnings,
and produce spans on every flow.

## Pull requests

Keep them focused, explain the reasoning in the description, and make sure
`dotnet build` and the test suite pass. If you are changing what the telemetry
looks like, say what a consumer of it would notice.
