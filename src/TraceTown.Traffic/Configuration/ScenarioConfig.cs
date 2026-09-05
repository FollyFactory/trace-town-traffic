using System.Text.Json.Serialization;
using TraceTown.Traffic.Configuration.Json;

namespace TraceTown.Traffic.Configuration;

/// <summary>
/// A timed sequence of faults. Switching scenario is the main thing you do at
/// runtime, and it is what makes the tool useful for demonstrating an incident
/// rather than just producing load.
/// </summary>
public sealed record ScenarioConfig
{
    public required string Id { get; init; }

    public string? Description { get; init; }

    /// <summary>Restart from the top once the last step's time has passed.</summary>
    public bool Loop { get; init; }

    public IReadOnlyList<ScenarioStepConfig> Steps { get; init; } = [];
}

public sealed record ScenarioStepConfig
{
    /// <summary>
    /// Offset from the start of the scenario — <c>0s</c>, <c>90s</c>, <c>2m30s</c>.
    /// Steps run in time order regardless of the order written here.
    /// </summary>
    public string At { get; init; } = "0s";

    /// <summary>Human note. Logged when the step fires, so a run narrates itself.</summary>
    public string? Note { get; init; }

    /// <summary>Remove every fault this scenario has applied so far.</summary>
    public bool Clear { get; init; }

    public IReadOnlyList<FaultConfig> Faults { get; init; } = [];
}

/// <summary>
/// One thing going wrong. Faults apply to services or flows and stack, so a
/// service can be both slow and erroring without the two definitions knowing
/// about each other.
/// </summary>
public sealed record FaultConfig
{
    /// <summary>
    /// Service id, <c>flow:id</c> for a flow, or a <c>*</c> glob against either
    /// — <c>*-api</c> hits every service whose id ends in <c>-api</c>.
    /// </summary>
    public required string Target { get; init; }

    public required FaultKind Kind { get; init; }

    /// <summary>Multiplies the current value. Use with <c>latency</c> or <c>traffic</c>.</summary>
    public double? Multiplier { get; init; }

    /// <summary>Sets latency outright, overriding the service's own numbers.</summary>
    public double? P50Ms { get; init; }

    public double? P99Ms { get; init; }

    /// <summary>Absolute error rate, 0..1. Use with <c>errors</c>.</summary>
    public double? ErrorRate { get; init; }

    /// <summary>Time messages wait before delivery. Use with <c>queueLag</c>.</summary>
    public double? LagMs { get; init; }

    /// <summary>Fraction of instances affected, 0..1. Models a partial failure.</summary>
    public double Coverage { get; init; } = 1.0;

    /// <summary>Ease the fault in over this long instead of applying it instantly.</summary>
    public string? Ramp { get; init; }

    /// <summary>Self-clear after this long. Omit to leave it until a step clears it.</summary>
    public string? Duration { get; init; }

    /// <summary>Free-text reason, attached to the spans and logs the fault produces.</summary>
    public string? Because { get; init; }
}

[JsonConverter(typeof(FlexibleEnumConverter<FaultKind>))]
public enum FaultKind
{
    /// <summary>Make it slower. Set <c>multiplier</c>, or <c>p50Ms</c>/<c>p99Ms</c>.</summary>
    Latency,

    /// <summary>Make it fail some of the time. Set <c>errorRate</c>.</summary>
    Errors,

    /// <summary>
    /// Take it down completely. Calls fail fast rather than slowly, which is
    /// what a refused connection actually looks like and reads very differently
    /// from a timeout.
    /// </summary>
    Outage,

    /// <summary>Change how much load a flow generates. Set <c>multiplier</c>.</summary>
    Traffic,

    /// <summary>Back a queue up. Set <c>lagMs</c>. Consumer lag metrics climb.</summary>
    QueueLag,

    /// <summary>
    /// Exhaust the thing's own resources — connection pool, memory, CPU. Shows
    /// up in infrastructure metrics before it shows up in latency, which is the
    /// whole reason to collect them.
    /// </summary>
    Saturation,
}
