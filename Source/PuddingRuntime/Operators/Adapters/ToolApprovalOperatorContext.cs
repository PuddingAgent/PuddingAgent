using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PuddingCode.Classification;
using PuddingCode.Operators;

namespace PuddingRuntime.Operators.Adapters;

/// <summary>
/// 工具审批场景的算子输入上下文（S1b 交付物 2 的输入适配层）：把<b>既有</b>的
/// <see cref="ToolCallClassificationContext"/> <b>包装</b>成算子公共输入面 <see cref="IOperatorContext"/>。
/// <para>
/// <b>为什么不改被包装者</b>：<see cref="ToolCallClassificationContext"/> 是审批仲裁链路的既有输入契约，
/// 给它加 <see cref="IOperatorContext"/> 会把它与算子抽象<b>永久耦合</b>（所有既有消费方都被动继承这层依赖）。
/// 适配器用「壳」承载它，既有类型一行不改，耦合被限制在适配器内部。
/// </para>
/// <para>
/// 同时实现 <see cref="IOperatorIdentityContext"/>：身份四元组是判定作用域与审计溯源的边界，
/// 由既有上下文<b>提升</b>（不是复制新造），使信封 <c>Identity</c> 与审批记录的身份一致。
/// </para>
/// </summary>
public sealed class ToolApprovalOperatorContext : IOperatorContext, IOperatorIdentityContext
{
    /// <summary>被包装的既有工具调用裁决输入（<b>原样</b>承载，不做裁剪、不做改写）。</summary>
    public required ToolCallClassificationContext Approval { get; init; }

    /// <inheritdoc />
    public string SceneKey => ToolApprovalOperatorAdapter.SceneKeyValue;

    /// <summary>
    /// 输入身份指纹。必须经 <see cref="Create"/> 构造（由工厂按固定字段顺序计算），
    /// 使「同一输入 ⇒ 同一指纹」成为构造期保证，而不是调用方的自律。
    /// </summary>
    public required string InputDigest { get; init; }

    /// <inheritdoc />
    public OperatorIdentity Identity => new()
    {
        WorkspaceId = Approval.WorkspaceId,
        SessionId = Approval.SessionId,
        AgentInstanceId = Approval.AgentInstanceId,
        UserId = Approval.UserId,
    };

    /// <summary>
    /// 规范构造路径：按 <see cref="ComputeInputDigest"/> 计算确定性指纹后构造上下文。
    /// </summary>
    /// <param name="approval">既有工具调用裁决输入。</param>
    /// <exception cref="ArgumentNullException"><paramref name="approval"/> 为 null。</exception>
    public static ToolApprovalOperatorContext Create(ToolCallClassificationContext approval)
    {
        ArgumentNullException.ThrowIfNull(approval);

        return new ToolApprovalOperatorContext
        {
            Approval = approval,
            InputDigest = ComputeInputDigest(approval),
        };
    }

    /// <summary>
    /// 确定性输入指纹：按<b>固定字段顺序</b>对<b>稳定字段</b>做 SHA-256。
    /// <para>
    /// 参与计算的字段（顺序即契约，<b>不得</b>重排、<b>不得</b>静默增删）：
    /// <c>ToolId, CommandName, ArgumentsJson, WorkingDirectory, Shell, WorkspaceId, SessionId,
    /// AgentInstanceId, UserId</c>。
    /// </para>
    /// <para>
    /// <b>禁止纳入易变字段</b>（时间、进程 id、随机值）——否则指纹每次都变，缓存键与去重立即失效。
    /// </para>
    /// <para>
    /// 人类撰写的说明文本（<c>OperationContext / Purpose / Necessity / FactBasis / TargetResources /
    /// RecentTrajectory / MatchedRuleSummaries</c>）<b>不参与</b>指纹：它们是同一实际调用的可重写描述，
    /// 纳入会让「同一裁决被重复请求」得到不同指纹，从而绕过缓存与去重。
    /// </para>
    /// <para>
    /// 缺省值参与计算（null 归一为空串、字段名与长度始终写入）：既不跳过字段（避免「字段缺失」与
    /// 「字段为空」产生同一指纹的歧义），也不做字符串拼接（长度前缀杜绝 <c>a|b</c> 与 <c>ab|</c> 类撞键）。
    /// </para>
    /// </summary>
    /// <param name="context">既有工具调用裁决输入。</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> 为 null。</exception>
    public static string ComputeInputDigest(ToolCallClassificationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var material = new StringBuilder();
        material.Append("tool-approval-input-digest/v1\n");

        AppendDigestField(material, nameof(ToolCallClassificationContext.ToolId), context.ToolId);
        AppendDigestField(material, nameof(ToolCallClassificationContext.CommandName), context.CommandName);
        AppendDigestField(material, nameof(ToolCallClassificationContext.ArgumentsJson), context.ArgumentsJson);
        AppendDigestField(material, nameof(ToolCallClassificationContext.WorkingDirectory), context.WorkingDirectory);
        AppendDigestField(material, nameof(ToolCallClassificationContext.Shell), context.Shell);
        AppendDigestField(material, nameof(ToolCallClassificationContext.WorkspaceId), context.WorkspaceId);
        AppendDigestField(material, nameof(ToolCallClassificationContext.SessionId), context.SessionId);
        AppendDigestField(material, nameof(ToolCallClassificationContext.AgentInstanceId), context.AgentInstanceId);
        AppendDigestField(material, nameof(ToolCallClassificationContext.UserId), context.UserId);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendDigestField(StringBuilder builder, string name, string? value)
    {
        var normalized = value ?? string.Empty;
        builder
            .Append(name)
            .Append(':')
            .Append(normalized.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(normalized)
            .Append('\n');
    }
}
