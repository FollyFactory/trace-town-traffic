namespace TraceTown.Traffic.Simulation;

/// <summary>
/// A seeded random source with the distributions the simulator needs. One per
/// flow, so a given flow is reproducible from its seed even though flows run
/// concurrently and the interleaving between them is not.
/// </summary>
public sealed class Rng(int seed)
{
    private readonly Random _random = new(seed);
    private double _spare;
    private bool _hasSpare;

    public double NextDouble() => _random.NextDouble();

    public bool Chance(double probability) => probability > 0 && _random.NextDouble() < probability;

    /// <summary>
    /// Inclusive at both ends, which is what callers reaching for a bounded
    /// integer nearly always mean. The 64-bit widening matters: an inclusive
    /// upper bound of <see cref="int.MaxValue"/> would otherwise overflow when
    /// converted to an exclusive one.
    /// </summary>
    public int Next(int minInclusive, int maxInclusive)
        => maxInclusive <= minInclusive
            ? minInclusive
            : (int)_random.NextInt64(minInclusive, (long)maxInclusive + 1);

    /// <summary>A full-width random word, for identifiers.</summary>
    public uint NextUInt32() => (uint)_random.NextInt64(0, uint.MaxValue + 1L);

    /// <summary>Standard normal, by Box–Muller. The second value is kept for next time.</summary>
    public double NextGaussian()
    {
        if (_hasSpare)
        {
            _hasSpare = false;
            return _spare;
        }

        double u1, u2, s;
        do
        {
            u1 = (2.0 * _random.NextDouble()) - 1.0;
            u2 = (2.0 * _random.NextDouble()) - 1.0;
            s = (u1 * u1) + (u2 * u2);
        }
        while (s is >= 1.0 or 0.0);

        double factor = Math.Sqrt(-2.0 * Math.Log(s) / s);
        _spare = u2 * factor;
        _hasSpare = true;
        return u1 * factor;
    }

    /// <summary>Poisson draw, for "how many requests arrived in this tick".</summary>
    public int NextPoisson(double lambda)
    {
        if (lambda <= 0)
        {
            return 0;
        }

        // Knuth's method is fine below ~30; above that the normal approximation
        // is both accurate and much faster, which matters at high request rates.
        if (lambda > 30)
        {
            return Math.Max(0, (int)Math.Round(lambda + (Math.Sqrt(lambda) * NextGaussian())));
        }

        double limit = Math.Exp(-lambda);
        double product = 1.0;
        int count = 0;

        do
        {
            count++;
            product *= _random.NextDouble();
        }
        while (product > limit);

        return count - 1;
    }

    public T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];
}
