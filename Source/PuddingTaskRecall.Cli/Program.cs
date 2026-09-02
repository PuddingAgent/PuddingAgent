using System.Globalization;
using Microsoft.Data.Sqlite;
using PuddingTaskRecall.Cli;

// ─────────────────────────────────────────────────────────────
// 历史脏数据一次性诊断/修复 CLI（看板卡 4ed930e7 原子任务③）
// 默认 dry-run：只读诊断并把报告落盘；显式 --apply 才写库，
// 写库前先复制 db 文件备份到 temp\（含时间戳），全部修复包在单事务中，失败整体回滚。
// ─────────────────────────────────────────────────────────────

var exitCode = await RunAsync(args);
return exitCode;

static async Task<int> RunAsync(string[] args)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;

    string? dbPath = null;
    var apply = false;
    string? outPath = null;
    string? backupDir = null;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--apply":
                apply = true;
                break;
            case "--db" when i + 1 < args.Length:
                dbPath = args[++i];
                break;
            case "--out" when i + 1 < args.Length:
                outPath = args[++i];
                break;
            case "--backup-dir" when i + 1 < args.Length:
                backupDir = args[++i];
                break;
            case "--help" or "-h":
                Console.WriteLine(Usage());
                return 0;
            default:
                if (dbPath is null && !args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    dbPath = args[i];
                    break;
                }
                Console.Error.WriteLine($"未知参数：{args[i]}");
                Console.Error.WriteLine(Usage());
                return 2;
        }
    }

    if (dbPath is null)
    {
        Console.Error.WriteLine(Usage());
        return 2;
    }
    if (!File.Exists(dbPath))
    {
        Console.Error.WriteLine($"数据库文件不存在：{dbPath}");
        return 2;
    }

    var nowUtc = DateTimeOffset.UtcNow;
    var tempDir = Path.Combine(Environment.CurrentDirectory, "temp");

    string? backupPath = null;
    if (apply)
    {
        var dir = string.IsNullOrWhiteSpace(backupDir) ? tempDir : backupDir!;
        Directory.CreateDirectory(dir);
        var stamp = nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        backupPath = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(dbPath)}_backup_{stamp}.db");
        while (File.Exists(backupPath))
        {
            backupPath = Path.Combine(dir,
                $"{Path.GetFileNameWithoutExtension(dbPath)}_backup_{stamp}-{Guid.NewGuid().ToString("N")[..6]}.db");
        }
        File.Copy(dbPath, backupPath);
        Console.WriteLine($"[backup] {backupPath}");
        Console.WriteLine("[warn] 备份为主 db 文件快照；宿主进程运行中时 WAL 内未 checkpoint 的数据不在备份内，--apply 建议在宿主停止后执行。");
    }

    var connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Mode = apply ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString();

    try
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var result = TaskRecallAuditEngine.Analyze(connection, Path.GetFullPath(dbPath), nowUtc);
        if (apply)
        {
            var outcome = await TaskRecallAuditEngine.ApplyAsync(connection, result, nowUtc);
            result = result with
            {
                Applied = outcome.Committed,
                ApplyResult = outcome with { BackupPath = backupPath },
            };
        }

        var markdown = TaskRecallReportWriter.BuildMarkdown(result);

        var reportPath = ResolveReportPath(outPath, tempDir, apply, nowUtc);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.WriteAllText(reportPath, markdown);
        Console.WriteLine();
        Console.WriteLine(markdown);
        Console.WriteLine($"[report] {reportPath}");

        if (apply && result.ApplyResult is { Committed: false })
        {
            Console.Error.WriteLine($"[error] APPLY 未提交（已整体回滚）：{result.ApplyResult.Error}");
            return 3;
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[fatal] {ex.GetType().Name}: {ex.Message}");
        Console.Error.WriteLine(ex.StackTrace);
        return 4;
    }
}

static string ResolveReportPath(string? outPath, string tempDir, bool apply, DateTimeOffset nowUtc)
{
    if (!string.IsNullOrWhiteSpace(outPath))
    {
        return Path.GetFullPath(outPath!.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || outPath!.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? outPath
            : Path.Combine(outPath, DefaultReportName(apply, nowUtc)));
    }
    return Path.Combine(tempDir, DefaultReportName(apply, nowUtc));
}

static string DefaultReportName(bool apply, DateTimeOffset nowUtc)
    => $"{(apply ? "recall-apply" : "recall-dryrun")}-{nowUtc:yyyyMMdd-HHmmss}.md";

static string Usage() => """
    用法：PuddingTaskRecall.Cli <dbPath> [--apply] [--out <dirOrFile>] [--backup-dir <dir>]

      <dbPath>        SQLite 库路径（如 D:\data\databases\pudding_platform.db）
      --apply         写库模式（默认 dry-run 只读诊断）；写库前自动备份 db 文件到 temp\
      --out           报告输出目录或 .md 文件路径（默认 <cwd>\temp\recall-{dryrun|apply}-<时间戳>.md）
      --backup-dir    备份目录（默认 <cwd>\temp；备份文件名含时间戳）

    修复对象：
      A. workspace_tasks status=Completed(8) 缺 TaskCompleted(event_type=10) 事件 → 补写事件
      B. task_execution_bindings 空 execution_id/session_id → 终态任务回填 'no-execution' 占位
      C. task_assignment_attempts status IN (Reserved=0, Assigned=1) 未释放 → 终态释放
      D. agent_availability_projection 过期陈旧投影 → 删除（挂靠任务仍存活的动态跳过）
      status=4 (Failed) 行仅输出明细供人工裁决，不自动修。
    """;
