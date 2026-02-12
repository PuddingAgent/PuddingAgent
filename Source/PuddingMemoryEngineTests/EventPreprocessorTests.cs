using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingRuntime.Services.Events;

namespace PuddingMemoryEngineTests;

[TestClass]
public sealed class EventPreprocessorTests
{
    [TestMethod]
    public async Task ProcessAsync_DifferentPayloadsFromSameSourceWithinDedupWindow_AreNotDropped()
    {
        var preprocessor = new EventPreprocessor(NullLogger<EventPreprocessor>.Instance);
        var first = CreateMqttEvent("打开空调");
        var second = CreateMqttEvent("打开加湿器");

        var firstBatch = await preprocessor.ProcessAsync([first]);
        var secondBatch = await preprocessor.ProcessAsync([second]);

        Assert.HasCount(1, firstBatch);
        Assert.HasCount(1, secondBatch);
        Assert.AreEqual("打开空调", ReadMessageText(firstBatch[0].Payload));
        Assert.AreEqual("打开加湿器", ReadMessageText(secondBatch[0].Payload));
    }

    [TestMethod]
    public async Task ProcessAsync_ExactDuplicateFromSameSourceWithinDedupWindow_IsDropped()
    {
        var preprocessor = new EventPreprocessor(NullLogger<EventPreprocessor>.Instance);
        var first = CreateMqttEvent("打开空调");
        var duplicate = first with { RawEventId = Guid.NewGuid().ToString("N") };

        var firstBatch = await preprocessor.ProcessAsync([first]);
        var duplicateBatch = await preprocessor.ProcessAsync([duplicate]);

        Assert.HasCount(1, firstBatch);
        Assert.IsEmpty(duplicateBatch);
    }

    private static RawEvent CreateMqttEvent(string messageText)
    {
        return new RawEvent
        {
            RawEventId = Guid.NewGuid().ToString("N"),
            Type = "connector.mqtt.command",
            Source = new EventSource
            {
                SourceType = "mqtt",
                SourceId = "home/living-room/command",
                ConnectorId = "mqtt-001",
            },
            WorkspaceId = "default",
            SessionId = "mqtt-session",
            Payload = JsonSerializer.SerializeToElement(new { messageText }),
        };
    }

    private static string? ReadMessageText(object? payload)
    {
        return payload is JsonElement element && element.TryGetProperty("messageText", out var value)
            ? value.GetString()
            : null;
    }
}
