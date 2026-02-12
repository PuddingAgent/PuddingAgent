using System.Text.Json;
using PuddingCode.Configuration;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class BenchmarkCaseFileServiceTests
{
    [TestMethod]
    public async Task ListAsync_ReturnsOnlyEnabledSafeSummariesWithoutPrompt()
    {
        var root = CreateTempRoot();
        WriteCases(root, [
            SafeCase("case-1", "Markdown 摘要"),
            SafeCase("disabled", "禁用") with { IsEnabled = false },
            SafeCase("unsafe", "泄露") with { Prompt = "这是一个基准测试，请使用工具完成。" },
        ]);
        var service = new BenchmarkCaseFileService(new BenchmarkCaseCatalogService(PuddingDataPaths.FromRoot(root)));

        var list = await service.ListAsync();

        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("case-1", list[0].Id);
        Assert.AreEqual("Markdown 摘要", list[0].Title);
        Assert.AreEqual("hard", list[0].Difficulty);
        Assert.AreEqual("12-18", list[0].EstimatedRounds);
    }

    [TestMethod]
    public async Task GetAsync_ReturnsPromptForEnabledSafeCase()
    {
        var root = CreateTempRoot();
        WriteCases(root, [SafeCase("case-1", "Markdown 摘要")]);
        var service = new BenchmarkCaseFileService(new BenchmarkCaseCatalogService(PuddingDataPaths.FromRoot(root)));

        var detail = await service.GetAsync("case-1");

        Assert.IsNotNull(detail);
        Assert.AreEqual("case-1", detail.Id);
        Assert.IsTrue(detail.Prompt.Contains("summary.md", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetAsync_ReturnsNullForUnsafePrompt()
    {
        var root = CreateTempRoot();
        WriteCases(root, [
            SafeCase("unsafe", "泄露") with { Prompt = "请用 shell 工具执行这个基准测试。" },
        ]);
        var service = new BenchmarkCaseFileService(new BenchmarkCaseCatalogService(PuddingDataPaths.FromRoot(root)));

        var detail = await service.GetAsync("unsafe");

        Assert.IsNull(detail);
    }

    [TestMethod]
    public async Task DefaultSeedCases_ShouldNotLeakBenchmarkOrSystemHints()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "PuddingAgent",
            "default-data"));
        var service = new BenchmarkCaseFileService(new BenchmarkCaseCatalogService(PuddingDataPaths.FromRoot(root)));

        var cases = await service.LoadAsync();

        Assert.IsTrue(cases.Count > 0);
        foreach (var item in cases)
            Assert.IsTrue(BenchmarkCaseFileService.IsPromptSafe(item.Prompt), $"Unsafe prompt: {item.Id}");
    }

    [TestMethod]
    public async Task PrepareServices_CopySeedFilesAndPersistRunMetadata()
    {
        var root = CreateTempRoot();
        var dataPaths = PuddingDataPaths.FromRoot(root);
        var benchmarkCase = SafeCase("case-seeded", "复杂资料整理") with
        {
            SeedId = "complex-docs",
            Difficulty = "extreme",
            EstimatedRounds = "25-40",
        };
        WriteSeedFile(root, "complex-docs", "inputs/readme.md", "# Project\nSeeded content");
        var seedService = new BenchmarkWorkspaceSeedService(dataPaths);
        var runService = new BenchmarkRunService(dataPaths);

        var seed = await seedService.PrepareAsync(benchmarkCase, "default", CancellationToken.None);
        var run = await runService.CreateAsync(benchmarkCase, "default", "session-1", seed, CancellationToken.None);

        var target = Path.Combine(root, "workspaces", "default", "inputs", "readme.md");
        var runFile = Path.Combine(root, "runtime", "benchmark-runs", $"{run.RunId}.json");
        Assert.IsTrue(File.Exists(target));
        Assert.AreEqual("# Project\nSeeded content", File.ReadAllText(target));
        Assert.AreEqual("complex-docs", seed.SeedId);
        Assert.AreEqual("inputs/readme.md", seed.Files.Single().Path);
        Assert.IsTrue(File.Exists(runFile));
        var runJson = File.ReadAllText(runFile);
        Assert.IsTrue(runJson.Contains("\"caseId\": \"case-seeded\"", StringComparison.Ordinal));
        Assert.IsTrue(runJson.Contains("\"sessionId\": \"session-1\"", StringComparison.Ordinal));
        Assert.IsTrue(runJson.Contains("\"seedId\": \"complex-docs\"", StringComparison.Ordinal));
    }

    private static BenchmarkCaseConfig SafeCase(string id, string title) => new()
    {
        Id = id,
        Title = title,
        Category = "文件处理",
        Coverage = ["file", "execution"],
        Difficulty = "hard",
        EstimatedRounds = "12-18",
        SeedId = "sample-docs",
        CapabilityTargets = ["file", "execution", "verification"],
        Prompt = "请创建一个 Markdown 摘要脚本，用途是扫描当前目录中的 .md 文件并生成 summary.md；功能是列出文件名、标题、字数估算和前两行内容，并运行一次验证。",
        IsEnabled = true,
        SortOrder = 10,
    };

    private static void WriteCases(string root, IReadOnlyList<BenchmarkCaseConfig> cases)
    {
        var path = Path.Combine(root, "config", "benchmark-cases", "hermes-agent-cases.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(cases, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        }));
    }

    private static void WriteSeedFile(string root, string seedId, string relativePath, string content)
    {
        var path = Path.Combine(root, "benchmark-seeds", seedId, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-benchmark-case-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
