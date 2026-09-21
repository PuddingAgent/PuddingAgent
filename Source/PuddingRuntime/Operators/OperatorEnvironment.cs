using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PuddingCode.Operators;

namespace PuddingRuntime.Operators;

/// <summary>
/// 算子运行环境：把基类需要的全部端口一次性注入（模型 / 阈值来源 / 健康 / 审计 / 缓存 / 日志 / 时钟 / 超时）。
/// <para>
/// S1a 只提供<b>接缝与最小实现</b>：不接真实模型、不改 DI 注册、不改 <c>appsettings.json</c>。
/// </para>
/// </summary>
public sealed record OperatorEnvironment
{
    /// <summary>模型端口（唯一接触供应商协议的地方）；未配置为 null（纯确定性算子）。</summary>
    public IClassifierModel? Model { get; init; }

    /// <summary>阈值策略来源（阈值必须外置，不得硬编码常量）。</summary>
    public IThresholdPolicyProvider? ThresholdPolicies { get; init; }

    /// <summary>健康上报端口（旁挂；失败只记 Warning）。</summary>
    public IOperatorHealthObserver? Health { get; init; }

    /// <summary>审计端口（旁挂；失败不影响裁决）。</summary>
    public IOperatorAuditSink? Audit { get; init; }

    /// <summary>判定缓存端口；null 表示不缓存。</summary>
    public IOperatorJudgementCache? Cache { get; init; }

    /// <summary>日志（审计 / 健康旁挂失败时记 Warning）。</summary>
    public ILogger? Logger { get; init; }

    /// <summary>时间源；null 表示使用系统时钟。</summary>
    public TimeProvider? Clock { get; init; }

    /// <summary>单次判定独立超时（必须为正时长）。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>模型调用额外重试次数（0 表示只尝试一次）。</summary>
    public int ModelRetryLimit { get; init; }
}

/// <summary>
/// 进程内判定缓存（最小实现）：进程内、无持久化、线程安全。
/// <para>键由基类按「算子 | 算子版本 | 指令版本 | 输入指纹」生成。</para>
/// </summary>
public sealed class InMemoryOperatorJudgementCache : IOperatorJudgementCache
{
    private readonly ConcurrentDictionary<string, ModelJudgement> _items = new(StringComparer.Ordinal);

    /// <summary>当前缓存条目数。</summary>
    public int Count => _items.Count;

    /// <inheritdoc />
    public bool TryGet(string key, out ModelJudgement judgement)
    {
        if (_items.TryGetValue(key, out var cached))
        {
            judgement = cached;
            return true;
        }

        judgement = null!;
        return false;
    }

    /// <inheritdoc />
    public void Set(string key, ModelJudgement judgement) => _items[key] = judgement;
}
