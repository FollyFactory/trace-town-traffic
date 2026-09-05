using TraceTown.Traffic.Configuration;

namespace TraceTown.Traffic.Simulation;

/// <summary>
/// The multiplier applied to a flow's base rate at a point in time. Kept
/// deterministic in the time argument — apart from jitter and the random walk —
/// so two flows on the same profile peak together the way real traffic does.
/// </summary>
public sealed class LoadProfile(LoadProfileConfig config, Rng rng)
{
    private readonly TimeSpan _period = Duration.Parse(config.Period, TimeSpan.FromMinutes(10));
    private double _walk = 1.0;

    public double Multiplier(DateTimeOffset now)
    {
        double baseValue = config.Kind switch
        {
            LoadProfileKind.Constant => 1.0,
            LoadProfileKind.Diurnal => Diurnal(now),
            LoadProfileKind.Sine => Sine(now),
            LoadProfileKind.Burst => Burst(now),
            LoadProfileKind.Ramp => Ramp(now),
            LoadProfileKind.RandomWalk => RandomWalk(),
            _ => 1.0,
        };

        if (config.Jitter > 0)
        {
            baseValue *= 1.0 + (rng.NextGaussian() * config.Jitter);
        }

        return Math.Max(0, baseValue);
    }

    /// <summary>
    /// A day in the life: trough around 04:00, peak around 15:00. Local time,
    /// because that is when the humans generating the traffic are awake.
    /// </summary>
    private double Diurnal(DateTimeOffset now)
    {
        double hour = now.ToLocalTime().TimeOfDay.TotalHours;
        double phase = (hour - 9.0) / 24.0 * 2 * Math.PI;
        return 1.0 + (config.Amplitude * Math.Sin(phase));
    }

    private double Sine(DateTimeOffset now)
    {
        double phase = Position(now) * 2 * Math.PI;
        return 1.0 + (config.Amplitude * Math.Sin(phase));
    }

    /// <summary>Flat, then a spike, then flat again. A cache stampede, a cron fan-out.</summary>
    private double Burst(DateTimeOffset now)
    {
        double position = Position(now);
        if (position >= config.Duty)
        {
            return 1.0;
        }

        // Ease in and out of the spike so it is a surge rather than a step
        // change, which no real traffic pattern is.
        double within = config.Duty <= 0 ? 0 : position / config.Duty;
        double shape = Math.Sin(within * Math.PI);
        return 1.0 + ((config.Peak - 1.0) * shape);
    }

    private double Ramp(DateTimeOffset now) => 1.0 + ((config.Peak - 1.0) * Position(now));

    private double RandomWalk()
    {
        _walk += rng.NextGaussian() * config.Amplitude * 0.05;
        _walk = Math.Clamp(_walk, 1.0 - config.Amplitude, 1.0 + config.Amplitude);
        return _walk;
    }

    /// <summary>Where we are through the current cycle, 0..1.</summary>
    private double Position(DateTimeOffset now)
    {
        if (_period <= TimeSpan.Zero)
        {
            return 0;
        }

        double ticks = now.UtcDateTime.Ticks % _period.Ticks;
        return ticks / _period.Ticks;
    }
}
