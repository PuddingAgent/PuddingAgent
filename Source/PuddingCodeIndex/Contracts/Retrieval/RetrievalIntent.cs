namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 单次检索偏好（ADR-089 §2）：语义面向 <b>Agent 的意图</b>，不是引擎细节。
/// <para>
/// <see cref="Auto"/> <b>必须占 0 值位置</b>（设计 §2.1；用户第五轮裁定原文：
/// "RetrievalIntent，最好增加一个 auto 的枚举值 …… auto 是默认设置，避免让 Agent 打转"）：
/// 于是 <c>default(RetrievalIntent)</c> 天然等于 <see cref="Auto"/>，
/// 而"缺省即 Auto"是**结构上成立**的，不依赖任何调用方记得传参。
/// </para>
/// <para>
/// 把 Auto 移出 0 值会让"缺省"静默变成某个**显式**意图 —— 那会直接违反
/// "显式意图优先 / 不显式即不问意图"的反向要求，故本刀用契约测试 A1 钉住该位置。
/// </para>
/// </summary>
public enum RetrievalIntent
{
    /// <summary>
    /// ★默认。引擎承担意图推断 + 多路检索 + 融合排序，把"试错"关进引擎（毫秒级、零 token），
    /// 而不是外包给 Agent（一个 LLM 轮）。语义是"<b>不问意图</b>"，不是"<b>忽略意图</b>"。
    /// </summary>
    Auto = 0,

    /// <summary>找文件（路径 / 文件名）。</summary>
    File = 1,

    /// <summary>命名空间 / 包 / 模块。</summary>
    Namespace = 2,

    /// <summary>类 / 接口 / 结构 / 枚举 / 委托（统称"类型"）。</summary>
    Type = 3,

    /// <summary>函数 / 方法 / 属性 / 字段 / 构造 / 事件。</summary>
    Member = 4,

    /// <summary>注释与文档（P1 层；Markdown 检索 <c>recall@1=0.023</c> 正是这里）。</summary>
    Comment = 5,

    /// <summary>某接口 / 抽象成员的实现。</summary>
    Implementation = 6,

    /// <summary>所有引用某符号的代码。</summary>
    References = 7,

    /// <summary>调用方。</summary>
    Callers = 8,

    /// <summary>被调用方。</summary>
    Callees = 9,

    /// <summary>传递影响（<c>GetImpact</c>）。</summary>
    Impact = 10,

    /// <summary>不限。</summary>
    Any = 11,
}
