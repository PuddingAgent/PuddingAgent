namespace PuddingCode.Models;

/// <summary>A resolved workspace has no receiving Agent for this address; retrying
/// the same terminal reply cannot restore a removed recipient.</summary>
public sealed class MessageTargetUnavailableException(string targetId)
    : InvalidOperationException($"Agent address '{targetId}' was not found or cannot receive messages in this workspace.")
{
    public string TargetId { get; } = targetId;
    public const string ErrorCode = "message_target_unavailable";
}
