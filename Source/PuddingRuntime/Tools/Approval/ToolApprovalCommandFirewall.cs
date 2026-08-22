using System.Text.Json;

namespace PuddingRuntime.Services.Tools;

/// <summary>防火墙确定性判定结果；null 表示灰区（落回既有 LLM 隐式审计）。</summary>
internal sealed record ToolApprovalFirewallDecision(bool Allowed, string RuleId, string Message)
{
    public static ToolApprovalFirewallDecision Allow(string ruleId, string message) =>
        new(true, ruleId, message);

    public static ToolApprovalFirewallDecision Deny(string ruleId, string message) =>
        new(false, ruleId, message);
}

/// <summary>
/// 审批字符串防火墙（任务 ce63f8c0，V1.0b 有意保持简单）。
/// 在 LLM 隐式审计之前做确定性判定，解决实测的两个问题：
/// 隐式审计每次裁决耗时 14-39 秒、同参数先拒后过的非确定性。
/// - 危险命令（rm/del/format/强杀/force-push 等字符串）→ 立即拒绝并引导 request_tool_approval；
/// - 安全命令（git 常规、构建/测试、只读探查）→ 立即放行；
/// - 其余灰区 → 返回 null，落回原有隐式审计（v2 三层漏斗上线后由其接管灰区）。
/// </summary>
internal static class ToolApprovalCommandFirewall
{
    internal const string SafeRuleId = "command_firewall_safe";
    internal const string DangerRuleId = "command_firewall_danger";

