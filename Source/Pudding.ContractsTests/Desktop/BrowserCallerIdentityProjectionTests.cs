using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>
/// 可信调用身份的投影（设计 §3.1/§3.2）：身份**只能**来自 Core 的执行上下文，
/// 缺运行身份/缺调用者/子代理缺父任务都必须**明确拒绝**（不能"给个默认身份"）。
/// </summary>
public sealed class BrowserCallerIdentityProjectionTests
{
    [Fact]
    public void AnAgentExecutionContextProjectsToAnAgentCaller()
    {
        Assert.True(
            BrowserCallerIdentityProjection.TryProject(
                isSubAgent: false, callerId: "agent-1", runId: "run-7", parentRunId: null,
                out var identity, out var reason),
            reason);

        Assert.Equal(BrowserCallerKind.Agent, identity!.Kind);
        Assert.Equal("agent-1", identity.CallerId);
        Assert.Equal("run-7", identity.TaskId);
        Assert.Null(identity.ParentTaskId);
    }

    [Fact]
    public void ASubAgentCarriesTheParentTaskSoItsScopeCanBeCheckedAsASubset()
    {
        Assert.True(
            BrowserCallerIdentityProjection.TryProject(
                isSubAgent: true, callerId: "agent-1", runId: "run-sub", parentRunId: "run-parent",
                out var identity, out var reason),
            reason);

        Assert.Equal(BrowserCallerKind.SubAgent, identity!.Kind);
        Assert.Equal("run-sub", identity.TaskId);
        Assert.Equal("run-parent", identity.ParentTaskId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ASubAgentWithoutAParentTaskIsRefused(string? parentRunId)
    {
        // §3.2：子代理只能取得父任务授权的**子集**；没有父任务就没法做子集校验 ⇒ 拒绝。
        Assert.False(
            BrowserCallerIdentityProjection.TryProject(
                isSubAgent: true, callerId: "agent-1", runId: "run-sub", parentRunId: parentRunId,
                out var identity, out var reason));

        Assert.Null(identity);
        Assert.Contains("parent task", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AMissingRunIdentityIsRefused(string? runId)
    {
        // 没有运行身份 ⇒ 授权无法绑定到一次具体任务（也就无法在任务结束后回收/撤权）。
        Assert.False(
            BrowserCallerIdentityProjection.TryProject(
                isSubAgent: false, callerId: "agent-1", runId: runId, parentRunId: null,
                out var identity, out var reason));

        Assert.Null(identity);
        Assert.Contains("run identity", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AMissingCallerIdentityIsRefused(string? callerId)
    {
        Assert.False(
            BrowserCallerIdentityProjection.TryProject(
                isSubAgent: false, callerId: callerId, runId: "run-7", parentRunId: null,
                out var identity, out var reason));

        Assert.Null(identity);
        Assert.Contains("caller identity", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentitiesAreTrimmedSoWhitespaceCannotForgeADifferentActor()
    {
        // "agent-1 " 与 "agent-1" 必须是**同一个**行动者；否则用空白变体就能绕过同行动者判据。
        Assert.True(
            BrowserCallerIdentityProjection.TryProject(
                isSubAgent: false, callerId: " agent-1 ", runId: " run-7 ", parentRunId: null,
                out var identity, out _));

        Assert.Equal("agent-1", identity!.CallerId);
        Assert.Equal("run-7", identity.TaskId);
    }

    [Fact]
    public void TheProjectionTakesNoBrowserRequestOrModelSuppliedInput()
    {
        // §3.1：「不让模型填写 epoch 或 owner 来获得权限」。这条以**签名形状**钉住：
        // 投影函数只接受执行上下文的标量（是否子代理 / 调用者 / 本次运行 / 父运行），
        // 没有任何承载工具参数（模型可控）的入参 —— 因此"模型自报身份"在这条路径上不可能发生。
        var method = typeof(BrowserCallerIdentityProjection).GetMethod(nameof(BrowserCallerIdentityProjection.TryProject));
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        var inputs = parameters.Where(parameter => !parameter.IsOut).ToArray();

        Assert.Equal(4, inputs.Length);
        Assert.Equal(typeof(bool), inputs[0].ParameterType);
        Assert.All(inputs.Skip(1), parameter => Assert.Equal(typeof(string), parameter.ParameterType));

        // 没有出现任何"请求/页面/授权/模型"类型的入参。
        Assert.DoesNotContain(
            parameters,
            parameter => parameter.ParameterType.Name.Contains("Request", StringComparison.Ordinal)
                || parameter.ParameterType.Name.Contains("Grant", StringComparison.Ordinal)
                || parameter.ParameterType.Name.Contains("Page", StringComparison.Ordinal));
    }
}
