namespace PuddingCode.Models;

using System.Collections.Generic;

/// <summary>
/// Worker 作用域定义，用于限制 Worker 可访问的文件路径和符号。
/// 作用域隔离是蜂群模式的核心机制，确保多个 Worker 并行工作不会相互干扰。
/// </summary>
/// <param name="AllowedPaths">允许访问的文件路径模式列表（支持通配符，如 "src/Auth/*"）。</param>
/// <param name="AllowedSymbols">允许访问的符号列表（如 "AuthService.LoginAsync"）。</param>
public sealed record WorkerScope(IReadOnlyList<string> AllowedPaths, IReadOnlyList<string> AllowedSymbols);
