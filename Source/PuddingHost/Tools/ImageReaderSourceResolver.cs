using System.Net;
using System.Net.Sockets;
using PuddingCode.Core;
using PuddingPlatform.Services;

namespace PuddingAgent.Tools;

/// <summary>Image Reader 解析后的图片来源流。SourceKind: image_reader_artifact | image_reader_local | image_reader_url。</summary>
public sealed record ImageReaderSource(
    string SourceKind,
    Stream Content,
    string? ExistingArtifactId = null);

/// <summary>
/// ADR-077 §5.4：image_reader 的图片来源解析器。
/// artifact:// 引用校验 Workspace ownership 后复用；http(s) 由 Pudding 有界流下载
/// （每跳重定向/DNS 重校验，永不原样交给 Provider）；绝对本地路径以 FileShare.Read 只读打开。
/// </summary>
public sealed class ImageReaderSourceResolver(
    VisionArtifactStorageService artifactStorage,
    IHttpClientFactory? httpClientFactory,
    ILogger<ImageReaderSourceResolver> logger)
{
    private const int MaxRedirects = 5;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "https",
    };

    public async Task<ImageReaderSource> ResolveAsync(
        string workspaceId,
        string path,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new VisionPipelineException(VisionErrorCodes.SourceInvalid, "path is required.");

        var trimmed = path.Trim();

        if (trimmed.StartsWith("artifact://", StringComparison.OrdinalIgnoreCase))
        {
            var artifactId = trimmed["artifact://".Length..].Trim();
            var localFile = await artifactStorage.ResolveLocalFileAsync(workspaceId, artifactId, ct)
                ?? throw new VisionPipelineException(
                    VisionErrorCodes.ArtifactForbidden,
                    $"Artifact '{artifactId}' does not exist in workspace '{workspaceId}'.");

            try
            {
                var stream = new FileStream(localFile.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                // ADR-077 §5.4：artifact:// 校验 Workspace ownership 后直接复用原 Artifact 身份。
                return new ImageReaderSource("image_reader_artifact", stream, artifactId);
            }
            catch (Exception ex)
            {
                throw new VisionPipelineException(
                    VisionErrorCodes.ArtifactMissing,
                    $"Artifact '{artifactId}' could not be read: {ex.Message}",
                    ex);
            }
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && AllowedSchemes.Contains(uri.Scheme))
        {
            return await DownloadAsync(workspaceId, uri, ct);
        }

        if (Path.IsPathFullyQualified(trimmed))
        {
            var fullPath = Path.GetFullPath(trimmed);
            if (Directory.Exists(fullPath))
                throw new VisionPipelineException(
                    VisionErrorCodes.SourceInvalid,
                    "path points to a directory; an image file is required.");

            if (!File.Exists(fullPath))
                throw new VisionPipelineException(
                    VisionErrorCodes.SourceInvalid,
                    "Image file does not exist.");

            try
            {
                var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return new ImageReaderSource("image_reader_local", stream);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new VisionPipelineException(
                    VisionErrorCodes.SourceAccessDenied,
                    $"Local image read was denied: {ex.Message}",
                    ex);
            }
            catch (IOException ex)
            {
                throw new VisionPipelineException(
                    VisionErrorCodes.SourceAccessDenied,
                    $"Local image could not be opened: {ex.Message}",
                    ex);
            }
        }

        throw new VisionPipelineException(
            VisionErrorCodes.SourceInvalid,
            "path must be an http(s) URL, an absolute local file path, or an artifact://vision-... reference.");
    }

    /// <summary>有界流下载：每跳重定向后重新校验目标；拒绝无限流与压缩炸弹。</summary>
    private async Task<ImageReaderSource> DownloadAsync(
        string workspaceId,
        Uri initialUri,
        CancellationToken ct)
    {
        var client = httpClientFactory?.CreateClient("image_reader") ?? SharedClient;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DownloadTimeout);

        var current = initialUri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            ValidateUri(current);

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new VisionPipelineException(
                    VisionErrorCodes.SourceDownloadFailed,
                    $"Image download timed out after {DownloadTimeout.TotalSeconds:F0}s.");
            }
            catch (Exception ex)
            {
                throw new VisionPipelineException(
                    VisionErrorCodes.SourceDownloadFailed,
                    $"Image download failed: {ex.Message}",
                    ex);
            }

            using (response)
            {
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location
                        ?? throw new VisionPipelineException(
                            VisionErrorCodes.SourceDownloadFailed,
                            $"Image download redirect ({(int)response.StatusCode}) carried no Location header.");

                    current = location.IsAbsoluteUri
                        ? location
                        : new Uri(current, location);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new VisionPipelineException(
                        VisionErrorCodes.SourceDownloadFailed,
                        $"Image download returned HTTP {(int)response.StatusCode}.");

                var declaredLength = response.Content.Headers.ContentLength ?? 0;
                if (declaredLength > VisionImageInspector.MaxCanonicalImageBytes)
                    throw new VisionPipelineException(
                        VisionErrorCodes.RequestLimitExceeded,
                        $"Image download declares {declaredLength} bytes; the product limit is " +
                        $"{VisionImageInspector.MaxCanonicalImageBytes}.");

                var bounded = new BoundedStream(
                    await response.Content.ReadAsStreamAsync(timeoutCts.Token),
                    VisionImageInspector.MaxCanonicalImageBytes);
                // 复制到内存后再交给 Artifact 存储；流随 response 释放，内存副本受 50MiB 上界约束。
                var memory = new MemoryStream();
                try
                {
                    await bounded.CopyToAsync(memory, timeoutCts.Token);
                }
                catch (VisionPipelineException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new VisionPipelineException(
                        VisionErrorCodes.SourceDownloadFailed,
                        $"Image download stream failed: {ex.Message}",
                        ex);
                }

                memory.Position = 0;
                logger.LogInformation(
                    "[ImageReader] Downloaded image bytes={Bytes}",
                    memory.Length);
                return new ImageReaderSource("image_reader_url", memory);
            }
        }

        throw new VisionPipelineException(
            VisionErrorCodes.SourceDownloadFailed,
            $"Image download exceeded {MaxRedirects} redirects.");
    }

    /// <summary>每跳校验：仅 http(s)，主机可解析；不转发任何 Cookie/Authorization/代理凭据。</summary>
    private static void ValidateUri(Uri uri)
    {
        if (!AllowedSchemes.Contains(uri.Scheme))
            throw new VisionPipelineException(
                VisionErrorCodes.SourceInvalid,
                $"URL scheme '{uri.Scheme}' is not allowed; use http or https.");

        if (uri.UserInfo.Contains(':', StringComparison.Ordinal)
            || uri.UserInfo.Length > 0)
            throw new VisionPipelineException(
                VisionErrorCodes.SourceInvalid,
                "URLs carrying embedded credentials are rejected.");

        try
        {
            var hostEntry = Dns.GetHostEntry(uri.Host);
            foreach (var address in hostEntry.AddressList)
                ValidateAddress(address, uri.Host);
        }
        catch (SocketException ex)
        {
            throw new VisionPipelineException(
                VisionErrorCodes.SourceDownloadFailed,
                $"URL host '{uri.Host}' could not be resolved: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// DNS rebinding 防护：解析到的任一地址进入 loopback/link-local/私网段时，
    /// 记录 zone 并要求本次工具调用已通过 High 权限运行时授权（调用链前提），
    /// 否则拒绝。公网地址直接放行。
    /// </summary>
    private static void ValidateAddress(IPAddress address, string host)
    {
        if (IPAddress.IsLoopback(address)
            || address.IsIPv6LinkLocal
            || IsPrivate(address))
            throw new VisionPipelineException(
                VisionErrorCodes.SourceAccessDenied,
                $"URL host '{host}' resolves to a non-public address ({address}); " +
                "private-network image fetches require explicit runtime network authorization.");
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254);
        }

        // IPv6 unique-local (fc00::/7)
        var v6 = address.GetAddressBytes();
        return (v6[0] & 0xFE) == 0xFC;
    }

    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    });

    /// <summary>有界读取流：超过上限即抛稳定错误，拒绝无限流与压缩炸弹。</summary>
    private sealed class BoundedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var read = await inner.ReadAsync(buffer, offset, count, ct);
            _read += read;
            if (_read > limit)
                throw new VisionPipelineException(
                    VisionErrorCodes.RequestLimitExceeded,
                    $"Image download exceeded the {limit} byte product limit.");
            return read;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
