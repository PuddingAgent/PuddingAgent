using Pudding.DesktopService;

// 交付清单采集入口（B7 内部一侧）。
//
// 用法：
//   Pudding.DeliveryManifest.Cli --build <id> --commit <sha> --publish <dir> --results <dir> --changelog <dir>
//                                [--extra-unverified <text>]... [--out <file>]
//
// 只做一件事：把磁盘上的事实（产物 / TRX 计数 / 日志里的未验证项）汇总成**已自检**的交付清单，
// 并把它写到 stdout（可再写文件）。**不合格就不产出**：以退避码 2 退出并打印具体原因，
// 绝不给出一份"看起来能交"的清单。

var arguments = ParseArguments(args);

// --conclude：外部一侧的模式 —— 读交付清单 + 外部观察事实，判定能否下 external-lifecycle-verified 结论。
if (arguments is not null && arguments.ContainsKey("conclude"))
{
    return Conclude(arguments);
}

if (arguments is null || arguments.ContainsKey("help"))
{
    Console.WriteLine("""
        用法（内部一侧）：
          Pudding.DeliveryManifest.Cli --build <id> --commit <sha> --publish <dir> --results <dir> --changelog <dir> [--extra-unverified <text>]... [--out <file>] [--report <file>]

        用法（外部一侧）：
          Pudding.DeliveryManifest.Cli --conclude <facts.json> --manifest <manifest.json> [--record <file>] [--accept-retries]

        采集三类事实并产出交付清单：
          --publish    发布目录（取其中的 .exe/.dll 作为产物，只记录文件名）
          --results    测试结果目录（读取其中的 *.trx 汇总计数）
          --changelog  修改日志目录（提取"内部证明不了的结论"）
          --build      构建标识   --commit 提交号
          --extra-unverified  额外补充的未验证项（可多次）
          --out        同时写入该文件（JSON）
          --report     同时渲染留痕报告（markdown + Front Matter）到该文件

        外部一侧判定（--conclude）：
          facts.json 形状：{ observedBuildId, smoke:[{checkId,outcome(passed|failed|not-run),evidence}],
                             lifecycle:[{step,passed,evidence}] }
          退出码：0 = 允许 external-lifecycle-verified；2 = 拒绝（原因逐条打印）
        """);
    return arguments is null ? 1 : 0;
}

var repositoryRoot = arguments.TryGetValue("repo", out var repo)
    ? repo
    : FindRepositoryRoot();

if (repositoryRoot is null)
{
    Console.Error.WriteLine("找不到仓库根（可用 --repo 指定）。");
    return 1;
}

var request = new DeliveryCollectionRequest(
    repositoryRoot,
    arguments.GetValueOrDefault("publish"),
    arguments.GetValueOrDefault("results") ?? string.Empty,
    arguments.GetValueOrDefault("changelog") ?? string.Empty,
    arguments.GetValueOrDefault("build") ?? string.Empty,
    arguments.GetValueOrDefault("commit") ?? string.Empty,
    arguments.TryGetValue("extra-unverified", out var extra) ? [extra] : null,
    WorkingTreeDirty: string.Equals(arguments.GetValueOrDefault("dirty"), "true", StringComparison.OrdinalIgnoreCase));

if (DeliveryFactCollector.TryCollect(request, out var manifest, out var problems))
{
    var json = DeliveryManifestValidator.Serialize(manifest!);
    Console.WriteLine(json);

    if (arguments.TryGetValue("out", out var outputPath) && !string.IsNullOrWhiteSpace(outputPath))
    {
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"# 已写入 {outputPath}");
    }

    // --report：把清单渲染成**留痕报告**（markdown + Front Matter），逐字带上未验证项。
    if (arguments.TryGetValue("report", out var reportPath) && !string.IsNullOrWhiteSpace(reportPath))
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var markdown = DeliveryManifestReport.Render(
            manifest!,
            arguments.GetValueOrDefault("report-title") ?? $"浏览器自动化能力交付清单（{manifest!.BuildId}）",
            today,
            arguments.GetValueOrDefault("report-slug") ?? $"reports-delivery-manifest-{today:yyyy-MM-dd}-{manifest!.Commit}");

        var directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(reportPath, markdown);
        Console.WriteLine($"# 报告已写入 {reportPath}");
    }

    Console.WriteLine($"# stage: {DesktopAcceptance.NameOf(manifest!.Stage)}");
    Console.WriteLine($"# 未验证项 {manifest.KnownUnverified!.Count} 条（内部证明不了的结论，已交给外部控制器）");
    return 0;
}

