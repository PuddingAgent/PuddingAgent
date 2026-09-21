namespace PuddingCode.Operators;

/// <summary>
/// 逐标签验收门槛的解析端口（判据外置：消费点不得自行决定门槛数值，否则事后无法回答
/// 「为什么放行」，且改进候选可悄悄移动判据）。
/// <para>
/// 契约：
/// <list type="bullet">
/// <item>按<b>稳定 id</b> 解析判据；</item>
/// <item><b>未配置 ⇒ 返回内置默认</b>，且该默认必须等于该消费点的既有常量——
/// 这样「不配置 = 行为逐位不变」是可验证事实，而不是承诺；</item>
/// <item>id 解析不到必须 <b>fail-closed</b>（抛错），绝不返回猜出来的默认值：
/// 静默兜底会让「门槛没生效」变成不可发现的事故。</item>
/// </list>
/// </para>
/// </summary>
public interface IAcceptanceThresholdPolicyProvider
{
    /// <summary>按稳定 id 解析判据；未配置 ⇒ 返回内置默认（等于既有常量）。</summary>
    /// <param name="policyId">判据稳定 id。</param>
    /// <exception cref="KeyNotFoundException">id 未注册（fail-closed，不猜测默认值）。</exception>
    AcceptanceThresholdPolicy Resolve(string policyId);
}
