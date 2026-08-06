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
    private readonly IPositionIncrementAttribute _posIncrAttr;
    private int _offsetPos;
    private IEnumerator<string>? _tokenEnumerator;

    public JiebaTokenizer(TextReader reader, JiebaSegmenter segmenter)
        : base(reader)
    {
        _segmenter = segmenter;
        _termAttr = AddAttribute<ICharTermAttribute>();
        _offsetAttr = AddAttribute<IOffsetAttribute>();
        _posIncrAttr = AddAttribute<IPositionIncrementAttribute>();
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
        _offsetPos = 0;
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
        // posInc=1：令词元位置相邻，CJKBigramFilter/短语查询才能跨分词段工作
        _posIncrAttr.PositionIncrement = 1;
        // 偏移量累加单调递增（Lucene 要求 offsets 不得回退）
        var start = _offsetPos;
        _offsetPos += token.Length;
        _offsetAttr.SetOffset(start, _offsetPos);
        return true;
    }
}
