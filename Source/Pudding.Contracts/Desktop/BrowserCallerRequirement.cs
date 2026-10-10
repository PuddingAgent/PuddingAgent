namespace Pudding.Contracts.Desktop;

/// <summary>
/// **浏览器自动化操作对调用者身份的统一要求**（方案 §3.2）。
///
/// 两条传输（能力通道 gRPC / Bridge WebSocket）与 Desktop 侧准入都消费**这一份**规则，
/// 避免出现"两条路径各自验出相反结论"（设计 §1.1）。
///
/// 规则（缺一即拒，绝不回退默认身份）：
/// <list type="bullet">
///   <item>没有身份 ⇒ 拒（浏览器操作不接受匿名调用）；</item>
///   <item><see cref="BrowserCallerKind.User"/> ⇒ 拒（用户原生操作走独立可信入口，工具不得冒充 User 绕过闸门）；</item>
///   <item>没有运行身份（<c>TaskId</c>）⇒ 拒（授权必须绑定到一次具体运行才可撤销/回收）。</item>
/// </list>
/// </summary>
public static class BrowserCallerRequirement
{
    public static bool TryRequireCaller(
        BrowserCallerIdentity? identity,
        out BrowserCallerIdentity caller,
        out string reason)
    {
        caller = identity!;

        if (identity is null)
        {
            caller = null!;
            reason = "the call carried no trusted browser caller identity";
            return false;
        }

        if (identity.Kind == BrowserCallerKind.User)
        {
            // §3.2：浏览器工具**不能**构造 User kind 绕过自动化闸门。
            caller = null!;
            reason = "a browser automation operation cannot be admitted as a User caller";
            return false;
        }

        if (string.IsNullOrWhiteSpace(identity.TaskId))
        {
            caller = null!;
            reason = "the browser caller carries no run identity; the authorization cannot be bound to a task";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
