
using System.Net.Http;
using System.Text;
using PuddingCode.Observability;

/// <summary>
/// 捕获日志必须同时给出「估算值」和「报文实际 token」——这是线上唯一能同时看到两侧的地方，
/// 少了后者就只剩一句「可能有偏差」。
/// </summary>
[TestClass]
public sealed class LlmRequestCaptureLoggingTests
{
    [TestMethod]
    public async Task WriteAsync_LogsBodyTokensAlongsideTheEstimate()
    {
        var logger = new CapturingLogger();
        using var capture = new LlmRequestCapture("inv-log", 849, logger);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/chat")
        {
            Content = new StringContent("{\"messages\":[{\"role\":\"user\",\"content\":\"你好，世界\"}]}",
                Encoding.UTF8, "application/json"),
        };

        await LlmRequestCapture.WriteAsync(request, "deepseek-v4.1-flash", CancellationToken.None);

        Assert.AreEqual(1, logger.Entries.Count, "必须正好写一条捕获日志");
        var fields = logger.Entries[0].ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.AreEqual("inv-log", fields["invocationId"]);
        Assert.AreEqual(849, fields["localEstimatedTokens"]);
        Assert.IsTrue(fields.ContainsKey("bodyTokens"), "日志必须带 bodyTokens");
        Assert.IsTrue(fields["bodyTokens"] is int tokens && tokens > 0, "bodyTokens 必须是正整数");
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<IReadOnlyList<KeyValuePair<string, object?>>> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
                Entries.Add(values);
        }
    }
}
