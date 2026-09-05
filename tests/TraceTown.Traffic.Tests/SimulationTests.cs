using System.Diagnostics;
using TraceTown.Traffic.Configuration;
using TraceTown.Traffic.Faults;
using TraceTown.Traffic.Model;
using TraceTown.Traffic.Simulation;

namespace TraceTown.Traffic.Tests;

public class LatencySamplerTests
{
    [Fact]
    public void Lognormal_reproduces_the_percentiles_it_was_given()
    {
        var latency = new LatencyConfig { P50Ms = 20, P99Ms = 200 };
        var rng = new Rng(42);

        double[] samples = [.. Enumerable.Range(0, 200_000).Select(_ => LatencySampler.Sample(latency, rng))];
        Array.Sort(samples);

        // Within 5% is as tight as it is sensible to assert on 200k samples.
        samples[(int)(samples.Length * 0.50)].ShouldBe(20, tolerance: 1.0);
        samples[(int)(samples.Length * 0.99)].ShouldBe(200, tolerance: 10.0);
    }

    [Fact]
    public void Constant_returns_exactly_the_p50()
    {
        var latency = new LatencyConfig
        {
            P50Ms = 7,
            P99Ms = 99,
            Distribution = LatencyDistribution.Constant,
        };

        LatencySampler.Sample(latency, new Rng(1)).ShouldBe(7);
    }

    [Fact]
    public void Never_returns_less_than_the_floor()
    {
        var latency = new LatencyConfig { P50Ms = 1, P99Ms = 2, FloorMs = 40 };
        var rng = new Rng(7);

        for (int i = 0; i < 1000; i++)
        {
            LatencySampler.Sample(latency, rng).ShouldBeGreaterThanOrEqualTo(40);
        }
    }

    [Fact]
    public void Is_reproducible_from_a_seed()
    {
        var latency = new LatencyConfig { P50Ms = 10, P99Ms = 100 };

        double[] first = [.. Enumerable.Range(0, 50).Select(_ => LatencySampler.Sample(latency, new Rng(99)))];
        double[] second = [.. Enumerable.Range(0, 50).Select(_ => LatencySampler.Sample(latency, new Rng(99)))];

        first.ShouldBe(second);
    }
}

public class RngTests
{
    [Fact]
    public void Handles_an_inclusive_upper_bound_of_int_max()
    {
        // The naive `max + 1` overflows here, which used to crash message id
        // generation.
        var rng = new Rng(1);
        Should.NotThrow(() => rng.Next(0, int.MaxValue));
    }

    [Fact]
    public void Poisson_has_the_mean_it_was_asked_for()
    {
        var rng = new Rng(5);

        foreach (double lambda in (double[])[0.5, 5, 40, 500])
        {
            double mean = Enumerable.Range(0, 20_000).Select(_ => rng.NextPoisson(lambda)).Average();
            mean.ShouldBe(lambda, tolerance: Math.Max(0.1, lambda * 0.05));
        }
    }
}

public class RequestSimulatorTests
{
    private static (RequestSimulator Simulator, Topology Topology, FaultBoard Faults) Build(string json)
    {
        TrafficConfig config = TestConfigs.Parse(json);
        var topology = new Topology(config);
        var faults = new FaultBoard(topology);
        return (new RequestSimulator(topology, faults, config.Simulation), topology, faults);
    }

    [Fact]
    public void A_database_call_produces_a_client_span_and_no_server_span()
    {
        (RequestSimulator simulator, Topology topology, _) = Build(TestConfigs.Simple);
        SimulatedRequest request = simulator.Simulate(topology.Flows[0], new Rng(3), DateTimeOffset.UtcNow);

        SpanPlan[] all = [.. request.Root.Descend()];
        SpanPlan db = all.Single(s => s.Peer?.Id == "db");

        db.Kind.ShouldBe(ActivityKind.Client);
        db.Emitter.Id.ShouldBe("api", "the caller records the span, not the database");
        db.Children.ShouldBeEmpty("nothing inside a database instruments itself");
        all.ShouldNotContain(s => s.Emitter.Id == "db");
    }

