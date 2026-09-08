using TraceTown.Traffic.Configuration;
using TraceTown.Traffic.Control;
using TraceTown.Traffic.Faults;
using TraceTown.Traffic.Model;
using TraceTown.Traffic.Simulation;

namespace TraceTown.Traffic.Tests;

public class ActivityLogTests
{
    private static SimulatedRequest Make(bool failed = false)
    {
        TrafficConfig config = TestConfigs.Parse(TestConfigs.Simple);
        var topology = new Topology(config);
        var board = new FaultBoard(topology);

        if (failed)
        {
            board.Add(
                new FaultConfig { Target = "db", Kind = FaultKind.Errors, ErrorRate = 1.0 },
                "test",
                DateTimeOffset.UtcNow);
        }

        var simulator = new RequestSimulator(topology, board, config.Simulation);
        return simulator.Simulate(topology.Flows[0], new Rng(failed ? 4 : 1), DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Returns_only_what_the_caller_has_not_seen()
    {
        var log = new ActivityLog();
        for (int i = 0; i < 5; i++)
        {
            log.Record(Make());
        }

        IReadOnlyList<TraceRecord> all = log.Since(0);
        all.Count.ShouldBe(5);

        log.Since(all[2].Seq).Count.ShouldBe(2, "everything after the third");
        log.Since(log.Latest).ShouldBeEmpty();
    }

    [Fact]
    public void Keeps_only_the_most_recent_within_its_capacity()
    {
        var log = new ActivityLog(capacity: 4);

        // Above the per-second budget the rest are dropped on the floor, which
        // is the point: this is a window, not a record.
        for (int i = 0; i < 12; i++)
        {
            log.Record(Make());
        }

        log.Since(0).Count.ShouldBeLessThanOrEqualTo(4);
    }

    [Fact]
    public void A_burst_of_healthy_traffic_cannot_push_out_the_failures()
    {
        var log = new ActivityLog();

        // Fill the healthy budget for this second several times over.
        for (int i = 0; i < 200; i++)
        {
            log.Record(Make());
        }

        log.Record(Make(failed: true));

        // Failures are the only thing anyone scrolls the feed looking for, so
        // they get their own admission budget.
        log.Since(0).ShouldContain(r => r.Failed);
    }

    [Fact]
    public void Marks_a_call_to_something_that_does_not_instrument_itself()
    {
        var log = new ActivityLog();
        log.Record(Make());

        TraceRecord record = log.Since(0).ShouldHaveSingleItem();
        record.Tree.Kind.ShouldBe("server");

        TraceSpan[] leaves = [.. Flatten(record.Tree).Where(s => s.Uninstrumented)];
        leaves.ShouldNotBeEmpty("the database and cache calls have no server span");
        leaves.ShouldAllBe(s => s.Children.Count == 0);
        leaves.ShouldContain(s => s.Peer == "db");
    }

    [Fact]
    public void Records_the_shape_of_the_trace_not_a_reference_to_it()
    {
        var log = new ActivityLog();
        SimulatedRequest request = Make();
        log.Record(request);

        // Mutating the plan afterwards must not change what the console will be
        // shown; the log holds a snapshot, not the live object graph.
        request.Root.DurationMs = 99_999;

        log.Since(0).ShouldHaveSingleItem().Tree.DurationMs.ShouldNotBe(99_999);
    }

    private static IEnumerable<TraceSpan> Flatten(TraceSpan span)
    {
        yield return span;
        foreach (TraceSpan child in span.Children)
        {
            foreach (TraceSpan descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }
}
