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
                            PageCount = 2
                        }
                    ]
                }, BrowserBridgeTestJson.Options)
            }, BrowserBridgeTestJson.Options)
        });

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

    private static BrowserPageDescriptor Page() => new()
    {
        ContextId = "ctx-visible",
        PageId = "page-visible",
        Title = "Test",
        Url = "https://test.local/",
        PageVersion = 4
    };
}
