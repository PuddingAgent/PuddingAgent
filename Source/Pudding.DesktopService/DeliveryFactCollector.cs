namespace Pudding.DesktopService;

/// <summary>
/// 交付事实采集的输入：仓库根、发布目录、测试结果目录（TRX）、日志目录与构建标识。
/// </summary>
public sealed record DeliveryCollectionRequest(
    string RepoRoot,
    string? PublishDirectory,
    string ResultsDirectory,
    string ChangelogDirectory,
    string BuildId,
    string Commit,
    IReadOnlyList<string>? AdditionalKnownUnverified = null,
    // 单次采集允许扫描的日志文件数（默认 = MaxChangelogFiles 常量；测试用它构造"语料被截断"的场景）。
    int ChangelogFileLimit = DeliveryFactCollector.MaxChangelogFiles);

/// <summary>
/// **交付事实采集**：把"跑测试/发版/写日志"之后留在磁盘上的东西，汇总成一份可自检的交付清单。
///
/// 这是 B7 交付链的入口（内部一侧）：调用方只需提供**目录**，不用手工拼清单。
/// 三件事都按"读不到就失败"处理，绝不猜：
/// <list type="bullet">
///   <item>产物：发布目录里的可执行文件与程序集（只取文件名，不把本机路径写进交付物）；</item>
///   <item>检查：结果目录里的 <c>*.trx</c>（套件名取文件名），由 <see cref="TrxTestResultParser"/> 读计数；</item>
///   <item>未验证项：日志目录里的 markdown，由 <see cref="KnownUnverifiedExtractor"/> 提取。</item>
/// </list>
/// </summary>
public static class DeliveryFactCollector
{
    /// <summary>
    /// 单次采集读取的日志文件上限。
    ///
    /// **2026-10-08 更正**：这里原先静默截断到 **120**，而仓库当时已有 **319** 个 changelog ⇒
    /// "未验证项"是从**被截断的语料**里提取的，清单却对此只字未提（与 round-124 修的"条目被截断"是同一类问题）。
    /// 现在的做法是：**扫全部**（markdown 总量很小），并把"扫了多少文件"作为**事实**写进清单；
    /// 只有在极端情况下才用上限兜底，且届时清单必须**显式报告**被截断。
    /// </summary>
    public const int MaxChangelogFiles = 100_000;

    public static bool TryCollect(
        DeliveryCollectionRequest request,
        out DeliveryManifest? manifest,
        out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(request);
        manifest = null;

        var collected = new List<string>();

        if (string.IsNullOrWhiteSpace(request.RepoRoot) || !Directory.Exists(request.RepoRoot))
        {
            collected.Add($"repo root does not exist: '{request.RepoRoot}'");
        }

        // ① 产物
        List<string> artifacts = [];
        if (string.IsNullOrWhiteSpace(request.PublishDirectory))
        {
            collected.Add("publish directory is required (there is nothing to deploy without artifacts)");
        }
        else if (!Directory.Exists(request.PublishDirectory))
        {
            collected.Add($"publish directory does not exist: '{request.PublishDirectory}'");
        }
        else
        {
            artifacts = Directory
                .EnumerateFiles(request.PublishDirectory)
                .Where(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (artifacts.Count == 0)
            {
                collected.Add($"publish directory has no .exe/.dll artifacts: '{request.PublishDirectory}'");
            }
        }

        // ② 检查（TRX）
        List<DeliveryCheck> checks = [];
        if (string.IsNullOrWhiteSpace(request.ResultsDirectory) || !Directory.Exists(request.ResultsDirectory))
        {
            collected.Add($"test results directory does not exist: '{request.ResultsDirectory}'");
        }
        else
        {
            var runs = Directory
                .EnumerateFiles(request.ResultsDirectory, "*.trx")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => (SuiteName: Path.GetFileNameWithoutExtension(path), TrxXml: (string?)SafeRead(path)))
                .ToArray();

            if (runs.Length == 0)
            {
                collected.Add($"no TRX results found in '{request.ResultsDirectory}'");
            }
            else if (!TrxTestResultParser.TryParseAll(runs, out var parsed, out var errors))
            {
                collected.AddRange(errors);
            }
            else
            {
                checks = parsed.ToList();
            }
        }

        // ③ 未验证项（日志）
        List<string>? changelogs = null;
        var changelogFilesTotal = 0;
        if (string.IsNullOrWhiteSpace(request.ChangelogDirectory) || !Directory.Exists(request.ChangelogDirectory))
        {
            collected.Add($"changelog directory does not exist: '{request.ChangelogDirectory}'");
        }
        else
        {
            // 先数总数，再决定读多少：这样"被截断"永远是可报告的**事实**，而不是沉默。
            var limit = request.ChangelogFileLimit;
            var all = Directory
                .EnumerateFiles(request.ChangelogDirectory, "*.md")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            changelogFilesTotal = all.Length;

            changelogs = all
                .Take(limit)
                .Select(path => SafeRead(path))
                .Where(text => text is not null)
                .Select(text => text!)
                .ToList();

            if (all.Length > limit)
            {
                collected.Add(
                    $"changelog corpus has {all.Length} markdown files but only {limit} were scanned;"
                    + " the manifest must not silently under-report unverified items");
            }
        }

        if (collected.Count > 0)
        {
            problems = collected;
            return false;
        }

        return DeliveryManifestBuilder.TryBuild(
            new DeliveryBuildFacts(
                request.BuildId,
                request.Commit,
                artifacts,
                checks,
                changelogs,
                request.AdditionalKnownUnverified,
                RequiredExternalSteps: null,
                ChangelogFilesTotal: changelogFilesTotal,
                ChangelogFilesScanned: changelogs?.Count ?? 0),
            out manifest,
            out problems);
    }

    /// <summary>读文件；读不到就返回 <c>null</c>（由上层按"事实缺失"处理，不静默当成空内容）。</summary>
    private static string? SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
