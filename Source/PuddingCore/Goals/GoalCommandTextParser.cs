using System.Text.RegularExpressions;

namespace PuddingCode.Goals;

/// <summary>
/// ADR-074 §4: /goal 严格 grammar 解析器（纯函数，无 I/O）。
/// <para>
/// 子命令消歧规则：首 token 命中保留字（status/set/edit/replace/pause/resume/cancel/clear，
/// 大小写不敏感）即按子命令处理并对其余部分做严格校验；否则整个剩余文本视为 objective
/// （set 简写）。`/goal` 等价 `/goal status`，空参数绝不隐式创建目标。
/// </para>
/// <para>
/// `--rounds N` 从 objective 文本中提取（最后一次出现生效），N 必须是 1..256 的整数；
/// 越界或格式非法返回 invalid_rounds，不静默截断。objective 去除首尾空白后 1–4000 字符。
/// </para>
/// </summary>
public static partial class GoalCommandTextParser
{
    private static readonly string[] ReservedSubcommands =
    [
        "status", "set", "edit", "replace", "pause", "resume", "cancel", "clear",
    ];

    public static bool TryParse(
        string? rawText,
        out GoalCommand command,
        out string? errorCode,
        out string? errorMessage)
    {
        command = null!;
        errorCode = null;
        errorMessage = null;

        var text = rawText?.Trim();
        if (string.IsNullOrWhiteSpace(text)
            || !text.StartsWith("/goal", StringComparison.OrdinalIgnoreCase)
            || (text.Length > 5 && !char.IsWhiteSpace(text[5])))
        {
            errorCode = GoalErrorCodes.InvalidCommand;
            errorMessage = "Command must start with '/goal'.";
            return false;
        }

        var rest = text[5..].Trim();

        // `/goal` 等价 `/goal status`（ADR-074 §4），不得隐式创建目标。
        if (rest.Length == 0)
        {
            command = new GoalCommand { Kind = GoalCommandKind.Status };
            return true;
        }

        var firstSpace = IndexOfFirstWhiteSpace(rest);
        var firstToken = firstSpace < 0 ? rest : rest[..firstSpace];
        var remainder = firstSpace < 0 ? string.Empty : rest[firstSpace..].Trim();

        if (TryMatchReservedSubcommand(firstToken, remainder, out command, out errorCode, out errorMessage))
            return command is not null;

        // 首 token 非保留字 → set 简写，整个 rest 是 objective。
        return TryBuildObjectiveCommand(GoalCommandKind.Set, rest, out command, out errorCode, out errorMessage);
    }

    private static bool TryMatchReservedSubcommand(
        string firstToken,
        string remainder,
        out GoalCommand? command,
        out string? errorCode,
        out string? errorMessage)
    {
        command = null;
        errorCode = null;
        errorMessage = null;

        var match = ReservedSubcommands.FirstOrDefault(
            reserved => string.Equals(reserved, firstToken, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;

        switch (match)
        {
            case "status":
            case "resume":
            case "clear":
                if (remainder.Length > 0)
                {
                    errorCode = GoalErrorCodes.InvalidCommand;
                    errorMessage = $"/goal {match} does not accept additional text.";
                    return true;
                }
                command = new GoalCommand { Kind = ToKind(match) };
                return true;

            case "pause":
            case "cancel":
                if (remainder.Length > GoalLimits.ObjectiveMaxLength)
                {
                    errorCode = GoalErrorCodes.InvalidObjective;
                    errorMessage = $"Reason exceeds {GoalLimits.ObjectiveMaxLength} characters.";
                    return true;
                }
                command = new GoalCommand
                {
                    Kind = ToKind(match),
                    Reason = remainder.Length == 0 ? null : remainder,
                };
                return true;

            case "set":
            case "edit":
            case "replace":
                // 保留字已消费：无论成败都不再回落到简写 objective 分支。
                TryBuildObjectiveCommand(ToKind(match), remainder, out command, out errorCode, out errorMessage);
                return true;

            default:
                errorCode = GoalErrorCodes.InvalidCommand;
                errorMessage = $"Unknown goal subcommand '{match}'.";
                return true;
        }
    }

    private static bool TryBuildObjectiveCommand(
        GoalCommandKind kind,
        string text,
        out GoalCommand? command,
        out string? errorCode,
        out string? errorMessage)
    {
        command = null;
        errorCode = null;
        errorMessage = null;

        var objective = ExtractRounds(text, out var rounds, out errorCode, out errorMessage);
        if (errorCode is not null)
            return false;

        objective = objective.Trim();
        if (objective.Length == 0)
        {
            errorCode = GoalErrorCodes.InvalidObjective;
            errorMessage = kind switch
            {
                GoalCommandKind.Edit => "/goal edit requires a non-empty objective.",
                _ => "Goal objective must be 1-4000 characters after trimming.",
            };
            return false;
        }

        if (objective.Length > GoalLimits.ObjectiveMaxLength)
        {
            errorCode = GoalErrorCodes.InvalidObjective;
            errorMessage = $"Goal objective exceeds {GoalLimits.ObjectiveMaxLength} characters (got {objective.Length}).";
            return false;
        }

        command = new GoalCommand { Kind = kind, Objective = objective, Rounds = rounds };
        return true;
    }

    /// <summary>提取并移除最后一次出现的 --rounds N；无则返回原文且 rounds = null。</summary>
    private static string ExtractRounds(
        string text,
        out int? rounds,
        out string? errorCode,
        out string? errorMessage)
    {
        rounds = null;
        errorCode = null;
        errorMessage = null;

        var match = RoundsRegex().Matches(text);
        if (match.Count == 0)
            return text;

        var last = match[^1];
        var rawValue = last.Groups["value"].Value;

        if (!int.TryParse(rawValue, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || !GoalLimits.IsValidIterationBudget(parsed))
        {
            errorCode = GoalErrorCodes.InvalidRounds;
            errorMessage =
                $"--rounds must be an integer between {GoalLimits.MinIterations} and " +
                $"{GoalLimits.MaxIterationsHardLimit}; got '{rawValue}'.";
            return text;
        }

        rounds = parsed;
        return text.Remove(last.Index, last.Length).TrimEnd();
    }

    private static int IndexOfFirstWhiteSpace(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
                return i;
        }

        return -1;
    }

    private static GoalCommandKind ToKind(string subcommand) => subcommand switch
    {
        "status" => GoalCommandKind.Status,
        "set" => GoalCommandKind.Set,
        "edit" => GoalCommandKind.Edit,
        "replace" => GoalCommandKind.Replace,
        "pause" => GoalCommandKind.Pause,
        "resume" => GoalCommandKind.Resume,
        "cancel" => GoalCommandKind.Cancel,
        "clear" => GoalCommandKind.Clear,
        _ => throw new InvalidOperationException($"Unknown reserved goal subcommand '{subcommand}'."),
    };

    [GeneratedRegex(@"--rounds[ \t]+(?<value>\S+)")]
    private static partial Regex RoundsRegex();
}
