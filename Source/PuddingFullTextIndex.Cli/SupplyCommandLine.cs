using System.Globalization;

namespace PuddingFullTextIndex.Cli;

/// <summary>CLI 命令（规格 §3 R2 只有这四个；未知命令一律用法错误）。</summary>
internal enum SupplyCliCommand
{
    Plan,
    Status,
    Build,
    Cancel,
}

/// <summary>
/// 解析后的命令行。<b>结构由 <see cref="SupplyCommandLine"/> 保证</b>：
/// 必填参数齐全、数值已校验、且不含该命令不支持的选项（因此处理逻辑里不再做二次判断）。
/// </summary>
internal sealed record SupplyCliOptions(
    SupplyCliCommand Command,
    IReadOnlyList<string> Scopes,
    string? JobId,
    long? BudgetBytes,
    string? IndexRoot,
    bool Wait,
    bool Json);

/// <summary>解析结果：成功给 <see cref="Options"/>，失败给可读 <see cref="Error"/>（调用方据此返回退出码 5）。</summary>
internal sealed record SupplyCliParseResult(SupplyCliOptions? Options, string? Error)
{
    internal bool Succeeded => Options is not null;
}

internal static class SupplyCommandLine
{
    /// <summary>用法与退出码契约（用法错误时原样打到 stderr，绝不把错误吞成成功）。</summary>
    internal const string UsageText = """
        用法：
          PuddingFullTextIndex.Cli plan   --scope <绝对路径> [--scope <路径>]... [--budget-bytes <n>] [--index-root <路径>] [--json]
          PuddingFullTextIndex.Cli status [--job <jobId>] [--scope <绝对路径>]... [--index-root <路径>] [--json]
          PuddingFullTextIndex.Cli build  --scope <绝对路径> [--scope <路径>]... --index-root <路径> [--budget-bytes <n>] [--wait] [--json]
          PuddingFullTextIndex.Cli cancel --job <jobId> [--json]

        退出码：
          0  成功（plan 全部接受 / build 已受理 / build --wait 成功 / status 完成报告 / cancel 成功）
          2  被拒（scope 非法：plan 有任一拒绝项，或 build 返回 Rejected）
          3  本进程无法完成：scope 租约被本进程之外的 owner 持有（Busy），
             或 --job 不在本进程（job 状态存储是进程内的，跨进程属后续切片），或 --wait 超时
          4  执行未成功（build --wait 到达 Failed/Cancelled，或未预期异常）
          5  用法错误（未知命令 / 缺必填参数 / 非法数值 / 该命令不支持的选项）

        约定：
          - plan 是干跑，零写入；build 会真实写索引，因此**必须**显式给出 --index-root。
          - status 允许省略 --index-root，并会打印解析出的索引根（默认解析可能指向生产索引根）。
          - --job 在本进程查不到时必须如实报告 not_in_this_process，绝不静默返回成功。
        """;

    internal static SupplyCliParseResult Parse(string[] args)
    {
        if (args is null || args.Length == 0)
            return Fail("缺失命令：第一个参数必须是 plan|status|build|cancel。");

        if (!TryParseCommand(args[0], out var command))
            return Fail($"未知命令 '{args[0]}'：只支持 plan|status|build|cancel。");

        var scopes = new List<string>();
        string? jobId = null;
        long? budgetBytes = null;
        string? indexRoot = null;
        var wait = false;
        var json = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scope":
                    if (!TryTakeValue(args, ref i, out var scope, out var scopeError))
                        return Fail(scopeError);
                    scopes.Add(scope);
                    break;

                case "--job":
                    if (jobId is not null)
                        return Fail("--job 只能给出一次。");
                    if (!TryTakeValue(args, ref i, out var job, out var jobError))
                        return Fail(jobError);
                    jobId = job;
                    break;

                case "--budget-bytes":
                    if (budgetBytes is not null)
                        return Fail("--budget-bytes 只能给出一次。");
                    if (!TryTakeValue(args, ref i, out var budgetText, out var budgetError))
                        return Fail(budgetError);
                    if (!long.TryParse(budgetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget)
                        || budget <= 0)
                    {
                        return Fail($"--budget-bytes 需要正的整数字节数，收到 '{budgetText}'。");
                    }

                    budgetBytes = budget;
                    break;

                case "--index-root":
                    if (indexRoot is not null)
                        return Fail("--index-root 只能给出一次。");
                    if (!TryTakeValue(args, ref i, out var root, out var rootError))
                        return Fail(rootError);
                    indexRoot = root;
                    break;

                case "--wait":
                    wait = true;
                    break;

                case "--json":
                    json = true;
                    break;

                default:
                    return Fail($"选项 '{args[i]}' 不被支持。");
            }
        }

        // 命令专属校验：规格里没写的选项组合一律判用法错误（宁可拒绝，也不默默忽略）。
        switch (command)
        {
            case SupplyCliCommand.Plan:
                if (scopes.Count == 0)
                    return Fail("plan 至少需要一个 --scope <绝对路径>。");
                if (jobId is not null)
                    return Fail("plan 不支持 --job。");
                if (wait)
                    return Fail("plan 不支持 --wait。");
                break;

            case SupplyCliCommand.Status:
                if (wait)
                    return Fail("status 不支持 --wait。");
                if (budgetBytes is not null)
                    return Fail("status 不支持 --budget-bytes。");
                break;

            case SupplyCliCommand.Build:
                if (scopes.Count == 0)
                    return Fail("build 至少需要一个 --scope <绝对路径>。");
                if (indexRoot is null)
                    return Fail("build 必须显式给出 --index-root <路径>：build 会真实写索引，缺省解析可能落到生产索引根。");
                if (jobId is not null)
                    return Fail("build 不支持 --job。");
                break;

            case SupplyCliCommand.Cancel:
                if (jobId is null)
                    return Fail("cancel 必须给出 --job <jobId>。");
                if (scopes.Count > 0)
                    return Fail("cancel 不支持 --scope。");
                if (budgetBytes is not null)
                    return Fail("cancel 不支持 --budget-bytes。");
                if (wait)
                    return Fail("cancel 不支持 --wait。");
                if (indexRoot is not null)
                    return Fail("cancel 不支持 --index-root。");
                break;
        }

        return new SupplyCliParseResult(
            new SupplyCliOptions(command, scopes, jobId, budgetBytes, indexRoot, wait, json),
            Error: null);
    }

    /// <summary>取下一个参数作为某个选项的值；缺失或下一个参数本身是选项（<c>--</c> 开头）即视为缺值。</summary>
    private static bool TryTakeValue(string[] args, ref int index, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;

        var option = args[index];
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            error = $"{option} 缺少数值。";
            return false;
        }

        value = args[++index];
        return true;
    }

    private static SupplyCliParseResult Fail(string error) => new(Options: null, Error: error);

    private static bool TryParseCommand(string raw, out SupplyCliCommand command)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "plan":
                command = SupplyCliCommand.Plan;
                return true;
            case "status":
                command = SupplyCliCommand.Status;
                return true;
            case "build":
                command = SupplyCliCommand.Build;
                return true;
            case "cancel":
                command = SupplyCliCommand.Cancel;
                return true;
            default:
                command = default;
                return false;
        }
    }
}
