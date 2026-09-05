namespace TraceTown.Traffic.Model;

/// <summary>
/// One completed request through the system: the span tree, whether it is being
/// traced, and when it finished. Timings are relative to the start, and the
/// emitter backdates the lot so the request ends now.
/// </summary>
public sealed class SimulatedRequest
{
    public required string FlowId { get; init; }

    public required SpanPlan Root { get; init; }

    /// <summary>
    /// Whether spans are exported. Metrics and error logs are recorded either
    /// way — sampling a metric would make the numbers wrong, not cheaper.
    /// </summary>
    public bool Sampled { get; set; }

    /// <summary>Whether the request failed as far as the caller is concerned.</summary>
    public bool Failed => Root.Failed;

    /// <summary>Total wall time, including any asynchronous continuation.</summary>
    public double DurationMs => Root.Descend().Max(s => s.EndOffsetMs);

    /// <summary>When the request completed. The emitter works backwards from here.</summary>
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}
