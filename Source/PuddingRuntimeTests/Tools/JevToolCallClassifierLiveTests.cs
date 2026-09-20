using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Classification;
using PuddingCode.Configuration;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <b>真实联网</b>测试（切片 S3c-2）：用真实 Jev（资源池 → JevDecisionOptionsProvider →
/// JevDecisionService → https://jevtypesafeai.com/api/v1/decide）驱动生产
/// <see cref="JevToolCallClassifier"/>，证明「真实返回的 answers / choice / noul 形状」
/// 能被解析器（ParseVerdict / ParseOutcome / TryGetConfidence）解析——
/// 排除「桩返回 JSON 与解析器互为口味」的自洽幻觉风险（桩口径下若真实字段/取值不一致，
/// 解析器会永远返回 Unknown，生产翻转默认值后所有非规则覆盖审批都会 deferred）。
/// <para>
/// 与 <see cref="JevDecisionLiveTests"/> 同款 gating：<c>[TestCategory("Live")]</c>、
/// 资源池路径解析（尊重 <c>JEV_LIVE_POOL_PATH</c>）、无密钥 <see cref="Assert.Inconclusive"/> 跳过、
/// 只发 1 次请求（一次 round trip、五问并行、共享 state 成本）。
/// 网络类失败（classifier.jev.unavailable）同样跳过而非失败。
/// </para>
/// <para>
/// 本测试是<b>只读探针</b>：无论结论如何都不修改生产代码；解析不一致时打印
/// 「解析器需要接受的字面量 / 字段路径」事实清单，供后续切片裁决。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class JevToolCallClassifierLiveTests
{
    /// <summary>显式指定资源池文件路径（优先于所有猜测路径）。</summary>
    private const string PoolPathEnvironmentVariable = "JEV_LIVE_POOL_PATH";

    /// <summary>资源池相对 data 根目录的路径。</summary>
    private const string PoolRelativePath = @"config\llm.providers.json";

    private const string ExpectedBaseUrl = "https://jevtypesafeai.com";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>解析器（ParseOutcome）接受的四选一字面量（trim + 小写后比较）。</summary>
    private static readonly string[] ParserAcceptedChoices =
    [
        "allow_once", "allow_permanent", "deny_once", "deny_permanent",
    ];

    /// <summary>解析器（TryGetConfidence）期望的四个逐分类可信度问题键。</summary>
    private static readonly string[] ParserConfidenceKeys =
    [
        "confidence.allow_once",
        "confidence.allow_permanent",
        "confidence.deny_once",
        "confidence.deny_permanent",
    ];

    [TestMethod]
    public async Task ClassifyAsync_RealJevLive_GitPushOriginMaster_ParsesVerdictNotUnknown()
    {
        // ── ① 定位资源池文件；缺失则跳过（无密钥环境不假失败） ──
        var poolPath = ResolvePoolPath();
        Console.WriteLine(
            $"[S3c-2 live][gate] poolPath = {poolPath ?? "<null：将 Inconclusive>"}");
        if (poolPath is null)
        {
            Assert.Inconclusive(
                $"未找到资源池文件（可设 {PoolPathEnvironmentVariable} 指定路径），跳过联网测试。");
            return;
        }

        var json = await File.ReadAllTextAsync(poolPath).ConfigureAwait(false);
        var poolRoot = JsonNode.Parse(json);
        var providerNode = poolRoot?["providers"]?.AsArray()
            .FirstOrDefault(candidate => string.Equals(
                (string?)candidate?["providerId"], "jev", StringComparison.OrdinalIgnoreCase));

        var poolApiKey = (string?)providerNode?["apiKey"];
        var environmentApiKey = Environment.GetEnvironmentVariable("JEV_API_KEY");
        Console.WriteLine(
            $"[S3c-2 live][gate] poolApiKey = {(string.IsNullOrWhiteSpace(poolApiKey) ? "<empty>" : poolApiKey.StartsWith("${", StringComparison.Ordinal) ? "${...占位符}" : "jv_***（池内真实密钥）")}；" +
            $"JEV_API_KEY 环境变量 = {(string.IsNullOrWhiteSpace(environmentApiKey) ? "<未设置>" : "<已设置>")}");
        if (string.IsNullOrWhiteSpace(environmentApiKey)
            && (string.IsNullOrWhiteSpace(poolApiKey) || poolApiKey.StartsWith("${", StringComparison.Ordinal)))
        {
            Assert.Inconclusive("资源池中 jev.apiKey 仍是占位符且环境无 JEV_API_KEY，跳过联网测试。");
            return;
        }

        // ── ② 真实资源池 → 生产选项提供者 → 真实 JevDecisionService（外面包一层捕获装饰器） ──
        var providersConfig = JsonSerializer.Deserialize<PuddingLlmProvidersConfig>(
            json, SerializerOptions);
        Assert.IsNotNull(providersConfig, "资源池 JSON 反序列化为 PuddingLlmProvidersConfig 失败。");

        var llmConfigService = new PuddingFileLlmConfigService(providersConfig);
        var optionsProvider = new JevDecisionOptionsProvider(
            new ConfigurationBuilder().Build(),
            llmConfigService,
            NullLogger<JevDecisionOptionsProvider>.Instance,
            keyVaultService: null);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var capture = new CapturingJevDecisionService(new JevDecisionService(
            new StaticHttpClientFactory(httpClient),
            optionsProvider,
            NullLogger<JevDecisionService>.Instance));

        // ── ③ 被测对象：生产 JevToolCallClassifier（Enabled=true，自身 deadline 30s） ──
        var classifier = new JevToolCallClassifier(
            capture,
            new ToolApprovalJevOptions { Enabled = true },
            TimeProvider.System);

        // ── ④ 真实感上下文：仓库根目录执行 git push origin master（不可逆、影响远端仓库） ──
        var context = new ToolCallClassificationContext
        {
            ToolId = "terminal",
            CommandName = "git push origin master",
            ArgumentsJson = """{"command":"git push origin master","cwd":"E:\\github\\AgentNetworkPlan\\PuddingAgent"}""",
            WorkingDirectory = "E:\\github\\AgentNetworkPlan\\PuddingAgent",
            Shell = "pwsh",
            OperationContext = "代码审阅与构建验证通过后，把本地 master 分支的提交推送到远端仓库。",
            Purpose = "将本切片已完成并验证的提交发布到 GitHub 远端，让协作者与 CI 能拉取到最新代码。",
            Necessity = "推送必须由 shell 执行 git push 完成，仓库没有其它写远端的通道。",
            FactBasis =
            [
                "本地 master 分支领先 origin/master 若干提交。",
                "远端 origin 指向 GitHub 仓库 AgentNetworkPlan/PuddingAgent。",
            ],
            TargetResources =
            [
                "git remote: origin（GitHub 仓库 AgentNetworkPlan/PuddingAgent）",
                "远端分支 refs/heads/master",
            ],
            IsIrreversibleOperation = true,
            MayDamageOrDeleteData = false,
            WorkspaceId = "default",
            SessionId = "live-s3c2-classifier",
            AgentInstanceId = "live-s3c2-agent",
            UserId = "live-s3c2-user",
            RecentTrajectory = "（切片 S3c-2 验证上下文：本地提交已通过 dotnet build 0 error，正准备推送远端。）",
        };

        // ── ⑤ 真实调用（仅一次请求，一次 round trip 五问并行） ──
        var verdict = await classifier.ClassifyAsync(context).ConfigureAwait(false);

        Assert.AreEqual(1, capture.CallCount, "每次 ClassifyAsync 必须只发 1 次 Jev 请求。");

        // ── ⑥ 网络类失败：跳过而非失败（fail-closed 语义本身已由离线用例锁定） ──
        if (verdict.Outcome == ClassificationOutcome.Unknown
            && verdict.ReasonCode == JevToolCallClassifier.ReasonCodeUnavailable)
        {
            Console.WriteLine(
                $"[S3c-2 live][unavailable] ReasonCode={verdict.ReasonCode} Reason={verdict.Reason}");
            Assert.Inconclusive(
                $"Jev 依赖不可用（网络/超时/上游非 2xx），跳过而非失败。Reason={verdict.Reason}");
            return;
        }

        // ── ⑦ 关键断言：真实返回能被解析器解析（不是 Unknown、不是解析失败类原因码） ──
        CollectionAssert.Contains(
            new[]
            {
                ClassificationOutcome.AllowOnce,
                ClassificationOutcome.AllowPermanent,
                ClassificationOutcome.DenyOnce,
                ClassificationOutcome.DenyPermanent,
            },
            verdict.Outcome,
            $"Outcome 必须是四选一，实际 {verdict.Outcome}（Reason={verdict.Reason}）");
        Assert.AreNotEqual(ClassificationOutcome.Unknown, verdict.Outcome);
        Assert.AreEqual(
            JevToolCallClassifier.ReasonCodeVerdict,
            verdict.ReasonCode,
            $"ReasonCode 不得是解析失败类（{JevToolCallClassifier.ReasonCodeUnparsed}）");

        // ── ⑧ 诊断输出（硬要求）：提问文本 / 真实返回形状 / 一致性结论 ──
        PrintCapturedRequest(capture.CapturedRequest!);
        PrintRawResultShape(capture.CapturedResult!);
        PrintVerdictAndConsistency(verdict, capture.CapturedResult!);
    }

    /// <summary>打印我们发出的提问文本（state + 五问：四选一 criteria 与四个可信度问）。</summary>
    private static void PrintCapturedRequest(JevDecisionRequest request)
    {
        Console.WriteLine("==== [S3c-2 live] 我们发出的提问（JevDecisionRequest，生产原始形状） ====");
        Console.WriteLine(
            "[S3c-2 live] 生产请求按原样发出（无任何补丁）；每个问题都必须带 Instructions —— "
            + "缺 instructions 的 choice 问题会被官方 API 以 400 拒绝（错误原文见 CapturingJevDecisionService 类注释）。");
        Console.WriteLine($"state = {request.State?.ToJsonString()}");
        foreach (var question in request.Questions)
        {
            var choiceCriteria = question.ChoiceCriteria is null
                ? null
                : string.Join("; ", question.ChoiceCriteria.Select(kv => $"{kv.Key} = {kv.Value}"));
            var noulCriteria = question.NoulCriteria is null
                ? null
                : $"True=\"{question.NoulCriteria.True}\"; False=\"{question.NoulCriteria.False}\"";
            Console.WriteLine(
                $"  - name={question.Name} type={question.Type.ToString().ToLowerInvariant()}"
                + (string.IsNullOrEmpty(question.Instructions)
                    ? ""
                    : $" instructions=\"{question.Instructions}\"")
                + (choiceCriteria is null ? "" : $" choiceCriteria=[{choiceCriteria}]")
                + (noulCriteria is null ? "" : $" noulCriteria({noulCriteria})"));
        }
    }

    /// <summary>打印真实返回的原始 JSON 形状（answers 键名层级 / choice 字面量 / noul 数值键名）。</summary>
    private static void PrintRawResultShape(JevDecisionResult result)
    {
        Console.WriteLine("==== [S3c-2 live] 真实返回的原始 JSON 形状（JevDecisionResult，生产 ParseResult 投影） ====");
        Console.WriteLine(JsonSerializer.Serialize(
            result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"answers 键名 = [{string.Join(", ", result.Answers.Keys)}]");
        var outcome = result.GetAnswer("outcome");
        Console.WriteLine(
            $"answers[\"outcome\"].type = \"{outcome?.Type}\"；answers[\"outcome\"].choice 字面量 = \"{outcome?.Choice}\"");
        foreach (var key in ParserConfidenceKeys)
        {
            var answer = result.GetAnswer(key);
            Console.WriteLine(
                $"answers[\"{key}\"].type = \"{answer?.Type}\"；answers[\"{key}\"].noul = " + (answer?.Noul is { } noul
                    ? noul.ToString("0.###", CultureInfo.InvariantCulture)
                    : "<missing>"));
        }
    }

    /// <summary>打印解析器产出（verdict）与「解析器期望 vs 真实返回」一致性结论。</summary>
    private static void PrintVerdictAndConsistency(ClassificationVerdict verdict, JevDecisionResult result)
    {
        Console.WriteLine("==== [S3c-2 live] 解析器产出（ClassificationVerdict） ====");
        Console.WriteLine($"Outcome = {verdict.Outcome}");
        Console.WriteLine($"ReasonCode = {verdict.ReasonCode}");
        Console.WriteLine($"ClassifierModel = {verdict.ClassifierModel}");
        Console.WriteLine(
            $"LatencyMs = {(verdict.LatencyMs is { } latency ? latency.ToString("0", CultureInfo.InvariantCulture) : "<null>")}");
        Console.WriteLine($"Reason = {verdict.Reason}");
        if (verdict.PerOutcomeConfidence is { Count: > 0 } confidences)
        {
            foreach (var pair in confidences)
            {
                Console.WriteLine(
                    $"PerOutcomeConfidence[{pair.Key}] = {pair.Value.ToString("0.000", CultureInfo.InvariantCulture)}");
            }
        }
        else
        {
            Console.WriteLine("PerOutcomeConfidence = <空：未携带任何键值>");
        }

        // ── 一致性判定：解析器期望的字段路径 / 取值字面量 vs 真实返回 ──
        var facts = new List<string>();
        var outcome = result.GetAnswer("outcome");
        if (outcome is null)
        {
            facts.Add("answers 缺少键 \"outcome\"（解析器 ParseVerdict 依赖 answers[outcome].choice）");
        }
        else
        {
            if (!string.Equals(outcome.Type, "choice", StringComparison.OrdinalIgnoreCase))
            {
                facts.Add($"answers[\"outcome\"].type 期望 \"choice\"，实际 \"{outcome.Type}\"");
            }

            var normalized = outcome.Choice?.Trim().ToLowerInvariant();
            if (!ParserAcceptedChoices.Contains(normalized))
            {
                facts.Add(
                    "解析器需要接受的 choice 字面量：\""
                    + outcome.Choice
                    + $"\"（当前仅接受 [{string.Join(", ", ParserAcceptedChoices)}]，trim+小写比较）");
            }
        }

        foreach (var key in ParserConfidenceKeys)
        {
            var answer = result.GetAnswer(key);
            if (answer is null)
            {
                facts.Add(
                    $"answers 缺少键 \"{key}\"（解析器 TryGetConfidence 依赖该键的 noul；"
                    + "永久类缺失会防御性降级为单次类）");
            }
            else if (!string.Equals(answer.Type, "noul", StringComparison.OrdinalIgnoreCase))
            {
                facts.Add($"answers[\"{key}\"].type 期望 \"noul\"，实际 \"{answer.Type}\"");
            }
            else if (answer.Noul is not { } value || double.IsNaN(value) || double.IsInfinity(value))
            {
                facts.Add(
                    $"answers[\"{key}\"].noul 期望 0..1 有限数值，实际 "
                    + (answer.Noul.HasValue ? answer.Noul.Value.ToString(CultureInfo.InvariantCulture) : "<missing>"));
            }
        }

        Console.WriteLine("==== [S3c-2 live] 结论：解析器期望 vs 真实返回 ====");
        if (facts.Count == 0)
        {
            Console.WriteLine(
                "一致：真实返回的字段路径与取值字面量能被我们的解析器解析"
                + "（Outcome 非 Unknown 且四个 confidence.* 键齐全且 noul 有效）。");
        }
        else
        {
            Console.WriteLine(
                "不一致：真实返回存在解析器无法按当前口径解析的形状。"
                + "解析器需要接受的字面量 / 字段路径事实清单（只报告，不改解析器）：");
            foreach (var fact in facts)
            {
                Console.WriteLine($"  - {fact}");
            }
        }
    }

    /// <summary>按优先级定位本机资源池文件；全部不存在返回 null。</summary>
    private static string? ResolvePoolPath()
    {
        var candidates = new List<string>();

        var explicitPath = Environment.GetEnvironmentVariable(PoolPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }

        var dataRoot = Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            candidates.Add(Path.Combine(dataRoot, PoolRelativePath));
        }

        // 本机部署默认位置
        candidates.Add(Path.Combine(@"D:\data", PoolRelativePath));

        // 从测试程序集目录回溯仓库根下的 data/config
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "data", PoolRelativePath));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 捕获真实服务收到的请求与返回的结果，同时保持调用语义原样透传（仅 1 次请求可断言）。
    /// <para>
    /// <b>测试侧最小装饰器（显式披露）：<b>不补丁</b>，只捕获 + 断言。</b>
    /// <para>
    /// 真链路首跑（2026-09-21）曾发现官方 API 对 choice 问题强制要求 instructions
    /// （HTTP 400 原文：<c>Question "outcome" needs instructions.</c>），而当时生产
    /// <c>BuildDecisionRequest</c> 的 outcome 问题未设置 Instructions。**该生产缺陷已由提交
    /// <c>231eb23f</c> 修复**（给 outcome choice 问题补 Instructions，并新增离线护栏）。
    /// </para>
    /// <para>
    /// 因此本装饰器**不再打补丁**（打补丁会让探针测不到真实生产形状），改为**断言**：
    /// 生产请求里任何一个问题缺 Instructions ⇒ 本探针在真实链路上失败。
    /// 同时捕获原始请求/响应，供诊断打印。
    /// </para>
    /// </summary>
    private sealed class CapturingJevDecisionService(IJevDecisionService inner) : IJevDecisionService
    {
        /// <summary>生产 <c>BuildDecisionRequest</c> 发出的原始请求（原样，未打补丁）。</summary>
        public JevDecisionRequest? CapturedRequest { get; private set; }

        /// <summary>实际发往真实端点的请求（已断言每个问题都带 Instructions；本装饰器不做任何补丁）。</summary>
        public JevDecisionRequest? DispatchedRequest { get; private set; }

        public JevDecisionResult? CapturedResult { get; private set; }

        public int CallCount { get; private set; }

        public async Task<JevDecisionResult> DecideAsync(JevDecisionRequest request, CancellationToken ct = default)
        {
            CallCount++;
            CapturedRequest = request;

            // **不补丁**：生产请求必须按原样发往真实端点，否则测到的不是真实生产形状。
            // 改为**断言**：生产一旦再有缺 Instructions 的问题，本探针就在真实链路上失败（这正是它存在的意义）。
            foreach (var question in request.Questions)
            {
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(question.Instructions),
                    $"生产 BuildDecisionRequest 发出的问题 '{question.Name}'（{question.Type}）缺 Instructions；"
                    + "官方 API 对缺 instructions 的 choice 问题返回 400（原文：Question \"outcome\" needs instructions.），"
                    + "会让分类器永远 Unknown ⇒ 审批全线 deferred。");
            }

            DispatchedRequest = request;

            var result = await inner.DecideAsync(request, ct).ConfigureAwait(false);
            CapturedResult = result;
            return result;
        }
    }

    /// <summary>始终返回同一个 HttpClient 的工厂（命名客户端超时已在组合根设置，此处等价复刻）。</summary>
    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