Console.Error.WriteLine("交付清单**未产出**（事实不完整或不合格）：");
foreach (var problem in problems)
{
    Console.Error.WriteLine($"  - {problem}");
}

return 2;

static Dictionary<string, string>? ParseArguments(string[] rawArguments)
{
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
    var extras = new List<string>();

    for (var index = 0; index < rawArguments.Length; index++)
    {
        var token = rawArguments[index];
        if (!token.StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"无法识别的参数：{token}");
            return null;
        }

        var name = token[2..];
        // 无取值开关（valueless switches）：解析器默认要求每个 --flag 后面跟取值，
        // 所以裸开关必须在这里显式登记，否则会被判成"缺少取值"而整体退化成用法错误。
        if (name is "help" or "accept-retries")
        {
            parsed[name] = "1";
            continue;
        }

        if (index + 1 >= rawArguments.Length || rawArguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"参数 --{name} 缺少取值。");
            return null;
        }

        // 同一个开关可重复（例如多个补充未验证项）：保留全部，用换行拼起来由提取器/清单去重。
        if (name == "extra-unverified")
        {
            extras.Add(rawArguments[++index]);
            parsed["extra-unverified"] = string.Join('\n', extras);
            continue;
        }

        parsed[name] = rawArguments[++index];
    }

    return parsed;
}

static string? FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
         directory is not null;
         directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
        {
            return directory.FullName;
        }
    }

    return null;
}

// 外部一侧：清单 + 观察事实 → 结论（0 = 允许 external-lifecycle-verified；2 = 拒绝）。
static int Conclude(Dictionary<string, string> arguments)
{
    if (!arguments.TryGetValue("manifest", out var manifestPath) || string.IsNullOrWhiteSpace(manifestPath))
    {
        Console.Error.WriteLine("--conclude 需要 --manifest <manifest.json>（内部交付的清单）。");
        return 1;
    }

    if (!File.Exists(manifestPath))
    {
        Console.Error.WriteLine($"找不到交付清单：{manifestPath}");
        return 1;
    }

    if (!DeliveryManifestValidator.TryDeserialize(
            File.ReadAllText(manifestPath), out var manifest, out var manifestProblems))
    {
        Console.Error.WriteLine("交付清单读取失败：");
        foreach (var problem in manifestProblems)
        {
            Console.Error.WriteLine($"  - {problem}");
        }

        return 2;
    }

    var factsPath = arguments["conclude"];
    if (!File.Exists(factsPath))
    {
        Console.Error.WriteLine($"找不到观察事实文件：{factsPath}");
        return 1;
    }

    if (!ExternalAcceptanceFacts.TryParse(
            File.ReadAllText(factsPath), manifest!, out var input, out var factsProblems))
    {
        Console.Error.WriteLine("观察事实读取失败（逐条如下）：");
        foreach (var problem in factsProblems)
        {
            Console.Error.WriteLine($"  - {problem}");
        }

        return 2;
    }

    // Accepting retried results is the ACCEPTOR's call: require an explicit flag, never assume it.
    var acceptRetries = arguments.ContainsKey("accept-retries");
    input = input! with { AcceptRetries = acceptRetries };
    if (manifest!.RetryNotes.Count > 0)
    {
        Console.WriteLine(
            $"# 交付清单里有 {manifest.RetryNotes.Count} 个套件是重跑后才绿的（enabled={acceptRetries}）");
        foreach (var note in manifest.RetryNotes)
        {
            Console.WriteLine($"#   - {note}");
        }
    }

    var concluded = ExternalAcceptanceSession.TryConclude(input!, out var problems);

    if (concluded)
    {
        Console.WriteLine(ExternalAcceptanceSession.ToMarkdown(input!, concluded: true));
        Console.WriteLine("# 允许作出 external-lifecycle-verified 结论（由外部控制器记录并留痕）");
    }
    else
    {
        Console.Error.WriteLine("**不允许**作出 external-lifecycle-verified 结论：");
        foreach (var problem in problems)
        {
            Console.Error.WriteLine($"  - {problem}");
        }

        Console.WriteLine(ExternalAcceptanceSession.ToMarkdown(input!, concluded: false));
    }

    if (arguments.TryGetValue("record", out var recordPath) && !string.IsNullOrWhiteSpace(recordPath))
    {
        var directory = Path.GetDirectoryName(recordPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            recordPath,
            ExternalAcceptanceSession.ToMarkdown(input!, concluded)
                + (concluded ? string.Empty : "\n校验问题：\n" + string.Join('\n', problems.Select(problem => $"- {problem}")) + "\n"));
        Console.WriteLine($"# 验收记录已写入 {recordPath}");
    }

    return concluded ? 0 : 2;
}
