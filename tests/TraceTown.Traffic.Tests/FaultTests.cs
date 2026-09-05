using TraceTown.Traffic.Configuration;
using TraceTown.Traffic.Faults;
using TraceTown.Traffic.Model;
using TraceTown.Traffic.Simulation;

namespace TraceTown.Traffic.Tests;

public class FaultBoardTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (FaultBoard Board, Topology Topology) Build(string json = TestConfigs.Simple)
    {
        var topology = new Topology(TestConfigs.Parse(json));
        return (new FaultBoard(topology), topology);
    }

    [Fact]
    public void A_latency_multiplier_scales_both_percentiles()
    {
        (FaultBoard board, Topology topology) = Build();
        board.Add(new FaultConfig { Target = "db", Kind = FaultKind.Latency, Multiplier = 10 }, "test", Now);

        LatencyConfig latency = board.Latency(topology["db"], 0, Now);

        latency.P50Ms.ShouldBe(30);
        latency.P99Ms.ShouldBe(200);
    }

    [Fact]
    public void An_absolute_latency_fault_overrides_the_services_own_numbers()
    {
        (FaultBoard board, Topology topology) = Build();
        board.Add(
            new FaultConfig { Target = "db", Kind = FaultKind.Latency, P50Ms = 900, P99Ms = 4000 },
            "test",
            Now);

        LatencyConfig latency = board.Latency(topology["db"], 0, Now);

        latency.P50Ms.ShouldBe(900);
        latency.P99Ms.ShouldBe(4000);
    }

    [Fact]
    public void A_ramp_eases_the_fault_in_rather_than_applying_it_at_once()
    {
        (FaultBoard board, Topology topology) = Build();
        board.Add(
            new FaultConfig { Target = "db", Kind = FaultKind.Latency, P50Ms = 103, Ramp = "100s" },
            "test",
            Now);

        board.Latency(topology["db"], 0, Now).P50Ms.ShouldBe(3, tolerance: 0.01);
        board.Latency(topology["db"], 0, Now.AddSeconds(50)).P50Ms.ShouldBe(53, tolerance: 0.5);
        board.Latency(topology["db"], 0, Now.AddSeconds(100)).P50Ms.ShouldBe(103, tolerance: 0.01);
        board.Latency(topology["db"], 0, Now.AddSeconds(500)).P50Ms.ShouldBe(103, tolerance: 0.01);
    }

    [Fact]
    public void A_glob_target_reaches_every_matching_service()
    {
        (FaultBoard board, Topology topology) = Build("""
            {
              "services": [
                { "id": "orders-api", "kind": "api", "latency": { "p50Ms": 10, "p99Ms": 20 } },
                { "id": "users-api", "kind": "api", "latency": { "p50Ms": 10, "p99Ms": 20 } },
                { "id": "db", "kind": "database", "latency": { "p50Ms": 10, "p99Ms": 20 } }
              ],
              "flows": [ { "id": "f", "entry": "orders-api" } ]
            }
            """);

        board.Add(new FaultConfig { Target = "*-api", Kind = FaultKind.Latency, Multiplier = 5 }, "test", Now);

        board.Latency(topology["orders-api"], 0, Now).P50Ms.ShouldBe(50);
        board.Latency(topology["users-api"], 0, Now).P50Ms.ShouldBe(50);
        board.Latency(topology["db"], 0, Now).P50Ms.ShouldBe(10, "the database does not match '*-api'");
    }

    [Fact]
    public void Partial_coverage_affects_only_some_replicas()
    {
        (FaultBoard board, Topology topology) = Build("""
            {
              "services": [ { "id": "api", "kind": "api", "instances": 6 } ],
              "flows": [ { "id": "f", "entry": "api" } ]
            }
            """);

        board.Add(
            new FaultConfig { Target = "api", Kind = FaultKind.Errors, ErrorRate = 1.0, Coverage = 0.5 },
            "test",
            Now);

        ServiceRuntime api = topology["api"];
        int broken = Enumerable.Range(0, 6).Count(i => board.ErrorRate(api, i, Now) > 0.5);

        broken.ShouldBe(3);
    }

    [Fact]
    public void An_outage_is_distinguishable_from_a_high_error_rate()
    {
        (FaultBoard board, Topology topology) = Build();
        board.Add(new FaultConfig { Target = "db", Kind = FaultKind.Outage }, "test", Now);

        board.IsDown(topology["db"], 0, Now).ShouldBeTrue();
        board.ErrorRate(topology["db"], 0, Now).ShouldBe(1.0);

        // Errors alone are a service that is up and failing, which fails slowly
        // rather than instantly.
        (FaultBoard other, Topology otherTopology) = Build();
        other.Add(
            new FaultConfig { Target = "db", Kind = FaultKind.Errors, ErrorRate = 1.0 },
            "test",
            Now);

        other.IsDown(otherTopology["db"], 0, Now).ShouldBeFalse();
    }

    [Fact]
    public void A_fault_with_a_duration_expires_on_its_own()
    {
        (FaultBoard board, Topology topology) = Build();
        board.Add(
            new FaultConfig { Target = "db", Kind = FaultKind.Latency, Multiplier = 10, Duration = "30s" },
            "test",
            Now);

        board.Expire(Now.AddSeconds(10));
        board.Latency(topology["db"], 0, Now).P50Ms.ShouldBe(30);

        board.Expire(Now.AddSeconds(31));
        board.Snapshot().ShouldBeEmpty();
        board.Latency(topology["db"], 0, Now).P50Ms.ShouldBe(3);
    }

    [Fact]
    public void Clearing_by_origin_leaves_other_origins_alone()
    {
        (FaultBoard board, _) = Build();
        board.Add(new FaultConfig { Target = "db", Kind = FaultKind.Errors, ErrorRate = 0.5 }, "cascade", Now);
        board.Add(new FaultConfig { Target = "api", Kind = FaultKind.Errors, ErrorRate = 0.5 }, "manual", Now);

        board.Clear("cascade").ShouldBe(1);

        ActiveFault survivor = board.Snapshot().ShouldHaveSingleItem();
        survivor.Origin.ShouldBe("manual");
    }

    [Fact]
    public void A_traffic_fault_scales_only_the_flow_it_names()
    {
        (FaultBoard board, _) = Build();
        board.Add(
            new FaultConfig { Target = "flow:main", Kind = FaultKind.Traffic, Multiplier = 8 },
            "test",
            Now);

        board.TrafficMultiplier("main", Now).ShouldBe(8);
        board.TrafficMultiplier("other", Now).ShouldBe(1);
    }
}

