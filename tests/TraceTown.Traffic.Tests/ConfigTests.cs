using TraceTown.Traffic.Configuration;

namespace TraceTown.Traffic.Tests;

public class DurationTests
{
    [Theory]
    [InlineData("500ms", 0.5)]
    [InlineData("30s", 30)]
    [InlineData("5m", 300)]
    [InlineData("1h", 3600)]
    [InlineData("2m30s", 150)]
    [InlineData("1h30m", 5400)]
    [InlineData("45", 45)]
    public void Parses_the_forms_the_config_uses(string text, double expectedSeconds)
        => Duration.Parse(text).TotalSeconds.ShouldBe(expectedSeconds, tolerance: 0.001);

    [Fact]
    public void Treats_a_missing_value_as_the_fallback()
        => Duration.Parse(null, TimeSpan.FromMinutes(2)).ShouldBe(TimeSpan.FromMinutes(2));

    [Theory]
    [InlineData("30x")]
    [InlineData("soon")]
    [InlineData("5 parsecs")]
    public void Rejects_trailing_junk_rather_than_silently_ignoring_it(string text)
        => Should.Throw<FormatException>(() => Duration.Parse(text));
}

public class GlobTests
{
    [Theory]
    [InlineData("*-api", "checkout-api", true)]
    [InlineData("*-api", "api-gateway", false)]
    [InlineData("postgres-*", "postgres-orders", true)]
    [InlineData("*", "anything", true)]
    [InlineData("redis-*", "postgres-orders", false)]
    [InlineData("exact", "exact", true)]
    [InlineData("exact", "exactly", false)]
    [InlineData("a*c", "abc", true)]
    [InlineData("a*c", "ac", true)]
    public void Matches(string pattern, string candidate, bool expected)
        => Glob.Matches(pattern, candidate).ShouldBe(expected);

    [Fact]
    public void Does_not_let_one_literal_be_matched_twice()
    {
        // "ab*ab" needs two separate occurrences, so a single "ab" must fail.
        Glob.Matches("ab*ab", "ab").ShouldBeFalse();
        Glob.Matches("ab*ab", "abab").ShouldBeTrue();
    }
}

public class EnumParsingTests
{
    [Theory]
    [InlineData("randomWalk")]
    [InlineData("random-walk")]
    [InlineData("random_walk")]
    [InlineData("RandomWalk")]
    [InlineData("RANDOMWALK")]
    public void Accepts_any_reasonable_spelling(string written)
    {
        TrafficConfig config = ConfigLoader.Parse($$"""
            {
              "services": [ { "id": "a", "kind": "api" } ],
              "flows": [ { "id": "f", "entry": "a", "profile": { "kind": "{{written}}" } } ]
            }
            """);

        config.Flows[0].Profile.Kind.ShouldBe(LoadProfileKind.RandomWalk);
    }

    [Fact]
    public void Names_the_valid_values_when_it_cannot_parse_one()
    {
        Exception error = Should.Throw<ConfigException>(() => ConfigLoader.Parse("""
            { "services": [ { "id": "a", "kind": "wobbly" } ] }
            """));

        error.Message.ShouldContain("gateway");
        error.Message.ShouldContain("database");
    }
}

public class ConfigLoaderTests
{
    [Fact]
    public void Allows_comments_and_trailing_commas()
    {
        TrafficConfig config = ConfigLoader.Parse("""
            {
              // These files are hand-written and long-lived.
              "services": [ { "id": "a", "kind": "api" }, ],
            }
            """);

        config.Services.Count.ShouldBe(1);
    }

    [Fact]
    public void Reports_the_line_number_when_the_json_is_broken()
    {
        ConfigException error = Should.Throw<ConfigException>(() => ConfigLoader.Parse("""
            {
              "services": [ { "id": "a", "kind": }
            }
            """, "broken.json"));

        error.Message.ShouldContain("broken.json");
        error.Message.ShouldContain("line");
    }
}
