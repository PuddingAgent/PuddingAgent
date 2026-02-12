using Lucene.Net.Analysis;
using Lucene.Net.Analysis.TokenAttributes;
using JiebaNet.Segmenter;

namespace PuddingFullTextIndex.Infrastructure.Text;

/// <summary>
/// Lucene Tokenizer 适配器：使用 jieba.NET JiebaSegmenter 进行中文精准分词。
/// 对英文/数字不做特殊处理，jieba 会将其作为单独词元输出。
/// </summary>
public sealed class JiebaTokenizer : Tokenizer
{
    private readonly JiebaSegmenter _segmenter;
    private readonly ICharTermAttribute _termAttr;
    private readonly IOffsetAttribute _offsetAttr;
    private IEnumerator<string>? _tokenEnumerator;

    public JiebaTokenizer(TextReader reader, JiebaSegmenter segmenter)
        : base(reader)
    {
        _segmenter = segmenter;
        _termAttr = AddAttribute<ICharTermAttribute>();
        _offsetAttr = AddAttribute<IOffsetAttribute>();
    }

    public override void Reset()
    {
        base.Reset();
        // 一次性读取全部文本，交给 jieba 分词
        var text = m_input.ReadToEnd();
        var tokens = _segmenter.Cut(text, cutAll: false, hmm: true)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        _tokenEnumerator = tokens.GetEnumerator();
    }

    public override bool IncrementToken()
    {
        if (_tokenEnumerator == null)
            return false;

        if (!_tokenEnumerator.MoveNext())
        {
            _tokenEnumerator.Dispose();
            _tokenEnumerator = null;
            return false;
        }

        var token = _tokenEnumerator.Current;
        ClearAttributes();
        _termAttr.Append(token);
        // 使用基于 token 索引的粗略偏移
        _offsetAttr.SetOffset(0, token.Length);
        return true;
    }
}
