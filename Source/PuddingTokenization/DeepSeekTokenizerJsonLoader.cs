using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace PuddingTokenization;

/// <summary>加载成功后的自述信息，用于诊断与测试断言。</summary>
/// <remarks>
/// 这些数字不是装饰：它们让「加载成功」可被证伪。实测中曾经出现过
/// 「LOADED 成功但中文全部编码为 0 个 token」的情况——只断言「不抛异常」
/// 完全抓不到那种失败，必须能核对词表规模、切分级数与 ByteLevel 开关。
/// </remarks>
public sealed record TokenizerLoadResult(
    BpeTokenizer Tokenizer,
    int VocabularySize,
    int MergeCount,
    int SpecialTokenCount,
    int SplitStageCount,
    bool UsesByteLevel,
    string ModelType);

/// <summary>
/// 把 HuggingFace <c>tokenizer.json</c> 解析成可用的 <see cref="BpeTokenizer"/>。
/// </summary>
/// <remarks>
/// <para>
/// 之所以需要这个类而不是一句 <c>BpeTokenizer.Create(vocab, merges)</c>：实测证明那条
/// 捷径对 DeepSeek 词表**完全不可用**——<c>"人工智能"</c> 会编码成 0 个 token，
/// <c>&lt;｜begin▁of▁sentence｜&gt;</c> 会被拆成 6 个普通 token。原因是缺两样东西：
/// </para>
/// <list type="number">
/// <item>字节级映射（<see cref="BpeOptions.ByteLevel"/>）——DeepSeek 词表以 GPT-2 风格
/// 的字节表示存储（空格写作 <c>Ġ</c>），不做映射则 UTF-8 中文匹配不到任何词条。</item>
/// <item>预分词级（三条正则 + special token 整体识别）——数字按 1~3 位切、
/// 中日韩字符整体切、标点与字母按规则切。少了这些，token 数会系统性偏移。</item>
/// </list>
/// <para>
/// 预分词规则**从 JSON 里读，不硬编码**。第三条正则含引号、反斜杠与 Unicode 类别转义，
/// 手抄极难保证与上游一致；而它与上游差一个字符，产出的计数就是错的却看不出来。
/// </para>
/// </remarks>
public static class DeepSeekTokenizerJsonLoader
{
    private const string ExpectedModelType = "BPE";

    /// <summary>
    /// 从 <c>tokenizer.json</c> 流构造 tokenizer。
    /// </summary>
    /// <exception cref="InvalidDataException">结构不符合预期（模型类型、缺 vocab/merges 等）。</exception>
    /// <exception cref="ArgumentException">预分词规则非法。</exception>
    public static TokenizerLoadResult Load(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var vocabulary = new List<KeyValuePair<string, int>>(160_000);
        var merges = new List<string>(160_000);
        var specialTokens = new Dictionary<string, int>(StringComparer.Ordinal);
        var splitPatterns = new List<string>(4);
        var usesByteLevel = false;
        string? modelType = null;

        // JsonDocument 持有整棵树；tokenizer.json 实测 ~6.4 MB，一次性解析可接受，
        // 且必须整棵可用（vocab 与 merges 都在 model 节点下）。
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("model", out var model))
            throw new InvalidDataException("tokenizer.json 缺少 model 节点。");

        modelType = model.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;

        if (!string.Equals(modelType, ExpectedModelType, StringComparison.Ordinal))
        {
            // 不猜测其他模型类型（Unigram / WordPiece）的语义：静默按 BPE 处理
            // 会产出看似成功、实则全错的计数。
            throw new InvalidDataException(
                $"不支持的 model.type=\"{modelType}\"，本加载器只处理 {ExpectedModelType}。");
        }

