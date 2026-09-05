using TraceTown.Traffic.Configuration;

namespace TraceTown.Traffic.Simulation;

/// <summary>
/// Turns a p50/p99 pair into individual durations.
/// </summary>
/// <remarks>
/// Lognormal is the default because service latency really is lognormal: a hard
/// floor at zero, a median well below the mean, and a long right tail. Sampling
/// from a normal distribution gives a tail so thin that a p99 fault barely
/// registers, which makes for a simulator you cannot test anything with.
/// </remarks>
public static class LatencySampler
{
    /// <summary>The standard normal quantile at the 99th percentile.</summary>
    private const double Z99 = 2.3263478740408408;

    public static double Sample(LatencyConfig latency, Rng rng)
    {
        double p50 = Math.Max(0.001, latency.P50Ms);
        double p99 = Math.Max(p50, latency.P99Ms);

        double value = latency.Distribution switch
        {
            LatencyDistribution.Constant => p50,

            LatencyDistribution.Uniform => latency.FloorMs + (rng.NextDouble() * (p99 - latency.FloorMs)),

            LatencyDistribution.Normal => p50 + (rng.NextGaussian() * ((p99 - p50) / Z99)),

            // median = exp(mu), so mu = ln(p50); p99 = exp(mu + sigma * z99).
            _ => Math.Exp(Math.Log(p50) + (Math.Log(p99 / p50) / Z99 * rng.NextGaussian())),
        };

        return Math.Max(latency.FloorMs, Math.Max(0.001, value));
    }
}
