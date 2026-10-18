using System.Reflection;

namespace PuddingBrowser.Protocol;

/// <summary>
/// Bridge 握手携带的**可诊断构建身份**（ADR-097 D9）：用于与发布清单核对，不参与授权判定。
///
/// 只暴露进程内程序集的名称/版本/MVID/路径；不读环境秘密。
/// 构建未 stamp 源修订时 `SourceRevision`/`Dirty` 如实为 <c>null</c>（unknown），不推断。
/// </summary>
public sealed record BrowserBridgeBuildIdentity
{
    public required string BuildId { get; init; }

    /// <summary>源提交修订（构建 stamp 了 `InformationalVersion` 的 `+rev` 才有；否则 null）。</summary>
    public string? SourceRevision { get; init; }

    public string? InformationalVersion { get; init; }

    public string? ModuleName { get; init; }

    /// <summary>程序集 MVID：同一源码不同编译可区分，作为"进程实际加载了哪份二进制"的证据。</summary>
    public string? ModuleVersionId { get; init; }

    /// <summary>加载程序集路径（诊断用，不含凭据）。</summary>
    public string? ModulePath { get; init; }

    /// <summary>构建树是否 dirty：仅当构建 stamp 明确标注时有值；未知为 null。</summary>
    public bool? Dirty { get; init; }

    public static BrowserBridgeBuildIdentity FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return FromInformationalVersion(
            informational,
            assembly.GetName().Name,
            assembly.ManifestModule.ModuleVersionId.ToString("D"),
            SafeLocation(assembly));
    }

    /// <summary>按构建 stamp 的信息化版本构造（无程序集依赖，便于验证解析规则）。</summary>
    public static BrowserBridgeBuildIdentity FromInformationalVersion(
        string? informationalVersion,
        string? moduleName = null,
        string? moduleVersionId = null,
        string? modulePath = null)
    {
        var revision = ExtractRevision(informationalVersion);
        var dirty = ExtractDirty(informationalVersion);

        return new BrowserBridgeBuildIdentity
        {
            BuildId = string.IsNullOrWhiteSpace(informationalVersion) ? "unknown" : informationalVersion,
            SourceRevision = revision,
            InformationalVersion = informationalVersion,
            ModuleName = moduleName,
            ModuleVersionId = moduleVersionId,
            ModulePath = modulePath,
            Dirty = dirty,
        };
    }

    /// <summary>一行诊断文本（日志用；不含载荷/凭据）。</summary>
    public string Describe() =>
        $"build={BuildId} rev={SourceRevision ?? "unknown"} dirty={Dirty?.ToString() ?? "unknown"} "
        + $"mvid={ModuleVersionId ?? "unknown"} path={ModulePath ?? "unknown"}";

    private static string? SafeLocation(Assembly assembly)
    {
        try
        {
            return string.IsNullOrWhiteSpace(assembly.Location) ? null : assembly.Location;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string? ExtractRevision(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return null;
        }

        var plus = informational.LastIndexOf('+');
        if (plus < 0 || plus == informational.Length - 1)
        {
            return null;
        }

        var revision = informational[(plus + 1)..];
        if (revision.EndsWith(".dirty", StringComparison.OrdinalIgnoreCase))
        {
            revision = revision[..^".dirty".Length];
        }
        else if (revision.EndsWith("-dirty", StringComparison.OrdinalIgnoreCase))
        {
            revision = revision[..^"-dirty".Length];
        }

        return string.IsNullOrWhiteSpace(revision) ? null : revision;
    }

    private static bool? ExtractDirty(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return null;
        }

        return informational.EndsWith(".dirty", StringComparison.OrdinalIgnoreCase)
            || informational.EndsWith("-dirty", StringComparison.OrdinalIgnoreCase)
            ? true
            : null;
    }
}
