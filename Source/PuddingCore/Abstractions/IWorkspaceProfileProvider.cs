namespace PuddingCode.Abstractions;

/// <summary>
/// 工作区用户画像提供者。由 Platform 层实现，Runtime 层通过该接口读取 USER 层上下文。
/// </summary>
public interface IWorkspaceProfileProvider
{
    Task<string?> GetWorkspaceUserProfileAsync(string workspaceId, CancellationToken ct = default);
}
