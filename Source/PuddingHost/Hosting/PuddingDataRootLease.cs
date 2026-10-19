using System.Diagnostics;

namespace PuddingHost.Hosting;

/// <summary>
/// Exclusive DataRoot lease shared by Console and Desktop child process entry points.
/// <para>
/// 获取租约时有界重试：Desktop 重启窗口内旧 Core 可能正在退出、尚未释放
/// <c>.pudding-host.lock</c>。立即失败会让新 Core 以未处理 <see cref="IOException"/> 崩溃
/// （Windows 事件日志 CLR20r3，历史熔断的直接原因）；旧实例在 <see cref="DefaultAcquireTimeout"/>
/// 内释放即可继续启动。超时后抛出带明确处置说明的 <see cref="IOException"/>，由调用方转为
/// 可读的启动失败信息与非零退出码。
/// </para>
/// </summary>
public sealed class PuddingDataRootLease : IDisposable
{
    /// <summary>DataRoot 独占租约文件名（Console 与 DesktopChild 共用）。</summary>
    public const string LockFileName = ".pudding-host.lock";

    /// <summary>旧实例退出时默认允许的等待时间。</summary>
    public static readonly TimeSpan DefaultAcquireTimeout = TimeSpan.FromSeconds(30);

    /// <summary>两次获取尝试之间的等待间隔。</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(200);

    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private FileStream? _file;

    public PuddingDataRootLease(string dataRoot)
        : this(dataRoot, DefaultAcquireTimeout)
    {
    }

    public PuddingDataRootLease(string dataRoot, TimeSpan acquireTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        Directory.CreateDirectory(dataRoot);
        var lockPath = Path.Combine(dataRoot, LockFileName);
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                _file = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return;
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                if (acquireTimeout <= TimeSpan.Zero || stopwatch.Elapsed >= acquireTimeout)
                {
                    throw new IOException(
                        $"DataRoot 已被另一个 Pudding Core 实例占用：{lockPath}"
                        + $"（等待 {acquireTimeout.TotalSeconds:0.#} 秒后仍无法取得独占租约）。"
                        + "请先结束占用该数据目录的 PuddingAgent 进程，或确认旧实例已完全退出后重试。",
                        ex);
                }

                Thread.Sleep(RetryInterval);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var win32Error = exception.HResult & 0xFFFF;
        return win32Error is ErrorSharingViolation or ErrorLockViolation;
    }

    public void Dispose() => _file?.Dispose();
}
