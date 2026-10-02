using System.Runtime.InteropServices;

namespace PuddingDesktop.Runtime;

/// <summary>
/// 读取「专用工作集 / Private Working Set」——即任务管理器「内存」列的显示口径。
///
/// 为什么需要它：<see cref="System.Diagnostics.Process.WorkingSet64"/> 是**总工作集**，
/// 除了进程私有页，还包含可共享的映像页、映射文件页与运行时生成的共享代码页。
/// 在 .NET/Core 进程上这部分通常占几十到几百 MiB，导致面板数值显著大于任务管理器。
/// 例：同一时刻的空闲 Core 进程，总工作集 248 MiB、专用工作集 113 MiB（实测）。
///
/// 数值来源：<c>NtQuerySystemInformation(SystemProcessInformation)</c> 返回的
/// <c>SYSTEM_PROCESS_INFORMATION.WorkingSetPrivateSize</c>（NT 内核结构，与任务管理器同源）。
/// 该结构前缀（NextEntryOffset / WorkingSetPrivateSize / UniqueProcessId）在 x64 上稳定；
/// 读取失败、非 x64 或结构长度不足时返回 <c>null</c>，由调用方回退到工作集口径并标注。
/// </summary>
internal static class ProcessMemoryReader
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // SYSTEM_PROCESS_INFORMATION（x64）前缀偏移：
    //   0x00 ULONG       NextEntryOffset
    //   0x04 ULONG       NumberOfThreads
    //   0x08 LARGE_INTEGER WorkingSetPrivateSize
    //   ...
    //   0x50 HANDLE      UniqueProcessId（0x48 的 KPRIORITY 后按 8 字节对齐）
    private const int WorkingSetPrivateSizeOffset = 0x08;
    private const int UniqueProcessIdOffset = 0x50;
    private const int InitialBufferBytes = 1 << 20;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    /// <summary>返回专用工作集字节数；不可读时返回 null。</summary>
    public static long? TryGetPrivateWorkingSetBytes(int processId)
    {
        // 布局按 x64 校验；32 位进程下直接放弃，避免读出错误数值。
        if (processId <= 0 || !Environment.Is64BitProcess) return null;

        var buffer = IntPtr.Zero;
        var size = InitialBufferBytes;
        try
        {
            buffer = Marshal.AllocHGlobal(size);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var required);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(required + (1 << 16), size * 2);
                    buffer = Marshal.ReAllocHGlobal(buffer, (IntPtr)size);
                    continue;
                }
                return status == 0 ? FindPrivateWorkingSet(buffer, processId) : null;
            }
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static long? FindPrivateWorkingSet(IntPtr buffer, int processId)
    {
        var offset = 0;
        while (true)
        {
            var entry = IntPtr.Add(buffer, offset);
            var nextEntryOffset = Marshal.ReadInt32(entry, 0);
            var entryProcessId = Marshal.ReadIntPtr(entry, UniqueProcessIdOffset).ToInt64();
            if (entryProcessId == processId)
            {
                var bytes = Marshal.ReadInt64(entry, WorkingSetPrivateSizeOffset);
                return bytes > 0 ? bytes : null;
            }
            if (nextEntryOffset <= 0) return null;
            offset += nextEntryOffset;
        }
    }
}
