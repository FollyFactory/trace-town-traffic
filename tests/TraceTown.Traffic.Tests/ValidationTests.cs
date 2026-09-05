using TraceTown.Traffic.Configuration;

namespace TraceTown.Traffic.Tests;

public class ValidationTests
{
    [Fact]
    public void Accepts_a_sound_config()
        => ConfigValidator.Validate(TestConfigs.Parse(TestConfigs.Simple)).IsValid.ShouldBeTrue();

    [Fact]
    public void Rejects_a_dependency_on_a_service_that_does_not_exist()
    {
        ValidationResult result = ConfigValidator.Validate(ConfigLoader.Parse("""
            {
              "services": [
                { "id": "api", "kind": "api", "dependencies": [ { "target": "postgress" } ] },
                { "id": "postgres", "kind": "database" }
              ],
              "flows": [ { "id": "f", "entry": "api" } ]
            }
            """));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("postgress"));
    }

    [Fact]
    public void Rejects_duplicate_service_ids()
    {
        ValidationResult result = ConfigValidator.Validate(ConfigLoader.Parse("""
            {
              "services": [ { "id": "api", "kind": "api" }, { "id": "api", "kind": "worker" } ]
            }
            """));

        result.Errors.ShouldContain(e => e.Contains("Duplicate service id"));
    }

    [Fact]
    public void Rejects_a_p99_below_the_p50()
    {
        ValidationResult result = ConfigValidator.Validate(ConfigLoader.Parse("""
            {
              "services": [
                { "id": "api", "kind": "api", "latency": { "p50Ms": 100, "p99Ms": 10 } }
              ],
              "flows": [ { "id": "f", "entry": "api" } ]
            }
            """));

        result.Errors.ShouldContain(e => e.Contains("p99Ms"));
    }

    [Fact]
    public void Rejects_consuming_from_something_that_is_not_a_queue()
    {
        ValidationResult result = ConfigValidator.Validate(ConfigLoader.Parse("""
            {
              "services": [
                { "id": "worker", "kind": "worker", "consumes": [ { "queue": "db" } ] },
                { "id": "db", "kind": "database" }
              ]
            }
            """));

        result.Errors.ShouldContain(e => e.Contains("not a queue"));
    }

    [Fact]
    public void Insists_a_traffic_fault_targets_a_flow()
    {
        ValidationResult result = ConfigValidator.Validate(ConfigLoader.Parse("""
            {
              "services": [ { "id": "api", "kind": "api" } ],
              "flows": [ { "id": "main", "entry": "api" } ],
              "scenarios": [ {
                "id": "s",
                "steps": [ { "at": "0s", "faults": [
                  { "target": "api", "kind": "traffic", "multiplier": 2 }
                ] } ]
              } ]
            }
            """));

        result.Errors.ShouldContain(e => e.Contains("must target a flow"));
    }

    [Fact]
    public void Insists_a_latency_fault_targets_a_service()
    {
        ValidationResult result = ConfigValidator.Validate(ConfigLoader.Parse("""
            {
              "services": [ { "id": "api", "kind": "api" } ],
              "flows": [ { "id": "main", "entry": "api" } ],
              "scenarios": [ {
                "id": "s",
                "steps": [ { "at": "0s", "faults": [
                  { "target": "flow:main", "kind": "latency", "multiplier": 2 }
                ] } ]
              } ]
            }
            """));

        result.Errors.ShouldContain(e => e.Contains("must target a service"));
    }

    [Fact]
    public void Warns_about_a_service_nothing_can_reach()
    {
        ValidationResult result = ConfigValidator.Validate(ConfigLoader.Parse("""
            {
              "services": [
                { "id": "api", "kind": "api" },
                { "id": "orphan", "kind": "api" }
              ],
              "flows": [ { "id": "f", "entry": "api" } ]
            }
            """));

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.Contains("orphan"));
    }

    [Fact]
    public void Does_not_call_a_queues_consumer_unreachable()
    {
        // The worker is reached through the broker, and the edge is declared
        // from the worker's end, so a naive graph walk would miss it.
        ValidationResult result = ConfigValidator.Validate(TestConfigs.Parse(TestConfigs.Queued));

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldNotContain(w => w.Contains("worker"));
    }

    [Fact]
    public void Warns_when_a_fault_pattern_matches_nothing()
    {
        ValidationResult result = ConfigValidator.Validate(ConfigLoader.Parse("""
            {
              "services": [ { "id": "api", "kind": "api" } ],
              "flows": [ { "id": "f", "entry": "api" } ],
              "scenarios": [ {
                "id": "s",
                "steps": [ { "at": "0s", "faults": [
                  { "target": "redis-*", "kind": "errors", "errorRate": 0.5 }
                ] } ]
              } ]
            }
            """));

        result.Warnings.ShouldContain(w => w.Contains("matches nothing"));
    }
}
