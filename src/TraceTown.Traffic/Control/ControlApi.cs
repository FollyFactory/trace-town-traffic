using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TraceTown.Traffic.Configuration;
using TraceTown.Traffic.Faults;
using TraceTown.Traffic.Model;
using TraceTown.Traffic.Simulation;

namespace TraceTown.Traffic.Control;

/// <summary>
/// The runtime control surface: switch scenario, inject a fault, change a rate,
/// see what is currently wrong. Everything here is also expressible in the
/// config file — this exists so you can do it without a restart, in front of an
/// audience or from a test.
/// </summary>
public static class ControlApi
{
    public static void MapControlApi(this IEndpointRouteBuilder app, Engine engine)
    {
        app.MapGet("/", () => Results.Content(ConsolePage.Page, "text/html; charset=utf-8"));

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/api/status", () =>
        {
            EngineStats stats = engine.Stats();
            return Results.Ok(new
            {
                town = engine.Config.Town.Name,
                environment = engine.Config.Town.Environment,
                running = engine.IsRunning,
                startedAt = engine.StartedAt,
                uptimeSeconds = Math.Round(stats.Uptime.TotalSeconds, 1),
                exporter = new
                {
                    endpoint = engine.Config.Exporter.Endpoint,
                    protocol = engine.Config.Exporter.Protocol.ToString(),
                    traces = engine.Config.Exporter.Traces,
                    metrics = engine.Config.Exporter.Metrics,
                    logs = engine.Config.Exporter.Logs,
                },
                counts = new
                {
                    services = engine.Topology.Services.Count,
                    flows = engine.Config.Flows.Count,
                    requests = stats.Requests,
                    errors = stats.Errors,
                    spans = stats.Spans,
                },
                scenario = new
                {
                    current = engine.Scenarios.CurrentId,
                    elapsedSeconds = Math.Round(engine.Scenarios.Elapsed.TotalSeconds, 1),
                    nextStepAt = engine.Scenarios.NextStepAt,
                },
                faults = engine.Faults.Snapshot().Count,
                paused = engine.Paused,
                simulation = Simulation(engine),
            });
        });

