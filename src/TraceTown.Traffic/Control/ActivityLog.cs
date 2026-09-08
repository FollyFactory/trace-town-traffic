using System.Diagnostics;
using TraceTown.Traffic.Model;

namespace TraceTown.Traffic.Control;

/// <summary>
/// A short, bounded tail of what the generator has just produced, so the console
/// can show that something is actually happening.
/// </summary>
/// <remarks>
/// This is a window, not a record. It holds the last few hundred requests and
/// throws the rest away — at a few thousand requests a second, keeping them all
/// would cost more memory than the simulation itself and answer no question the
/// backend cannot answer better.
/// <para>
/// Failures get their own admission budget. They are rare by design and are the
/// only thing anyone actually scrolls the feed looking for, so a burst of
/// healthy traffic must not push them out.
/// </para>
/// </remarks>
public sealed class ActivityLog(int capacity = 250)
{
    private readonly Lock _gate = new();
    private readonly Queue<TraceRecord> _records = new();
    private long _sequence;

    private long _second;
    private int _healthyThisSecond;
    private int _failedThisSecond;

    /// <summary>Per second, so a busy town does not fill the window in one tick.</summary>
    private const int HealthyBudget = 15;
    private const int FailedBudget = 15;

    public void Record(SimulatedRequest request)
    {
        long second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (_gate)
        {
            if (second != _second)
            {
                _second = second;
                _healthyThisSecond = 0;
                _failedThisSecond = 0;
            }

            if (request.Failed)
            {
                if (_failedThisSecond >= FailedBudget)
                {
                    return;
                }

                _failedThisSecond++;
            }
            else
            {
                if (_healthyThisSecond >= HealthyBudget)
                {
                    return;
                }

                _healthyThisSecond++;
            }

            _records.Enqueue(new TraceRecord(
                ++_sequence,
                request.CompletedAt,
                request.FlowId,
                request.Root.Name,
                Math.Round(request.DurationMs, 2),
                request.Root.Descend().Count(),
                request.Failed,
                request.Root.ErrorType,
                request.Sampled,
                [.. request.Root.Descend().Select(s => s.Emitter.Id).Distinct()],
                Convert(request.Root)));

            while (_records.Count > capacity)
            {
                _records.Dequeue();
            }
        }
    }

    /// <summary>Everything newer than <paramref name="since"/>, oldest first.</summary>
    public IReadOnlyList<TraceRecord> Since(long since, int limit = 100)
    {
        lock (_gate)
        {
            return [.. _records.Where(r => r.Seq > since).Take(limit)];
        }
    }

    public long Latest
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    /// <summary>
    /// Flattens a span plan into something serialisable. Done at record time
    /// rather than on request, because the plan is a live object graph and the
    /// console asking about it later should not reach into the simulation.
    /// </summary>
    private static TraceSpan Convert(SpanPlan plan) => new(
        plan.Name,
        plan.Emitter.Id,
        Kind(plan.Kind),
        Math.Round(plan.StartOffsetMs, 2),
        Math.Round(plan.DurationMs, 2),
        plan.Failed,
        plan.ErrorType,
        plan.Peer?.Id,

        // A client span with no child is a call to something that does not
        // instrument itself. The console draws that differently, so it needs to
        // know here rather than infer it from an empty array.
        plan.Peer is not null && plan.Children.Count == 0,
        [.. plan.Children.Select(Convert)]);

    private static string Kind(ActivityKind kind) => kind switch
    {
        ActivityKind.Server => "server",
        ActivityKind.Client => "client",
        ActivityKind.Producer => "producer",
        ActivityKind.Consumer => "consumer",
        _ => "internal",
    };
}

public sealed record TraceRecord(
    long Seq,
    DateTimeOffset At,
    string Flow,
    string Root,
    double DurationMs,
    int Spans,
    bool Failed,
    string? ErrorType,
    bool Sampled,
    IReadOnlyList<string> Services,
    TraceSpan Tree);

public sealed record TraceSpan(
    string Name,
    string Service,
    string Kind,
    double StartMs,
    double DurationMs,
    bool Failed,
    string? ErrorType,
    string? Peer,
    bool Uninstrumented,
    IReadOnlyList<TraceSpan> Children);
