using PuddingBrowser.Abstractions;
using PuddingBrowser.AgentTools;
using PuddingBrowser.Protocol;
using Pudding.Contracts.Desktop;
using PuddingHost.BrowserBridge;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>
/// §3.2（Bridge 传输）：Core 侧把**可信调用者身份**随每条命令下发；
/// 缺身份/冒充 User/无运行身份时**不发送**（fail-closed），也不给默认身份。
/// Desktop 侧还有一道独立校验（见 DesktopBrowserBridgeClient）。
/// </summary>
public sealed class RemoteBrowserRuntimeCallerTests
{
    [Fact]
    public async Task EveryBridgeCommandCarriesTheTrustedCaller()
    {
        var broker = new RecordingBroker();
        using var scope = BrowserCallerIdentityScope.Push(
            new BrowserCallerIdentity(BrowserCallerKind.SubAgent, "agent-1", "run-sub", "run-parent"),
            null);

        await using var runtime = new RemoteBrowserRuntime(broker, callerIdentity: new BrowserCallerIdentityAccessor());
        await runtime.ListContextsAsync(CancellationToken.None);

        var command = Assert.Single(broker.Commands);
        Assert.NotNull(command.Caller);
        Assert.Equal("sub_agent", command.Caller!.Kind);
        Assert.Equal("agent-1", command.Caller.CallerId);
        Assert.Equal("run-sub", command.Caller.TaskId);
        Assert.Equal("run-parent", command.Caller.ParentTaskId);
    }

    [Fact]
    public async Task WithoutATrustedCallerTheCommandIsNotSentAtAll()
    {
        var broker = new RecordingBroker();
        // 作用域里没有身份（投影失败的情形）：命令**不得**被发出。
        using var scope = BrowserCallerIdentityScope.Push(null, "no trusted run identity");

        await using var runtime = new RemoteBrowserRuntime(broker, callerIdentity: new BrowserCallerIdentityAccessor());
        var ex = await Assert.ThrowsAsync<BrowserOperationException>(
            () => runtime.ListContextsAsync(CancellationToken.None));

        Assert.Equal(BrowserBridgeErrorCodes.BrowserUnauthorized, ex.Code);
        Assert.Contains("no trusted", ex.Message, StringComparison.Ordinal);
        Assert.Empty(broker.Commands);
    }

    [Fact]
    public async Task AUserCallerCannotBeUsedToBypassTheGate()
    {
        var broker = new RecordingBroker();
        using var scope = BrowserCallerIdentityScope.Push(
            new BrowserCallerIdentity(BrowserCallerKind.User, "desktop-ui", "run-1"),
            null);

        await using var runtime = new RemoteBrowserRuntime(broker, callerIdentity: new BrowserCallerIdentityAccessor());
        var ex = await Assert.ThrowsAsync<BrowserOperationException>(
            () => runtime.ListContextsAsync(CancellationToken.None));

        Assert.Equal(BrowserBridgeErrorCodes.BrowserUnauthorized, ex.Code);
        Assert.Contains("User caller", ex.Message, StringComparison.Ordinal);
        Assert.Empty(broker.Commands);
    }

    private sealed class RecordingBroker : IDesktopBrowserCommandBroker
    {
        public List<BrowserBridgeCommand> Commands { get; } = [];

        public bool IsDesktopConnected => true;

        public Task<BrowserBridgeCommandResult> ExecuteAsync(BrowserBridgeCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(new BrowserBridgeCommandResult
            {
                OperationId = command.OperationId,
                Success = true,
                Value = System.Text.Json.JsonSerializer.SerializeToElement(new BrowserContextListDescriptor { Contexts = [] }),
            });
        }

        public Task CancelAsync(Guid operationId, CancellationToken cancellationToken) => Task.CompletedTask;

        public void HandleResult(Guid connectionId, long generation, BrowserBridgeCommandResult result) { }

        public void FailPendingForConnection(Guid connectionId, long generation, string errorCode, string message) { }
    }
}