        MapTopology(app, engine);
        MapScenarios(app, engine);
        MapFaults(app, engine);
        MapFlows(app, engine);
        MapSimulation(app, engine);
        MapActivity(app, engine);
    }

    /// <summary>The knobs that apply to the whole run rather than to one thing.</summary>
    private static void MapSimulation(IEndpointRouteBuilder app, Engine engine)
    {
        app.MapGet("/api/simulation", () => Results.Ok(Simulation(engine)));

        app.MapPost("/api/simulation", (SimulationRequest body) =>
        {
            if (body.RateMultiplier is { } rate)
            {
                if (rate < 0)
                {
                    return Results.BadRequest(new { error = "rateMultiplier cannot be negative." });
                }

                engine.RateMultiplier = rate;
            }

            if (body.TraceSampleRatio is { } ratio)
            {
                if (ratio is < 0 or > 1)
                {
                    return Results.BadRequest(new { error = "traceSampleRatio must be between 0 and 1." });
                }

                engine.TraceSampleRatio = ratio;
            }

            if (body.Paused is { } paused)
            {
                engine.Paused = paused;
            }

            return Results.Ok(Simulation(engine));
        });
    }

    private static object Simulation(Engine engine) => new
    {
        rateMultiplier = engine.RateMultiplier,
        traceSampleRatio = engine.TraceSampleRatio,
        errorTraceSampleRatio = engine.Config.Simulation.ErrorTraceSampleRatio,
        paused = engine.Paused,
        seed = engine.Config.Simulation.Seed,
    };

    /// <summary>
    /// A tail of what has just been produced. The console polls this with the
    /// highest sequence number it has seen, so it never re-reads a trace and
    /// never misses one between polls.
    /// </summary>
    private static void MapActivity(IEndpointRouteBuilder app, Engine engine)
    {
        app.MapGet("/api/activity", (long? since, int? limit) =>
        {
            EngineStats stats = engine.Stats();
            return Results.Ok(new
            {
                latest = engine.Activity.Latest,
                traces = engine.Activity.Since(since ?? 0, Math.Clamp(limit ?? 60, 1, 200)),
                counts = new
                {
                    requests = stats.Requests,
                    errors = stats.Errors,
                    spans = stats.Spans,
                },
                uptimeSeconds = Math.Round(stats.Uptime.TotalSeconds, 1),
                paused = engine.Paused,
            });
        });
    }

    private static void MapTopology(IEndpointRouteBuilder app, Engine engine)
    {
        app.MapGet("/api/topology", () => Results.Ok(new
        {
            services = engine.Topology.Services.Select(s => new
            {
                id = s.Id,
                name = s.Name,
                kind = s.Kind.ToString().ToLowerInvariant(),
                group = s.Group,
                version = s.Version,
                instances = s.Instances.Length,
                emitsServerSpans = s.EmitsServerSpans,
                dependencies = s.Config.Dependencies.Select(d => new
                {
                    target = d.Target,
                    operation = d.Operation,
                    probability = d.Probability,
                }),
                consumes = s.Config.Consumes.Select(c => new { queue = c.Queue, lagMs = c.LagMs }),
            }),

            // The edges as a viewer would want them: who calls whom, including
            // the queue hops, which are the ones you cannot infer from the
            // dependency list alone.
            edges = Edges(engine.Topology),
        }));
    }

    private static IEnumerable<object> Edges(Topology topology)
    {
        foreach (ServiceRuntime service in topology.Services)
        {
            foreach (DependencyConfig dependency in service.Config.Dependencies)
            {
                yield return new { from = service.Id, to = dependency.Target, kind = "call" };
            }

            foreach (ConsumerConfig consumer in service.Config.Consumes)
            {
                yield return new { from = consumer.Queue, to = service.Id, kind = "consume" };
            }
        }
    }

    private static void MapScenarios(IEndpointRouteBuilder app, Engine engine)
    {
        app.MapGet("/api/scenarios", () => Results.Ok(
            engine.Config.Scenarios.Select(s => new
            {
                id = s.Id,
                description = s.Description,
                loop = s.Loop,
                steps = s.Steps.Count,
                current = s.Id == engine.Scenarios.CurrentId,
            })));

        app.MapPost("/api/scenarios/{id}", (string id) =>
            engine.Scenarios.Start(id, DateTimeOffset.UtcNow)
                ? Results.Ok(new { scenario = id, started = true })
                : Results.NotFound(new
                {
                    error = $"No scenario '{id}'.",
                    available = engine.Scenarios.Available,
                }));

        app.MapDelete("/api/scenarios/current", () =>
        {
            engine.Scenarios.Stop();
            return Results.Ok(new { stopped = true });
        });
    }

    private static void MapFaults(IEndpointRouteBuilder app, Engine engine)
    {
        app.MapGet("/api/faults", () => Results.Ok(
            engine.Faults.Snapshot().Select(f => Describe(f, DateTimeOffset.UtcNow))));

        app.MapPost("/api/faults", (FaultConfig fault) =>
        {
            ValidationResult result = ValidateFault(engine, fault);
            if (!result.IsValid)
            {
                return Results.BadRequest(new { errors = result.Errors });
            }

            ActiveFault active = engine.Faults.Add(fault, "manual", DateTimeOffset.UtcNow);
            return Results.Ok(Describe(active, DateTimeOffset.UtcNow));
        });

        app.MapDelete("/api/faults/{id}", (string id) =>
            engine.Faults.Remove(id)
                ? Results.Ok(new { removed = id })
                : Results.NotFound(new { error = $"No active fault '{id}'." }));

        app.MapDelete("/api/faults", () => Results.Ok(new { removed = engine.Faults.Clear() }));
    }

    /// <summary>
    /// Runs an injected fault through the same checks the config file gets. A
    /// typo through the API would otherwise apply cleanly and do nothing, which
    /// is the most confusing possible outcome.
    /// </summary>
    private static ValidationResult ValidateFault(Engine engine, FaultConfig fault)
    {
        TrafficConfig probe = engine.Config with
        {
            Scenarios = [new ScenarioConfig
            {
                Id = InjectedScenarioId,
                Steps = [new ScenarioStepConfig { At = "0s", Faults = [fault] }],
            }],
            Simulation = engine.Config.Simulation with { Scenario = null },
        };

        ValidationResult result = ConfigValidator.Validate(probe);

        // The probe scenario is an implementation detail; the caller sent a
        // fault, so the message should talk about the fault.
        List<string> errors = [.. result.Errors
            .Where(e => e.Contains(InjectedScenarioId, StringComparison.Ordinal))
            .Select(e => e[(e.IndexOf(": ", StringComparison.Ordinal) + 2)..])];

        return new ValidationResult(errors, result.Warnings);
    }

    private const string InjectedScenarioId = "__injected";

    private static object Describe(ActiveFault fault, DateTimeOffset now) => new
    {
        id = fault.Id,
        origin = fault.Origin,
        target = fault.Config.Target,
        kind = fault.Config.Kind.ToString().ToLowerInvariant(),
        intensity = Math.Round(fault.Intensity(now), 3),
        ageSeconds = Math.Round((now - fault.StartedAt).TotalSeconds, 1),
        expiresIn = fault.Lifetime > TimeSpan.Zero
            ? Math.Round((fault.StartedAt + fault.Lifetime - now).TotalSeconds, 1)
            : (double?)null,
        because = fault.Config.Because,
    };

    private static void MapFlows(IEndpointRouteBuilder app, Engine engine)
    {
        app.MapGet("/api/flows", () =>
        {
            EngineStats stats = engine.Stats();
            return Results.Ok(engine.Config.Flows.Select(f =>
            {
                stats.Flows.TryGetValue(f.Id, out FlowSnapshot? snapshot);
                return new
                {
                    id = f.Id,
                    entry = f.Entry,
                    route = f.Route,
                    configuredRps = f.Rps,
                    effectiveRps = engine.EffectiveRate(f),
                    profile = f.Profile.Kind.ToString().ToLowerInvariant(),
                    trafficMultiplier = Math.Round(
                        engine.Faults.TrafficMultiplier(f.Id, DateTimeOffset.UtcNow), 3),
                    requests = snapshot?.Requests ?? 0,
                    errors = snapshot?.Errors ?? 0,
                };
            }));
        });

        app.MapPost("/api/flows/{id}/rate", (string id, RateRequest body) =>
            engine.SetFlowRate(id, body.Rps)
                ? Results.Ok(new { flow = id, rps = body.Rps })
                : Results.NotFound(new { error = $"No flow '{id}'." }));

        app.MapDelete("/api/flows/{id}/rate", (string id) =>
        {
            engine.ClearFlowRate(id);
            return Results.Ok(new { flow = id, reset = true });
        });
    }
}

public sealed record RateRequest(double Rps);

/// <summary>All optional: send only the knobs you are turning.</summary>
public sealed record SimulationRequest(double? RateMultiplier, double? TraceSampleRatio, bool? Paused);
