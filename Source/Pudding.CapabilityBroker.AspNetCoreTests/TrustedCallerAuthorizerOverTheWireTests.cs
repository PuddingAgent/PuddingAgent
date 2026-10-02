using Grpc.Core;
using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace CapabilityBrokerAspNetCoreTests;

/// <summary>
/// 授权在**真实通道**上的行为（不是单测替身）。本轮把产品授权器从 `DenyAll` 换成可信身份门禁，
/// 因此必须验证两件只有端到端才看得见的事：
/// ① 无身份 ⇒ 被拒，**且命令根本不下发**（授权必须发生在发送之前——否则"拒绝"只是桌面侧白挨一次命令）；
/// ② 有可信身份 ⇒ 命令正常下发、结果正常回填。
/// </summary>
public sealed class TrustedCallerAuthorizerOverTheWireTests
{
    private const string DesktopIdValue = "desk-test";

    [Fact]
    public async Task WithoutIdentity_DeniesAndNeverSendsTheCommand()
    {
        var identities = new MutableIdentityProvider();
        await using var harness = await StartAsync(identities);

        var call = harness.Connect();
        await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello(DesktopIdValue));
        await harness.ReadAsync(call, frame => frame.HelloAck is not null, "hello_ack");
        var session = harness.Broker.Sessions.Single();
        var context = new DesktopCallContext(
            new DesktopInstanceId(DesktopIdValue), new OperationId("op-denied"), DateTimeOffset.UtcNow.AddSeconds(30));

        var pending = session.NavigateAsync(
            new NavigateRequest(new DesktopPageTarget("ctx-1", "page-1"), new Uri("https://example.com/a")),
            context);

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, result.Error!.Code);

        // 关键断言：拒绝发生在发送之前 —— Desktop 侧不应看到任何帧。
        // 读取窗口内没有帧时 harness 会以 Cancelled 结束这次流读取，因此把它折成 null。
        async Task<Proto.CoreFrame?> ReadWithinWindowAsync()
        {
            try
            {
                return await harness.ReadAsync(call);
            }
            catch (RpcException)
            {
                return null;
            }
        }

        Assert.Null(await ReadWithinWindowAsync());
    }

    [Fact]
    public async Task WithTrustedIdentity_SendsTheCommandAndCompletesTheCall()
    {
        var identities = new MutableIdentityProvider { Current = Identity() };
        await using var harness = await StartAsync(identities);

        var call = harness.Connect();
        await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello(DesktopIdValue));
        await harness.ReadAsync(call, frame => frame.HelloAck is not null, "hello_ack");
        var session = harness.Broker.Sessions.Single();
        var context = new DesktopCallContext(
            new DesktopInstanceId(DesktopIdValue), new OperationId("op-allowed"), DateTimeOffset.UtcNow.AddSeconds(30));

        var pending = session.NavigateAsync(
            new NavigateRequest(new DesktopPageTarget("ctx-1", "page-1"), new Uri("https://example.com/a")),
            context);

        var commandFrame = await harness.ReadAsync(call, frame => frame.Command is not null, "navigate command");
        Assert.Equal("webview.navigate", commandFrame.Command.Capability);
        Assert.Equal("op-allowed", commandFrame.Command.OperationId);

        await call.RequestStream.WriteAsync(new Proto.DesktopFrame
        {
            Result = new Proto.OperationResult
            {
                OperationId = "op-allowed",
                Generation = 1,
                Navigate = new Proto.NavigateOutcome
                {
                    Disposition = Proto.NavigateDisposition.Completed,
                    CurrentUrl = "https://example.com/done",
                    PageVersion = 4,
                },
            },
        });

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsSuccess);
        Assert.Equal(NavigateDisposition.Completed, result.Value.Disposition);
    }

    [Fact]
    public async Task IdentityWithoutExecution_DeniesMutatingCapabilityBeforeSending()
    {
        // 身份在、但缺"某次可识别的 Agent 执行" ⇒ 变更类能力仍必须被拒（审计要求）。
        var identities = new MutableIdentityProvider
        {
            Current = Identity() with { ExecutionId = null },
        };
        await using var harness = await StartAsync(identities);

        var call = harness.Connect();
        await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello(DesktopIdValue));
        await harness.ReadAsync(call, frame => frame.HelloAck is not null, "hello_ack");
        var session = harness.Broker.Sessions.Single();
        var context = new DesktopCallContext(
            new DesktopInstanceId(DesktopIdValue), new OperationId("op-no-exec"), DateTimeOffset.UtcNow.AddSeconds(30));

        var pending = session.NavigateAsync(
            new NavigateRequest(new DesktopPageTarget("ctx-1", "page-1"), new Uri("https://example.com/a")),
            context);

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, result.Error!.Code);

        async Task<Proto.CoreFrame?> ReadWithinWindowAsync()
        {
            try
            {
                return await harness.ReadAsync(call);
            }
            catch (RpcException)
            {
                return null;
            }
        }

        Assert.Null(await ReadWithinWindowAsync());
    }

    private static async Task<CapabilityHostHarness> StartAsync(IDesktopCallerIdentityProvider identities) =>
        await CapabilityHostHarness.StartAsync(
            configure: builder => builder.Authorizer = new TrustedCallerDesktopCapabilityAuthorizer(identities));

    private static DesktopCallerIdentity Identity() => new()
    {
        SessionId = "session-1",
        AgentInstanceId = "agent-1",
        ExecutionId = "exec-1",
        UserId = "user-1",
    };

    private sealed class MutableIdentityProvider : IDesktopCallerIdentityProvider
    {
        public DesktopCallerIdentity? Current { get; set; }

        public DesktopCallerIdentity? TryGetCurrent() => Current;
    }
}
