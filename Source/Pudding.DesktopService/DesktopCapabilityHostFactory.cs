using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;

namespace Pudding.DesktopService;

/// <summary>
/// 用配置构造能力宿主（组合根里**唯一需要动脑**的那步，做成可直接测试的一次调用）。
///
/// 组合根因此只剩三件事：读 `desktop.json` → 调本工厂 → `StartAsync`。
/// 刻意<b>不在这里启动</b>：启动会占用「同一 DesktopId 单一活动传输」的名额，
/// 属于组合根的生命周期决定（关闭时必须根本不构造/不启动，见规格 §7.2）。
/// </summary>
public static class DesktopCapabilityHostFactory
{
    public static CapabilityResult<DesktopCapabilityHost> Create(
        DesktopCapabilityChannelSettings? settings,
        DesktopProcessInstanceId processInstanceId,
        DesktopChannelAuthentication? authentication,
        IDesktopChannelStreamFactory streamFactory,
        IDesktopCapabilityExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        ArgumentNullException.ThrowIfNull(executor);

        if (settings is null || !settings.Enabled)
        {
            // 关闭是**正常状态**：调用方据此保持旧 Bridge，不要构造宿主。
            return CapabilityResult<DesktopCapabilityHost>.Failure(
                DesktopCapabilityError.InvalidRequest("capability channel is disabled; keep the legacy bridge"));
        }

        var connectionOptions = settings.CreateConnectionOptions(processInstanceId, authentication);
        if (connectionOptions.IsFailure)
        {
            return CapabilityResult<DesktopCapabilityHost>.Failure(connectionOptions.Error);
        }

        try
        {
            var host = new DesktopCapabilityHost(
                new DesktopCapabilityHostOptions
                {
                    DesktopId = new DesktopInstanceId(settings.DesktopId),
                    Mode = DesktopCapabilityTransportMode.GrpcCapabilityChannel,
                },
                DesktopCapabilityHost.CreateGrpcSupervisorFactory(
                    streamFactory, executor, connectionOptions.Value));

            return CapabilityResult<DesktopCapabilityHost>.Success(host);
        }
        catch (ArgumentException ex)
        {
            // 例如 DesktopId 非法：配置问题要变成可判定的错误，而不是启动期异常。
            return CapabilityResult<DesktopCapabilityHost>.Failure(
                DesktopCapabilityError.InvalidRequest($"capability settings are not usable ({ex.ParamName})"));
        }
    }
}