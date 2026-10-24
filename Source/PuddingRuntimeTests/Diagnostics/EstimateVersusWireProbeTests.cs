using System.Net;
using System.Text;
using System.Text.Json;
using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Platform.Options;

namespace PuddingRuntimeTests.Diagnostics;

/// <summary>
/// 诊断探针（只写文件、不做门禁）：同一批消息经真实网关序列化后，
/// 比较「我们估算的 token」与「我们自己发出去的报文按同一分词器数出来的 token」。
/// 若两者接近 ⇒ 高估不在计数环节，而在服务端词表；若前者显著更高 ⇒ 我们在数对方收不到的东西。
/// </summary>
[TestClass]
public sealed class EstimateVersusWireProbeTests
{
    [TestMethod]
    public async Task Estimate_And_WireBody_UseTheSameScale()
    {
        // 探针需要和生产同口径：指向真实的词表目录，否则走 Fallback（实测会少算 3.6 倍）。
        const string tokenizerRoot = @"D:\Data\models\tokenizers";
        const string model = "deepseek-v4.1-flash";
        if (Directory.Exists(tokenizerRoot))
            Environment.SetEnvironmentVariable("PUDDING_TOKENIZER_ASSETS", tokenizerRoot);

        var messages = BuildMessages();
        var handler = new CapturingHandler();
        var gateway = new OpenAiLlmGateway(
            new HttpClient(handler),
            new PuddingCode.Platform.Options.LlmOptions("https://provider.example/v1", "test-key", "deepseek-chat", MaxTokens: 64));

        await gateway.ChatAsync(messages, []);

        Assert.AreEqual(1, handler.Bodies.Count, "必须正好发出一次请求");

        var wireBody = handler.Bodies[0];
        var wireTokens = ContextUsageSnapshotStore.CountTokens(wireBody, model);
        // 用生产同一入口（预算守卫走的就是 CaptureLlmRequest），而不是旁路度量。
        var store = new ContextUsageSnapshotStore();
        var estimate = store
            .CaptureLlmRequest("probe-session", messages, tools: null, model,
                LlmCallPurposes.Agent, "inv-probe", "inv-probe:a0")
            .RawEstimatedTokens;

        var ratio = wireTokens == 0 ? 0d : (double)estimate / wireTokens;
        var tokenizer = JsonSerializer.Serialize(ContextUsageSnapshotStore.DescribeTokenization());
        var report =
            $"messages={messages.Count}\n" +
            $"wireChars={wireBody.Length}\n" +
            $"wireTokens={wireTokens}\n" +
            $"estimate={estimate}\n" +
            $"estimateOverWire={ratio:F4}\n" +
            $"tokenizer={tokenizer}\n";

        File.WriteAllText(
            Path.Combine(AppContext.BaseDirectory, "estimate-vs-wire.txt"),
            report,
            new UTF8Encoding(false));

        Assert.IsTrue(estimate > 0);
        Assert.IsTrue(wireTokens > 0);
    }

    private static List<ChatMessage> BuildMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你是鲸鲸，负责代码开发与审阅。回答必须给证据，禁止把假设写成结论。"),
        };

        for (var i = 0; i < 8; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User,
                $"第 {i} 轮：请核对这处改动的归属。我关心的是「谁在什么时候写了这段」，"
                + "而不是它看起来是否合理。证据必须能取红，否则不算校验。"));
            messages.Add(new ChatMessage(ChatRole.Assistant,
                $"收到第 {i} 轮。已核磁盘：工作树干净，AHEAD 计数与上次一致；"
                + "结论是这批改动属于上一个失败的子代理，不应并入本次提交。"));
        }

        for (var i = 0; i < 3; i++)
        {
            messages.Add(new ChatMessage(ChatRole.Tool,
                $"匹配 7 处：LlmOptions.cs:96 / :177 / :221 / :407 / :900 / :902 / :907，"
                + "其中只有一处写入点，其余为读取。"));
        }

        return messages;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
