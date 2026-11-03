using System.Diagnostics;
using System.Text;
using FlaUI.UIA3;
using PuddingComputer.Benchmark;
using PuddingComputer.BenchmarkRunner;
using PuddingComputer.UIAutomation;

Console.OutputEncoding = Encoding.UTF8;

// ============================================================
// 端到端跑批：加载任务清单 → 观察 → 匹配 → 评估 → 判定
// 严格只读：不点击、不输入、不改任何应用状态。
// ============================================================

var repoRoot = FindRepoRoot();
if (repoRoot is null) { Console.WriteLine("找不到仓库根"); return 1; }

var runId = DateTime.Now.ToString("yyyyMMdd-HHmm");
var workDir = Path.Combine(repoRoot, ".work", "e2e", runId);
Directory.CreateDirectory(workDir);
Console.WriteLine($"工作目录：{workDir}");
Console.WriteLine();

var catalogPath = Path.Combine(repoRoot, "Source", "PuddingComputer.Benchmark", "benchmarks", "native-apps.json");
if (!File.Exists(catalogPath)) { Console.WriteLine($"找不到任务清单：{catalogPath}"); return 1; }

var tasks = BenchmarkCatalog.Load(File.ReadAllText(catalogPath));
Console.WriteLine($"加载任务 {tasks.Count} 个");

var problems = BenchmarkCatalog.Validate(tasks);
if (problems.Count > 0)
{
    Console.WriteLine("清单校验失败：");
    foreach (var p in problems) Console.WriteLine($"  - {p}");
    return 1;
}
Console.WriteLine("清单校验通过");
Console.WriteLine();

// ---------- 按应用分组，每组启动一次 ----------
using var automation = new UIA3Automation();
using var inspector = new FlaUiInspector(automation);
var options = new UiaCaptureOptions(MaxDepth: 12, MaxNodes: 2000);

var results = new List<(BenchmarkTask Task, UiaAvailability Availability, OracleEvaluation Eval, AttemptDecision Decision)>();
var processes = new List<Process>();

try
{
    foreach (var group in tasks.GroupBy(t => t.App))
    {
        var app = group.Key;
        Console.WriteLine(new string('=', 74));
        Console.WriteLine($"### {app}");
        Console.WriteLine(new string('=', 74));

        var hwnd = EnsureAppWindow(automation, app, processes);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine($"  ✗ 找不到 {app} 的窗口");
            foreach (var t in group)
                results.Add((t, UiaAvailability.Unavailable,
                    OracleEvaluation.Unknown("找不到应用窗口", t.Oracle.Description),
                    AttemptDecision.Blocked));
            Console.WriteLine();
            continue;
        }

        var snapshot = inspector.Capture(hwnd, options);
        Console.WriteLine($"  可用性 = {snapshot.Availability}  节点 = {snapshot.Nodes.Count}  可操作 = {snapshot.ActionableCount}");
        if (snapshot.Reason is not null) Console.WriteLine($"  原因   = {snapshot.Reason}");
        Console.WriteLine();

        foreach (var task in group)
        {
            var eval = OracleEvaluator.Evaluate(task.Oracle, snapshot);
            var decision = BenchmarkPolicy.Decide(task, eval, attemptsUsed: 1);
            results.Add((task, snapshot.Availability, eval, decision));

            var mark = eval.Verdict switch
            {
                OracleVerdict.Pass => "PASS",
                OracleVerdict.Fail => "FAIL",
                _ => "????",
            };
            Console.WriteLine($"  [{mark}] {task.Id}");
            Console.WriteLine($"         oracle = {task.Oracle.Description}");
            Console.WriteLine($"         证据   = {eval.Evidence}");
            Console.WriteLine($"         决策   = {decision}");
        }
        Console.WriteLine();
    }
}
finally
{
    // 只关闭本程序启动的进程；原本就在运行的不动。
    foreach (var p in processes)
    {
        try { if (!p.HasExited) { p.CloseMainWindow(); p.WaitForExit(2000); if (!p.HasExited) p.Kill(true); } }
        catch { }
    }
}

// ---------- 汇总 ----------
Console.WriteLine(new string('=', 74));
Console.WriteLine("### 汇总");
Console.WriteLine(new string('=', 74));

int pass = results.Count(r => r.Eval.Verdict == OracleVerdict.Pass);
int fail = results.Count(r => r.Eval.Verdict == OracleVerdict.Fail);
int unknown = results.Count(r => r.Eval.Verdict == OracleVerdict.Unknown);

Console.WriteLine($"  PASS={pass}  FAIL={fail}  UNKNOWN={unknown}  合计={results.Count}");
Console.WriteLine();

// ---------- 写报告 ----------
var sb = new StringBuilder();
sb.AppendLine($"# 端到端跑批报告 {runId}");
sb.AppendLine();
sb.AppendLine("**只读**：本程序不点击、不输入、不改变任何应用状态。");
sb.AppendLine();
sb.AppendLine("| 任务 | 应用 | 可用性 | 判定 | 决策 | 证据 |");
sb.AppendLine("|---|---|---|---|---|---|");
foreach (var r in results)
{
    var v = r.Eval.Verdict switch { OracleVerdict.Pass => "✅ PASS", OracleVerdict.Fail => "❌ FAIL", _ => "❔ UNKNOWN" };
    var ev = r.Eval.Evidence.Replace("|", "\\|");
    sb.AppendLine($"| `{r.Task.Id}` | {r.Task.App} | {r.Availability} | {v} | {r.Decision} | {ev} |");
}
sb.AppendLine();
sb.AppendLine($"PASS={pass} · FAIL={fail} · UNKNOWN={unknown} · 合计={results.Count}");
sb.AppendLine();
sb.AppendLine("## oracle 明细");
sb.AppendLine();
foreach (var r in results)
{
    sb.AppendLine($"- `{r.Task.Id}`：{r.Task.Oracle.Description}");
}
var reportPath = Path.Combine(workDir, "report.md");
File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
Console.WriteLine($"报告已写：{reportPath}");

return 0;

// ============================================================

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Agents.md")) &&
            Directory.Exists(Path.Combine(dir.FullName, "Source")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

/// <summary>确保应用窗口存在并返回句柄；若需启动则记录下来以便收尾关闭。</summary>
static IntPtr EnsureAppWindow(UIA3Automation automation, string app, List<Process> started)
{
    var hints = app.ToLowerInvariant() switch
    {
        "calculator" => new[] { "计算器", "Calculator" },
        "notepad" => new[] { "记事本", "Notepad" },
        _ => new[] { app },
    };

    var hwnd = FindWindowByHints(automation, hints);
    if (hwnd != IntPtr.Zero) return hwnd;

    var exe = app.ToLowerInvariant() switch
    {
        "calculator" => "calc.exe",
        "notepad" => "notepad.exe",
        _ => null,
    };
    if (exe is null) return IntPtr.Zero;

    var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    if (p is not null) started.Add(p);
    Thread.Sleep(2500);

    return FindWindowByHints(automation, hints);
}

static IntPtr FindWindowByHints(UIA3Automation automation, string[] hints)
{
    var desktop = automation.GetDesktop();
    foreach (var w in desktop.FindAllChildren())
    {
        string name;
        int pid;
        try { name = w.Name ?? ""; } catch { continue; }
        try { pid = w.Properties.ProcessId.ValueOrDefault; } catch { pid = -1; }

        bool byName = hints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
        if (!byName) continue;

        // 跳过明显的系统窗口
        if (name.Contains("Program Manager", StringComparison.OrdinalIgnoreCase)) continue;

        try { return w.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
        _ = pid;
    }
    return IntPtr.Zero;
}
