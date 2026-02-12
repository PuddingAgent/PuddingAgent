namespace PuddingCode.Models;

using System.Collections.Generic;

/// <summary>
/// Worker 信息记录，包含蜂群中 Worker 实例的元数据。
/// 用于 WorkerManager 追踪和管理活跃的 Worker 实例。
/// </summary>
/// <param name="Id">Worker 唯一标识符。</param>
/// <param name="Role">Worker 角色（Leader/Builder/QA/Docs）。</param>
/// <param name="Name">Worker 名称（用于 UI 显示）。</param>
/// <param name="WorktreePath">Worker 专属 Git Worktree 路径。</param>
/// <param name="Scope">Worker 作用域约束（允许访问的路径和符号）。</param>
public sealed record WorkerInfo(string Id, WorkerRole Role, string Name, string WorktreePath, WorkerScope Scope);
