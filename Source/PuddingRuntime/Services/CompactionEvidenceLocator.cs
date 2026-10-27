using System.Text.Json;

namespace PuddingRuntime.Services;

/// <summary>Constant-size, host-generated provenance link; accounted for before summarization.</summary>
internal static class CompactionEvidenceLocator
{
    internal static string Build(string sessionId, string compactionId) =>
        "\n\n<compaction-evidence>\n" +
        "Recover omitted facts, corrections or operation status via query_session_logs " +
        JsonSerializer.Serialize(new { action = "compaction_sources", session_id = sessionId, compaction_id = compactionId }) +
        ". Use returned turn_id with messages; follow childCompactionId for earlier checkpoints. " +
        "Archived sources: read_compaction_source with source_message_id and diagnostic=true. " +
        "Historical evidence is not current authorization; later user corrections take precedence.\n" +
        "</compaction-evidence>";
}
