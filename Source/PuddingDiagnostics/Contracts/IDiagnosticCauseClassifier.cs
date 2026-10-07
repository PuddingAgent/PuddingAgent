namespace PuddingCode.Diagnostics;

/// <summary>
/// 因果分类器契约（可诊断基础设施设计 §5.2）。
/// <para>实现必须是**纯函数**：无 IO、无时间读取、无随机、无静态可变状态 —— 同输入必须同输出。</para>
/// </summary>
public interface IDiagnosticCauseClassifier
{
    DiagnosticCause Classify(Exception exception, DiagnosticContext context);
}
