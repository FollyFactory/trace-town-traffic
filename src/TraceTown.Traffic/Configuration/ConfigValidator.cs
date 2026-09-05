namespace TraceTown.Traffic.Configuration;

/// <summary>
/// Checks a config makes sense before anything is exported. A misspelt
/// dependency target would otherwise show up as a service that mysteriously
/// never gets called, which is a miserable thing to debug through a trace
/// viewer.
/// </summary>
public static class ConfigValidator
{
    public static ValidationResult Validate(TrafficConfig config)
    {
        List<string> errors = [];
        List<string> warnings = [];

        Dictionary<string, ServiceConfig> byId = [];
        foreach (ServiceConfig service in config.Services)
        {
            if (string.IsNullOrWhiteSpace(service.Id))
            {
                errors.Add("A service has no id.");
                continue;
            }

            if (!byId.TryAdd(service.Id, service))
            {
                errors.Add($"Duplicate service id '{service.Id}'.");
            }
        }

        if (byId.Count == 0)
        {
            errors.Add("No services defined — there is nothing to simulate.");
        }

        if (config.Flows.Count == 0 && !config.Services.Any(s => s.Kind == ServiceKind.Cron))
        {
            warnings.Add(
                "No flows and no cron services: nothing will generate load, so you will only " +
                "see idle infrastructure metrics.");
        }

        ValidateServices(config, byId, errors, warnings);
        ValidateFlows(config, byId, errors);
        ValidateScenarios(config, byId, errors, warnings);
        ValidateSimulation(config, errors);
        WarnOnUnreachable(config, byId, warnings);

        return new ValidationResult(errors, warnings);
    }

    private static void ValidateServices(
        TrafficConfig config,
        Dictionary<string, ServiceConfig> byId,
        List<string> errors,
        List<string> warnings)
    {
        foreach (ServiceConfig service in config.Services)
        {
            string where = $"service '{service.Id}'";

            if (service.Instances < 1)
            {
                errors.Add($"{where}: instances must be at least 1.");
            }

            if (service.ErrorRate is < 0 or > 1)
            {
                errors.Add($"{where}: errorRate must be between 0 and 1.");
            }

            if (service.Latency.P99Ms < service.Latency.P50Ms)
            {
                errors.Add($"{where}: p99Ms ({service.Latency.P99Ms}) is below p50Ms ({service.Latency.P50Ms}).");
            }

            foreach (DependencyConfig dependency in service.Dependencies)
            {
                if (!byId.TryGetValue(dependency.Target, out ServiceConfig? target))
                {
                    errors.Add($"{where}: depends on '{dependency.Target}', which is not defined.");
                    continue;
                }

                if (dependency.Target == service.Id)
                {
                    errors.Add($"{where}: depends on itself.");
                }

                if (dependency.Probability is < 0 or > 1)
                {
                    errors.Add($"{where}: dependency on '{dependency.Target}' has probability outside 0..1.");
                }

                if (dependency.Calls < 0 || (dependency.CallsMax is { } max && max < dependency.Calls))
                {
                    errors.Add($"{where}: dependency on '{dependency.Target}' has an impossible call count.");
                }

                if (target.Kind == ServiceKind.Worker)
                {
                    warnings.Add(
                        $"{where}: calls worker '{dependency.Target}' directly. Workers are normally " +
                        "reached through a queue — a direct call will render as a synchronous hop.");
                }
            }

            foreach (ConsumerConfig consumer in service.Consumes)
            {
                if (!byId.TryGetValue(consumer.Queue, out ServiceConfig? queue))
                {
                    errors.Add($"{where}: consumes from '{consumer.Queue}', which is not defined.");
                }
                else if (queue.Kind != ServiceKind.Queue)
                {
                    errors.Add(
                        $"{where}: consumes from '{consumer.Queue}', which is a {queue.Kind.ToString().ToLowerInvariant()}, " +
                        "not a queue.");
                }

                if (consumer.Batch < 1)
                {
                    errors.Add($"{where}: consumer batch size must be at least 1.");
                }
            }

            WarnOnMismatchedKindConfig(service, warnings);

            if (service.Kind is ServiceKind.Database or ServiceKind.Cache or ServiceKind.External
                && service.Dependencies.Count > 0)
            {
                warnings.Add(
                    $"{where}: a {service.Kind.ToString().ToLowerInvariant()} has dependencies. Nothing stops you, " +
                    "but the simulator cannot see inside it, so those calls will appear to come from " +
                    "a service that does not instrument itself.");
            }

            if (service.Kind == ServiceKind.Cron && service.Cron is null)
            {
                warnings.Add($"{where}: is a cron service with no cron block, so it will use the default 5m schedule.");
            }
        }
    }

