namespace PuddingCode.Configuration;

/// <summary>
/// PA-06 06A instance-level purpose model policy stored in the existing Agent manifest (the configuration authority).
/// Only the instance layer exists here; workspace/system layers are resolved elsewhere. Revision is the CAS token.
/// </summary>
public sealed record AgentPurposeModelPolicy(string Purpose, string? ModelId = null, bool InheritPrimary = false);

public sealed record AgentModelPolicy(long Revision = 0, long PolicyGeneration = 0,
    List<AgentPurposeModelPolicy>? Purposes = null);
