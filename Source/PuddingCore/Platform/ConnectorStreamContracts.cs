namespace PuddingCode.Platform;

/// <summary>
/// Generic connector operations used by a channel-specific streaming projection.
/// The Agent never invokes these operations directly.
/// </summary>
public static class ConnectorStreamOperations
{
    public const string Create = "stream.create";
    public const string Publish = "stream.publish";
    public const string Update = "stream.update";
    public const string Finish = "stream.finish";
}

/// <summary>Stable parameter names for <see cref="ConnectorStreamOperations"/>.</summary>
public static class ConnectorStreamParameters
{
    public const string Content = "content";
    public const string Summary = "summary";
    public const string ResourceId = "resource_id";
    public const string ExternalMessageId = "external_message_id";
    public const string ElementId = "element_id";
    public const string Sequence = "sequence";
    public const string Uuid = "uuid";
}

/// <summary>
/// Metadata placed on the durable terminal delivery when it must finalize an
/// already-published connector stream instead of creating a second reply.
/// </summary>
public static class ConnectorStreamMetadata
{
    public const string ReplyMode = "connector_reply_mode";
    public const string FinalizeReplyMode = "stream_finalize";
    public const string ProjectionId = "connector_stream_projection_id";
    public const string ResourceId = "connector_stream_resource_id";
    public const string ElementId = "connector_stream_element_id";
    public const string ContentSequence = "connector_stream_content_sequence";
    public const string FinishSequence = "connector_stream_finish_sequence";
}

/// <summary>Durable connector-stream lifecycle states.</summary>
public static class ConnectorStreamProjectionStatuses
{
    public const string Starting = "starting";
    public const string ResourceCreated = "resource_created";
    public const string Active = "active";
    public const string Finalizing = "finalizing";
    public const string Completed = "completed";
    public const string Failed = "failed";
}
