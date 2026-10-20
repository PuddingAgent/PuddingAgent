using Microsoft.Web.WebView2.Core;
using PuddingDesktop.Foundation;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PuddingDesktop;

/// <summary>
/// 面板画布的**本地图片通道**：拦截 <c>pudding-image://</c> 并按白名单读盘。
///
/// <para>
/// 纯判定（scheme、路径还原、扩展名、白名单）在 <see cref="PanelImageSource"/>，
/// 那里可以在无窗口环境下测；这里只做「拦截 → 查文件 → 回响应」三件事。
/// </para>
///
/// <para>
/// 为什么不直接把图片转成 base64 塞进 HTML：一张 4K 照片 base64 后是几 MB，
/// 画布会明显卡顿；而图片标注要反复回写同一份内容，体积会越滚越大。
/// </para>
/// </summary>
internal static class PanelImageBridge
{
    /// <summary>
    /// 挂到一块画布上。<paramref name="allowedRoots"/> 是允许读取的根目录；
    /// 空白名单等于「什么都不给」，不会退化成「什么都不限」。
    /// </summary>
    public static void Attach(CoreWebView2 core, IReadOnlyList<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(allowedRoots);

        core.AddWebResourceRequestedFilter(
            PanelImageSource.Scheme + "://*/*", CoreWebView2WebResourceContext.Image);

        core.WebResourceRequested += (_, args) => Handle(core, args, allowedRoots);
    }

    private static void Handle(
        CoreWebView2 core,
        CoreWebView2WebResourceRequestedEventArgs args,
        IReadOnlyList<string> allowedRoots)
    {
        if (!PanelImageSource.TryResolve(args.Request.Uri, allowedRoots, out var path, out var rejection))
        {
            // 拒绝时只回状态码，不把路径回显给页面：那等于把本机目录结构告诉内容。
            args.Response = core.Environment.CreateWebResourceResponse(
                null, StatusFor(rejection), "Blocked", "Content-Type: text/plain");
            return;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                args.Response = core.Environment.CreateWebResourceResponse(
                    null, 404, "Not Found", "Content-Type: text/plain");
                return;
            }

            if (info.Length > PanelImageSource.MaxImageBytes)
            {
                // 先看长度再开流：不然会把一个几 GB 的文件交给 WebView2。
                args.Response = core.Environment.CreateWebResourceResponse(
                    null, 413, "Payload Too Large", "Content-Type: text/plain");
                return;
            }

            // AsRandomAccessStream 是懒读：WebView2 要多少读多少，不把整张图搬进内存。
            var stream = File.OpenRead(path).AsRandomAccessStream();
            args.Response = core.Environment.CreateWebResourceResponse(
                stream, 200, "OK", "Content-Type: " + MimeFor(path));
        }
        catch (Exception)
        {
            // 用 404 而不是把异常文本回给页面：异常消息里常带绝对路径。
            args.Response = core.Environment.CreateWebResourceResponse(
                null, 404, "Not Found", "Content-Type: text/plain");
        }
    }

    private static int StatusFor(PanelImageRejection rejection) => rejection switch
    {
        PanelImageRejection.OutsideAllowedRoots => 403,
        PanelImageRejection.UnsupportedExtension => 415,
        _ => 400,
    };

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
}
