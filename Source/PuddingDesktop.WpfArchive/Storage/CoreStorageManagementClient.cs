using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using PuddingBrowser.Protocol;
using PuddingCode.Storage;

namespace PuddingDesktop.Storage;

public interface ICoreStorageManagementClient : IDisposable
{
    bool IsAvailable { get; }

    void Configure(
        Uri? coreAddress,
        Func<CancellationToken, Task<string>>? controlTokenProvider);

    Task<StorageDatabaseAnalysis> AnalyzeAsync(CancellationToken cancellationToken);

    Task<StorageCleanupPreview> PreviewCleanupAsync(
        StorageCleanupPreviewRequest request,
        CancellationToken cancellationToken);

    Task<StorageCleanupResult> ExecuteCleanupAsync(
        Guid previewId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Loopback client for the Core-owned storage-management API. The rotating
/// Desktop control token is resolved per request and is never cached or shown.
/// </summary>
public sealed class CoreStorageManagementClient : ICoreStorageManagementClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly object _configurationLock = new();
    private Uri? _coreAddress;
    private Func<CancellationToken, Task<string>>? _controlTokenProvider;

    public CoreStorageManagementClient()
        : this(new HttpClientHandler())
    {
    }

    internal CoreStorageManagementClient(HttpMessageHandler handler)
    {
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(6),
        };
    }

    public bool IsAvailable
    {
        get
        {
            lock (_configurationLock)
                return _coreAddress is not null && _controlTokenProvider is not null;
        }
    }

    public void Configure(
        Uri? coreAddress,
        Func<CancellationToken, Task<string>>? controlTokenProvider)
    {
        lock (_configurationLock)
        {
            _coreAddress = coreAddress;
            _controlTokenProvider = controlTokenProvider;
        }
    }

    public Task<StorageDatabaseAnalysis> AnalyzeAsync(CancellationToken cancellationToken)
        => SendAsync<StorageDatabaseAnalysis>(
            HttpMethod.Get,
            relativePath: "/api/admin/storage/databases",
            body: null,
            cancellationToken);

    public Task<StorageCleanupPreview> PreviewCleanupAsync(
        StorageCleanupPreviewRequest request,
        CancellationToken cancellationToken)
        => SendAsync<StorageCleanupPreview>(
            HttpMethod.Post,
            relativePath: "/api/admin/storage/databases/cleanup/preview",
            request,
            cancellationToken);

    public Task<StorageCleanupResult> ExecuteCleanupAsync(
        Guid previewId,
        CancellationToken cancellationToken)
        => SendAsync<StorageCleanupResult>(
            HttpMethod.Post,
            relativePath: "/api/admin/storage/databases/cleanup/execute",
            new StorageCleanupExecuteRequest { PreviewId = previewId },
            cancellationToken);

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken cancellationToken)
    {
        Uri? coreAddress;
        Func<CancellationToken, Task<string>>? tokenProvider;
        lock (_configurationLock)
        {
            coreAddress = _coreAddress;
            tokenProvider = _controlTokenProvider;
        }

        if (coreAddress is null || tokenProvider is null)
            throw new InvalidOperationException("Core 尚未就绪，无法读取或清理数据库。");

        var token = await tokenProvider(cancellationToken);
        using var request = new HttpRequestMessage(
            method,
            new Uri(coreAddress, relativePath));
        request.Headers.TryAddWithoutValidation(
            BrowserBridgeProtocol.ControlTokenHeader,
            token);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await TryReadErrorAsync(response, cancellationToken);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"Core 存储管理请求失败（HTTP {(int)response.StatusCode}）。"
                    : error);
        }

        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return result ?? throw new InvalidDataException("Core 返回了空的存储管理结果。");
    }

    private static async Task<string?> TryReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("message", out var message))
                return message.GetString();
            if (document.RootElement.TryGetProperty("title", out var title))
                return title.GetString();
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public void Dispose() => _httpClient.Dispose();
}
