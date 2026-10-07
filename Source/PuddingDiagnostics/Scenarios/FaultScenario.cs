namespace PuddingCode.Diagnostics;

/// <summary>
/// 故障场景：**「注入的失败 → 期望的诊断结论」对照表**（可诊断基础设施设计 §6）。
/// <para>
/// 这是把「诊断知识」从文档搬进门禁的载体：任何一条场景失败即测试取红。
/// 新增 <see cref="DiagnosticCauseCode"/> 取值而不加场景 ⇒ 目录漂移用例取红。
/// </para>
/// </summary>
/// <param name="Name">场景名（kebab-case，稳定）。</param>
/// <param name="ExpectedCauseCode">期望稳定码。</param>
/// <param name="ExpectedPhase">期望阶段。</param>
/// <param name="ExpectedRetryable">期望可重试性（在未产出增量、且上下文默认标志下）。</param>
/// <param name="ExpectedHttpStatus">期望 HTTP 状态（仅 HTTP 类场景）。</param>
/// <param name="Context">现场上下文（消费方在真实代码里会填的那组值）。</param>
/// <param name="CreateException">异常工厂；必须抛出/返回**真实异常链**（堆栈可用于帧指纹判定）。</param>
/// <param name="DocAnchor">人读判读文档的锚点（Docs/08_how_debuge）。</param>
public sealed record FaultScenario(
    string Name,
    string ExpectedCauseCode,
    DiagnosticPhaseKind ExpectedPhase,
    bool ExpectedRetryable,
    int? ExpectedHttpStatus,
    DiagnosticContext Context,
    Func<Exception> CreateException,
    string DocAnchor);