    [Fact]
    public void A_database_call_carries_the_database_conventions()
    {
        (RequestSimulator simulator, Topology topology, _) = Build(TestConfigs.Simple);
        SimulatedRequest request = simulator.Simulate(topology.Flows[0], new Rng(3), DateTimeOffset.UtcNow);

        SpanPlan db = request.Root.Descend().Single(s => s.Peer?.Id == "db");
        Dictionary<string, object?> tags = db.Attributes.ToDictionary(a => a.Key, a => a.Value);

        tags["db.system.name"].ShouldBe("postgresql");
        tags["db.operation.name"].ShouldBe("SELECT");
        tags["db.collection.name"].ShouldBe("users");
        tags["db.query.text"].ShouldBe("SELECT * FROM users WHERE id = $1");
        tags["server.port"].ShouldBe(5432);
    }

    [Fact]
    public void A_call_to_an_instrumented_service_produces_both_halves()
    {
        (RequestSimulator simulator, Topology topology, _) = Build("""
            {
              "services": [
                { "id": "a", "kind": "api", "dependencies": [ { "target": "b", "operation": "GET /thing" } ] },
                { "id": "b", "kind": "api" }
              ],
              "flows": [ { "id": "f", "entry": "a", "route": "GET /" } ]
            }
            """);

        SimulatedRequest request = simulator.Simulate(topology.Flows[0], new Rng(1), DateTimeOffset.UtcNow);
        SpanPlan client = request.Root.Descend().Single(s => s.Kind == ActivityKind.Client);

        client.Emitter.Id.ShouldBe("a");
        SpanPlan server = client.Children.ShouldHaveSingleItem();
        server.Kind.ShouldBe(ActivityKind.Server);
        server.Emitter.Id.ShouldBe("b");
    }

    [Fact]
    public void A_queue_publish_produces_a_producer_span_with_the_consumer_beneath_it()
    {
        (RequestSimulator simulator, Topology topology, _) = Build(TestConfigs.Queued);
        SimulatedRequest request = simulator.Simulate(topology.Flows[0], new Rng(11), DateTimeOffset.UtcNow);

        SpanPlan producer = request.Root.Descend().Single(s => s.Kind == ActivityKind.Producer);
        producer.Emitter.Id.ShouldBe("api");

        SpanPlan consumer = producer.Children.Single(c => c.Kind == ActivityKind.Consumer);
        consumer.Emitter.Id.ShouldBe("worker");

        // Trace context reaches the consumer through the message, which is the
        // only reason the two halves end up in one trace.
        consumer.Attributes.ShouldContain(a => a.Key == "messaging.consumer.group.name");
    }

    [Fact]
    public void The_consumer_starts_a_queue_lag_after_the_producer_finishes()
    {
        (RequestSimulator simulator, Topology topology, _) = Build(TestConfigs.Queued);
        SimulatedRequest request = simulator.Simulate(topology.Flows[0], new Rng(11), DateTimeOffset.UtcNow);

        SpanPlan producer = request.Root.Descend().Single(s => s.Kind == ActivityKind.Producer);
        SpanPlan consumer = producer.Children.Single(c => c.Kind == ActivityKind.Consumer);

        // The dead time between publishing and picking up is the queue lag, and
        // drawing it as a gap in the trace is the point of modelling it at all.
        (consumer.StartOffsetMs - producer.EndOffsetMs).ShouldBe(500, tolerance: 1);
    }

    [Fact]
    public void Every_span_offset_is_relative_to_the_start_of_the_request()
    {
        (RequestSimulator simulator, Topology topology, _) = Build(TestConfigs.Simple);
        SimulatedRequest request = simulator.Simulate(topology.Flows[0], new Rng(7), DateTimeOffset.UtcNow);

        // A child cannot begin before its parent, and a synchronous one cannot
        // outlive it. Getting this wrong stacks every span at the trace origin,
        // which a viewer renders as a plausible-looking lie.
        foreach (SpanPlan parent in request.Root.Descend())
        {
            foreach (SpanPlan child in parent.Children)
            {
                child.StartOffsetMs.ShouldBeGreaterThanOrEqualTo(parent.StartOffsetMs);
                child.EndOffsetMs.ShouldBeLessThanOrEqualTo(parent.EndOffsetMs + 0.001);
            }
        }
    }

