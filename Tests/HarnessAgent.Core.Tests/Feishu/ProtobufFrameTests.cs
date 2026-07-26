using System.Text;
using System.Text.Json;
using HarnessAgent.Core.Connectors.Feishu;

namespace HarnessAgent.Core.Tests.Feishu;

[TestClass]
public sealed class ProtobufFrameTests
{
    [TestMethod]
    public void EncodeParse_RoundTripsOfficialPbbp2Fields()
    {
        var original = new ProtobufFrame
        {
            SeqId = 9,
            LogId = 10,
            Service = 100,
            Method = ProtobufFrame.Data,
            Headers = new Dictionary<string, string>
            {
                ["type"] = "event",
                ["message_id"] = "message-1",
                ["sum"] = "1",
                ["seq"] = "0",
            },
            PayloadEncoding = "json",
            PayloadType = "application/json",
            Payload = Encoding.UTF8.GetBytes("""{"event":"ok"}"""),
            LogIdNew = "log-new",
        };

        var parsed = ProtobufFrame.Parse(original.Encode());

        Assert.AreEqual(original.SeqId, parsed.SeqId);
        Assert.AreEqual(original.LogId, parsed.LogId);
        Assert.AreEqual(original.Service, parsed.Service);
        Assert.AreEqual(ProtobufFrame.Data, parsed.Method);
        Assert.AreEqual("event", parsed.GetHeader("type"));
        Assert.AreEqual("message-1", parsed.GetHeader("message_id"));
        Assert.AreEqual("json", parsed.PayloadEncoding);
        Assert.AreEqual("application/json", parsed.PayloadType);
        Assert.AreEqual("""{"event":"ok"}""", Encoding.UTF8.GetString(parsed.Payload));
        Assert.AreEqual("log-new", parsed.LogIdNew);
    }

    [TestMethod]
    public void NewPing_UsesOfficialControlMethodAndTypeHeader()
    {
        var ping = ProtobufFrame.Parse(ProtobufFrame.NewPing(42).Encode());

        Assert.AreEqual(42, ping.Service);
        Assert.AreEqual(0, ping.Method);
        Assert.AreEqual("ping", ping.GetHeader("type"));
    }

    [TestMethod]
    public void FeishuEvent_SnakeCasePayload_MapsMessageIdentityAndText()
    {
        const string json =
            """
            {
              "schema": "2.0",
              "header": {
                "event_id": "evt_1",
                "event_type": "im.message.receive_v1"
              },
              "event": {
                "sender": {
                  "sender_id": {
                    "open_id": "ou_sender"
                  }
                },
                "message": {
                  "message_id": "om_message",
                  "chat_id": "oc_chat",
                  "message_type": "text",
                  "content": "{\"text\":\"hello pudding\"}"
                }
              }
            }
            """;
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };

        var evt = JsonSerializer.Deserialize<FeishuEvent>(json, options);

        Assert.IsNotNull(evt);
        Assert.AreEqual("om_message", evt!.ExtractMessageId());
        Assert.AreEqual("oc_chat", evt.ExtractChatId());
        Assert.AreEqual("ou_sender", evt.ExtractSenderId());
        Assert.AreEqual("hello pudding", evt.ExtractText());
    }

    [TestMethod]
    public void FeishuImageEvent_SnakeCasePayload_ExtractsImageKey()
    {
        const string json =
            """
            {
              "schema": "2.0",
              "header": {
                "event_id": "evt_image",
                "event_type": "im.message.receive_v1"
              },
              "event": {
                "sender": { "sender_id": { "open_id": "ou_sender" } },
                "message": {
                  "message_id": "om_image",
                  "chat_id": "oc_chat",
                  "message_type": "image",
                  "content": "{\"image_key\":\"img_v3_test\"}"
                }
              }
            }
            """;
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };

        var evt = JsonSerializer.Deserialize<FeishuEvent>(json, options);

        Assert.IsNotNull(evt);
        Assert.AreEqual("img_v3_test", evt!.ExtractImageKey());
        Assert.AreEqual("[image]", evt.ExtractText());
    }
}
