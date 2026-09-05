using TraceTown.Traffic.Configuration;

namespace TraceTown.Traffic.Tests;

/// <summary>Small configs the tests build on, so each test states only what it varies.</summary>
internal static class TestConfigs
{
    /// <summary>An API over a database and a cache, reached by one flow.</summary>
    internal const string Simple = """
        {
          "town": { "name": "test-town" },
          "services": [
            {
              "id": "api", "kind": "api",
              "latency": { "p50Ms": 10, "p99Ms": 50 },
              "dependencies": [
                { "target": "db", "operation": "SELECT users" },
                { "target": "cache", "operation": "GET user" }
              ]
            },
            { "id": "db", "kind": "database", "latency": { "p50Ms": 3, "p99Ms": 20 } },
            { "id": "cache", "kind": "cache", "latency": { "p50Ms": 1, "p99Ms": 4 } }
          ],
          "flows": [ { "id": "main", "entry": "api", "route": "GET /users", "rps": 10 } ]
        }
        """;

    /// <summary>A publisher, a broker and a consumer — the asynchronous case.</summary>
    internal const string Queued = """
        {
          "services": [
            {
              "id": "api", "kind": "api",
              "dependencies": [ { "target": "bus", "operation": "orders.placed" } ]
            },
            { "id": "bus", "kind": "queue", "queue": { "system": "kafka", "partitions": 3 } },
            {
              "id": "worker", "kind": "worker",
              "consumes": [ { "queue": "bus", "destination": "orders.placed", "lagMs": 500 } ],
              "dependencies": [ { "target": "db", "operation": "UPDATE orders" } ]
            },
            { "id": "db", "kind": "database" }
          ],
          "flows": [ { "id": "main", "entry": "api", "route": "POST /orders", "rps": 1 } ]
        }
        """;

    internal static TrafficConfig Parse(string json) => ConfigLoader.Parse(json);
}
