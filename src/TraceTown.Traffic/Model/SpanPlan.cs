using System.Diagnostics;

namespace TraceTown.Traffic.Model;

/// <summary>
/// One span the simulation decided happened, before anything has been exported.
/// The tree is the shape of the trace: a client span and the server span it
/// caused are parent and child, and a database call is a client span with no
/// child because there is nothing on the far side that instruments itself.
/// </summary>
public sealed class SpanPlan
{
    /// <summary>The service whose SDK records this span. Decides the resource.</summary>
    public required ServiceRuntime Emitter { get; init; }

    public required ActivityKind Kind { get; init; }

    public required string Name { get; init; }

    /// <summary>Which replica of <see cref="Emitter"/> handled it.</summary>
    public ServiceInstance? Instance { get; set; }

    /// <summary>Milliseconds from the start of the request.</summary>
    public double StartOffsetMs { get; set; }

    public double DurationMs { get; set; }

    public double EndOffsetMs => StartOffsetMs + DurationMs;

    public bool Failed { get; set; }

    /// <summary>
    /// Becomes <c>error.type</c>. A short, low-cardinality token — an exception
    /// name, a status code, <c>timeout</c> — never a message.
    /// </summary>
    public string? ErrorType { get; set; }

    public string? StatusDescription { get; set; }

    public List<KeyValuePair<string, object?>> Attributes { get; } = [];

    public List<SpanPlan> Children { get; } = [];

    public List<LogPlan> Logs { get; } = [];

    /// <summary>
    /// Which metric this span contributes to. Recorded for every request,
    /// sampled or not, which is the whole reason it is a separate field.
    /// </summary>
    public SpanMetric Metric { get; init; } = SpanMetric.None;

    /// <summary>The far end of the call, for client-side spans.</summary>
    public ServiceRuntime? Peer { get; init; }

    public SpanPlan Add(string key, object? value)
    {
        if (value is not null)
        {
            Attributes.Add(new KeyValuePair<string, object?>(key, value));
        }

        return this;
    }

    public IEnumerable<SpanPlan> Descend()
    {
        yield return this;
        foreach (SpanPlan child in Children)
        {
            foreach (SpanPlan descendant in child.Descend())
            {
                yield return descendant;
            }
        }
    }
}

/// <summary>Which duration histogram a span feeds.</summary>
public enum SpanMetric
{
    None,
    HttpServer,
    HttpClient,
    DbClient,
    MessagingPublish,
    MessagingProcess,
}

/// <summary>A log record produced during a span, correlated to it.</summary>
public sealed record LogPlan(LogSeverity Severity, string Message)
{
    public List<KeyValuePair<string, object?>> Attributes { get; } = [];

    /// <summary>
    /// Infrastructure logs — a slow-query line from Postgres, an eviction
    /// warning from Redis — have no span to correlate to, because the thing
    /// that wrote them was not part of the trace.
    /// </summary>
    public bool Uncorrelated { get; init; }

    public LogPlan With(string key, object? value)
    {
        if (value is not null)
        {
            Attributes.Add(new KeyValuePair<string, object?>(key, value));
        }

        return this;
    }
}

public enum LogSeverity
{
    Debug,
    Info,
    Warn,
    Error,
}