        if (!model.TryGetProperty("vocab", out var vocabElement) ||
            vocabElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("tokenizer.json 缺少 model.vocab 对象。");

        foreach (var entry in vocabElement.EnumerateObject())
            vocabulary.Add(new KeyValuePair<string, int>(entry.Name, entry.Value.GetInt32()));

        if (!model.TryGetProperty("merges", out var mergesElement) ||
            mergesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("tokenizer.json 缺少 model.merges 数组。");

        foreach (var merge in mergesElement.EnumerateArray())
        {
            var value = merge.GetString();
            if (!string.IsNullOrEmpty(value))
                merges.Add(value);
        }

        if (vocabulary.Count == 0 || merges.Count == 0)
            throw new InvalidDataException(
                $"词表为空（vocab={vocabulary.Count}, merges={merges.Count}）。");

        // added_tokens 才是 special token 的权威来源（实测 1,283 条）。
        if (root.TryGetProperty("added_tokens", out var addedTokens) &&
            addedTokens.ValueKind == JsonValueKind.Array)
        {
            foreach (var token in addedTokens.EnumerateArray())
            {
                if (!token.TryGetProperty("content", out var contentElement) ||
                    !token.TryGetProperty("id", out var idElement))
                    continue;

                var content = contentElement.GetString();
                if (!string.IsNullOrEmpty(content))
                    specialTokens[content] = idElement.GetInt32();
            }
        }

        // 递归收集 Split 正则，并记录是否存在 ByteLevel 节点。
        if (root.TryGetProperty("pre_tokenizer", out var preTokenizer))
            WalkPreTokenizer(preTokenizer, splitPatterns, ref usesByteLevel);

        if (splitPatterns.Count == 0)
            throw new InvalidDataException("pre_tokenizer 里没有找到任何 Split 正则。");

        if (!usesByteLevel)
        {
            // 没有 ByteLevel 就等于没有字节级映射；此时中文会编码成 0 个 token。
            // 宁可明确失败，也不要返回一个「能构造但计数全错」的对象。
            throw new InvalidDataException(
                "pre_tokenizer 里没有 ByteLevel 节点，该词表无法正确编码非 ASCII 文本。");
        }

        var stages = new List<PreTokenizer>(splitPatterns.Count);
        foreach (var pattern in splitPatterns)
        {
            Regex regex;
            try
            {
                regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"预分词正则无法编译: {pattern}", ex);
            }

            // specialTokens 必须逐级传入：只给 BpeOptions 时 special token 不会被
            // 整体识别（实测会被拆成 12 个普通 token）。
            stages.Add(new RegexPreTokenizer(regex, specialTokens));
        }

        var composite = new CompositePreTokenizer(stages, specialTokens);

        // ByteLevel 走 BpeOptions 开关，而不是自写 Normalizer：
        // 官方语义是 Sequence[Split..., ByteLevel]（先切分再字节映射），而
        // Create(preTokenizer, normalizer) 是「先 normalizer 后 preTokenizer」，
        // 自写 Normalizer 会把顺序反转，导致正则匹配不到原文而失效。
        var tokenizer = BpeTokenizer.Create(new BpeOptions(vocabulary)
        {
            Merges = merges,
            ByteLevel = true,
            SpecialTokens = specialTokens,
            PreTokenizer = composite,
        });

        return new TokenizerLoadResult(
            tokenizer,
            vocabulary.Count,
            merges.Count,
            specialTokens.Count,
            stages.Count,
            usesByteLevel,
            modelType!);
    }

    private static void WalkPreTokenizer(
        JsonElement element,
        List<string> splitPatterns,
        ref bool usesByteLevel)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        if (element.TryGetProperty("type", out var typeElement))
        {
            var typeName = typeElement.GetString();
            if (string.Equals(typeName, "ByteLevel", StringComparison.Ordinal))
                usesByteLevel = true;

            if (string.Equals(typeName, "Split", StringComparison.Ordinal) &&
                element.TryGetProperty("pattern", out var pattern) &&
                pattern.ValueKind == JsonValueKind.Object &&
                pattern.TryGetProperty("Regex", out var regexElement))
            {
                var regex = regexElement.GetString();
                if (!string.IsNullOrEmpty(regex))
                    splitPatterns.Add(regex);
            }
        }

        // Sequence 节点下的键名可能是 pretokenizers（官方）或 pretokenizer（单数）。
        foreach (var key in SequenceChildKeys)
        {
            if (element.TryGetProperty(key, out var children) &&
                children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                    WalkPreTokenizer(child, splitPatterns, ref usesByteLevel);
            }
        }
    }

    private static readonly string[] SequenceChildKeys = ["pretokenizers", "pretokenizer"];
}
