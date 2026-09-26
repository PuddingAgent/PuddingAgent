using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// 资源压力采样结果（方案 §3.7：CPU / 磁盘超阈值 ⇒ 立即退避；采样不可用 ⇒ 按系统繁忙处理）。
/// </summary>
/// <param name="CpuPercent">系统 CPU 忙碌百分比；**未采样**为 null（不得伪报 0）。</param>
/// <param name="DiskBusyPercent">目标磁盘忙碌百分比；**未采样**为 null。</param>
/// <param name="Message">可读说明（采样来源 / 采样失败原因）。</param>
public sealed record ResourcePressureSample(
    double? CpuPercent,
    double? DiskBusyPercent,
    string? Message);

/// <summary>
/// 低优先级体检的**资源压力采样接缝**（方案 §3.7）。
/// <para>
/// 采样返回 <c>null</c> ⇒ 调用方**必须按「系统繁忙」退避**，绝不按「空闲」放行
/// （「量不到」和「没占用」是两件事：把前者当后者会让体检在满负载机器上硬跑）。
/// </para>
/// <para>
/// 判定阈值不在这里：唯一真源是 <see cref="MaintenanceOptions.CpuHighWatermarkPercent"/> /
/// <see cref="MaintenanceOptions.DiskHighWatermarkPercent"/>，本接缝只负责**如实采样**。
/// </para>
/// </summary>
public interface IResourcePressureProbe
{
    /// <summary>采样一次。返回 null = 不可采样（⇒ 按系统繁忙退避）。</summary>
    ResourcePressureSample? Sample();
}

/// <summary>
/// 默认资源压力探针：**系统 CPU**（kernel32 <c>GetSystemTimes</c>）+ **磁盘忙碌度暂不采样**。
/// <para>
/// 为什么不用 PDH / PerformanceCounter：本片**不得改任何 .csproj**，而
/// <c>System.Diagnostics.PerformanceCounter</c> 需要额外包引用；<c>GetSystemTimes</c> 是 kernel32 导出，
/// 零依赖即可拿到系统级 CPU 时间（idle / kernel / user）。
/// </para>
/// <para>
/// ⚠️ 诚实登记（本片未做项）：<b>磁盘忙碌度不采样</b>（<see cref="ResourcePressureSample.DiskBusyPercent"/>
/// 恒为 null），因此 <see cref="MaintenanceOptions.DiskHighWatermarkPercent"/> 在本片**没有生效路径**。
/// 方案 §3.7 明确要求「具体 API 和阈值必须通过 S3 实测，不得只写死百分比后宣称完成」，
/// 所以这里只落地可实测的 CPU 维度；磁盘维度留待引入 PDH 封装的切片（阈值旋钮已就位，接上即生效）。
/// </para>
/// <para>
/// ⚠️ 非 Windows / API 不可用 / 首次采样无法给出区间 ⇒ 返回 null（⇒ 退避一次），
/// **绝不**返回「0% 忙碌」这种伪造的空闲结论。
/// </para>
/// <para>本类零后台线程、零文件句柄、零目录访问；<see cref="Sample"/> 是唯一的副作用点（读内核计数器）。</para>
/// </summary>
public sealed class SystemCpuResourcePressureProbe : IResourcePressureProbe
{
    private readonly object _lock = new();
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private bool _hasPrevious;

    /// <inheritdoc />
    public ResourcePressureSample? Sample()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        if (!TryReadSystemTimes(out var idle, out var kernel, out var user))
            return null;

        lock (_lock)
        {
            if (!_hasPrevious)
            {
                _previousIdle = idle;
                _previousKernel = kernel;
                _previousUser = user;
                _hasPrevious = true;

                // 首次采样没有「区间」⇒ 无法给出百分比。返回 null（= 不可采样 ⇒ 退避一次）而不是编一个 0。
                return null;
            }

            var idleDelta = idle - _previousIdle;
            var kernelDelta = kernel - _previousKernel;
            var userDelta = user - _previousUser;

            _previousIdle = idle;
            _previousKernel = kernel;
            _previousUser = user;

            // kernel 时间**包含** idle 时间（Windows 文档），故总忙碌 = kernel + user − idle。
            var total = kernelDelta + userDelta;
            if (total == 0)
                return null;

            var busy = total > idleDelta ? total - idleDelta : 0;
            var cpuPercent = 100.0 * busy / total;

            return new ResourcePressureSample(
                cpuPercent,
                DiskBusyPercent: null,
                Message: $"GetSystemTimes 区间采样（idle={idleDelta}，kernel={kernelDelta}，user={userDelta}）");
        }
    }

    private static bool TryReadSystemTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        idle = kernel = user = 0;

        try
        {
            if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
                return false;

            idle = ToUInt64(idleTime);
            kernel = ToUInt64(kernelTime);
            user = ToUInt64(userTime);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    private static ulong ToUInt64(FILETIME time) =>
        ((ulong)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FILETIME idleTime,
        out FILETIME kernelTime,
        out FILETIME userTime);
}
