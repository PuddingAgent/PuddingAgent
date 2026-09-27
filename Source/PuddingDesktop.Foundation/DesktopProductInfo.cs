namespace PuddingDesktop.Foundation;

/// <summary>
/// DS-01 About card. The version comes from the running build; nothing here is hard coded, and the
/// card never presents a debug path as a user-editable form.
/// </summary>
public static class DesktopProductInfo
{
    public const string ProductName = "Pudding Desktop";
    public const string ShellDescription = "WinUI 3 · 非打包桌面应用";
    public const string KernelDescription = "Core 以进程内 DLL 运行，与 Desktop 同进程；不经过 HTTP 转发层。";

    /// <summary>Help entry carried over from the Web admin header link; external, so the UI must label it.</summary>
    public const string HelpUrl = "https://github.com/PuddingAgent/PuddingAgent";

    public const string UnknownVersion = "未知（构建未写入版本信息）";

    /// <summary>Informational version wins; the build never invents a version when assembly metadata is absent.</summary>
    public static string NormalizeVersion(string? informationalVersion, string? fileVersion)
    {
        var informational = informationalVersion?.Split('+')[0].Trim();
        if (!string.IsNullOrEmpty(informational)) return informational;
        var file = fileVersion?.Trim();
        return string.IsNullOrEmpty(file) ? UnknownVersion : file;
    }

    public static bool IsExternalLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>Configuration locations shown read-only; the About card never offers them for editing.</summary>
    public static string DescribeLocations(string stateRoot, string? dataRoot) =>
        $"偏好与内核配置：{Path.Combine(Path.GetFullPath(stateRoot), "desktop.preferences.json")}\n" +
        $"内核配置：{Path.Combine(Path.GetFullPath(stateRoot), "desktop.kernel.json")}\n" +
        $"数据目录：{(string.IsNullOrWhiteSpace(dataRoot) ? "未配置" : Path.GetFullPath(dataRoot))}";
}
