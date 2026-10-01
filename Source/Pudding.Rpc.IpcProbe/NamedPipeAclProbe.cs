using System.Runtime.InteropServices;

namespace PuddingRpc.IpcProbe;

/// <summary>
/// Named Pipe 端点安全探针：读出 Kestrel 创建的管道 DACL（SDDL），
/// 并检查 ASP.NET Core 是否提供 <c>NamedPipeTransportOptions.CreatePipe</c> 以便产品期注入自定义 ACL。
/// </summary>
internal static class NamedPipeAclProbe
{
    private const int SeFileObject = 1;
    private const int SeKernelObject = 6;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint SddlRevision1 = 1;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ReadControl = 0x00020000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    public static void Run(string pipeName, ProbeReport report)
    {
        InspectPipeDacl(pipeName, report);
        InspectCreatePipeHook(report);
    }

    private static void InspectPipeDacl(string pipeName, ProbeReport report)
    {
        string? sddl = null;
        string source = string.Empty;

        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(
                ".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            client.Connect(5000);

            // 客户端句柄通常没有 READ_CONTROL，故依次尝试两种对象类型；失败再用显式 READ_CONTROL 句柄。
            uint fileError = 0;
            uint kernelError = 0;
            sddl = TryReadSddl(client.SafePipeHandle, SeFileObject, out fileError);
            if (sddl is null)
            {
                sddl = TryReadSddl(client.SafePipeHandle, SeKernelObject, out kernelError);
            }

            if (sddl is null)
            {
                report.Info($"pipe-acl: client handle query failed (file={fileError}, kernel={kernelError})");
            }
            else
            {
                source = "client handle";
            }
        }
        catch (Exception ex)
        {
            report.Info($"pipe-acl: client connect failed ({ex.GetType().Name})");
        }

        if (sddl is null)
        {
            sddl = TryReadSddlViaReadControlHandle(pipeName, out var error);
            if (sddl is null)
            {
                report.Info($"pipe-acl: READ_CONTROL handle query failed (win32 {error})");
                return;
            }

            source = "READ_CONTROL handle";
        }

        report.Info($"pipe DACL SDDL ({source}): {sddl}");

        var anonymous = sddl.Contains(";;;AN)", StringComparison.Ordinal);
        var everyone = sddl.Contains(";;;WD)", StringComparison.Ordinal);
        var authenticatedUsers = sddl.Contains(";;;AU)", StringComparison.Ordinal);

        if (anonymous)
        {
            report.Fail("pipe-acl", "named pipe DACL grants Anonymous");
        }
        else if (everyone)
        {
            report.Fail("pipe-acl", "named pipe DACL grants Everyone");
        }
        else
        {
            report.Pass(
                "pipe-acl",
                $"DACL 未对 Anonymous/Everyone 授权（AuthenticatedUsers={authenticatedUsers}，来源：{source}）");
        }
    }

    private static string? TryReadSddl(SafeHandle handle, int objectType, out uint error)
    {
        error = GetSecurityInfo(
            handle,
            objectType,
            DaclSecurityInformation | OwnerSecurityInformation,
            out _,
            out _,
            out _,
            out _,
            out var descriptor);

        if (error != 0)
        {
            return null;
        }

        return ConvertAndFree(descriptor, out error);
    }

    private static string? TryReadSddlViaReadControlHandle(string pipeName, out uint error)
    {
        error = 0;
        using var handle = CreateFile(
            $@"\\.\pipe\{pipeName}",
            GenericRead | GenericWrite | ReadControl,
            0,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            error = (uint)Marshal.GetLastWin32Error();
            return null;
        }

        return TryReadSddl(handle, SeFileObject, out error);
    }

    private static string? ConvertAndFree(IntPtr descriptor, out uint error)
    {
        error = 0;
        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptor(
                    descriptor,
                    SddlRevision1,
                    DaclSecurityInformation | OwnerSecurityInformation,
                    out var sddlPointer,
                    out _))
            {
                error = (uint)Marshal.GetLastWin32Error();
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(sddlPointer);
            }
            finally
            {
                LocalFree(sddlPointer);
            }
        }
        finally
        {
            // 只释放 GetSecurityInfo 返回的描述符本体；owner/dacl 等指针指向描述符内部，重复释放会破坏堆。
            LocalFree(descriptor);
        }
    }

    private static void InspectCreatePipeHook(ProbeReport report)
    {
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .FirstOrDefault(candidate => candidate.Name == "NamedPipeTransportOptions");

            if (type is null)
            {
                report.Info("pipe-acl: NamedPipeTransportOptions not found (cannot confirm ACL injection hook)");
                return;
            }

            var members = string.Join(", ", type.GetProperties().Select(property => property.Name));
            report.Info($"pipe-acl: {type.FullName} properties = [{members}]");

            // ACL 注入钩子在不同版本里叫 CreatePipe 或 CreateNamedPipeServerStream；两者取其一。
            var hook = type.GetProperty("CreatePipe") ?? type.GetProperty("CreateNamedPipeServerStream");
            if (hook is not null)
            {
                report.Pass("pipe-acl-hook", $"产品期可用 NamedPipeTransportOptions.{hook.Name} 注入受限 ACL");
            }
            else
            {
                report.Info("pipe-acl-hook: 该版本无管道创建钩子；ACL 只能依赖 Kestrel 默认策略");
            }

            var currentUserOnly = type.GetProperty("CurrentUserOnly");
            if (currentUserOnly is not null)
            {
                var instance = Activator.CreateInstance(type);
                var value = currentUserOnly.GetValue(instance);
                report.Info($"pipe-acl-hook: NamedPipeTransportOptions.CurrentUserOnly 默认 = {value}");
            }
        }
        catch (Exception ex)
        {
            report.Info($"pipe-acl-hook: probe skipped ({ex.GetType().Name})");
        }
    }

    private static IEnumerable<Type> SafeGetTypes(System.Reflection.Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException)
        {
            return [];
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        SafeHandle handle,
        int objectType,
        uint securityInfo,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(
        IntPtr securityDescriptor,
        uint revision,
        uint securityInformation,
        out IntPtr stringSecurityDescriptor,
        out uint stringSecurityDescriptorLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}

/// <summary>探针结论收集与退出码。</summary>
internal sealed class ProbeReport
{
    private readonly List<string> _lines = [];
    private readonly List<string> _failures = [];

    public void Info(string message) => _lines.Add($"INFO  {message}");

    public void Pass(string step, string detail) => _lines.Add($"PASS  {step}: {detail}");

    public void Fail(string step, string detail)
    {
        _lines.Add($"FAIL  {step}: {detail}");
        _failures.Add($"{step}: {detail}");
    }

    public int Print()
    {
        Console.WriteLine("=== PuddingRpc.IpcProbe（Desktop 主动 gRPC 能力通道 · 真实端点探针）===");
        foreach (var line in _lines)
        {
            Console.WriteLine(line);
        }

        var passed = _lines.Count(line => line.StartsWith("PASS", StringComparison.Ordinal));
        Console.WriteLine($"=== 结论：{passed} 项通过，{_failures.Count} 项失败 ===");
        foreach (var failure in _failures)
        {
            Console.WriteLine($"  !! {failure}");
        }

        return _failures.Count == 0 ? 0 : 1;
    }
}
