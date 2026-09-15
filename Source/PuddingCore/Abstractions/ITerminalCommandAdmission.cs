namespace PuddingCode.Abstractions;

/// <summary>
/// ADR-092 §13.4：受控检查（build/test/postcondition）执行前的命令准入。
/// <para>
/// 与 terminal 工具共用同一实现（Runtime 的 <c>ITerminalCommandPolicy</c>）：受控检查
/// 不得成为绕过白名单、危险模式拦截与宿主安全不变量的旁路。抽象落在 Core，是因为
/// PuddingPlatform（Goal 检查执行器所在层）只依赖 Core，不能反向依赖 PuddingRuntime。
/// </para>
/// </summary>
public interface ITerminalCommandAdmission
{
    /// <summary>
    /// 校验命令是否允许执行；不允许时抛出（fail-closed）。
    /// 调用方不得吞掉异常继续启动进程，也不得把拒绝降级为通过。
    /// </summary>
    /// <exception cref="System.UnauthorizedAccessException">命令不在白名单或匹配危险模式。</exception>
    void EnsureAllowed(string command, bool isYoloMode);
}
