using TraceTown.Traffic.Configuration;

namespace TraceTown.Traffic.Simulation;

/// <summary>Per-request scratch space, so the recursion does not need six arguments.</summary>
internal sealed class RequestContext
{
    public required string FlowId { get; init; }

    public required Rng Rng { get; init; }

    public required DateTimeOffset Now { get; init; }

    public IReadOnlyList<string> Always { get; init; } = [];

    public IReadOnlyList<string> Except { get; init; } = [];

    public int MaxDepth { get; init; } = 12;

    /// <summary>Whether this flow may call the given service, at any depth.</summary>
    public bool Allows(string targetId)
        => Except.Count == 0 || !Except.Any(e => Glob.Matches(e, targetId));

    /// <summary>
    /// Whether this flow always takes the branch to the given service, ignoring
    /// the probability on the dependency edge. Probabilities describe the
    /// average across all traffic; a named flow is a specific journey through
    /// the same graph, and needs to be able to say so.
    /// </summary>
    public bool Forces(string targetId)
        => Always.Count > 0 && Always.Any(a => Glob.Matches(a, targetId));
}
