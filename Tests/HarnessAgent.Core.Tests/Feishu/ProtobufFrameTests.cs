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

    [TestMethod]
    public void FeishuPostEvent_DirectContent_ConvertsToMarkdown()
    {
        var evt = new FeishuEvent
        {
            Event = new FeishuEventV2
            {
                Message = new FeishuMessageEvent
                {
                    MessageType = "post",
                    Content =
                        """
                        {
                          "title": "开发计划",
                          "content": [
                            [{"tag":"text","text":"旧格式内容"}]
                          ],
                          "content_v2": [
                            [{"tag":"text","text":"核心文件："}],
                            [
                              {"tag":"text","text":"- "},
                              {
                                "tag":"a",
                                "href":"/E:/repo/ADR-066.md",
                                "text":"ADR-066"
                              }
                            ],
                            [],
                            [
                              {
                                "tag":"at",
                                "user_id":"ou_user",
                                "user_name":"小明"
                              },
                              {"tag":"text","text":" 请开始"}
                            ],
                            [
                              {
                                "tag":"code_block",
                                "language":"csharp",
                                "text":"var ready = true;"
                              }
                            ]
                          ]
                        }
                        """,
                },
            },
        };

        var markdown = evt.ExtractText();

        Assert.AreEqual(
            "# 开发计划\n\n"
            + "核心文件：\n"
            + "- [ADR-066](/E:/repo/ADR-066.md)\n\n"
            + "@小明 请开始\n"
            + "```csharp\nvar ready = true;\n```",
            markdown);
        Assert.DoesNotContain("旧格式内容", markdown);
        Assert.DoesNotContain("[post]", markdown);
    }

    [TestMethod]
    public void FeishuPostEvent_LocalizedContent_UsesReadableTextFallback()
    {
        var evt = new FeishuEvent
        {
            Event = new FeishuEventV2
            {
                Message = new FeishuMessageEvent
                {
                    MessageType = "post",
                    Content =
                        """
                        {
                          "zh_cn": {
                            "title": "",
                            "content": [
                              [
                                {
                                  "tag":"future_container",
                                  "children":[
                                    {
                                      "tag":"future_tag",
                                      "text":"至少降维成文本"
                                    }
                                  ]
                                }
                              ]
                            ]
                          }
                        }
                        """,
                },
            },
        };

        Assert.AreEqual("至少降维成文本", evt.ExtractText());
    }

    [TestMethod]
    public void FeishuPostEvent_MalformedContent_DoesNotReturnTypePlaceholder()
    {
        var evt = new FeishuEvent
        {
            Event = new FeishuEventV2
            {
                Message = new FeishuMessageEvent
                {
                    MessageType = "post",
                    Content = "{not-json",
                },
            },
        };

        var text = evt.ExtractText();

        Assert.AreEqual(
            "用户从飞书发送了一条富文本消息，但其中没有可提取的文本内容。",
            text);
        Assert.AreNotEqual("[post]", text);
    }

    [TestMethod]
    public void FeishuPostEvent_MixedTextAndImages_ExtractsImageKeysInOrder()
    {
        var evt = new FeishuEvent
        {
            Event = new FeishuEventV2
            {
                Message = new FeishuMessageEvent
                {
                    MessageType = "post",
                    Content =
                        """
                        {
                          "title": "",
                          "content_v2": [
                            [{"tag":"text","text":"第一段文字"}],
                            [
                              {"tag":"img","image_key":"img_v2_first","width":100,"height":100},
                              {"tag":"text","text":"看图："}
                            ],
                            [
                              {"tag":"text","text":"第二张："},
                              {"tag":"img","image_key":"img_v2_second","width":200,"height":200}
                            ]
                          ]
                        }
                        """,
                },
            },
        };

        var keys = evt.ExtractPostImageKeys();

        CollectionAssert.AreEqual(
            new List<string> { "img_v2_first", "img_v2_second" },
            keys);
        // Text extraction must keep working (占位符可保留).
        StringAssert.Contains(evt.ExtractText(), "第一段文字");
        StringAssert.Contains(evt.ExtractText(), "[图片]");
    }

    [TestMethod]
    public void FeishuPostEvent_LegacyContentArray_ExtractsImageKeysInOrder()
    {
        var evt = new FeishuEvent
        {
            Event = new FeishuEventV2
            {
                Message = new FeishuMessageEvent
                {
                    MessageType = "post",
                    Content =
                        """
                        {
                          "title": "旧格式",
                          "content": [
                            [{"tag":"img","image_key":"img_legacy_1"}],
                            [{"tag":"img","image_key":"img_legacy_2"}]
                          ]
                        }
                        """,
                },
            },
        };

        var keys = evt.ExtractPostImageKeys();

        CollectionAssert.AreEqual(
            new List<string> { "img_legacy_1", "img_legacy_2" },
            keys);
    }

    [TestMethod]
    public void FeishuPostEvent_WithoutImages_ReturnsEmptyList()
    {
        var evt = new FeishuEvent
        {
            Event = new FeishuEventV2
            {
                Message = new FeishuMessageEvent
                {
                    MessageType = "post",
                    Content =
                        """
                        {
                          "title": "",
                          "content_v2": [
                            [{"tag":"text","text":"只有文字"}]
                          ]
                        }
                        """,
                },
            },
        };

        Assert.IsEmpty(evt.ExtractPostImageKeys());
    }

    [TestMethod]
    public void FeishuImageEvent_ExtractPostImageKeys_ReturnsEmptyList()
    {
        var evt = new FeishuEvent
        {
            Event = new FeishuEventV2
            {
                Message = new FeishuMessageEvent
                {
                    MessageType = "image",
                    Content = "{\"image_key\":\"img_v3_test\"}",
                },
            },
        };

        Assert.IsEmpty(evt.ExtractPostImageKeys());
    }
}