    private static readonly HashSet<string> FirewallToolIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "shell",
        "terminal_start",
    };

    /// <summary>危险子串（对引号外内容做小写比较）。V1 保持字符串判断，不做语法分析。</summary>
    private static readonly string[] DangerPatterns =
    [
        "rm -rf", "rm -fr", "rm -r ", "rmdir", "rd /s", " del ", "del /", "erase ",
        "remove-item", "clear-recyclebin", "format ", "shred", "diskpart", "mkfs",
        "reg delete", "taskkill", "stop-process", "kill -9", "kill -15", "pkill",
        "git push --force", "git push -f ", "git push -f\"", "push --force",
        "git clean -f", "git reset --hard", "git checkout -- .",
        "shutdown", "restart-computer", "stop-computer",
    ];

    /// <summary>危险首词：段首即为破坏性命令（引号外），覆盖 "del file" 这类无内嵌空格形态。</summary>
    private static readonly HashSet<string> DangerFirstTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "del", "erase", "rmdir", "rd", "shred", "diskpart", "format",
        "taskkill", "shutdown", "pkill", "mkfs",
    };

    /// <summary>安全首词：只读探查、构建测试、常规开发命令。</summary>
    private static readonly HashSet<string> SafeFirstTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        // 只读探查（pwsh / POSIX 双方言）
        "get-childitem", "gci", "dir", "ls", "get-content", "gc", "cat", "type",
        "head", "tail", "select-string", "sls", "grep", "findstr", "rg",
        "test-path", "get-item", "get-location", "pwd", "where.exe", "which", "where",
        "measure-object", "sort-object", "select-object", "measure-command", "tree", "wc",
        "echo", "write-output", "write-host", "set-location", "cd", "pushd", "popd",
        // 构建 / 测试 / 包管理
        "dotnet", "npm", "pnpm", "yarn", "node", "npx", "tsc", "vitest", "jest",
        "pytest", "cargo", "go", "mvn", "gradle", "make", "cmake",
        // git（push 例外见下方动词白名单）
        "git", "git.exe",
        // 轻度文件操作（不破坏既有内容）
        "copy-item", "copy", "cp", "xcopy", "new-item", "mkdir", "md", "touch",
        "get-filehash", "get-date", "compare-object", "fc", "diff",
    };

    /// <summary>git 安全动词白名单（push 默认放行普通形态，force 类已在危险表）。</summary>
    private static readonly HashSet<string> SafeGitVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "show", "branch", "add", "commit", "checkout", "switch",
        "restore", "stash", "fetch", "pull", "merge", "rebase", "grep", "ls-files",
        "init", "remote", "tag", "describe", "rev-parse", "config", "--version",
        "push", "worktree", "shortlog", "blame", "cherry-pick", "revert", "apply",
    };

    public static ToolApprovalFirewallDecision? Evaluate(string normalizedToolId, string? argumentsJson)
    {
        if (!FirewallToolIds.Contains(normalizedToolId))
            return null;

        var command = ExtractCommand(argumentsJson);
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var segments = SplitSegments(command);
        if (segments.Count == 0)
            return null;

        // 危险优先：任一段命中即确定性拒绝（秒级、可复现）。
        foreach (var segment in segments)
        {
            var danger = FindDanger(segment);
            if (danger is not null)
            {
                return ToolApprovalFirewallDecision.Deny(
                    DangerRuleId,
                    $"Command firewall blocked this command deterministically (matched '{danger}'). " +
                    "Destructive operations are never auto-approved. " +
                    "Recommended next step: call request_tool_approval with the exact planned command, purpose, rollback plan, and safety checks.");
            }
        }

        // 安全白名单：全部段都必须命中，且 git 段动词在白名单内。
        foreach (var segment in segments)
        {
            if (!IsSafeSegment(segment))
                return null; // 灰区：交回隐式审计
        }

        return ToolApprovalFirewallDecision.Allow(
            SafeRuleId,
            "Command firewall allowed this command deterministically " +
            "(read-only/build/test/regular-git class, no destructive pattern). No approval ticket required.");
    }

    private static string? ExtractCommand(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (document.RootElement.TryGetProperty("command", out var command)
                && command.ValueKind == JsonValueKind.String)
            {
                return command.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>按分隔符拆分复合命令；每段再去掉管道/重定向外壳，保留可判定主体。</summary>
    private static List<string> SplitSegments(string command)
    {
        var raw = command.Replace("\r\n", "\n").Split(
            [';', '&', '|', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return raw.Where(s => s.Length > 0).ToList();
    }

    /// <summary>危险匹配只看引号外内容，避免 commit 消息里的 " del " 之类误伤。</summary>
    private static string? FindDanger(string segment)
    {
        var outsideQuotes = StripQuotedParts(segment);
        var firstToken = outsideQuotes.TrimStart().Split(' ').FirstOrDefault()?.TrimEnd(':') ?? string.Empty;
        if (DangerFirstTokens.Contains(firstToken))
            return firstToken;

        var lowered = outsideQuotes.ToLowerInvariant();
        foreach (var pattern in DangerPatterns)
        {
            if (lowered.Contains(pattern, StringComparison.Ordinal))
                return pattern;
        }

        return null;
    }

    private static string StripQuotedParts(string segment)
    {
        var sb = new System.Text.StringBuilder(segment.Length);
        var inDouble = false;
        var inSingle = false;
        foreach (var c in segment)
        {
            if (c == '"' && !inSingle) { inDouble = !inDouble; continue; }
            if (c == '\'' && !inDouble) { inSingle = !inSingle; continue; }
            if (!inDouble && !inSingle)
                sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsSafeSegment(string segment)
    {
        // 用原始命令分词（保留引号）：git -C "path" verb 的参数值配对依赖原始词序，
        // 剥引号会让 -C 的值被误认为动词。引号内 first token 实践中不存在。
        var tokens = segment.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return false;

        var first = tokens[0].Trim('"', ':').ToLowerInvariant();
        if (!SafeFirstTokens.Contains(first))
            return false;

        if (first is "git" or "git.exe")
        {
            // git [-C <path>] [--work-tree <path>] ... <verb> ...：跳过带值全局参数找动词
            var index = 1;
            while (index < tokens.Length && tokens[index].StartsWith('-'))
            {
                index += tokens[index] is "-C" or "--git-dir" or "--work-tree" or "-c"
                    ? 2
                    : 1;
            }

            if (index >= tokens.Length)
                return false; // 只有参数没有动词，视为灰区
            var verb = tokens[index].Trim('"').ToLowerInvariant();
            if (!SafeGitVerbs.Contains(verb))
                return false;
        }

        return true;
    }
}