    private static void WarnOnMismatchedKindConfig(ServiceConfig service, List<string> warnings)
    {
        (object? block, ServiceKind expected, string name)[] blocks =
        [
            (service.Database, ServiceKind.Database, "database"),
            (service.Cache, ServiceKind.Cache, "cache"),
            (service.Queue, ServiceKind.Queue, "queue"),
            (service.Cron, ServiceKind.Cron, "cron"),
        ];

        foreach ((object? block, ServiceKind expected, string name) in blocks)
        {
            if (block is not null && service.Kind != expected)
            {
                warnings.Add(
                    $"service '{service.Id}': has a '{name}' block but is kind " +
                    $"'{service.Kind.ToString().ToLowerInvariant()}', so the block is ignored.");
            }
        }
    }

    private static void ValidateFlows(
        TrafficConfig config,
        Dictionary<string, ServiceConfig> byId,
        List<string> errors)
    {
        HashSet<string> seen = [];
        foreach (FlowConfig flow in config.Flows)
        {
            string where = $"flow '{flow.Id}'";

            if (string.IsNullOrWhiteSpace(flow.Id))
            {
                errors.Add("A flow has no id.");
                continue;
            }

            if (!seen.Add(flow.Id))
            {
                errors.Add($"Duplicate flow id '{flow.Id}'.");
            }

            if (!byId.ContainsKey(flow.Entry))
            {
                errors.Add($"{where}: entry '{flow.Entry}' is not a defined service.");
            }

            if (flow.Rps < 0)
            {
                errors.Add($"{where}: rps cannot be negative.");
            }

            if (flow.ClientErrorRate is < 0 or > 1)
            {
                errors.Add($"{where}: clientErrorRate must be between 0 and 1.");
            }

            if (!Duration.TryParse(flow.Profile.Period, out _))
            {
                errors.Add($"{where}: profile period '{flow.Profile.Period}' is not a duration.");
            }
        }
    }

    private static void ValidateScenarios(
        TrafficConfig config,
        Dictionary<string, ServiceConfig> byId,
        List<string> errors,
        List<string> warnings)
    {
        HashSet<string> flowIds = [.. config.Flows.Select(f => f.Id)];
        HashSet<string> seen = [];

        foreach (ScenarioConfig scenario in config.Scenarios)
        {
            if (!seen.Add(scenario.Id))
            {
                errors.Add($"Duplicate scenario id '{scenario.Id}'.");
            }

            foreach (ScenarioStepConfig step in scenario.Steps)
            {
                string where = $"scenario '{scenario.Id}' step at '{step.At}'";

                if (!Duration.TryParse(step.At, out _))
                {
                    errors.Add($"{where}: '{step.At}' is not a duration.");
                }

                foreach (FaultConfig fault in step.Faults)
                {
                    ValidateFault(fault, where, byId, flowIds, errors, warnings);
                }
            }
        }

        if (config.Simulation.Scenario is { } startup && !seen.Contains(startup))
        {
            errors.Add($"simulation.scenario is '{startup}', which is not a defined scenario.");
        }
    }

    private static void ValidateFault(
        FaultConfig fault,
        string where,
        Dictionary<string, ServiceConfig> byId,
        HashSet<string> flowIds,
        List<string> errors,
        List<string> warnings)
    {
        bool isFlow = fault.Target.StartsWith("flow:", StringComparison.Ordinal);
        string bare = isFlow ? fault.Target["flow:".Length..] : fault.Target;

        if (!bare.Contains('*', StringComparison.Ordinal))
        {
            bool known = isFlow ? flowIds.Contains(bare) : byId.ContainsKey(bare);
            if (!known)
            {
                errors.Add($"{where}: targets '{fault.Target}', which is not a defined {(isFlow ? "flow" : "service")}.");
            }
        }
        else if (!Glob.MatchesAny(bare, isFlow ? flowIds : byId.Keys))
        {
            warnings.Add($"{where}: pattern '{fault.Target}' matches nothing, so the fault will do nothing.");
        }

        if (fault.Kind == FaultKind.Traffic && !isFlow)
        {
            errors.Add($"{where}: a 'traffic' fault must target a flow — write 'flow:{fault.Target}'.");
        }

        if (fault.Kind != FaultKind.Traffic && isFlow)
        {
            errors.Add($"{where}: a '{fault.Kind.ToString().ToLowerInvariant()}' fault must target a service, not a flow.");
        }

        if (fault.ErrorRate is { } rate and (< 0 or > 1))
        {
            errors.Add($"{where}: errorRate {rate} is outside 0..1.");
        }

        if (fault.Coverage is < 0 or > 1)
        {
            errors.Add($"{where}: coverage must be between 0 and 1.");
        }

        if (!Duration.TryParse(fault.Ramp, out _))
        {
            errors.Add($"{where}: ramp '{fault.Ramp}' is not a duration.");
        }

        if (!Duration.TryParse(fault.Duration, out _))
        {
            errors.Add($"{where}: duration '{fault.Duration}' is not a duration.");
        }

        bool hasParameter = fault.Multiplier.HasValue
            || fault.P50Ms.HasValue
            || fault.P99Ms.HasValue
            || fault.ErrorRate.HasValue
            || fault.LagMs.HasValue;

        if (!hasParameter && fault.Kind is not (FaultKind.Outage or FaultKind.Saturation))
        {
            warnings.Add(
                $"{where}: a '{fault.Kind.ToString().ToLowerInvariant()}' fault with no parameters will " +
                "fall back to a default effect. Set multiplier, errorRate or lagMs to be explicit.");
        }
    }

