using System.Net.Http;

namespace PuddingChat.WinUI;

/// <summary>External image fetching only; never uses the Core client, cookies or default credentials.</summary>
internal sealed class RemoteImageSource(HttpClient client) : IRemoteImageSource
{
    internal static IRemoteImageSource Shared { get; } = new RemoteImageSource(new HttpClient(new HttpClientHandler
        { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }) { Timeout = TimeSpan.FromSeconds(15) });
    public async Task<RemoteImageData> LoadAsync(Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (RemoteImageReference.Resolve(uri.AbsoluteUri) is null) throw new InvalidDataException("图片地址无效。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("image/png,image/jpeg,image/webp,image/gif,image/bmp");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var target = response.Headers.Location ?? throw new InvalidDataException("图片重定向无效。");
                uri = target.IsAbsoluteUri ? target : new Uri(uri, target); continue;
            }
            response.EnsureSuccessStatusCode();
            var mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (mime is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif" or "image/bmp"))
                throw new InvalidDataException("不支持的图片格式。");
            if (response.Content.Headers.ContentLength is > RemoteImageData.MaxBytes) throw new InvalidDataException("图片超过 8 MiB。");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream(); var chunk = new byte[16 * 1024];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) != 0)
            {
                if (output.Length + count > RemoteImageData.MaxBytes) throw new InvalidDataException("图片超过 8 MiB。");
                output.Write(chunk, 0, count);
            }
            if (output.Length == 0) throw new InvalidDataException("图片内容为空。");
            return new(output.ToArray(), mime);
        }
        throw new InvalidDataException("图片重定向次数过多。");
    }
}
