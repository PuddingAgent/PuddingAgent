using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 方案 §4.2 的**账本命名空间**：`BrowserOperationLedgerKey` = (桌面进程实例, **连接世代**, operationId)。
/// 世代必须**进键** —— "重连后的同 ID 落在新的命名空间，不会被旧世代的记录误判为重复"。
/// 背景（真实缺陷）：Bridge 派发器曾把这两项写成**常量**（"bridge", 0）⇒ 重连后同 ID 会命中旧世代记录。
/// 该用例钉住"命名空间随进程实例与当前世代变化"，从而保证派发器能取到**真实**值。
/// </summary>
public sealed class BrowserLedgerNamespaceTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-1");
    private static readonly DesktopProcessInstanceId ProcessId = new("proc-1");

    [Fact]
    public void NamespaceCarriesTheProcessInstanceAndFollowsTheConnectionGeneration()
    {
        var lifecycle = BrowserPageLifecycle.Create(DesktopId, ProcessId);
        var initial = lifecycle.LedgerNamespace;

        Assert.Equal(ProcessId.Value, initial.ProcessInstance);

        lifecycle.Authority.NotifyConnectionGeneration(ConnectionGeneration.Require(7));
        var afterReconnect = lifecycle.LedgerNamespace;

        Assert.Equal(7, afterReconnect.Generation);
        Assert.NotEqual(initial.Generation, afterReconnect.Generation);
        // 进程实例不因重连改变（同一进程），但世代必须变 ⇒ 同 operationId 的两代不会互相误判。
        Assert.Equal(initial.ProcessInstance, afterReconnect.ProcessInstance);
    }
}