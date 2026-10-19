using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PuddingCode.Configuration;
using PuddingHomeAssistant.Contracts;
using PuddingHomeAssistant.Policy;
using PuddingHomeAssistant.Rest;

namespace PuddingAgent.HomeAssistant;

/// <summary>
/// 进程内共享的 HA 传输层。持有单个 <see cref="HttpClient"/>（连接池复用），
/// 禁用自动重定向（防止被 302 引向内网其它服务），超时统一由调用方的 CancellationToken 控制。
/// </summary>
public sealed class HaClientFactory : IDisposable
{
    private readonly HttpClient _http;

    public HaClientFactory()
    {
        _http = new HttpClient(
            new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AllowAutoRedirect = false,
            },
            disposeHandler: true)
        {
            // 组件契约：deadline 由 CancellationToken 表达，HttpClient 不再叠加自己的超时。
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public HaRestClient Create() => new(_http);

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// 从 Agent 私有目录读取 Home Assistant 实例配置：
/// <c>&lt;DataRoot&gt;/agents/&lt;configAgentId&gt;/secrets/home-assistant.json</c>。
///
/// <para>文件格式：</para>
/// <code>
/// { "instances": [
///     { "alias": "asus", "baseUrl": "http://192.168.100.10:8123/", "token": "…", "optInDomains": ["media_player"] }
/// ] }
/// </code>
///
/// <para>不变量：</para>
/// <list type="bullet">
/// <item>token 只在内存中存在；不写日志、不进工具回执；</item>
/// <item>单条非法配置**跳过并告警**，不影响其它实例（fail-soft 到配置粒度）；</item>
/// <item>按文件 mtime + 长度做缓存失效，Admin 改配置后下一次调用即生效。</item>
/// </list>
/// </summary>
public sealed class HaInstanceStore(PuddingDataPaths paths, ILogger<HaInstanceStore> logger)
{
    private const string FileName = "home-assistant.json";
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public string ResolvePath(string configAgentId) =>
        Path.Combine(paths.AgentInstancesRoot, configAgentId, "secrets", FileName);

    /// <summary>读取某 Agent 的全部实例配置；文件不存在时返回空集合（不是错误）。</summary>
    public IReadOnlyList<HaInstance> Load(string configAgentId)
    {
        if (string.IsNullOrWhiteSpace(configAgentId))
        {
            return [];
        }

        var path = ResolvePath(configAgentId);
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Home Assistant 配置路径不可访问: {Path}", path);
            return [];
        }

        if (!info.Exists)
        {
            return [];
        }

        if (_cache.TryGetValue(configAgentId, out var cached) &&
            cached.LastWriteUtc == info.LastWriteTimeUtc &&
            cached.Length == info.Length)
        {
            return cached.Instances;
        }

        IReadOnlyList<HaInstance> instances;
        try
        {
            using var stream = File.OpenRead(path);
            var doc = JsonSerializer.Deserialize<ConfigDocument>(stream, s_json);
            instances = Parse(doc);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Home Assistant 配置读取失败: {Path}", path);
            return cached?.Instances ?? [];
        }

        _cache[configAgentId] = new CacheEntry(info.LastWriteTimeUtc, info.Length, instances);
        return instances;
    }

    /// <summary>按 alias 查找；alias 为空且只有一个实例时返回该实例（便利但仍然是显式寻址）。</summary>
    public HaInstance? Find(string configAgentId, string? alias)
    {
        var all = Load(configAgentId);
        if (all.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(alias))
        {
            return all.Count == 1 ? all[0] : null;
        }

        return all.FirstOrDefault(i => string.Equals(i.Alias, alias.Trim(), StringComparison.Ordinal));
    }

    /// <summary>供工具回执展示的实例描述（永不含 token）。</summary>
    public static IReadOnlyList<HaInstanceDescriptor> Describe(IEnumerable<HaInstance> instances) =>
        instances.Select(i => new HaInstanceDescriptor(
            i.Alias,
            i.BaseUrl.ToString(),
            !string.IsNullOrEmpty(i.Token),
            i.OptInDomains.OrderBy(d => d, StringComparer.Ordinal).ToArray())).ToArray();

    /// <summary>令牌预览（Admin 界面用）：只给前 6 后 4，永不回传完整令牌。</summary>
    public static string? PreviewToken(string? token) =>
        string.IsNullOrEmpty(token)
            ? null
            : token.Length <= 12
                ? "****"
                : token[..6] + "…" + token[^4..];

    /// <summary>
    /// 原子保存实例配置：写临时文件后 <c>File.Move(overwrite)</c> 替换，避免半写入。
    /// 保存后立即失效缓存，使工具下一次调用就读到新值。
    /// </summary>
    public async Task SaveAsync(string configAgentId, IReadOnlyList<HaInstance> instances, CancellationToken ct)
    {
        var path = ResolvePath(configAgentId);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var doc = new ConfigDocument
        {
            Instances = instances.Select(i => new ConfigItem
            {
                Alias = i.Alias,
                BaseUrl = i.BaseUrl.ToString(),
                Token = i.Token,
                OptInDomains = i.OptInDomains.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            }).ToList(),
        };

        var json = JsonSerializer.Serialize(
            doc,
            new JsonSerializerOptions(s_json) { WriteIndented = true });

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
            .ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
        _cache.TryRemove(configAgentId, out _);
    }

    private IReadOnlyList<HaInstance> Parse(ConfigDocument? doc)
    {
        if (doc?.Instances is null || doc.Instances.Count == 0)
        {
            return [];
        }

        var result = new List<HaInstance>(doc.Instances.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in doc.Instances)
        {
            if (string.IsNullOrWhiteSpace(item.Alias) ||
                !Uri.TryCreate(item.BaseUrl, UriKind.Absolute, out var baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            {
                logger.LogWarning("跳过非法的 Home Assistant 实例配置（alias/baseUrl）: alias={Alias}", item.Alias);
                continue;
            }

            if (!seen.Add(item.Alias.Trim()))
            {
                logger.LogWarning("跳过重复 alias 的 Home Assistant 实例: {Alias}", item.Alias);
                continue;
            }

            var optIn = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in item.OptInDomains ?? [])
            {
                if (HaDomainPolicy.TryNormalize(raw, out var normalized))
                {
                    optIn.Add(normalized);
                }
            }

            result.Add(new HaInstance
            {
                Alias = item.Alias.Trim(),
                BaseUrl = baseUri,
                Token = item.Token,
                OptInDomains = optIn,
            });
        }

        return result;
    }

    private sealed record CacheEntry(DateTime LastWriteUtc, long Length, IReadOnlyList<HaInstance> Instances);

    private sealed class ConfigDocument
    {
        public List<ConfigItem>? Instances { get; set; }
    }

    private sealed class ConfigItem
    {
        public string? Alias { get; set; }
        public string? BaseUrl { get; set; }
        public string? Token { get; set; }
        public List<string>? OptInDomains { get; set; }
    }
}
