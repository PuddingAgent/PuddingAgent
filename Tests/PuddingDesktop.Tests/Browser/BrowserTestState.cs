using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

/// <summary>
/// Shell 侧测试的共享夹具：**唯一**权威实例与生命周期端口。
///
/// 为什么测试也要统一来源：控制权状态在 Shell 侧曾有"每个闸门一个 bool"的历史
/// （审计 2026-10-08 结论 #1），测试若各自 new 一个调度器/状态，就再次允许那种结构存在。
///
/// 2026-10-12 复核后补：**成功用例必须显式授权**。R3 要求 Bridge 页面命令在**无页面授权**时
/// 于 handler 之前被拒；因此"期望执行成功"的用例不能再依赖"闸门缺失"来通过。
/// </summary>
internal static class BrowserTestState
{
    internal static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(33);

    /// <summary>测试用的可信 Core 会话 owner（由桥命令 Origin 注入，模型不能自报）。</summary>
    internal const string OwnerId = "session-agent-a";

    internal static BrowserPageLifecycle CreateLifecycle() =>
        BrowserPageLifecycle.Create(
            new DesktopInstanceId("desk-test"),
            new DesktopProcessInstanceId("proc-test"));

    internal static BrowserBridgeCommandDispatcher CreateDispatcher() => new(CreateLifecycle());

    /// <summary>桥命令的可信 Origin（生产由 Host 从 Core 执行上下文投影，模型不可填写）。</summary>
    internal static BrowserBridgeCommandOrigin Origin(string? ownerId = null) => new()
    {
        WorkspaceId = "workspace-test",
        AgentInstanceId = "agent-a",
        SessionId = ownerId ?? OwnerId,
        ConversationId = ownerId ?? OwnerId,
        RunId = "run-agent-a",
        ToolCallId = Guid.NewGuid().ToString("N"),
        ToolName = "browser_test",
    };

    /// <summary>
    /// 用户交页夹具（ADR-097 D3）：登记目标页并记录“待交页”。归属由第一条带可信 Origin 的
    /// 命令确认（<c>TryClaimControlOwner</c>），因此这里**不**直接创建会话。
    /// </summary>
    internal static void HandOver(
        BrowserPageLifecycle lifecycle,
        string contextId = "ctx",
        string pageId = "page")
    {
        lifecycle.NotifyBridgeConnected(Generation.Value);
        lifecycle.RegisterContext(contextId, DesktopContextTrust.AgentAuthorized);
        lifecycle.RegisterPage(
            new DesktopPageTarget(contextId, pageId),
            DesktopPageVersion.Require(1),
            DesktopPageReadiness.Interactive);
        lifecycle.SetAgentTarget(contextId, pageId);
    }

    /// <summary>
    /// 为"已合法授权"的用例签发页面授权（Read|Write，5 分钟）。
    /// 这是**正向**夹具：它证明闸门打开时合法调用仍能成功，而不是靠"一律拒绝"取绿。
    /// </summary>
    internal static BrowserPageGrantId GrantAgentPage(
        BrowserPageLifecycle lifecycle,
        string callerId = "agent-a",
        string taskId = "run-agent-a",
        string contextId = "ctx",
        string pageId = "page")
    {
        lifecycle.Authority.NotifyConnectionGeneration(Generation);

        var now = DateTimeOffset.UtcNow;
        var grantId = new BrowserPageGrantId($"grant-{callerId}-{contextId}-{pageId}");
        lifecycle.Authority.IssueGrant(new BrowserPageGrant(
            grantId,
            new BrowserPageGrantBinding(
                new DesktopInstanceId("desk-test"), new DesktopProcessInstanceId("proc-test"), Generation),
            new DesktopPageTarget(contextId, pageId),
            new BrowserCallerIdentity(BrowserCallerKind.Agent, callerId, taskId),
            BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
            epoch: default,
            now,
            expiresAtUtc: now.AddMinutes(5)));

        return grantId;
    }
}
