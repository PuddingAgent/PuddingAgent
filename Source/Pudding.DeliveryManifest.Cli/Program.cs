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

if (arguments is null || arguments.ContainsKey("help"))
{
    Console.WriteLine("""
        用法：Pudding.DeliveryManifest.Cli --build <id> --commit <sha> --publish <dir> --results <dir> --changelog <dir> [--extra-unverified <text>]... [--out <file>]

        采集三类事实并产出交付清单：
          --publish    发布目录（取其中的 .exe/.dll 作为产物，只记录文件名）
          --results    测试结果目录（读取其中的 *.trx 汇总计数）
          --changelog  修改日志目录（提取"内部证明不了的结论"）
          --build      构建标识   --commit 提交号
          --extra-unverified  额外补充的未验证项（可多次）
          --out        同时写入该文件
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
    arguments.TryGetValue("extra-unverified", out var extra) ? [extra] : null);

if (DeliveryFactCollector.TryCollect(request, out var manifest, out var problems))
{
    var json = DeliveryManifestValidator.Serialize(manifest!);
    Console.WriteLine(json);

    if (arguments.TryGetValue("out", out var outputPath) && !string.IsNullOrWhiteSpace(outputPath))
    {
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"# 已写入 {outputPath}");
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
        if (name is "help")
        {
            parsed["help"] = "1";
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