    private static void ValidateSimulation(TrafficConfig config, List<string> errors)
    {
        SimulationConfig simulation = config.Simulation;

        if (simulation.TickMs < 1)
        {
            errors.Add("simulation.tickMs must be at least 1.");
        }

        if (simulation.TraceSampleRatio is < 0 or > 1)
        {
            errors.Add("simulation.traceSampleRatio must be between 0 and 1.");
        }

        if (simulation.ErrorTraceSampleRatio is < 0 or > 1)
        {
            errors.Add("simulation.errorTraceSampleRatio must be between 0 and 1.");
        }

        if (simulation.RateMultiplier < 0)
        {
            errors.Add("simulation.rateMultiplier cannot be negative.");
        }

        if (simulation.MaxDepth < 0)
        {
            errors.Add("simulation.maxDepth cannot be negative.");
        }

        if (!Duration.TryParse(simulation.Duration, out _))
        {
            errors.Add($"simulation.duration '{simulation.Duration}' is not a duration.");
        }

        if (config.Exporter.MetricIntervalMs < 100)
        {
            errors.Add("exporter.metricIntervalMs below 100 will spend more time exporting than simulating.");
        }

        if (!Uri.TryCreate(config.Exporter.Endpoint, UriKind.Absolute, out _))
        {
            errors.Add($"exporter.endpoint '{config.Exporter.Endpoint}' is not an absolute URL.");
        }
    }

    /// <summary>
    /// Flags services nothing can ever reach. Usually a typo; occasionally
    /// deliberate, which is why it is a warning.
    /// </summary>
    private static void WarnOnUnreachable(
        TrafficConfig config,
        Dictionary<string, ServiceConfig> byId,
        List<string> warnings)
    {
        HashSet<string> reachable = [];
        Queue<string> pending = [];

        foreach (FlowConfig flow in config.Flows)
        {
            if (reachable.Add(flow.Entry))
            {
                pending.Enqueue(flow.Entry);
            }
        }

        foreach (ServiceConfig cron in config.Services.Where(s => s.Kind == ServiceKind.Cron))
        {
            if (reachable.Add(cron.Id))
            {
                pending.Enqueue(cron.Id);
            }
        }

        while (pending.Count > 0)
        {
            string id = pending.Dequeue();
            if (!byId.TryGetValue(id, out ServiceConfig? service))
            {
                continue;
            }

            foreach (DependencyConfig dependency in service.Dependencies)
            {
                if (reachable.Add(dependency.Target))
                {
                    pending.Enqueue(dependency.Target);
                }
            }

            // A queue reaches its consumers, even though the edge is declared
            // from the other end.
            if (service.Kind == ServiceKind.Queue)
            {
                foreach (ServiceConfig consumer in config.Services.Where(
                    s => s.Consumes.Any(c => c.Queue == service.Id)))
                {
                    if (reachable.Add(consumer.Id))
                    {
                        pending.Enqueue(consumer.Id);
                    }
                }
            }
        }

        foreach (ServiceConfig service in config.Services.Where(s => !reachable.Contains(s.Id)))
        {
            warnings.Add(
                $"service '{service.Id}' is never called by any flow or cron job. It will appear " +
                "in the town as an idle building.");
        }
    }
}

public sealed record ValidationResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}
