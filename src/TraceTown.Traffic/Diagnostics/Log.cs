using Microsoft.Extensions.Logging;
using TraceTown.Traffic.Configuration;

namespace TraceTown.Traffic.Diagnostics;

/// <summary>
/// The tool's own console output, source-generated so the call sites read as
/// intent rather than as string templates. Note that this is the generator's
/// logging about itself — it has nothing to do with the simulated log records
/// it exports, which are built in <see cref="Emit.TelemetryEmitter"/>.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Simulating {Services} service(s) across {Flows} flow(s), exporting to {Endpoint}")]
    internal static partial void SimulationStarted(this ILogger logger, int services, int flows, string endpoint);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Flow '{Flow}' took {ElapsedMs}ms to generate {Count} request(s) for a {TickMs}ms tick — the configured rate is not being met")]
    internal static partial void FlowFallingBehind(
        this ILogger logger, string flow, long elapsedMs, int count, int tickMs);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Flow '{Flow}' rate set to {Rps} rps")]
    internal static partial void FlowRateSet(this ILogger logger, string flow, double rps);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Flushing buffered telemetry")]
    internal static partial void FlushingTelemetry(this ILogger logger);

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "Scenario '{Scenario}' started — {Steps} step(s), looping={Loop}")]
    internal static partial void ScenarioStarted(this ILogger logger, string scenario, int steps, bool loop);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Information,
        Message = "Scenario stopped; all its faults cleared")]
    internal static partial void ScenarioStopped(this ILogger logger);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Information,
        Message = "[{Scenario} @ {At}] {Note}")]
    internal static partial void ScenarioNote(this ILogger logger, string scenario, string at, string note);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Information,
        Message = "[{Scenario} @ {At}] cleared {Count} fault(s)")]
    internal static partial void ScenarioCleared(this ILogger logger, string scenario, string at, int count);

    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Information,
        Message = "[{Scenario} @ {At}] {Kind} on {Target} ({FaultId}) {Because}")]
    internal static partial void ScenarioFault(
        this ILogger logger, string scenario, string at, FaultKind kind, string target, string faultId, string because);

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Information,
        Message = "Control API listening on {Url}")]
    internal static partial void ControlApiListening(this ILogger logger, string url);

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Warning,
        Message = "Control API is bound to {Host} with no token set — anyone who can reach it can reshape your telemetry")]
    internal static partial void ControlApiUnprotected(this ILogger logger, string host);

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Warning,
        Message = "config: {Warning}")]
    internal static partial void ConfigWarning(this ILogger logger, string warning);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Information,
        Message = "Stopping after configured duration of {Duration}")]
    internal static partial void DurationReached(this ILogger logger, TimeSpan duration);
}