public class ScenarioIntegrationTests
{
    [Fact]
    public void An_outage_makes_calls_fail_fast_rather_than_slowly()
    {
        TrafficConfig config = TestConfigs.Parse(TestConfigs.Simple);
        var topology = new Topology(config);
        var board = new FaultBoard(topology);
        var simulator = new RequestSimulator(topology, board, config.Simulation);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        board.Add(new FaultConfig { Target = "db", Kind = FaultKind.Outage }, "test", now);

        var rng = new Rng(4);
        List<SpanPlan> dbSpans = [];
        for (int i = 0; i < 40; i++)
        {
            dbSpans.AddRange(simulator.Simulate(topology.Flows[0], rng, now)
                .Root.Descend().Where(s => s.Peer?.Id == "db"));
        }

        dbSpans.ShouldNotBeEmpty();
        dbSpans.ShouldAllBe(s => s.Failed);
        dbSpans.ShouldAllBe(s => s.ErrorType == "connection_error");

        // A refused connection returns in about a millisecond. That difference
        // from a timeout is the whole diagnostic value.
        dbSpans.ShouldAllBe(s => s.DurationMs < 3);
    }

    [Fact]
    public void A_failing_dependency_propagates_to_its_caller()
    {
        TrafficConfig config = TestConfigs.Parse(TestConfigs.Simple);
        var topology = new Topology(config);
        var board = new FaultBoard(topology);
        var simulator = new RequestSimulator(topology, board, config.Simulation);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        board.Add(new FaultConfig { Target = "db", Kind = FaultKind.Errors, ErrorRate = 1.0 }, "test", now);

        SimulatedRequest request = simulator.Simulate(topology.Flows[0], new Rng(4), now);
        request.Failed.ShouldBeTrue();
    }

    [Fact]
    public void A_dependency_marked_non_propagating_absorbs_the_failure()
    {
        TrafficConfig config = ConfigLoader.Parse("""
            {
              "services": [
                { "id": "api", "kind": "api", "dependencies": [
                  { "target": "optional", "operation": "GET /x", "propagates": false }
                ] },
                { "id": "optional", "kind": "external" }
              ],
              "flows": [ { "id": "f", "entry": "api" } ]
            }
            """);

        var topology = new Topology(config);
        var board = new FaultBoard(topology);
        var simulator = new RequestSimulator(topology, board, config.Simulation);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        board.Add(new FaultConfig { Target = "optional", Kind = FaultKind.Outage }, "test", now);

        var rng = new Rng(9);
        for (int i = 0; i < 30; i++)
        {
            SimulatedRequest request = simulator.Simulate(topology.Flows[0], rng, now);

            // The call still fails and is still visible in the trace — the
            // caller simply carries on, which is what a fallback looks like.
            request.Root.Descend().ShouldContain(s => s.Peer != null && s.Peer.Id == "optional" && s.Failed);
            request.Failed.ShouldBeFalse();
        }
    }
}
