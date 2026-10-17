using System.Net.Http;
using System.Text;
using System.Text.Json;
using PuddingDeepSeekWeb;

namespace PuddingDeepSeekWebTests;

/// <summary>
/// 真实 DeepSeek Search / Fetch 冒烟（<c>[TestCategory("Live")]</c>）。
/// <para>
/// 凭据解析顺序：环境变量 <c>DEEPSEEK_API_KEY</c>（可选 <c>DEEPSEEK_BASE_URL</c>）→
/// <c>PUDDING_DATA_ROOT</c> / 本机 <c>D:\data</c> 下的 <c>config/llm.providers.json</c> →
/// 从 <see cref="AppContext.BaseDirectory"/> 向上查找 <c>data/config/llm.providers.json</c>；
/// 读取 <c>providers[]</c> 中 <c>providerId == "deepseek"</c> 的 <c>baseUrl</c>/<c>apiKey</c>。
/// 拿不到可用凭据时用例 <see cref="Assert.Inconclusive(string)"/>，不会失败。
/// </para>
/// <para>
/// 安全：绝不硬编码、打印或断言 API Key；生产代码（组件）仍然不直接使用 HTTP —— 本文件的
/// <see cref="HttpClient"/> 适配器只存在于**测试工程**，用来实现组件的传输端口。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed class DeepSeekWebLiveTests
{
    private const string SearchQuery = "DeepSeek 官方文档";
    private const int SearchCount = 3;

    private static DeepSeekSearchResult? _firstSearchResult;

    /// <summary>用例 1：真实 Search 返回 ≥1 条结果，首条 Title / Url 非空。</summary>
    [TestMethod]
    [TestCategory("Live")]
    public async Task Live_Search_Returns_DeepSeek_Docs_Results()
    {
        var credentials = DeepSeekCredentials.Resolve();
        if (credentials is null)
        {
            Assert.Inconclusive(
                "未找到 DeepSeek 凭据：请设置 DEEPSEEK_API_KEY，或让 "
                + "PUDDING_DATA_ROOT / D:\\data / 仓库上层的 config/llm.providers.json "
                + "包含 providerId=deepseek 且 apiKey 可用的 provider。");
            return;
        }

        var service = new DeepSeekWebService(new HttpClientDeepSeekTransport());
        var connection = credentials.ToConnection();
        Console.WriteLine(
            $"[Live][Search] baseUrl={connection.BaseUrl} query={SearchQuery} count={SearchCount} need_content=false");

        var result = await service.SearchAsync(
            connection,
            new DeepSeekSearchRequest
            {
                Query = SearchQuery,
                Count = SearchCount,
                NeedContent = false,
            });

        Assert.IsTrue(result.IsSuccess, "[Live][Search] real API call failed: " + Describe(result));

        var response = result.Value!;
        Console.WriteLine($"[Live][Search] results={response.Results.Count}");
        Assert.IsGreaterThanOrEqualTo(
            1,
            response.Results.Count,
            "[Live][Search] expected at least one result");

        var first = response.Results[0];
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(first.Title),
            "[Live][Search] first result title must not be empty");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(first.Url),
            "[Live][Search] first result url must not be empty");

        Console.WriteLine($"[Live][Search] firstTitle={first.Title}");
        Console.WriteLine($"[Live][Search] firstUrl={first.Url}");
        Console.WriteLine(
            $"[Live][Search] firstRerankScore={first.RerankScore} authority={first.Authority} "
            + $"snippetLength={first.Snippet.Length} contentLength={first.Content.Length}");
        Console.WriteLine(
            $"[Live][Search] allTitles={string.Join(" | ", response.Results.Select(item => item.Title))}");

        _firstSearchResult = first;
    }

    /// <summary>用例 2：用用例 1 的首条 Url 调真实 Fetch，返回非空 Markdown 正文。</summary>
    [TestMethod]
    [TestCategory("Live")]
    public async Task Live_Fetch_Returns_Markdown_Content_For_Search_Result()
    {
        var credentials = DeepSeekCredentials.Resolve();
        if (credentials is null)
        {
            Assert.Inconclusive(
                "未找到 DeepSeek 凭据：请设置 DEEPSEEK_API_KEY，或让 "
                + "PUDDING_DATA_ROOT / D:\\data / 仓库上层的 config/llm.providers.json "
                + "包含 providerId=deepseek 且 apiKey 可用的 provider。");
            return;
        }

        var connection = credentials.ToConnection();
        var first = await ResolveFirstSearchResultAsync(connection);

        var service = new DeepSeekWebService(new HttpClientDeepSeekTransport());
        Console.WriteLine($"[Live][Fetch] url={first.Url}");

        var result = await service.FetchAsync(connection, new DeepSeekFetchRequest { Url = first.Url });

        Assert.IsTrue(result.IsSuccess, "[Live][Fetch] real API call failed: " + Describe(result));

        var response = result.Value!;
        Console.WriteLine($"[Live][Fetch] title={response.Title}");
        Console.WriteLine($"[Live][Fetch] contentLength={response.Content.Length}");
        Assert.IsGreaterThan(
            0,
            response.Content.Length,
            "[Live][Fetch] expected non-empty Markdown content");

        var preview = response.Content.Length <= 160
            ? response.Content
            : response.Content[..160];
        preview = preview.Replace('\r', ' ').Replace('\n', ' ');
        Console.WriteLine($"[Live][Fetch] contentPreview={preview}");
    }

    /// <summary>用例 2 单独运行时自行先做一次 Search，保证两个 Live 用例彼此独立可跑。</summary>
    private static async Task<DeepSeekSearchResult> ResolveFirstSearchResultAsync(
        DeepSeekWebConnection connection)
    {
        if (_firstSearchResult is { } cached && !string.IsNullOrWhiteSpace(cached.Url))
            return cached;

        var service = new DeepSeekWebService(new HttpClientDeepSeekTransport());
        var search = await service.SearchAsync(
            connection,
            new DeepSeekSearchRequest { Query = SearchQuery, Count = SearchCount });
        Assert.IsTrue(search.IsSuccess, "[Live][Fetch] setup search failed: " + Describe(search));

        var first = search.Value!.Results.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Url));
        Assert.IsNotNull(
            first,
            "[Live][Fetch] setup search returned no result with a non-empty url");

        _firstSearchResult = first;
        return first;
    }

    private static string Describe<T>(DeepSeekWebResult<T> result)
        where T : class =>
        result.Error is null
            ? "success"
            : $"{result.Error.Kind} (HTTP {result.Error.HttpStatusCode?.ToString() ?? "n/a"}) "
              + result.Error.Message;

    // =====================================================================
    // 凭据解析（只读；不存在时返回 null → Inconclusive）
    // =====================================================================

    private sealed record DeepSeekCredentials(string ApiKey, string BaseUrl)
    {
        public DeepSeekWebConnection ToConnection() => new()
        {
            ApiKey = ApiKey,
            BaseUrl = BaseUrl,
            TimeoutSeconds = 60,
        };

        public static DeepSeekCredentials? Resolve()
        {
            var environmentKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            if (!string.IsNullOrWhiteSpace(environmentKey))
            {
                var environmentBaseUrl = Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL");
                return new DeepSeekCredentials(
                    environmentKey.Trim(),
                    string.IsNullOrWhiteSpace(environmentBaseUrl)
                        ? DeepSeekWebConnection.DefaultBaseUrl
                        : environmentBaseUrl.Trim());
            }

            foreach (var configPath in CandidateConfigPaths())
            {
                if (!File.Exists(configPath))
                    continue;

                if (TryReadDeepSeekProvider(configPath) is not { } provider)
                    continue;

                var apiKey = ResolveSecretReference(provider.ApiKey);
                if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("${", StringComparison.Ordinal))
                {
                    Console.WriteLine(
                        $"[Live][Config] providerId=deepseek found but apiKey is unresolved at {configPath}");
                    continue;
                }

                var baseUrl = string.IsNullOrWhiteSpace(provider.BaseUrl)
                    ? DeepSeekWebConnection.DefaultBaseUrl
                    : provider.BaseUrl.Trim();
                Console.WriteLine($"[Live][Config] resolved credentials from {configPath}");
                return new DeepSeekCredentials(apiKey, baseUrl);
            }

            return null;
        }

        private static string ResolveSecretReference(string? rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                return string.Empty;

            var value = rawValue.Trim();
            if (value.StartsWith("${", StringComparison.Ordinal)
                && value.EndsWith('}')
                && value.Length > 3)
            {
                var variableName = value[2..^1].Trim();
                return Environment.GetEnvironmentVariable(variableName) ?? string.Empty;
            }

            return value;
        }

        private static IEnumerable<string> CandidateConfigPaths()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var dataRoot = Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT");
            if (!string.IsNullOrWhiteSpace(dataRoot))
            {
                var fromEnvironment = Path.Combine(
                    dataRoot.Trim(),
                    "config",
                    "llm.providers.json");
                if (seen.Add(fromEnvironment))
                    yield return fromEnvironment;
            }

            var localDataRoot = Path.Combine(@"D:\data", "config", "llm.providers.json");
            if (seen.Add(localDataRoot))
                yield return localDataRoot;

            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                var aboveOutput = Path.Combine(
                    directory.FullName,
                    "data",
                    "config",
                    "llm.providers.json");
                if (seen.Add(aboveOutput))
                    yield return aboveOutput;
            }
        }

        private static ProviderEntry? TryReadDeepSeekProvider(string configPath)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                if (!document.RootElement.TryGetProperty("providers", out var providers)
                    || providers.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var provider in providers.EnumerateArray())
                {
                    if (provider.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!provider.TryGetProperty("providerId", out var providerId)
                        || providerId.ValueKind != JsonValueKind.String
                        || !string.Equals(providerId.GetString(), "deepseek", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var apiKey = provider.TryGetProperty("apiKey", out var apiKeyElement)
                                 && apiKeyElement.ValueKind == JsonValueKind.String
                        ? apiKeyElement.GetString()
                        : null;
                    var baseUrl = provider.TryGetProperty("baseUrl", out var baseUrlElement)
                                  && baseUrlElement.ValueKind == JsonValueKind.String
                        ? baseUrlElement.GetString()
                        : null;
                    return new ProviderEntry(apiKey, baseUrl);
                }

                return null;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[Live][Config] {configPath} is not valid JSON: {ex.GetType().Name}");
                return null;
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[Live][Config] cannot read {configPath}: {ex.GetType().Name}");
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"[Live][Config] no permission to read {configPath}: {ex.GetType().Name}");
                return null;
            }
        }

        private sealed record ProviderEntry(string? ApiKey, string? BaseUrl);
    }

    // =====================================================================
    // 测试专用传输：BCL HttpClient（仅测试工程允许；组件本身不得直接说 HTTP）
    // =====================================================================

    private sealed class HttpClientDeepSeekTransport : IDeepSeekWebTransport
    {
        private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };

        public async Task<DeepSeekWebTransportResponse> SendAsync(
            DeepSeekWebTransportRequest request,
            CancellationToken ct = default)
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url)
            {
                Content = new StringContent(request.Body, Encoding.UTF8, request.ContentType),
            };
            foreach (var header in request.Headers)
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (request.TimeoutSeconds is int timeoutSeconds && timeoutSeconds > 0)
                cancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var response = await Client
                .SendAsync(message, cancellation.Token)
                .ConfigureAwait(false);
            var body = await response.Content
                .ReadAsStringAsync(cancellation.Token)
                .ConfigureAwait(false);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers)
                headers[header.Key] = string.Join(",", header.Value);
            foreach (var header in response.Content.Headers)
                headers[header.Key] = string.Join(",", header.Value);

            return new DeepSeekWebTransportResponse
            {
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase ?? string.Empty,
                Body = body,
                Headers = headers,
            };
        }
    }
}
