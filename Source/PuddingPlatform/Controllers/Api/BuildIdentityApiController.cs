using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// **运行实例构建身份**（只读、匿名）：给外部部署预检提供「运行中实例到底加载了哪一份构建」的凭据。
/// <para>
/// 为什么需要（复核 R5）：预检脚本过去只能读磁盘目录与 HTTP 首页哈希，既不能证明端口对应的进程，
/// 也不能证明**实际加载的关键程序集**身份 —— 于是"最新磁盘目录"被误当成"运行中服务目录"。
/// 本端点返回 PID + 进程启动标识 + 关键程序集（入口与 Runtime/Core/Memory/Platform）的
/// MVID 与文件 SHA256，使脚本能把「在线实例」与构件清单逐项比对。
/// </para>
/// <para>
/// 只暴露构建身份事实：不返回密钥、配置值、令牌、用户数据或完整路径（程序集只回文件名）。
/// </para>
/// </summary>
[ApiController]
[Route("api/system/build-identity")]
[AllowAnonymous]
public sealed class BuildIdentityApiController : ControllerBase
{
    /// <summary>
    /// 参与身份比对的关键程序集（本次上下文治理修复触及的核心链）。
    /// 未加载的程序集显式标记 loaded=false，绝不猜。
    /// </summary>
    private static readonly string[] TrackedAssemblyNames =
    [
        "PuddingAgent",
        "PuddingCore",
        "PuddingRuntime",
        "PuddingMemoryEngine",
        "PuddingPlatform",
    ];

    [HttpGet]
    public IActionResult Get()
    {
        var process = Process.GetCurrentProcess();
        DateTimeOffset? startedAt = null;
        try
        {
            startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception)
        {
            // 取不到启动时间就如实为 null（脚本据此判 unknown，不猜）。
        }

        var assemblies = new List<object>();
        foreach (var name in TrackedAssemblyNames)
        {
            assemblies.Add(DescribeAssembly(name));
        }

        string? entryPath = null;
        try
        {
            entryPath = Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch (Exception)
        {
            entryPath = null;
        }

        return Ok(new
        {
            processId = process.Id,
            processStartedAtUtc = startedAt?.ToString("O"),
            entryFileName = string.IsNullOrWhiteSpace(entryPath) ? null : Path.GetFileName(entryPath),
            baseDirectory = AppContext.BaseDirectory,
            contentRoot = Directory.GetCurrentDirectory(),
            machineName = Environment.MachineName,
            runtimeVersion = Environment.Version.ToString(),
            assemblies,
        });
    }

    private static object DescribeAssembly(string name)
    {
        Assembly? assembly = null;
        try
        {
            assembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            assembly ??= Assembly.Load(new AssemblyName(name));
        }
        catch (Exception)
        {
            assembly = null;
        }

        if (assembly is null)
            return new { name, loaded = false, mvid = (string?)null, informationalVersion = (string?)null, fileSha256 = (string?)null, fileName = (string?)null };

        string? mvid = null;
        try
        {
            mvid = assembly.ManifestModule.ModuleVersionId.ToString("D");
        }
        catch (Exception)
        {
            mvid = null;
        }

        string? informational = null;
        try
        {
            informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        }
        catch (Exception)
        {
            informational = null;
        }

        string? location = null;
        try
        {
            location = assembly.Location;
        }
        catch (Exception)
        {
            location = null;
        }

        string? sha256 = null;
        if (!string.IsNullOrWhiteSpace(location) && System.IO.File.Exists(location))
        {
            try
            {
                using var stream = System.IO.File.OpenRead(location);
                sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            }
            catch (Exception)
            {
                sha256 = null;
            }
        }

        return new
        {
            name,
            loaded = true,
            mvid,
            informationalVersion = informational,
            fileSha256 = sha256,
            fileName = string.IsNullOrWhiteSpace(location) ? null : Path.GetFileName(location),
        };
    }
}
