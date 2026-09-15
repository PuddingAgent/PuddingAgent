using Microsoft.Extensions.Logging;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// Goal 验收迭代1：从 objective 文本解析**显式证据声明**，为目标级验收条件提供受检目标。
/// <para>
/// 语法是显式、字面的：objective 中出现标记「证据:」「证据：」「evidence:」「evidence：」
/// （英文标记大小写不敏感）时，标记之后同一行的内容按分隔符（中英文逗号/顿号/分号与空白）
/// 切分为候选目标；每个候选必须通过 <see cref="GoalCheckDefinitionRegistry.IsSafeTarget"/>
/// 才被接受，不安全者丢弃并记日志（绝不生成条件）。不做模糊推断、不做动态项目发现、
/// 不接受任意 shell 字符串。objective 为 null/空或未声明标记时返回空列表。
/// </para>
/// </summary>
public static class GoalObjectiveEvidenceParser
{
    private static readonly string[] Markers =
    [
        "证据:",
        "证据：",
        "evidence:",
        "evidence：",
    ];

    private static readonly char[] Separators = [',', '，', '、', ';', '；', ' ', '\t'];

    /// <summary>解析 objective 的显式证据目标；去重（Ordinal）并保持出现顺序。</summary>
    public static IReadOnlyList<string> ExtractEvidenceTargets(string? objective, ILogger? logger = null)
    {
        var targets = new List<string>();
        if (string.IsNullOrWhiteSpace(objective))
            return targets;

        foreach (var rawLine in objective.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            foreach (var (valueStart, segmentEnd) in CollectMarkerSegments(line))
            {
                var segment = line[valueStart..segmentEnd];
                foreach (var rawToken in segment.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
                {
                    var token = rawToken.Trim();
                    if (token.Length == 0)
                        continue;
                    if (!GoalCheckDefinitionRegistry.IsSafeTarget(token))
                    {
                        logger?.LogWarning(
                            "[GoalContract] rejecting unsafe evidence target {Target} from objective declaration",
                            token);
                        continue;
                    }

                    if (!targets.Contains(token, StringComparer.Ordinal))
                        targets.Add(token);
                }
            }
        }

        return targets;
    }

    /// <summary>
    /// 找出单行内所有标记的位置；每个标记的候选段从该标记结束处开始、到下一个标记开始处
    /// （或行尾）结束，使多个标记互不重叠、各自管理自己之后的内容。
    /// </summary>
    private static List<(int ValueStart, int SegmentEnd)> CollectMarkerSegments(string line)
    {
        var positions = new List<(int Start, int Length)>();
        foreach (var marker in Markers)
        {
            var scanFrom = 0;
            while (scanFrom <= line.Length - marker.Length)
            {
                var at = line.IndexOf(marker, scanFrom, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    break;
                positions.Add((at, marker.Length));
                scanFrom = at + marker.Length;
            }
        }

        if (positions.Count == 0)
            return [];

        positions.Sort((left, right) => left.Start.CompareTo(right.Start));

        var segments = new List<(int ValueStart, int SegmentEnd)>(positions.Count);
        for (var index = 0; index < positions.Count; index++)
        {
            var valueStart = positions[index].Start + positions[index].Length;
            var segmentEnd = index + 1 < positions.Count ? positions[index + 1].Start : line.Length;
            segments.Add((valueStart, segmentEnd));
        }

        return segments;
    }
}
