using System.Text.RegularExpressions;

namespace PuddingMemoryGraph;

/// <summary>召回线索的种类。种类决定「要不要立刻去查记忆」，而不决定查什么库。</summary>
public enum MemoryCueKind
{
    /// <summary>实体：项目名、类名、引号内的专名。</summary>
    Entity = 0,
    /// <summary>文件路径 / URL。</summary>
    Path = 1,
    /// <summary>时间引用：「上次」「之前」「继续」—— 这类词几乎只在「要你想起过去」时出现。</summary>
    TimeReference = 2,
    /// <summary>未完成状态：「还没弄完」「待办」。指向会话外的长期记忆。</summary>
    UnfinishedTask = 3,
    /// <summary>纠正 / 否定：用户说「不对」时，必须招回被推翻的旧结论。</summary>
    Correction = 4,
    /// <summary>
    /// 进度 / 状态类提问：「检查进度」「总结一下」「做到哪了」。
    /// 这类问题**天然预设了累积状态**，不查记忆就只能编。
    /// （来自真实短对话抽样：它们此前一条线索都提不出来，属漏召回。）
    /// </summary>
    ProgressQuery = 5,
}

/// <summary>一条线索。Strength 用于决定召回预算（强线索放宽，弱线索只顺带）。</summary>
public sealed record MemoryCue
{
    public required MemoryCueKind Kind { get; init; }
    public required string Text { get; init; }

    /// <summary>0..1。强线索（路径/时间引用/纠正）≥0.8，中等 0.5~0.7，弱 <0.5。</summary>
    public required double Strength { get; init; }

    /// <summary>是否足够强，强到本轮应该主动去查长期记忆。</summary>
    public bool IsStrong => Strength >= 0.8;
}

/// <summary>
/// 从对话文本里提取**召回线索**（纯函数，无 IO）。
/// <para>
/// 为什么不能直接拿整句话去搜：那样召回的成败取决于措辞巧合。
/// 线索提取把「用户这句话里哪些部分指向过去」显式化，这样：
/// ① 召回可以被单测；② 回放评估有了稳定的输入；③ 阈值可以按线索类型分别调。
/// </para>
/// <para>
/// 刻意**不做的**：不做分词、不做语义模型、不猜同义词。这些都该在检索层做，
/// 混进线索层会让「为什么这条线索被提出来」变得无法解释。
/// </para>
/// </summary>
public static class MemoryCueExtractor
{
    // 只保留"指向过去"的词。「继续」「continue」属于推进语义（1408 次实测），
    // 放进这里会让近半数 turn 误触发召回 —— 它们由 ResumeWords 以弱线索处理。
    private static readonly string[] TimeWords =
    [
        "上次", "上回", "之前", "刚才", "早先", "那天", "昨天", "前天",
        "接着说", "还没说完", "上次说", "上次那个", "接着上次",
        "earlier", "last time", "previously", "as before",
    ];

    /// <summary>
    /// 进度 / 状态类提问。**只收短语，不收裸词**。
    /// <para>
    /// 实测教训：第一版收了「进度 / 总结 / 状态 / 汇报 / summary / status」，
    /// 结果长任务简报的触发率从 78.4% 飙到 98.4% —— 因为那些词在模板文字里到处都是，
    /// 并不表示"在问进度"。所以这里要求「动词 + 对象」的短语形态。
    /// </para>
    /// </summary>
    private static readonly string[] ProgressWords =
    [
        "检查进度", "看下进度", "当前进度", "进度如何", "进展如何",
        "总结一下", "总结今天", "工作总结", "报告你的", "汇报一下",
        "做到哪", "还剩什么", "剩下什么", "做完多少",
        "自我诊断", "诊断一下",
    ];

    /// <summary>"继续干活"类词：说明任务没做完，不说明要想起过去。</summary>
    private static readonly string[] ResumeWords =
    [
        "继续", "接着", "往下", "continue", "go on",
    ];

    private static readonly string[] CorrectionWords =
    [
        "不对", "不太对", "不是这样", "错了", "纠正", "改成", "应该是", "其实不是",
        "no,", "actually", "that's wrong", "correction",
    ];

    private static readonly string[] UnfinishedWords =
    [
        "还没", "没弄完", "未完成", "待办", "剩下", "剩下的", "遗留",
        "todo", "pending", "unfinished", "left over",
    ];

