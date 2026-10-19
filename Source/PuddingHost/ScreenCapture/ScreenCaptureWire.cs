using System.Text.Json;

namespace PuddingHost.ScreenCapture;

/// <summary>
/// 屏幕采集的**线格式**：领域模型与工具 JSON 输出之间的唯一转换边界。
///
/// <para>存在的理由：领域模型里的窗口句柄是 <see cref="nint"/>（Win32 调用要原值），
/// 而 <c>System.Text.Json</c> **不支持序列化 <see cref="IntPtr"/>**——直接序列化
/// <see cref="ScreenWindow"/> 会让整个 <c>action=displays</c> 失败（实测错误：
/// <c>Serialization and deserialization of 'System.IntPtr' instances is not supported</c>）。</para>
///
/// <para>因此句柄在边界上被**显式**投影为 <c>0x</c> 十六进制字符串，与
/// <see cref="ScreenCaptureGeometry.TryParseHandle"/> 的入参格式一一对应，
/// 调用方可以拿 <c>action=displays</c> 的输出原样回传给 <c>windowHandle</c>。
/// 格式与解析放在同一处（<see cref="FormatHandle"/> / <c>TryParseHandle</c>），避免两侧漂移。</para>
/// </summary>
public static class ScreenCaptureWire
{
    /// <summary>线格式的 JSON 选项：camelCase，与工具其余输出保持一致。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 句柄 → 线格式字符串（<c>0x</c> 前缀、大写十六进制、无前导零）。
    /// <para>不变量：对任何非零句柄，结果都能被 <see cref="ScreenCaptureGeometry.TryParseHandle"/> 还原。</para>
    /// </summary>
    public static string FormatHandle(nint handle) => "0x" + handle.ToInt64().ToString("X");

    /// <summary>
    /// 窗口列表 → 可序列化的窗口描述。
    /// <para>不变量：不做任何过滤与重排，返回长度与顺序一一对应入参。</para>
    /// </summary>
    public static WindowDescriptor[] ProjectWindows(IReadOnlyList<ScreenWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var projected = new WindowDescriptor[windows.Count];
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            projected[index] = new WindowDescriptor(
                Handle: FormatHandle(window.Handle),
                window.Title,
                window.ProcessName,
                window.Bounds,
                window.IsVisible);
        }

        return projected;
    }
}

/// <summary>窗口的线格式：句柄为 <c>0x</c> 十六进制字符串，其余字段与领域模型同名。</summary>
/// <param name="Handle">形如 <c>0x1A2B3C</c>；可直接作为 <c>windowHandle</c> 回传。</param>
public sealed record WindowDescriptor(
    string Handle,
    string Title,
    string ProcessName,
    PixelRect Bounds,
    bool IsVisible);
