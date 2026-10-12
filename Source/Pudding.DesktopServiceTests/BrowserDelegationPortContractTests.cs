using PuddingCode.Abstractions;

namespace DesktopServiceTests;

/// <summary>
/// R4（§6.2）**委派生命周期端口**的契约用例：该端口是 Runtime/Platform 与 Host 之间的唯一接缝，
/// 因此它的**语义**必须被钉住 —— 尤其是"**拒绝必须可判且必须给理由**"：
/// 调用方要据此 fail-closed（子授权拿不到就不该让子运行带着浏览器权限跑）。
/// </summary>
public sealed class BrowserDelegationPortContractTests
{
    [Fact]
    public void DeniedGrantIsNotGrantedAndMustCarryAReason()
    {
        var denied = new BrowserDelegationGrantResult(
            GrantId: null,
            GrantedScopeName: null,
            ExpiresAtUtc: null,
            DenialReason: "the parent grant does not cover this target");

        // 判据：没有 GrantId 就**不是**已授权（调用方据此 fail-closed）。
        Assert.False(denied.Granted);
        // 拒绝**必须**能说清原因（无声的"没有权限"是不可诊断的）。
        Assert.False(string.IsNullOrWhiteSpace(denied.DenialReason));
    }

    [Fact]
    public void GrantedGrantCarriesAnIdentifierAndADeadline()
    {
        var expires = new DateTimeOffset(2026, 10, 12, 12, 5, 0, TimeSpan.Zero);

        var granted = new BrowserDelegationGrantResult("grant-1", "read", expires);

        Assert.True(granted.Granted);
        Assert.Equal("grant-1", granted.GrantId);
        Assert.Equal(expires, granted.ExpiresAtUtc);
    }

    [Fact]
    public void RequestCarriesTheTrustedParentAndTheRealChildRun()
    {
        var request = new BrowserDelegationGrantRequest(
            CallerKind: "sub_agent",
            CallerId: "child-agent",
            ParentRunId: "run-parent",
            ParentTaskId: "task-1",
            ChildRunId: "run-child",
            ContextId: "ctx-1",
            PageId: "page-1",
            ScopeName: "read",
            RequestedValidity: TimeSpan.FromMinutes(5));

        // 父身份与**真实子 run** 都必须在请求里 —— 实现方据此校验"父授权覆盖 + 子范围是父的子集"。
        Assert.Equal("run-parent", request.ParentRunId);
        Assert.Equal("run-child", request.ChildRunId);
        Assert.NotEqual(request.ParentRunId, request.ChildRunId);
        // 范围用**线名**（Core 不引用 Contracts 的枚举）⇒ 未知线名由实现侧明确拒绝。
        Assert.Equal("read", request.ScopeName);
    }
}
