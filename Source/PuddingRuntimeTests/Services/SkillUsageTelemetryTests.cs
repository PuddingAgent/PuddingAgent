using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Skills.Telemetry;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// RSI-G2 切片1：技能使用遥测契约测试。
/// 核心约定：遥测是旁路 —— 有无 sink、sink 是否故障，EnforceAsync 的返回都必须逐项相同。
/// </summary>
[TestClass]
public sealed class SkillUsageTelemetryTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Sink_WriteMultipleRecords_FileHasExactlyNParseableLines()
    {
        using var temp = new TempDataRoot();
        var sink = CreateSink(temp.TelemetryDirectory);

        await sink.RecordAsync(Record("skill-a", injected: true, contentBytes: 128));
        await sink.RecordAsync(Record("skill-b", injected: false, failureReason: "boom"));
        await sink.RecordAsync(Record("skill-c", injected: true, contentBytes: 5, keywords: ["alpha", "中文"]));

        var path = Path.Combine(temp.TelemetryDirectory, "skill-usage-20260921.jsonl");
        Assert.IsTrue(File.Exists(path), $"expected telemetry file {path}");

        var lines = File.ReadAllLines(path);
        Assert.AreEqual(3, lines.Length);

        var expectedIds = new[] { "skill-a", "skill-b", "skill-c" };
        for (var i = 0; i < lines.Length; i++)
        {
            using var doc = JsonDocument.Parse(lines[i]); // 每行必须是独立完整 JSON
            Assert.AreEqual(expectedIds[i], doc.RootElement.GetProperty("skillId").GetString());
        }

        using var first = JsonDocument.Parse(lines[0]);
        Assert.AreEqual("injected", first.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual(128, first.RootElement.GetProperty("contentBytes").GetInt32());

        using var second = JsonDocument.Parse(lines[1]);
        Assert.AreEqual("readFailed", second.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual("boom", second.RootElement.GetProperty("failureReason").GetString());
    }

    [TestMethod]
    public async Task Sink_FailOpen_WhenDirectoryPathIsBlockedByFile()
    {
        using var temp = new TempDataRoot();
        Directory.CreateDirectory(temp.Root);
        var blocker = Path.Combine(temp.Root, "blocker");
        await File.WriteAllTextAsync(blocker, "i am a file");

        // 中间路径组件是文件 → Directory.CreateDirectory 必抛，但 sink 必须 fail-open
        var blockedDir = Path.Combine(blocker, "nested");
        var sink = new JsonlSkillUsageTelemetrySink(
            blockedDir, NullLogger<JsonlSkillUsageTelemetrySink>.Instance, () => FixedUtc);

        await sink.RecordAsync(Record("skill-x", injected: true, contentBytes: 1));
        Assert.IsFalse(Directory.Exists(blockedDir));
    }

    [TestMethod]
    public async Task Sink_FailOpen_WhenTargetFileIsADirectory()
    {
        using var temp = new TempDataRoot();
        Directory.CreateDirectory(temp.TelemetryDirectory);
        // 目标分片文件名被同名目录占用 → AppendAllText 必抛，但 sink 必须 fail-open
        Directory.CreateDirectory(Path.Combine(temp.TelemetryDirectory, "skill-usage-20260921.jsonl"));

        var sink = CreateSink(temp.TelemetryDirectory);
        await sink.RecordAsync(Record("skill-y", injected: true, contentBytes: 2));
    }

    [TestMethod]
    public async Task Enforcer_WithoutSink_BehavesAsBefore()
    {
        using var temp = new TempDataRoot();
        var files = await CreateSkillsAsync(temp,
            ("skill-a", "Alpha Helper", new[] { "alpha" }),
            ("skill-b", "Beta Helper", new[] { "beta" }));
        var enforcer = new SkillEnforcerService(files, NullLogger<SkillEnforcerService>.Instance);

        var both = await enforcer.EnforceAsync("agent-1", "alpha and beta together");
        Assert.IsNotNull(both);
        Assert.AreEqual(2, both.Count);
        CollectionAssert.AreEquivalent(
            new[] { "skill-a", "skill-b" },
            both.Select(x => x.SkillId).ToArray());

        Assert.IsNull(await enforcer.EnforceAsync("agent-1", ""));              // 空消息
        Assert.IsNull(await enforcer.EnforceAsync("agent-1", "   "));           // 空白消息
        Assert.IsNull(await enforcer.EnforceAsync("agent-1", "no match here")); // 无匹配
    }

    [TestMethod]
    public async Task Enforcer_WithSink_ReturnsIdenticalResultsAsWithoutSink()
    {
        using var temp = new TempDataRoot();
        var files = await CreateSkillsAsync(temp,
            ("skill-a", "Alpha Helper", new[] { "alpha" }),
            ("skill-b", "Beta Helper", new[] { "beta" }),
            ("skill-c", "Gamma Helper", new[] { "gamma" }));
        const string message = "use alpha and beta now";

        var withoutSink = await new SkillEnforcerService(files, NullLogger<SkillEnforcerService>.Instance)
            .EnforceAsync("agent-1", message);
        var withSink = await new SkillEnforcerService(
                files, NullLogger<SkillEnforcerService>.Instance, CreateSink(temp.TelemetryDirectory))
            .EnforceAsync("agent-1", message);

        // 零行为变更硬证据：有无 sink，返回集合逐项相同
        Assert.IsNotNull(withoutSink);
        Assert.IsNotNull(withSink);
        Assert.AreEqual(withoutSink.Count, withSink.Count);
        for (var i = 0; i < withoutSink.Count; i++)
        {
            Assert.AreEqual(withoutSink[i].SkillId, withSink[i].SkillId);
            Assert.AreEqual(withoutSink[i].MarkdownContent, withSink[i].MarkdownContent);
        }

        // 同一次运行：每个命中技能恰好落一条记录
        var path = Path.Combine(temp.TelemetryDirectory, "skill-usage-20260921.jsonl");
        Assert.AreEqual(withoutSink.Count, File.ReadAllLines(path).Length);
    }

    [TestMethod]
    public async Task Enforcer_RecordsOnlyActuallyMatchedKeywords()
    {
        using var temp = new TempDataRoot();
        var files = await CreateSkillsAsync(temp,
            ("kw-probe", "Probe Utility", new[] { "alpha", "beta", "gamma" }));
        var enforcer = new SkillEnforcerService(
            files, NullLogger<SkillEnforcerService>.Instance, CreateSink(temp.TelemetryDirectory));

        var result = await enforcer.EnforceAsync("agent-1", "please use alpha now");
        Assert.IsNotNull(result);

        var path = Path.Combine(temp.TelemetryDirectory, "skill-usage-20260921.jsonl");
        using var doc = JsonDocument.Parse(File.ReadAllText(path).TrimEnd('\n'));
        var keywords = doc.RootElement.GetProperty("matchedKeywords")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        // 只记录命中的那一个，绝不泄全量关键词空间
        CollectionAssert.AreEquivalent(new[] { "alpha" }, keywords);

        // ContentBytes 与注入正文严格一致
        var expectedBytes = Encoding.UTF8.GetByteCount("# Test Skill\n\ncontent-body");
        Assert.AreEqual(expectedBytes, doc.RootElement.GetProperty("contentBytes").GetInt32());
        Assert.AreEqual("kw-probe", doc.RootElement.GetProperty("skillId").GetString());
        Assert.AreEqual("agent-1", doc.RootElement.GetProperty("agentInstanceId").GetString());
    }

    [TestMethod]
    public async Task Enforcer_ReadFailure_RecordsReadFailedWithReason()
    {
        using var temp = new TempDataRoot();
        var files = await CreateSkillsAsync(temp,
            ("broken-skill", "Broken Thing", new[] { "brk" }));
        File.Delete(Path.Combine(
            temp.Paths.AgentInstanceRoot("agent-1"), "skills", "broken-skill", "SKILL.md"));

        var enforcer = new SkillEnforcerService(
            files, NullLogger<SkillEnforcerService>.Instance, CreateSink(temp.TelemetryDirectory));

        // 行为不变：读失败 → 整体仍返回 null
        var result = await enforcer.EnforceAsync("agent-1", "trigger brk mode");
        Assert.IsNull(result);

        var path = Path.Combine(temp.TelemetryDirectory, "skill-usage-20260921.jsonl");
        using var doc = JsonDocument.Parse(File.ReadAllText(path).TrimEnd('\n'));
        Assert.AreEqual("readFailed", doc.RootElement.GetProperty("outcome").GetString());
        Assert.IsFalse(doc.RootElement.GetProperty("injected").GetBoolean());
        Assert.IsFalse(string.IsNullOrEmpty(doc.RootElement.GetProperty("failureReason").GetString()));
        Assert.AreEqual(0, doc.RootElement.GetProperty("contentBytes").GetInt32());
    }

    [TestMethod]
    public async Task Enforcer_EmptyOrNoMatch_WritesNoTelemetry()
    {
        using var temp = new TempDataRoot();
        var files = await CreateSkillsAsync(temp, ("skill-a", "Alpha Helper", new[] { "alpha" }));
        var enforcer = new SkillEnforcerService(
            files, NullLogger<SkillEnforcerService>.Instance, CreateSink(temp.TelemetryDirectory));

        Assert.IsNull(await enforcer.EnforceAsync("agent-1", ""));
        Assert.IsNull(await enforcer.EnforceAsync("agent-1", "   "));
        Assert.IsNull(await enforcer.EnforceAsync("agent-1", "nothing relevant here"));

        var files2 = Directory.Exists(temp.TelemetryDirectory)
            ? Directory.EnumerateFiles(temp.TelemetryDirectory, "*.jsonl").ToList()
            : [];
        Assert.AreEqual(0, files2.Count);
    }

    [TestMethod]
    public async Task Sink_DifferentUtcDates_ProduceDifferentFiles()
    {
        using var temp = new TempDataRoot();
        var dir = temp.TelemetryDirectory;
        var sinkA = new JsonlSkillUsageTelemetrySink(
            dir, NullLogger<JsonlSkillUsageTelemetrySink>.Instance,
            () => new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero));
        var sinkB = new JsonlSkillUsageTelemetrySink(
            dir, NullLogger<JsonlSkillUsageTelemetrySink>.Instance,
            () => new DateTimeOffset(2026, 1, 2, 23, 59, 59, TimeSpan.Zero));

        await sinkA.RecordAsync(Record("skill-a", injected: true));
        await sinkB.RecordAsync(Record("skill-b", injected: true));

        var fileA = Path.Combine(dir, "skill-usage-20260921.jsonl");
        var fileB = Path.Combine(dir, "skill-usage-20260102.jsonl");
        Assert.IsTrue(File.Exists(fileA));
        Assert.IsTrue(File.Exists(fileB));
        Assert.AreEqual(1, File.ReadAllLines(fileA).Length);
        Assert.AreEqual(1, File.ReadAllLines(fileB).Length);
    }

    [TestMethod]
    public async Task Sink_WritesUtf8WithoutBom_AndPreservesCjkKeywords()
    {
        using var temp = new TempDataRoot();
        var sink = CreateSink(temp.TelemetryDirectory);

        await sink.RecordAsync(Record("skill-cjk", injected: true, contentBytes: 10, keywords: ["中文关键词"]));

        var path = Path.Combine(temp.TelemetryDirectory, "skill-usage-20260921.jsonl");
        var bytes = File.ReadAllBytes(path);
        Assert.IsTrue(bytes.Length > 0);
        // UTF-8 无 BOM：文件头不得是 EF BB BF
        Assert.IsFalse(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8).TrimEnd('\n'));
        Assert.AreEqual("中文关键词", doc.RootElement.GetProperty("matchedKeywords")[0].GetString());
    }

    /// <summary>
    /// RSI-G2 切片2：宿主 DI 形状验证（与 PuddingServiceCollectionExtensions.Runtime.cs:139+ 同形）。
    /// 核心断言：SkillEnforcerService 的第三个构造参数是【带默认值的可选参数】——
    /// 若 DI 不注入可选参数，本用例必然失败（遥测文件不会出现）。
    /// 这是「生产端真的会采集」的可机械验证证据；只断言「注册了一行」不算证据。
    /// </summary>
    [TestMethod]
    public async Task HostDiShape_ResolvesEnforcerWithSinkInjected_AndRecordsRealInjection()
    {
        using var temp = new TempDataRoot();
        var services = new ServiceCollection();
        services.AddSingleton(temp.Paths);
        services.AddSingleton<ILogger<JsonlSkillUsageTelemetrySink>>(NullLogger<JsonlSkillUsageTelemetrySink>.Instance);
        services.AddSingleton<ILogger<SkillEnforcerService>>(NullLogger<SkillEnforcerService>.Instance);
        services.AddSingleton<AgentSkillFileService>();
        services.AddSingleton<ISkillUsageTelemetrySink>(sp => new JsonlSkillUsageTelemetrySink(
            sp.GetRequiredService<PuddingDataPaths>().SkillUsageTelemetryRoot,
            sp.GetRequiredService<ILogger<JsonlSkillUsageTelemetrySink>>()));
        services.AddSingleton<SkillEnforcerService>();

        using var provider = services.BuildServiceProvider();

        // 经容器解析的技能文件服务写盘：证明路径确实来自 PuddingDataPaths.SkillUsageTelemetryRoot
        var files = provider.GetRequiredService<AgentSkillFileService>();
        await files.CreateAsync("agent-1", new AgentSkillCreateRequest
        {
            SkillId = "skill-a",
            Name = "Alpha Helper",
            Keywords = ["alpha"],
            SkillMarkdown = "# Test Skill\n\ncontent-body",
        });

        var enforcer = provider.GetRequiredService<SkillEnforcerService>();
        var results = await enforcer.EnforceAsync("agent-1", "please use alpha now");

        Assert.IsNotNull(results, "DI 解析出的 enforcer 必须仍能正常返回注入结果");
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("skill-a", results[0].SkillId);

        var telemetryDir = temp.Paths.SkillUsageTelemetryRoot;
        Assert.IsTrue(Directory.Exists(telemetryDir), $"遥测目录未创建，说明 sink 未被注入：{telemetryDir}");
        var shards = Directory.GetFiles(telemetryDir, "skill-usage-*.jsonl");
        Assert.AreEqual(1, shards.Length);

        using var doc = JsonDocument.Parse(File.ReadAllLines(shards[0]).Single());
        Assert.AreEqual("skill-a", doc.RootElement.GetProperty("skillId").GetString());
        Assert.AreEqual("agent-1", doc.RootElement.GetProperty("agentInstanceId").GetString());
        Assert.AreEqual("injected", doc.RootElement.GetProperty("outcome").GetString());
    }

    private static JsonlSkillUsageTelemetrySink CreateSink(string directory) =>
        new(directory, NullLogger<JsonlSkillUsageTelemetrySink>.Instance, () => FixedUtc);

    private static async Task<AgentSkillFileService> CreateSkillsAsync(
        TempDataRoot temp,
        params (string SkillId, string Name, string[] Keywords)[] skills)
    {
        var files = new AgentSkillFileService(temp.Paths);
        foreach (var (skillId, name, keywords) in skills)
        {
            await files.CreateAsync("agent-1", new AgentSkillCreateRequest
            {
                SkillId = skillId,
                Name = name,
                Keywords = keywords,
                SkillMarkdown = "# Test Skill\n\ncontent-body",
            });
        }

        return files;
    }

    private static SkillUsageRecord Record(
        string skillId,
        bool injected,
        int contentBytes = 0,
        string? failureReason = null,
        string[]? keywords = null) =>
        new()
        {
            SkillId = skillId,
            AgentInstanceId = "agent-1",
            MatchedKeywords = keywords ?? [],
            Injected = injected,
            ContentBytes = contentBytes,
            FailureReason = failureReason,
            OccurredAtUtc = FixedUtc,
            Outcome = injected ? SkillUsageOutcome.Injected : SkillUsageOutcome.ReadFailed,
        };

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-skill-usage-tests", Guid.NewGuid().ToString("N"));
            Paths = PuddingDataPaths.FromRoot(Root);
            TelemetryDirectory = Path.Combine(Root, "telemetry");
        }

        public string Root { get; }

        public PuddingDataPaths Paths { get; }

        public string TelemetryDirectory { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