    // 只认「ASCII 路径段」：中文斜杠串（如 /目标进展/健康指标/下一步）不是路径。
    // 字符集刻意收窄到 ASCII，避免把随后的中文标点一起吃进来。
    private static readonly Regex PathPattern = new(
        @"(?:[A-Za-z]:(?!\\[ntr])\\[A-Za-z0-9._\\/\-]{2,})|(?:/(?:[A-Za-z0-9._\-]{2,}/)+[A-Za-z0-9._\-]{2,})|(?:https?://[^\s""'<>\u4e00-\u9fff]+)",
        RegexOptions.CultureInvariant);

    private static readonly Regex QuotedPattern = new(
        @"[「『""](?<t>[^」』""]{2,40})[」』""]|`(?<t>[^`]{2,40})`",
        RegexOptions.CultureInvariant);

    // 允许「缩写前缀 + PascalCase」：IAgentLoopHook / URLBuilder / XDocumentReader 都要能认出来。
    // 只匹配首字母大写且至少两段 CamelCase 的形态，避免把普通英文单词当成标识符。
    private static readonly Regex IdentifierPattern = new(
        @"\b(?<t>[A-Z]{0,3}[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+)\b",
        RegexOptions.CultureInvariant);

    /// <summary>提取线索，按强度降序、同类内保持出现顺序。</summary>
    public static IReadOnlyList<MemoryCue> Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var cues = new List<MemoryCue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(MemoryCueKind kind, string value, double strength)
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0)
                return;
            if (!seen.Add(kind + "|" + trimmed))
                return;
            cues.Add(new MemoryCue { Kind = kind, Text = trimmed, Strength = strength });
        }

        foreach (Match m in PathPattern.Matches(text))
            Add(MemoryCueKind.Path, m.Value, 0.95);

        foreach (var w in TimeWords)
        {
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase))
                Add(MemoryCueKind.TimeReference, w, 0.9);
        }

        foreach (var w in CorrectionWords)
        {
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase))
                Add(MemoryCueKind.Correction, w, 0.9);
        }

        foreach (var w in UnfinishedWords)
        {
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase))
                Add(MemoryCueKind.UnfinishedTask, w, 0.6);
        }

        foreach (var w in ProgressWords)
        {
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase))
                Add(MemoryCueKind.ProgressQuery, w, 0.9);
        }

        foreach (var w in ResumeWords)
        {
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase))
                Add(MemoryCueKind.UnfinishedTask, w, 0.5);
        }

        foreach (Match m in QuotedPattern.Matches(text))
        {
            var value = m.Groups["t"].Value;
            if (!string.IsNullOrWhiteSpace(value))
                Add(MemoryCueKind.Entity, value, 0.6);
        }

        foreach (Match m in IdentifierPattern.Matches(text))
            Add(MemoryCueKind.Entity, m.Groups["t"].Value, 0.4);

        return cues
            .OrderByDescending(c => c.Strength)
            .ThenBy(c => c.Kind)
            .ToList();
    }

    /// <summary>
    /// 本轮是否值得主动查长期记忆。
    /// <para>
    /// 只有「强线索」才算数：普通闲聊不该触发召回 —— 每轮全量召回会挤爆预算，
    /// 而预算一旦被无关记忆占满，真正该想起的东西反而进不来。
    /// </para>
    /// </summary>
    public static bool ShouldRecall(IReadOnlyList<MemoryCue> cues)
    {
        if (cues is not { Count: > 0 })
            return false;

        // 实测依据（3000 条真实 turn）：仅靠 Path 判定会把 42.9% 的 turn 打成"要召回"，
        // 但绝大多数路径只是"当前要操作的文件"，与回忆过去无关。
        // ⇒ 只有「指向过去」的线索才单独触发：
        //   · Correction  —— 必须招回被推翻的旧结论
        //   · TimeReference —— 「上次」「之前」
        //   · UnfinishedTask（≥0.6）——「还没弄完」的真未完成语义（「继续」只算 0.5，不触发）
        return cues.Any(c =>
            c.Kind is MemoryCueKind.Correction or MemoryCueKind.TimeReference or MemoryCueKind.ProgressQuery
            || (c.Kind == MemoryCueKind.UnfinishedTask && c.Strength >= 0.6));
    }
}
