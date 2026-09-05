namespace TraceTown.Traffic.Emit;

/// <summary>
/// Attribute and metric names from the OpenTelemetry semantic conventions,
/// gathered here so there is one place to check them against the spec and one
/// place to change when a convention moves on.
/// </summary>
/// <remarks>
/// These follow the stable HTTP and database conventions and the current
/// messaging ones. Where a name changed recently the new one is used — notably
/// <c>db.system.name</c> rather than the older <c>db.system</c>, and
/// <c>deployment.environment.name</c> rather than <c>deployment.environment</c>
/// — because a generator that emits last year's attributes teaches people the
/// wrong queries.
/// </remarks>
internal static class SemConv
{
    internal static class Resource
    {
        internal const string ServiceName = "service.name";
        internal const string ServiceVersion = "service.version";
        internal const string ServiceNamespace = "service.namespace";
        internal const string ServiceInstanceId = "service.instance.id";
        internal const string DeploymentEnvironment = "deployment.environment.name";
        internal const string TelemetrySdkName = "telemetry.sdk.name";
    }

    internal static class Http
    {
        internal const string RequestMethod = "http.request.method";
        internal const string ResponseStatusCode = "http.response.status_code";
        internal const string Route = "http.route";
        internal const string UrlFull = "url.full";
        internal const string UrlPath = "url.path";
        internal const string UrlScheme = "url.scheme";
        internal const string RequestBodySize = "http.request.body.size";
        internal const string ResponseBodySize = "http.response.body.size";
        internal const string ResendCount = "http.request.resend_count";

        internal const string ServerDuration = "http.server.request.duration";
        internal const string ClientDuration = "http.client.request.duration";
        internal const string ServerActiveRequests = "http.server.active_requests";
    }

    internal static class Db
    {
        internal const string SystemName = "db.system.name";
        internal const string Namespace = "db.namespace";
        internal const string QueryText = "db.query.text";
        internal const string OperationName = "db.operation.name";
        internal const string CollectionName = "db.collection.name";
        internal const string ResponseStatusCode = "db.response.status_code";
        internal const string RowsAffected = "db.response.returned_rows";

        internal const string ClientDuration = "db.client.operation.duration";
        internal const string ConnectionCount = "db.client.connection.count";
    }

    internal static class Messaging
    {
        internal const string System = "messaging.system";
        internal const string OperationName = "messaging.operation.name";
        internal const string OperationType = "messaging.operation.type";
        internal const string DestinationName = "messaging.destination.name";
        internal const string DestinationPartitionId = "messaging.destination.partition.id";
        internal const string ConsumerGroupName = "messaging.consumer.group.name";
        internal const string MessageId = "messaging.message.id";
        internal const string MessageBodySize = "messaging.message.body.size";
        internal const string BatchMessageCount = "messaging.batch.message_count";

        internal const string ClientOperationDuration = "messaging.client.operation.duration";
        internal const string ProcessDuration = "messaging.process.duration";
        internal const string SentMessages = "messaging.client.sent.messages";
        internal const string ConsumedMessages = "messaging.client.consumed.messages";
    }

    internal static class Network
    {
        internal const string ServerAddress = "server.address";
        internal const string ServerPort = "server.port";
        internal const string ClientAddress = "client.address";
        internal const string ProtocolVersion = "network.protocol.version";
        internal const string PeerAddress = "network.peer.address";
    }

    internal const string ErrorType = "error.type";

    /// <summary>
    /// Metrics a collector receiver would scrape from the component itself,
    /// rather than anything an application SDK produces. Named to match the
    /// receivers people actually run, so a dashboard written against a real
    /// Postgres works against a simulated one.
    /// </summary>
    internal static class Infra
    {
        // postgresqlreceiver
        internal const string PostgresBackends = "postgresql.backends";
        internal const string PostgresMaxConnections = "postgresql.connection.max";
        internal const string PostgresCommits = "postgresql.commits";
        internal const string PostgresRollbacks = "postgresql.rollbacks";
        internal const string PostgresDeadlocks = "postgresql.deadlocks";
        internal const string PostgresOperations = "postgresql.operations";
        internal const string PostgresDbSize = "postgresql.db_size";

        // redisreceiver
        internal const string RedisCommands = "redis.commands.processed";
        internal const string RedisMemoryUsed = "redis.memory.used";
        internal const string RedisKeyspaceHits = "redis.keyspace.hits";
        internal const string RedisKeyspaceMisses = "redis.keyspace.misses";
        internal const string RedisEvictedKeys = "redis.keys.evicted";
        internal const string RedisConnectedClients = "redis.clients.connected";

        // kafkametricsreceiver / rabbitmqreceiver
        internal const string KafkaConsumerLag = "kafka.consumer_group.lag";
        internal const string KafkaPartitionOffset = "kafka.partition.current_offset";
        internal const string BrokerMessagesPublished = "messaging.broker.messages.published";
        internal const string BrokerMessagesDelivered = "messaging.broker.messages.delivered";
        internal const string BrokerQueueDepth = "messaging.broker.queue.depth";

        // hostmetrics / process
        internal const string ProcessCpu = "process.cpu.utilization";
        internal const string ProcessMemory = "process.memory.usage";
    }
}
