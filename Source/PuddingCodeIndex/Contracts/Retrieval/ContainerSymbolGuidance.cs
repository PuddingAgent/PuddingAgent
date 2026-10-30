namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 容器符号（命名空间 / 类型 / 类 / 接口 / 结构 / 枚举 / 委托）在<b>关系面 0 条结果</b>时的统一说法。
/// <para>
/// 为什么需要它：索引里的关系边挂在<b>成员</b>上，容器自身可以一条边都没有。此时“该符号确实没有关系边”为真，
/// 但极易被读成“没人用它” ⇒ Agent 会换查询打转（ADR-089 §8.9 要消灭的正是这种空洞否定）。
/// </para>
/// <para>
/// 为什么放在契约层：同一件事实有两个面 —— 关系面检索
/// （<c>workspace_search(intent=callers, symbolRef=…)</c>）与
/// <c>workspace_open(view=relations, relation=…)</c>。若各写一份文案，两处会慢慢分叉，
/// 于是同一个问题在不同面得到不同答案。这里保留<b>唯一</b>一份文案，两面共用。
/// </para>
/// </summary>
public static class ContainerSymbolGuidance
{
    /// <summary>
    /// 说话的一方 = 关系面检索（<c>workspace_search</c>）。放在这里而不是调用点，
    /// 是为了让“文案里说谁”只有一处定义 —— 调用点只传常量，不写字面量。
    /// </summary>
    public const string RetrievalFace = "本端口";

    /// <summary>说话的一方 = <c>workspace_open(view=relations)</c>。</summary>
    public const string OpenViewFace = "本视图";

    /// <summary>
    /// 该种类是否“自己有成员、边挂在成员上”。
    /// <para>
    /// 只有关系面空结果需要区分它：别的意图下“确实没有”不会被误读成“没人用”。
    /// </para>
    /// </summary>
    public static bool IsContainer(CodeSymbolKind kind) =>
        kind is CodeSymbolKind.Namespace
            or CodeSymbolKind.Type
            or CodeSymbolKind.Class
            or CodeSymbolKind.Interface
            or CodeSymbolKind.Struct
            or CodeSymbolKind.Enum
            or CodeSymbolKind.Delegate;

    /// <summary>
    /// 容器锚点 0 条关系的说明文案。
    /// </summary>
    /// <param name="symbolName">锚点符号的显示名（用名字而非仅 id：调用方要能认出是哪个符号）。</param>
    /// <param name="kind">锚点符号种类，写进文案便于核对。</param>
    /// <param name="face">
    /// 是谁在说话 —— <c>本端口</c>（检索面）或 <c>本视图</c>（workspace_open）。
    /// 措辞必须与调用方实际看到的东西一致，否则“我没展开成员”这句话本身就成了误导。
    /// </param>
    public static string BuildEmptyRelationText(string symbolName, CodeSymbolKind kind, string face) =>
        $"锚点 {symbolName} 是容器符号（{kind}）：索引中的关系边挂在它的成员上，" +
        $"{face}不展开成员 ⇒ 该符号自身确实没有关系边，但这**不代表没人用它**。" +
        "要看谁在用它，请对具体成员（构造函数 / 方法 / 属性）签发 ref 再查。";

    /// <summary>
    /// 同上，但补上「索引可能未收录」这层可能。
    /// <para>
    /// 给 <c>workspace_open(view=relations)</c> 用：那个面原本只说“也可能是索引尚未建成”，
    /// 于是容器符号的 0 条被解释成“去查索引”，而不是“去看成员”。
    /// 两件事都要说，但<b>主次不能颠倒</b>——先给可执行的下一步（查成员），再留索引这条退路。
    /// </para>
    /// </summary>
    public static string BuildEmptyRelationNote(string symbolName, CodeSymbolKind kind, string face) =>
        BuildEmptyRelationText(symbolName, kind, face) +
        "（若对成员查询同样 0 条，才需要怀疑索引尚未收录该符号。）";
}
