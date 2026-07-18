namespace PuddingCode.Platform;

public record AgentAvatarDefinition(
    string AvatarId,
    string Name,
    string FileName,
    string UrlPath,
    string? Personality,
    string? RecommendedUse,
    int SortOrder,
    bool IsEnabled
);

public interface IAgentAvatarCatalog
{
    IReadOnlyList<AgentAvatarDefinition> List();
    AgentAvatarDefinition GetDefault();
    AgentAvatarDefinition? Find(string avatarId);
    string? ResolveUrl(string? avatarId);
}
