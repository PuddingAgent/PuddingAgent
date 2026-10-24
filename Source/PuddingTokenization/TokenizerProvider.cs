using System.Collections.Concurrent;
using Microsoft.ML.Tokenizers;

namespace PuddingTokenization;

/// <summary>
/// 一次「按模型取 tokenizer」的结果：要么拿到精确 tokenizer，要么带着原因回退。
/// </summary>
/// <remarks>
/// 这里刻意**不提供**「回退时也返回一个数字」的便捷方法。回退意味着算出来的数字
/// 不是该模型的，静默返回只会让偏差继续被当成事实使用——正是当前缺陷的成因。
/// 调用方必须先看 <see cref="IsExact"/>，再决定要不要用 <see cref="CountTokens"/>。
/// </remarks>
public sealed class TokenizerHandle
{
    private readonly BpeTokenizer? _tokenizer;

    internal TokenizerHandle(
        string identity,
        BpeTokenizer? tokenizer,
        TokenizerFallbackReason fallbackReason,
        string? fallbackDetail)
    {
        Identity = identity;
        _tokenizer = tokenizer;
        FallbackReason = fallbackReason;
        FallbackDetail = fallbackDetail;
    }

    /// <summary>词表身份；未登记时为 <c>"(unregistered)"</c>。</summary>
    public string Identity { get; }

    /// <summary>是否拿到了该模型的精确 tokenizer。</summary>
    public bool IsExact => _tokenizer is not null;

    /// <summary>
    /// 底层 tokenizer（<c>BpeTokenizer</c> 本身就是 <c>Tokenizer</c> 子类，
    /// 所以调用方可以直接嵌入现有计数链路，不必为了适配再加一层包装）。
    /// </summary>
    public BpeTokenizer? UnderlyingTokenizer => _tokenizer;

    /// <summary>未能精确时的原因；精确时为 <see cref="TokenizerFallbackReason.None"/>。</summary>
    public TokenizerFallbackReason FallbackReason { get; }

    /// <summary>回退的补充说明（文件路径、异常消息），仅供诊断输出。</summary>
    public string? FallbackDetail { get; }

    /// <summary>
    /// 按该模型的词表计数。
    /// </summary>
    /// <exception cref="InvalidOperationException">当前是回退状态，计数结果不代表该模型。</exception>
    public int CountTokens(string? text)
    {
        if (_tokenizer is null)
        {
            throw new InvalidOperationException(
                $"未取得模型 {Identity} 的精确 tokenizer（{FallbackReason}），" +
                "计数值不可用于该模型；调用方应先检查 IsExact。");
        }

        return string.IsNullOrEmpty(text) ? 0 : _tokenizer.EncodeToIds(text).Count;
    }

    /// <summary>非抛出版本：精确时返回 true 并给出计数。</summary>
    public bool TryCountTokens(string? text, out int tokens)
    {
        if (_tokenizer is null)
        {
            tokens = 0;
            return false;
        }

        tokens = string.IsNullOrEmpty(text) ? 0 : _tokenizer.EncodeToIds(text).Count;
        return true;
    }
}

/// <summary>
/// 按 modelId 解析并缓存 <see cref="TokenizerHandle"/>。
/// </summary>
/// <remarks>
/// <para>
/// 线程安全：缓存用 <see cref="ConcurrentDictionary{TKey,TValue}"/> 承载
/// <see cref="Lazy{T}"/>，保证同一词表只解析一次。128K 词表 + 127K merges 实测
/// 构建约 185 ms，重复构建会明显拖慢首轮请求，因此必须缓存而不是每次新建。
/// </para>
/// <para>
/// 失败不抛：任何加载问题都转成 <see cref="TokenizerFallbackReason"/> 交给调用方，
/// 让「拿不到精确词表」永远退回到既有行为，而不是让计数链路整体失败。
/// </para>
/// </remarks>
public sealed class TokenizerProvider
{
    private const string UnregisteredIdentity = "(unregistered)";
    private readonly string? _assetDirectory;
    private readonly ConcurrentDictionary<string, Lazy<TokenizerHandle>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private TokenizerProvider(string? assetDirectory) => _assetDirectory = assetDirectory;

    /// <summary>
    /// 创建 provider。
    /// </summary>
    /// <param name="assetDirectory">
    /// 存放 <c>&lt;identity&gt;.tokenizer.json</c> 的目录。为 null 或空 ⇒ 一律回退
    /// （<see cref="TokenizerFallbackReason.NotConfigured"/>），不报错。
    /// </param>
    public static TokenizerProvider Create(string? assetDirectory)
        => new(string.IsNullOrWhiteSpace(assetDirectory) ? null : assetDirectory.Trim());

    /// <summary>解析 modelId 对应的 handle（结果被缓存）。</summary>
    public TokenizerHandle Resolve(string? modelId)
    {
        var identity = ModelTokenizerCatalog.ResolveExact(modelId);
        if (identity is null)
        {
            return new TokenizerHandle(
                UnregisteredIdentity,
                null,
                TokenizerFallbackReason.ModelNotRegistered,
                modelId);
        }

        if (_assetDirectory is null)
        {
            return new TokenizerHandle(
                identity,
                null,
                TokenizerFallbackReason.NotConfigured,
                null);
        }

        return ResolveByIdentity(identity);
    }

    /// <summary>
    /// 按**词表身份**直接解析，跳过 modelId 别名映射。
    /// </summary>
    /// <remarks>
    /// 供诊断路径使用。诊断若反过来借 <see cref="Resolve"/> 拿身份，只有在「身份字符串恰好也是
    /// 一个已登记别名」时才成立——把诊断正确性挂在巧合上，改一次别名表就会静默失效。
    /// </remarks>
    public TokenizerHandle ResolveByIdentity(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        if (_assetDirectory is null)
        {
            return new TokenizerHandle(
                identity,
                null,
                TokenizerFallbackReason.NotConfigured,
                null);
        }

        return _cache.GetOrAdd(
            identity,
            key => new Lazy<TokenizerHandle>(() => Load(key), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    private TokenizerHandle Load(string identity)
    {
        var path = Path.Combine(_assetDirectory!, identity + ".tokenizer.json");
        if (!File.Exists(path))
            return new TokenizerHandle(identity, null, TokenizerFallbackReason.AssetMissing, path);

        try
        {
            using var stream = File.OpenRead(path);
            var result = DeepSeekTokenizerJsonLoader.Load(stream);
            return new TokenizerHandle(identity, result.Tokenizer, TokenizerFallbackReason.None, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            // 内容级失败：文件在、但结构不符预期。JsonException 也属于这一类——
            // 「JSON 都解不开」与「字段缺失」对调用方是同一件事：这份资源不能用。
            return new TokenizerHandle(identity, null, TokenizerFallbackReason.AssetInvalid, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new TokenizerHandle(identity, null, TokenizerFallbackReason.LoadFailed, ex.Message);
        }
    }
}
