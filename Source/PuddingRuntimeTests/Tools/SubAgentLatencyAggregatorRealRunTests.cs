using System.Globalization;
using System.Text.Json;
using PuddingCode.SubAgents;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 2026-11-16：用真实 run 的 events.jsonl 校验聚合器的配对与分位。
///
/// <para>
/// 断言方式不是「等于我自己算的那个数」这种复述，而是与**本测试内独立实现**逐项对齐：
/// 调用次数、LLM 累计耗时、token 与缓存四分量。若配对逻辑坏掉（例如丢了 started），
/// 两项会同时偏移，断言立刻取红。分位另有单调性约束 P50 ≤ P95 ≤ max。
/// </para>
///
/// <para>
/// 需要 <c>PUDDING_USAGE_PROBE_EVENTS</c> 指向一份真实事件文件；未设置时 Inconclusive，
/// 不把本地数据缺失变成红灯。
/// </para>
/// </summary>
[TestClass]
public sealed class SubAgentLatencyAggregatorRealRunTests
{
    [TestMethod]
    public void Real_Run_Matches_An_Independent_Aggregation()
    {
        var path = Environment.GetEnvironmentVariable("PUDDING_USAGE_PROBE_EVENTS");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Assert.Inconclusive("set PUDDING_USAGE_PROBE_EVENTS to a real events.jsonl to run this probe");
            return;
        }

        var lines = File.ReadAllLines(path);

        var aggregator = new SubAgentLatencyAggregator();
        foreach (var line in lines)
            aggregator.AddEventLine(line);
        var actual = aggregator.Build("probe");

        // 独立 oracle：只在本测试内使用，不参与产品代码，也不作为第二套对外口径。
        var calls = 0;
        long llmMs = 0;
        long? started = null;
        long prompt = 0, completion = 0, hit = 0, miss = 0, total = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("eventType", out var typeElement)
                || !root.TryGetProperty("timestamp", out var stampElement))
                continue;

            var type = typeElement.GetString();
            if (!DateTimeOffset.TryParse(
                    stampElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var at))
                continue;

            var epochMs = at.ToUnixTimeMilliseconds();
            if (type == "subagent.llm.started")
            {
                started = epochMs;
            }
            else if (type == "subagent.llm.completed")
            {
                if (started is { } s)
                {
                    llmMs += Math.Max(0, epochMs - s);
                    calls++;
                    started = null;
                }

                if (root.TryGetProperty("payload", out var payload))
                {
                    prompt += ReadLong(payload, "prompt_tokens");
                    completion += ReadLong(payload, "completion_tokens");
                    hit += ReadLong(payload, "cache_hit_tokens");
                    miss += ReadLong(payload, "cache_miss_tokens");
                    total += ReadLong(payload, "total_tokens");
                }
            }
        }

        Assert.AreEqual(calls, actual.LlmCallCount, "LLM 调用次数必须与独立计数一致");
        Assert.AreEqual(llmMs, actual.LlmDurationMs, "LLM 累计耗时必须与独立配对一致");
        Assert.AreEqual(prompt, actual.PromptTokens, "prompt token 合计不一致");
        Assert.AreEqual(completion, actual.CompletionTokens, "completion token 合计不一致");
        Assert.AreEqual(hit, actual.CacheHitTokens, "缓存命中合计不一致");
        Assert.AreEqual(miss, actual.CacheMissTokens, "缓存未命中合计不一致");
        Assert.AreEqual(total, actual.TotalTokens, "total token 合计不一致");
        Assert.IsTrue(actual.LlmP50Ms <= actual.LlmP95Ms, "P50 不得大于 P95");
        Assert.IsTrue(actual.LlmP95Ms <= actual.LlmMaxMs, "P95 不得大于最大值");
        Assert.IsTrue(actual.CacheHitPct is >= 0 and <= 100, "命中率必须落在 [0,100]");

        Console.WriteLine(
            $"PROBE calls={actual.LlmCallCount} llm_ms={actual.LlmDurationMs} " +
            $"max={actual.LlmMaxMs} p50={actual.LlmP50Ms} p95={actual.LlmP95Ms} " +
            $"cache_pct={actual.CacheHitPct} prompt={actual.PromptTokens} total={actual.TotalTokens}");
    }

    private static long ReadLong(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0L;
}