    [Fact]
    public void Publishing_does_not_charge_the_caller_for_the_consumers_work()
    {
        (RequestSimulator simulator, Topology topology, _) = Build(TestConfigs.Queued);
        SimulatedRequest request = simulator.Simulate(topology.Flows[0], new Rng(11), DateTimeOffset.UtcNow);

        SpanPlan producer = request.Root.Descend().Single(s => s.Kind == ActivityKind.Producer);
        SpanPlan consumer = producer.Children.Single(c => c.Kind == ActivityKind.Consumer);

        // The whole point of a queue: the request returns long before the work
        // it queued has finished.
        producer.DurationMs.ShouldBeLessThan(consumer.EndOffsetMs);
        request.Root.DurationMs.ShouldBeLessThan(consumer.EndOffsetMs);
    }

    [Fact]
    public void A_flow_with_zero_depth_touches_only_its_entry_point()
    {
        (RequestSimulator simulator, Topology topology, _) = Build("""
            {
              "services": [
                { "id": "api", "kind": "api", "dependencies": [ { "target": "db" } ] },
                { "id": "db", "kind": "database" }
              ],
              "flows": [ { "id": "health", "entry": "api", "route": "GET /healthz", "depth": 0 } ]
            }
            """);

        SimulatedRequest request = simulator.Simulate(topology.Flows[0], new Rng(1), DateTimeOffset.UtcNow);
        request.Root.Descend().Count().ShouldBe(1);
    }

    [Fact]
    public void Always_overrides_the_probability_on_a_dependency_edge()
    {
        (RequestSimulator simulator, Topology topology, _) = Build("""
            {
              "services": [
                { "id": "bff", "kind": "api", "dependencies": [
                  { "target": "rare", "operation": "GET /x", "probability": 0.001 }
                ] },
                { "id": "rare", "kind": "api" }
              ],
              "flows": [ { "id": "f", "entry": "bff", "always": ["rare"] } ]
            }
            """);

        var rng = new Rng(1);
        for (int i = 0; i < 50; i++)
        {
            SimulatedRequest request = simulator.Simulate(topology.Flows[0], rng, DateTimeOffset.UtcNow);
            request.Root.Descend().ShouldContain(s => s.Peer != null && s.Peer.Id == "rare");
        }
    }

    [Fact]
    public void Except_prunes_a_branch_at_any_depth()
    {
        (RequestSimulator simulator, Topology topology, _) = Build("""
            {
              "services": [
                { "id": "a", "kind": "api", "dependencies": [ { "target": "b" } ] },
                { "id": "b", "kind": "api", "dependencies": [ { "target": "deep" } ] },
                { "id": "deep", "kind": "database" }
              ],
              "flows": [ { "id": "f", "entry": "a", "except": ["deep"] } ]
            }
            """);

        var rng = new Rng(1);
        for (int i = 0; i < 50; i++)
        {
            SimulatedRequest request = simulator.Simulate(topology.Flows[0], rng, DateTimeOffset.UtcNow);
            request.Root.Descend().ShouldNotContain(s => s.Peer != null && s.Peer.Id == "deep");
        }
    }

    [Fact]
    public void A_client_error_does_not_mark_the_request_as_failed()
    {
        (RequestSimulator simulator, Topology topology, _) = Build("""
            {
              "services": [ { "id": "api", "kind": "api" } ],
              "flows": [ { "id": "f", "entry": "api", "clientErrorRate": 1.0 } ]
            }
            """);

        SimulatedRequest request = simulator.Simulate(topology.Flows[0], new Rng(1), DateTimeOffset.UtcNow);

        // A 4xx is the caller's problem, not an incident. A town that rained
        // every time someone sent a malformed request would be useless.
        request.Failed.ShouldBeFalse();
        request.Root.Attributes.ShouldContain(a =>
            a.Key == "http.response.status_code" && (int)a.Value! >= 400 && (int)a.Value! < 500);
    }

    [Fact]
    public void Recursion_stops_at_the_configured_depth()
    {
        (RequestSimulator simulator, Topology topology, _) = Build("""
            {
              "simulation": { "maxDepth": 3 },
              "services": [
                { "id": "a", "kind": "api", "dependencies": [ { "target": "b" } ] },
                { "id": "b", "kind": "api", "dependencies": [ { "target": "a" } ] }
              ],
              "flows": [ { "id": "f", "entry": "a" } ]
            }
            """);

        SimulatedRequest request = Should.NotThrow(
            () => simulator.Simulate(topology.Flows[0], new Rng(1), DateTimeOffset.UtcNow));

        request.Root.Descend().Count().ShouldBeLessThan(20);
    }
}
