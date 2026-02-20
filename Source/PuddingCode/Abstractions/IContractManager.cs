using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// 契约管理。Leader 用于定义契约并验证 Worker 实现是否匹配。
/// </summary>
public interface IContractManager
{
    /// <summary>
    /// 根据规格说明定义契约（接口/类/方法签名 + 契约注释）。
    /// </summary>
    /// <param name="specification">契约描述（自然语言 + 约束条件）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>创建的契约定义</returns>
    Task<Contract> DefineContractAsync(string specification, CancellationToken ct = default);

    /// <summary>
    /// 验证 Worker 实现是否匹配契约签名。
    /// </summary>
    /// <param name="contractId">契约 ID</param>
    /// <param name="worktreePath">Worker 工作树路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>验证通过返回 true，否则返回 false</returns>
    Task<bool> ValidateContractAsync(string contractId, string worktreePath, CancellationToken ct = default);

    /// <summary>
    /// 初始化蜂群目录结构（首次启动时创建）。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>蜂群根目录路径</returns>
    Task<string> InitializeSwarmDirectoryAsync(CancellationToken ct = default);
}
