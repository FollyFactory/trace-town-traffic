using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TraceTown.Traffic.Configuration;
using TraceTown.Traffic.Diagnostics;
using TraceTown.Traffic.Emit;
using TraceTown.Traffic.Faults;
using TraceTown.Traffic.Model;

namespace TraceTown.Traffic.Simulation;

/// <summary>
/// Runs the simulation: one loop per flow, one per cron job, and a housekeeping
/// loop that advances scenarios and infrastructure counters.
/// </summary>
public sealed class Engine : IAsyncDisposable
{
    private readonly ILogger<Engine> _logger;
    private readonly TelemetryEmitter _emitter;
    private readonly RequestSimulator _simulator;
    private readonly InfraTicker _infra;
    private readonly ConcurrentDictionary<string, double> _rateOverrides = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FlowStats> _stats = new(StringComparer.Ordinal);
    private readonly List<Task> _loops = [];
    private readonly int _seed;

    private CancellationTokenSource? _cancellation;

    public Engine(TrafficConfig config, ILoggerFactory loggerFactory)
    {
        Config = config;
        _logger = loggerFactory.CreateLogger<Engine>();
        _seed = config.Simulation.Seed ?? Environment.TickCount;

        Topology = new Topology(config);
        Faults = new FaultBoard(Topology);
        Scenarios = new ScenarioRunner(Topology, Faults, loggerFactory.CreateLogger<ScenarioRunner>());

        _emitter = new TelemetryEmitter(Topology, config);
        _simulator = new RequestSimulator(Topology, Faults, config.Simulation);
        _infra = new InfraTicker(Topology, Faults);

        foreach (FlowConfig flow in config.Flows)
        {
            _stats[flow.Id] = new FlowStats();
        }
    }

    public TrafficConfig Config { get; }

    public Topology Topology { get; }

    public FaultBoard Faults { get; }

    public ScenarioRunner Scenarios { get; }

    public DateTimeOffset StartedAt { get; private set; }

    public bool IsRunning => _cancellation is { IsCancellationRequested: false };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = _cancellation.Token;
        StartedAt = DateTimeOffset.UtcNow;

        if (Config.Simulation.Scenario is { } scenario)
        {
            Scenarios.Start(scenario, StartedAt);
        }

        int index = 0;
        foreach (FlowConfig flow in Config.Flows)
        {
            int seed = _seed + (++index * 7919);
            _loops.Add(Task.Run(() => RunFlowAsync(flow, new Rng(seed), token), token));
        }

        foreach (ServiceRuntime cron in Topology.Services.Where(s => s.Kind == ServiceKind.Cron))
        {
            int seed = _seed + (++index * 7919);
            _loops.Add(Task.Run(() => RunCronAsync(cron, new Rng(seed), token), token));
        }

        _loops.Add(Task.Run(() => RunHousekeepingAsync(new Rng(_seed - 1), token), token));

