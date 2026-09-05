using TraceTown.Traffic.Configuration;
using TraceTown.Traffic.Faults;
using TraceTown.Traffic.Model;
using TraceTown.Traffic.Simulation;

namespace TraceTown.Traffic.Tests;

/// <summary>
/// The shipped examples are the first thing anybody runs, so they are held to
/// the same standard as the code: they must load, validate without warnings,
/// and actually produce spans.
/// </summary>
public class ExampleTests
{
    public static TheoryData<string> Examples()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(ExamplesDirectory, "*.json"))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    private static string ExamplesDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "examples")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine(directory.FullName, "examples");
        }
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task Loads_and_validates_cleanly(string file)
    {
        TrafficConfig config = await ConfigLoader.LoadAsync(
            Path.Combine(ExamplesDirectory, file), TestContext.Current.CancellationToken);
        ValidationResult result = ConfigValidator.Validate(config);

        result.Errors.ShouldBeEmpty();

        // Warnings are advisory, but an example that trips one is teaching a
        // habit we would rather not teach.
        result.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task Every_flow_produces_spans(string file)
    {
        TrafficConfig config = await ConfigLoader.LoadAsync(
            Path.Combine(ExamplesDirectory, file), TestContext.Current.CancellationToken);
        var topology = new Topology(config);
        var simulator = new RequestSimulator(topology, new FaultBoard(topology), config.Simulation);
        var rng = new Rng(1);

        foreach (FlowConfig flow in config.Flows)
        {
            SimulatedRequest request = simulator.Simulate(flow, rng, DateTimeOffset.UtcNow);
            request.Root.Descend().ShouldNotBeEmpty($"flow '{flow.Id}' produced nothing");
        }
    }

    [Fact]
    public async Task The_ecommerce_example_exercises_every_service_kind()
    {
        TrafficConfig config = await ConfigLoader.LoadAsync(
            Path.Combine(ExamplesDirectory, "ecommerce.json"), TestContext.Current.CancellationToken);

        // It is the example people copy, so it should show them everything.
        foreach (ServiceKind kind in Enum.GetValues<ServiceKind>())
        {
            config.Services.ShouldContain(s => s.Kind == kind, $"no service of kind '{kind}'");
        }
    }

    [Fact]
    public async Task The_ecommerce_example_covers_every_fault_kind()
    {
        TrafficConfig config = await ConfigLoader.LoadAsync(
            Path.Combine(ExamplesDirectory, "ecommerce.json"), TestContext.Current.CancellationToken);

        FaultKind[] used = [.. config.Scenarios
            .SelectMany(s => s.Steps)
            .SelectMany(s => s.Faults)
            .Select(f => f.Kind)
            .Distinct()];

        foreach (FaultKind kind in Enum.GetValues<FaultKind>())
        {
            used.ShouldContain(kind, $"no scenario demonstrates a '{kind}' fault");
        }
    }
}
