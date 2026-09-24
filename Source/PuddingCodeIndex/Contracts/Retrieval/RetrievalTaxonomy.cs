namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 命中层（ADR-089 §1 的六层分层 + Unknown）。
/// <para>
/// <b>枚举值即层序</b>（0 = 最优先）：Auto 与各显式 intent 的层序表由
/// <see cref="RetrievalIntentPolicy.LayerOrder"/> 给出，比较器
/// （<see cref="RetrievalHitComparer"/>）必须使用同一组顺序，否则"排序可解释"会退化。
/// </para>
/// <para>
/// <see cref="Unknown"/> 排在最后（最不可信），不是 0：本枚举的 0 值有层序语义，
/// 而 <see cref="RetrievalHit"/> 的构造函数**不提供** kind 默认值，
/// 所以"没填 kind 就悄悄变成 Declaration"这种空洞路径不可表示。
/// </para>
/// </summary>
public enum RetrievalHitKind
{
    /// <summary>声明处 —— 定义该符号的文件（优先级最高）。</summary>
    Declaration = 0,

    /// <summary>实现 / 派生处 —— 实现该接口或继承它的类。</summary>
    Implementation = 1,

    /// <summary>直接引用处（new / 字段与参数类型 / using / import）。</summary>
    Reference = 2,

    /// <summary>调用处 —— 调用了它的成员。</summary>
    CallSite = 3,

    /// <summary>注释 / 文档提及 —— 只出现在文字里。</summary>
    DocMention = 4,

    /// <summary>代码正文偶然命中 —— 只是同名片段。</summary>
    TextHit = 5,

    /// <summary>无法归层。</summary>
    Unknown = 6,
}

/// <summary>
/// 可信度（ADR-089 §3："每条关系都要能回答证据在哪 + 多可信"）。
/// <para>
/// 数值即 <b>信任秩</b>（0 = 最不可信）：<c>Semantic</c> &gt; <c>Lexical</c> &gt; <c>Unknown</c>。
/// 过滤面的"置信度下限"按"保留信任秩 ≥ 下限"解释；<c>default</c> 落在
/// <see cref="Unknown"/>，即"什么都没说" ⇒ 最保守，不会被误当成语义级证据。
/// </para>
/// </summary>
public enum RetrievalConfidence
{
    /// <summary>未知 / 未判定（最保守）。</summary>
    Unknown = 0,

    /// <summary>词法猜测（如多数语言 outliner 给出的结构）。</summary>
    Lexical = 1,

    /// <summary>语义级（如 Roslyn 解析）。</summary>
    Semantic = 2,
}

/// <summary>
/// 关系类型（ADR-089 §3 的关系模型；与既有 <c>CodeRelationKind</c> 并存，
/// 本枚举只描述"检索面需要表达的关系"）。
/// <para><see cref="Unknown"/> 占 0：<c>default</c> = 没关系，不会伪装成 Calls。</para>
/// </summary>
public enum RetrievalRelationKind
{
    /// <summary>无 / 未判定。</summary>
    Unknown = 0,

    /// <summary>调用。</summary>
    Calls = 1,

    /// <summary>一般性引用（类型引用 / 字段与参数类型 / using）。</summary>
    References = 2,

    /// <summary>继承。</summary>
    Inherits = 3,

    /// <summary>实现。</summary>
    Implements = 4,

    /// <summary>重写。</summary>
    Overrides = 5,

    /// <summary>导入（using / import / require）—— 弱关系，提升文件级召回。</summary>
    Imports = 6,

    /// <summary>注释 / 文档里提到某符号。</summary>
    DocMentions = 7,
}

/// <summary>
/// 匹配域（ADR-089 §2.3）：<b>匹配发生在哪个面上</b>。
/// <para>
/// 用户第七轮裁定原文："假设：要搜索一个 conf 的类，那么 Agent 可以给出过滤条件，
/// 比如搜 '.cs'、限制目录、<b>只关注类名称</b>等" —— "只关注类名称"就是这个面，
/// 而现状（实测 <c>code_symbol_search("conf")</c> 返回 40 条全是 <c>.ctor</c>，
/// 命中发生在签名文本而非 <c>name</c>）**完全缺失**该面。
/// </para>
/// <para>
/// 显式 <see cref="FlagsAttribute"/>：多个面可组合（引擎可以"符号名 + 文档注释"一起匹配）。
/// <see cref="None"/> 是**可构造但会被拒绝**的取值 —— 一个不匹配任何域的过滤面必然返回空，
/// 属于"空洞否定"的燃料，见 <see cref="RetrievalFilter"/> 的校验。
/// </para>
/// </summary>
[Flags]
public enum RetrievalMatchTarget
{
    /// <summary>不匹配任何域（非法取值，构造过滤器时拒绝）。</summary>
    None = 0,

    /// <summary>符号名（"只关注类名称"就是这个）。</summary>
    SymbolName = 1,

    /// <summary>声明文本（含签名）。</summary>
    Declaration = 2,

    /// <summary>文档注释。</summary>
    DocComment = 4,

    /// <summary>代码正文。</summary>
    Body = 8,

    /// <summary>文件路径。</summary>
    Path = 16,

    /// <summary>全部域。</summary>
    All = SymbolName | Declaration | DocComment | Body | Path,
}