        _logger.SimulationStarted(
            Topology.Services.Count,
            Config.Flows.Count,
            Config.Exporter.Endpoint);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Generates one flow's requests. Arrivals are Poisson rather than evenly
    /// spaced, because evenly spaced arrivals produce percentiles that no real
    /// system has ever had.
    /// </summary>
    private async Task RunFlowAsync(FlowConfig flow, Rng rng, CancellationToken token)
    {
        var profile = new LoadProfile(flow.Profile, rng);
        TimeSpan tick = TimeSpan.FromMilliseconds(Config.Simulation.TickMs);
        double tickSeconds = tick.TotalSeconds;
        FlowStats stats = _stats[flow.Id];
        var timer = new PeriodicTimer(tick);

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            double rate = _rateOverrides.TryGetValue(flow.Id, out double over) ? over : flow.Rps;
            double expected = rate
                * profile.Multiplier(now)
                * Faults.TrafficMultiplier(flow.Id, now)
                * Config.Simulation.RateMultiplier
                * tickSeconds;

            int count = rng.NextPoisson(expected);
            long started = Environment.TickCount64;

            for (int i = 0; i < count && !token.IsCancellationRequested; i++)
            {
                SimulatedRequest request = _simulator.Simulate(flow, rng, now);
                request.Sampled = ShouldSample(request, rng);
                request.CompletedAt = now;

                _emitter.Emit(request);
                stats.Record(request.Failed);
            }

            long elapsed = Environment.TickCount64 - started;
            if (elapsed > tick.TotalMilliseconds * 2)
            {
                // Better to say so than to quietly emit less traffic than the
                // config asks for and let someone draw conclusions from it.
                _logger.FlowFallingBehind(flow.Id, elapsed, count, (int)tick.TotalMilliseconds);
            }
        }
    }

    private async Task RunCronAsync(ServiceRuntime service, Rng rng, CancellationToken token)
    {
        CronConfig config = service.Config.Cron ?? new CronConfig();
        TimeSpan every = Duration.Parse(config.Every, TimeSpan.FromMinutes(5));
        TimeSpan jitter = Duration.Parse(config.Jitter);
        string flowId = $"cron:{service.Id}";
        _stats.TryAdd(flowId, new FlowStats());

        while (!token.IsCancellationRequested)
        {
            TimeSpan wait = every;
            if (jitter > TimeSpan.Zero)
            {
                wait += TimeSpan.FromMilliseconds(rng.NextDouble() * jitter.TotalMilliseconds);
            }

            try
            {
                await Task.Delay(wait, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            SimulatedRequest request = _simulator.SimulateCron(service, rng, now);
            request.Sampled = ShouldSample(request, rng);
            request.CompletedAt = now;

            _emitter.Emit(request);
            _stats[flowId].Record(request.Failed);
        }
    }

    private async Task RunHousekeepingAsync(Rng rng, CancellationToken token)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Faults.Expire(now);
            Scenarios.Tick(now);
            _infra.Tick(now, rng);
        }
    }

    /// <summary>
    /// Head sampling, with failures sampled harder. A rare error is the whole
    /// reason anyone opens a trace viewer, and at a 1% sample rate you would
    /// almost never catch one.
    /// </summary>
    private bool ShouldSample(SimulatedRequest request, Rng rng)
    {
        SimulationConfig simulation = Config.Simulation;
        return request.Failed
            ? rng.Chance(simulation.ErrorTraceSampleRatio)
            : rng.Chance(simulation.TraceSampleRatio);
    }

    /// <summary>Overrides a flow's rate until the next override or a restart.</summary>
    public bool SetFlowRate(string flowId, double rps)
    {
        if (!Config.Flows.Any(f => f.Id == flowId))
        {
            return false;
        }

        _rateOverrides[flowId] = Math.Max(0, rps);
        _logger.FlowRateSet(flowId, rps);
        return true;
    }

    public void ClearFlowRate(string flowId) => _rateOverrides.TryRemove(flowId, out _);

    public double EffectiveRate(FlowConfig flow)
        => _rateOverrides.TryGetValue(flow.Id, out double over) ? over : flow.Rps;

    public EngineStats Stats()
    {
        long requests = 0;
        long errors = 0;

        Dictionary<string, FlowSnapshot> flows = [];
        foreach ((string id, FlowStats stats) in _stats)
        {
            (long total, long failed) = stats.Read();
            requests += total;
            errors += failed;
            flows[id] = new FlowSnapshot(total, failed);
        }

        return new EngineStats(
            requests,
            errors,
            _emitter.SpansEmitted,
            DateTimeOffset.UtcNow - StartedAt,
            flows);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is { } cancellation)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);

            try
            {
                await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Shutting down; a loop that will not stop promptly is not
                // worth blocking the flush for.
            }

            cancellation.Dispose();
        }

        _logger.FlushingTelemetry();
        _emitter.Flush(TimeSpan.FromSeconds(10));
        _emitter.Dispose();
    }

    private sealed class FlowStats
    {
        private long _requests;
        private long _errors;

        internal void Record(bool failed)
        {
            Interlocked.Increment(ref _requests);
            if (failed)
            {
                Interlocked.Increment(ref _errors);
            }
        }

        internal (long Requests, long Errors) Read()
            => (Interlocked.Read(ref _requests), Interlocked.Read(ref _errors));
    }
}

public sealed record EngineStats(
    long Requests,
    long Errors,
    long Spans,
    TimeSpan Uptime,
    IReadOnlyDictionary<string, FlowSnapshot> Flows);

public sealed record FlowSnapshot(long Requests, long Errors);
