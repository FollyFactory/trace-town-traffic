using Microsoft.Extensions.Logging;
using TraceTown.Traffic.Configuration;
using TraceTown.Traffic.Diagnostics;
using TraceTown.Traffic.Model;

namespace TraceTown.Traffic.Faults;

/// <summary>
/// Plays a scenario's steps against the fault board as their times come round.
/// Switching scenario clears whatever the previous one left behind, so you never
/// end up with two incidents overlapping by accident.
/// </summary>
public sealed class ScenarioRunner(Topology topology, FaultBoard faults, ILogger<ScenarioRunner> logger)
{
    private readonly Lock _gate = new();
    private ScenarioConfig? _scenario;
    private (TimeSpan At, ScenarioStepConfig Step)[] _steps = [];
    private DateTimeOffset _startedAt;
    private int _next;

    public string? CurrentId
    {
        get
        {
            lock (_gate)
            {
                return _scenario?.Id;
            }
        }
    }

    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                return _scenario is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - _startedAt;
            }
        }
    }

    /// <summary>The step that will fire next, for the status endpoint.</summary>
    public string? NextStepAt
    {
        get
        {
            lock (_gate)
            {
                return _next < _steps.Length ? _steps[_next].Step.At : null;
            }
        }
    }

    public IReadOnlyList<string> Available => [.. topology.Config.Scenarios.Select(s => s.Id)];

    /// <summary>Switches scenario. Passing null stops without starting another.</summary>
    public bool Start(string? id, DateTimeOffset now)
    {
        ScenarioConfig? scenario = id is null
            ? null
            : topology.Config.Scenarios.FirstOrDefault(
                s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

        if (id is not null && scenario is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (_scenario is { } previous)
            {
                faults.Clear(previous.Id);
            }

            _scenario = scenario;
            _startedAt = now;
            _next = 0;
            _steps = scenario is null
                ? []
                : [.. scenario.Steps
                    .Select(s => (At: Duration.Parse(s.At), Step: s))
                    .OrderBy(s => s.At)];
        }

        if (scenario is not null)
        {
            logger.ScenarioStarted(scenario.Id, _steps.Length, scenario.Loop);
        }
        else
        {
            logger.ScenarioStopped();
        }

        return true;
    }

    public void Stop() => Start(null, DateTimeOffset.UtcNow);

    /// <summary>Fires any step whose time has arrived. Called once per engine tick.</summary>
    public void Tick(DateTimeOffset now)
    {
        List<ScenarioStepConfig> due = [];
        string origin;

        lock (_gate)
        {
            if (_scenario is null || _steps.Length == 0)
            {
                return;
            }

            origin = _scenario.Id;
            TimeSpan elapsed = now - _startedAt;

            while (_next < _steps.Length && _steps[_next].At <= elapsed)
            {
                due.Add(_steps[_next].Step);
                _next++;
            }

            if (_next >= _steps.Length && _scenario.Loop)
            {
                // Rewind rather than drift: the next cycle starts one full
                // scenario length after this one, not one tick later.
                TimeSpan length = _steps[^1].At;
                if (length > TimeSpan.Zero && elapsed >= length)
                {
                    _startedAt = _startedAt + length;
                    _next = 0;
                }
            }
        }

        foreach (ScenarioStepConfig step in due)
        {
            Apply(step, origin, now);
        }
    }

    private void Apply(ScenarioStepConfig step, string origin, DateTimeOffset now)
    {
        if (step.Note is { } note)
        {
            logger.ScenarioNote(origin, step.At, note);
        }

        if (step.Clear)
        {
            int cleared = faults.Clear(origin);
            if (cleared > 0)
            {
                logger.ScenarioCleared(origin, step.At, cleared);
            }
        }

        foreach (FaultConfig fault in step.Faults)
        {
            ActiveFault active = faults.Add(fault, origin, now);
            logger.ScenarioFault(
                origin,
                step.At,
                fault.Kind,
                fault.Target,
                active.Id,
                fault.Because ?? string.Empty);
        }
    }
}
