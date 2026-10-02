using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using PuddingBrowser.Protocol;

namespace PuddingDesktop.Debug;

/// <summary>Core 的日志级别快照（与 <c>DiagnosticsLogLevelController</c> 的响应一一对应）。</summary>
/// <param name="Level">当前生效级别（Serilog 正式名）。</param>
/// <param name="IsVerbose">是否处于会放大日志量的档位（Verbose/Debug）。</param>
/// <param name="SupportedLevels">可切换的级别。</param>
/// <param name="ConfigFile">持久化文件路径（便于用户知道改到哪去了）。</param>
public sealed record DesktopLogLevelSnapshot(
    string Level,
    bool IsVerbose,
    IReadOnlyList<string> SupportedLevels,
    string ConfigFile);

/// <summary>
/// Desktop Shell 的 Debug 按钮用：读取/切换 Core 的日志级别。
///
/// <para>
/// 为什么走 HTTP 而不是进程内调用：日志级别是 **Core 进程**的状态，Shell 与 Core 是两个进程
/// （Shell 不装配进程内 PuddingHost），只能经 Core 的 loopback 控制面 + ControlToken 访问
/// —— 与既有的存储管理客户端同一套做法。
/// </para>
/// <para>
/// 为什么按调用传入地址与令牌：Shell 里 Core 地址与令牌是**随 Core 生命周期变化**的
/// （重启后端口/令牌都可能变）。把两者做成构造依赖会让本服务必须跟着 Core 一起重建；
/// 按调用传入则本服务可以是一个长命单例，也不会持有过期令牌。
/// </para>
/// <para>
/// 失败语义：非 2xx **抛异常并带上服务端 detail**（不返回"看起来成功"的假快照）——
/// 按钮据此提示用户，而不会出现"界面显示已开启、实际没生效"。
/// </para>
/// </summary>
public sealed class DesktopLogLevelService(HttpClient httpClient)
{
    /// <summary>Core 上的日志级别端点（真源见 <c>DiagnosticsLogLevelController</c>）。</summary>
    public const string RelativePath = "/api/admin/diagnostics/log-level";

    /// <summary>会放大日志量的档位名（与 Core 侧 <c>PuddingLogLevelStore</c> 同一判定口径）。</summary>
    public const string DebugLevel = "Debug";

    /// <summary>关闭调试后的落点。</summary>
    public const string DefaultLevel = "Information";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Debug 按钮的切换目标：当前是 Debug ⇒ 回落默认级别；否则 ⇒ 打开 Debug。</summary>
    /// <param name="isVerbose">当前是否处于会放大日志量的档位。</param>
    public static string ToggleTarget(bool isVerbose) => isVerbose ? DefaultLevel : DebugLevel;

    /// <summary>读取当前级别。</summary>
    /// <param name="coreAddress">Core 的 loopback 基地址。</param>
    /// <param name="controlToken">取 ControlToken 的委托（每次调用现取，避免用到过期令牌）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<DesktopLogLevelSnapshot> GetAsync(
        Uri coreAddress,
        Func<CancellationToken, Task<string>> controlToken,
        CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, coreAddress, controlToken, body: null, cancellationToken);

    /// <summary>切换级别；成功即 Core 侧已生效且已落盘。</summary>
    /// <param name="coreAddress">Core 的 loopback 基地址。</param>
    /// <param name="controlToken">取 ControlToken 的委托。</param>
    /// <param name="level">目标级别（建议用 <see cref="ToggleTarget"/> 的结果）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<DesktopLogLevelSnapshot> SetAsync(
        Uri coreAddress,
        Func<CancellationToken, Task<string>> controlToken,
        string level,
        CancellationToken cancellationToken = default)
        => SendAsync(
            HttpMethod.Put,
            coreAddress,
            controlToken,
            body: new Dictionary<string, string> { ["level"] = level },
            cancellationToken);

    private async Task<DesktopLogLevelSnapshot> SendAsync(
        HttpMethod method,
        Uri coreAddress,
        Func<CancellationToken, Task<string>> controlToken,
        object? body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(coreAddress);
        ArgumentNullException.ThrowIfNull(controlToken);

        var token = await controlToken(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, new Uri(coreAddress, RelativePath));
        request.Headers.TryAddWithoutValidation(BrowserBridgeProtocol.ControlTokenHeader, token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"切换/读取 Core 日志级别失败（HTTP {(int)response.StatusCode}）。");
        }

        var snapshot = await response.Content
            .ReadFromJsonAsync<DesktopLogLevelSnapshot>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return snapshot ?? throw new InvalidDataException("Core 返回了空的日志级别快照。");
    }
}
