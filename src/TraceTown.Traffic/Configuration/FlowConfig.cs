using System.Text.Json.Serialization;
using TraceTown.Traffic.Configuration.Json;

namespace TraceTown.Traffic.Configuration;

/// <summary>
/// A stream of requests entering the system. Flows are the only thing that
/// generates load — services are passive until something calls them.
/// </summary>
public sealed record FlowConfig
{
    public required string Id { get; init; }

    /// <summary>The service requests arrive at. Usually a gateway.</summary>
    public required string Entry { get; init; }

    /// <summary>
    /// The operation being requested — <c>POST /api/checkout</c>. Becomes the
    /// root span name and the <c>http.route</c> attribute.
    /// </summary>
    public string? Route { get; init; }

    /// <summary>Requests per second at a load profile value of 1.0.</summary>
    public double Rps { get; init; } = 1;

    /// <summary>How the rate varies over time.</summary>
    public LoadProfileConfig Profile { get; init; } = new();

    /// <summary>
    /// Failures originating at the edge — bad requests, auth rejections. These
    /// are 4xx-shaped and, unlike a downstream failure, are nobody's incident.
    /// </summary>
    public double ClientErrorRate { get; init; }

    /// <summary>
    /// Services this flow always reaches, whatever probability their dependency
    /// edge carries. This is how one flow differs from another over a shared
    /// graph: a checkout flow lists <c>checkout-api</c> and therefore always
    /// goes there, while a browse flow over the same gateway does not.
    /// Globs allowed.
    /// </summary>
    public IReadOnlyList<string> Always { get; init; } = [];

    /// <summary>
    /// Services this flow never calls, at any depth. Globs allowed. Takes
    /// precedence over <see cref="Always"/>.
    /// </summary>
    public IReadOnlyList<string> Except { get; init; } = [];

    /// <summary>
    /// How far down the call graph this flow reaches. <c>0</c> is the entry
    /// point on its own, which is what a health check looks like. Null uses
    /// <c>simulation.maxDepth</c>.
    /// </summary>
    public int? Depth { get; init; }
}

public sealed record LoadProfileConfig
{
    public LoadProfileKind Kind { get; init; } = LoadProfileKind.Constant;

    /// <summary>
    /// How far the rate swings either side of the base, 0..1. At 0.5 a
    /// diurnal profile runs between half and one-and-a-half times
    /// <see cref="FlowConfig.Rps"/>.
    /// </summary>
    public double Amplitude { get; init; } = 0.3;

    /// <summary>Cycle length for <c>sine</c> and <c>burst</c>. Ignored by <c>diurnal</c>.</summary>
    public string Period { get; init; } = "10m";

    /// <summary>
    /// Fraction of the period a <c>burst</c> spends elevated, 0..1. The rest of
    /// the cycle sits at the base rate.
    /// </summary>
    public double Duty { get; init; } = 0.1;

    /// <summary>Peak multiplier for <c>burst</c> and end multiplier for <c>ramp</c>.</summary>
    public double Peak { get; init; } = 3.0;

    /// <summary>Gaussian noise added to every profile, as a fraction of the rate.</summary>
    public double Jitter { get; init; } = 0.05;
}

[JsonConverter(typeof(FlexibleEnumConverter<LoadProfileKind>))]
public enum LoadProfileKind
{
    /// <summary>Flat, plus jitter.</summary>
    Constant,

    /// <summary>A 24-hour cycle peaking mid-afternoon local time.</summary>
    Diurnal,

    /// <summary>A sine wave over <see cref="LoadProfileConfig.Period"/>.</summary>
    Sine,

    /// <summary>Quiet, then a spike to <c>peak</c> for <c>duty</c> of the period.</summary>
    Burst,

    /// <summary>Climbs from the base rate to <c>peak</c> over the period, then repeats.</summary>
    Ramp,

    /// <summary>Drifts. Realistic-looking noise with no pattern to lock onto.</summary>
    RandomWalk,
}
