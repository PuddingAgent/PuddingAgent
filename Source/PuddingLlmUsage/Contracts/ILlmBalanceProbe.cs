namespace PuddingLlmUsage.Contracts;

/// <summary>
/// 余额采样端口：把「去哪查、怎么查」挡在接口后面，让消耗分析只看见
/// <see cref="LlmBalanceObservation"/>。
/// <para>
/// <b>为什么不抛异常</b>：采样失败本身是要被记录的<b>事实</b>（见
/// <see cref="LlmBalanceObservation.Failure"/>）。若端口用异常表达失败，调用方要么吞掉（信息丢失），
/// 要么让整个采样循环中断 —— 两种都会在曲线上留下「看起来没变化」的假象。
/// 因此约定：<b>凡是能描述成一次观测的失败，一律返回 Failure 观测；只有取消才抛。</b>
/// </para>
/// <para>
/// 一个 provider 可能同时有多币种，所以返回值是列表：每个币种一条观测。
/// 币种集合为空也返回一条 Failure（不能返回空列表让调用方误以为「这个 provider 没有余额」）。
/// </para>
/// </summary>
public interface ILlmBalanceProbe
{
    /// <summary>
    /// 查询指定 provider 的余额，并打上 <paramref name="observedAtUtc"/> 时间戳。
    /// </summary>
    /// <param name="providerId">服务商标识（与 <c>llm.providers.json</c> 一致）。</param>
    /// <param name="observedAtUtc">观测时刻（UTC），由调用方给，端口不自己读时钟。</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 被取消。</exception>
    Task<IReadOnlyList<LlmBalanceObservation>> QueryAsync(
        string providerId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);
}
