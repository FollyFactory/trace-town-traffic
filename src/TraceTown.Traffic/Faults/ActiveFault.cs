using TraceTown.Traffic.Configuration;

namespace TraceTown.Traffic.Faults;

/// <summary>A fault that is currently applied, and since when.</summary>
public sealed class ActiveFault
{
    private static int _counter;

    public ActiveFault(FaultConfig config, string origin, DateTimeOffset startedAt)
    {
        Config = config;
        Origin = origin;
        StartedAt = startedAt;
        Id = $"f{Interlocked.Increment(ref _counter):D4}";
        Ramp = Duration.Parse(config.Ramp);
        Lifetime = Duration.Parse(config.Duration);
    }

    public string Id { get; }

    public FaultConfig Config { get; }

    /// <summary>Scenario id, or <c>manual</c> when injected through the API.</summary>
    public string Origin { get; }

    public DateTimeOffset StartedAt { get; }

    public TimeSpan Ramp { get; }

    public TimeSpan Lifetime { get; }

    /// <summary>
    /// How far in the fault is, 0..1. Ramping matters more than it sounds:
    /// a dependency that degrades over ninety seconds and one that falls over
    /// instantly are different incidents, and only one of them is realistic.
    /// </summary>
    public double Intensity(DateTimeOffset now)
    {
        if (Ramp <= TimeSpan.Zero)
        {
            return 1.0;
        }

        double elapsed = (now - StartedAt).TotalMilliseconds;
        return Math.Clamp(elapsed / Ramp.TotalMilliseconds, 0.0, 1.0);
    }

    public bool HasExpired(DateTimeOffset now)
        => Lifetime > TimeSpan.Zero && now - StartedAt >= Lifetime;

    /// <summary>
    /// Whether this fault reaches a given replica. Partial coverage is how a
    /// bad deploy to two pods out of six looks — errors, but not everywhere.
    /// </summary>
    public bool Covers(int instanceIndex, int instanceCount)
    {
        if (Config.Coverage >= 1.0)
        {
            return true;
        }

        if (Config.Coverage <= 0.0)
        {
            return false;
        }

        int affected = Math.Max(1, (int)Math.Round(Config.Coverage * instanceCount));
        return instanceIndex < affected;
    }
}
