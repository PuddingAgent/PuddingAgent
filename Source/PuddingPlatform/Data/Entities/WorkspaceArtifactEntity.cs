using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>One committed, versioned workspace artifact. The artifact row and its outbox event commit together.</summary>
[Table("workspace_artifacts")]
public sealed class WorkspaceArtifactEntity
{
    [Key] [Column("artifact_version_id")] public string ArtifactVersionId { get; set; } = "";
    [Column("workspace_id")] public string WorkspaceId { get; set; } = "";
    [Column("artifact_id")] public string ArtifactId { get; set; } = "";
    [Column("version")] public string Version { get; set; } = "";
    [Column("content_ref")] public string ContentRef { get; set; } = "";
    [Column("payload_hash")] public string PayloadHash { get; set; } = "";
    [Column("created_at")] public DateTimeOffset CreatedAt { get; set; }
}
