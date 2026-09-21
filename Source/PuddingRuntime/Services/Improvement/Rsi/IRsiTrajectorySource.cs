namespace PuddingRuntime.Services.Improvement.Rsi;

/// <summary>
/// RSI S3 B3（规格 §2.11）：一次取数的身份上下文 —— 由调用方显式给出，本层<b>不推导</b>。
/// <para>
/// 查询维度<b>只用 <see cref="ConversationId"/></b>（已索引键，§2.9 / §2.10）；
/// 其余三个字段只用于向输出轨迹<b>盖章</b>，不得参与取数、不得被推导：
/// turn 行没有 agent / session 列，chat_messages 的 (SessionId, TurnId) 非唯一 ⇒ 任何「查行回填」都不确定（§2.11 三条禁令）。
/// </para>
/// </summary>
public sealed record RsiScope(
    string WorkspaceId, string AgentInstanceId, string SessionId, string ConversationId);

/// <summary>
/// RSI S3 B3（规格 §2.11 / §2.12.1）：会话级轨迹源 —— 把 B2 的数据访问接缝与 B1 的装配器串起来：
/// 按会话取最近 N 个 turn → 取这些 turn 的工具事件 → 装配成带结局的轨迹。
/// <para>
/// 位置冻结（§2.12.1）：与 <see cref="RsiTrajectory"/> 同程序集（PuddingRuntime），不得移到 PuddingCore
/// （Core 是下层，引用不到 Runtime 的 DTO，接口放 Core 不可能编译）。
/// 本类型在 S4 真实消费前<b>不注册 DI</b>（§2.12.3）。
/// </para>
/// </summary>
public interface IRsiTrajectorySource
{
    /// <summary>取指定会话最近 <paramref name="limit"/> 个 turn 的带结局轨迹（时间升序）。</summary>
    Task<IReadOnlyList<RsiTrajectory>> GetRecentAnnotatedAsync(
        RsiScope scope, int limit, CancellationToken ct);
}
