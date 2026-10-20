using System.Text.Json;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using PuddingBrowser.Protocol;
using PuddingCode.Tools;

namespace PuddingHost.Tests.BrowserBridge;

public sealed class BrowserAgentToolBridgeIntegrationTests
{
    [Fact]
    public async Task BrowserContextTool_TraversesAuthenticatedBridgeAndReturnsDesktopResult()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using var socket = await host.ConnectWebSocketAsync();
        var ack = await BrowserBridgeTestHost.CompleteHelloAsync(socket);
        Assert.True(ack.Accepted);

        var registry = host.Services.GetRequiredService<IPuddingToolRegistry>();
        var tool = registry.GetTool("browser_context");
        Assert.NotNull(tool);

        var invocation = tool!.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "bridge-integration-tool-call",
            ArgumentsJson = """{"action":"list"}""",
            Context = new ToolExecutionContext
            {
                    // §3.1：浏览器工具执行前必须有可信调用者身份（缺即拒，不生成默认身份）。
                    ExecutionIdentity = new PuddingCode.Runtime.RuntimeExecutionIdentity
                    {
                        Kind = PuddingCode.Runtime.RuntimeExecutionKind.ConversationTurn,
                        ConversationId = "conv-bridge",
                        RunId = "run-bridge",
                        TraceId = null
                    },
                WorkspaceId = "default",
                SessionId = "session-bridge",
                AgentInstanceId = "agent-bridge"
            }
        }, CancellationToken.None);

        var commandEnvelope = await BrowserBridgeTestHost.ReceiveEnvelopeAsync(socket);
        Assert.Equal(BrowserBridgeMessageKind.Command, commandEnvelope.Kind);
        var command = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeCommand>(commandEnvelope);
        Assert.Equal(BrowserBridgeCommandNames.ContextList, command.Name);

        await BrowserBridgeTestHost.SendEnvelopeAsync(socket, new BrowserBridgeEnvelope
        {
            MessageId = Guid.NewGuid(),
            CorrelationId = command.OperationId,
            Kind = BrowserBridgeMessageKind.CommandResult,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new BrowserBridgeCommandResult
            {
                OperationId = command.OperationId,
                Success = true,
                Value = JsonSerializer.SerializeToElement(new BrowserContextListDescriptor
                {
                    Contexts =
                    [
                        new BrowserContextDescriptor
                        {
                            ContextId = "ctx-visible",
                            UserDataDirectory = "C:/isolated-browser",
                            PageCount = 0
                        }
                    ]
                }, BrowserBridgeTestJson.Options)
            }, BrowserBridgeTestJson.Options)
        });

        // 迁移后 list 走窄端口 ⇒ 与 Desktop 侧"同形"要求适配器逐层解析实体
        // （context.getInfo → page.list → 每页 page.getInfo）。替身按真实 Desktop 的方式逐条应答；
        // 列表里 0 页 ⇒ 恰好两条后续命令。
        for (var answered = 0; answered < 2; answered++)
        {
            var followUpEnvelope = await BrowserBridgeTestHost.ReceiveEnvelopeAsync(socket);
            var followUpCommand = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeCommand>(followUpEnvelope);
            var followUpValue = followUpCommand.Name == BrowserBridgeCommandNames.PageList
                ? JsonSerializer.SerializeToElement(
                    new BrowserPageListDescriptor { Pages = [] }, BrowserBridgeTestJson.Options)
                : JsonSerializer.SerializeToElement(new BrowserContextDescriptor
                {
                    ContextId = "ctx-visible",
                    UserDataDirectory = "C:/isolated-browser",
                    PageCount = 0
                }, BrowserBridgeTestJson.Options);

            await BrowserBridgeTestHost.SendEnvelopeAsync(socket, new BrowserBridgeEnvelope
            {
                MessageId = Guid.NewGuid(),
                CorrelationId = followUpCommand.OperationId,
                Kind = BrowserBridgeMessageKind.CommandResult,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new BrowserBridgeCommandResult
                {
                    OperationId = followUpCommand.OperationId,
                    Success = true,
                    Value = followUpValue
                }, BrowserBridgeTestJson.Options)
            });
        }

        var result = await invocation;

        Assert.True(result.Success);
        using var output = JsonDocument.Parse(result.Output);
        Assert.True(output.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("ctx-visible",
            output.RootElement.GetProperty("value")[0].GetProperty("contextId").GetString());
    }

    [Fact]
    public async Task BrowserSnapshotTool_TraversesAuthenticatedBridgeAndReturnsVersionedRefs()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using var socket = await host.ConnectWebSocketAsync();
        Assert.True((await BrowserBridgeTestHost.CompleteHelloAsync(socket)).Accepted);
        var tool = host.Services.GetRequiredService<IPuddingToolRegistry>().GetTool("browser_snapshot");
        Assert.NotNull(tool);

        var invocation = tool!.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "bridge-snapshot-call",
            ArgumentsJson = """{"context_id":"ctx-visible","page_id":"page-visible"}""",
            Context = new ToolExecutionContext
            {
                    // §3.1：浏览器工具执行前必须有可信调用者身份（缺即拒，不生成默认身份）。
                    ExecutionIdentity = new PuddingCode.Runtime.RuntimeExecutionIdentity
                    {
                        Kind = PuddingCode.Runtime.RuntimeExecutionKind.ConversationTurn,
                        ConversationId = "conv-bridge",
                        RunId = "run-bridge",
                        TraceId = null
                    },
                WorkspaceId = "default", SessionId = "session-bridge", AgentInstanceId = "agent-bridge"
            }
        }, CancellationToken.None);

        var contextCommand = await ReceiveCommandAsync(socket, BrowserBridgeCommandNames.ContextGetInfo);
        await ReplyAsync(socket, contextCommand, new BrowserContextDescriptor
        {
            ContextId = "ctx-visible", UserDataDirectory = "C:/isolated-browser", PageCount = 1
        });
        var pageCommand = await ReceiveCommandAsync(socket, BrowserBridgeCommandNames.PageGetInfo);
        await ReplyAsync(socket, pageCommand, Page());
        var snapshotCommand = await ReceiveCommandAsync(socket, BrowserBridgeCommandNames.PageSnapshot);
        await ReplyAsync(socket, snapshotCommand, new BrowserSnapshotDescriptor
        {
            AccessibilityTree = "button ref=v4-n1 name=\"Save\"",
            NodeCount = 1,
            PageVersion = 4
        });

        var result = await invocation;
        Assert.True(result.Success);
        Assert.Contains("v4-n1", result.Output, StringComparison.Ordinal);
    }

    private static async Task<BrowserBridgeCommand> ReceiveCommandAsync(WebSocket socket, string expectedName)
    {
        var envelope = await BrowserBridgeTestHost.ReceiveEnvelopeAsync(socket);
        var command = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeCommand>(envelope);
        Assert.Equal(expectedName, command.Name);
        return command;
    }

    private static Task ReplyAsync(WebSocket socket, BrowserBridgeCommand command, object value)
        => BrowserBridgeTestHost.SendEnvelopeAsync(socket, new BrowserBridgeEnvelope
        {
            MessageId = Guid.NewGuid(),
            CorrelationId = command.OperationId,
            Kind = BrowserBridgeMessageKind.CommandResult,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new BrowserBridgeCommandResult
            {
                OperationId = command.OperationId,
                Success = true,
                Value = JsonSerializer.SerializeToElement(value, BrowserBridgeTestJson.Options)
            }, BrowserBridgeTestJson.Options)
        });

    [Fact]
    public async Task PreviewDocumentTool_IsDiscoveredAndFailsHonestlyWithoutCapabilityChannel()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();

        // 先建立**旧 Bridge** 连接：这样调用上下文工厂能返回 call context，
        // 请求会真正走到文档表面（否则工具会在“没有 Desktop”处提前失败，
        // 根本测不到“有 Desktop、但没有能力通道”这个分支）。
        using var socket = await host.ConnectWebSocketAsync();
        Assert.True((await BrowserBridgeTestHost.CompleteHelloAsync(socket)).Accepted);

        // 能拿到工具就同时证明两件事：① [Tool] 扫描发现了它；② 它的两个依赖
        // （IDesktopDocumentCapabilitySurface / 调用上下文工厂）在宿主里可解析。
        var tool = host.Services.GetRequiredService<IPuddingToolRegistry>().GetTool("preview_document");
        Assert.NotNull(tool);

        // Bridge 已连、能力通道未启用 ⇒ 文档预览它没能力做（旧 Bridge 从未有 document.preview 命令）。
        // 必须如实报不可用，**不得**回退到 Bridge 假装成功。
        var result = await tool!.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "preview-document-without-channel",
            ArgumentsJson = """{"path":"Docs/a.md"}""",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "default",
                SessionId = "session-preview",
                AgentInstanceId = "agent-preview",
            },
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("capability channel", result.Error!, StringComparison.Ordinal);
    }

    private static BrowserPageDescriptor Page() => new()
    {
        ContextId = "ctx-visible",
        PageId = "page-visible",
        Title = "Test",
        Url = "https://test.local/",
        PageVersion = 4
    };
}
