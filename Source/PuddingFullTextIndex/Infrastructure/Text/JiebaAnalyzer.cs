using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Util;

namespace PuddingFullTextIndex.Infrastructure.Text;

/// <summary>
/// 中文全文搜索 Analyzer — 使用 jieba.NET JiebaTokenizer + LowerCaseFilter。
/// 中文精准分词（"全文搜索" → "全文"、"搜索"），英文做标准分词和小写。
/// </summary>
public sealed class JiebaAnalyzer : Analyzer
{
    private static readonly LuceneVersion MatchVersion = LuceneVersion.LUCENE_48;

    protected override TokenStreamComponents CreateComponents(string fieldName, System.IO.TextReader reader)
    {
        // ① jieba 分词（共享单例 Segmenter，避免重复加载词典）
        var tokenizer = new JiebaTokenizer(reader, JiebaSegmenterPool.Instance);

        // ② 英文小写（对中文无影响）
        var lowerFilter = new LowerCaseFilter(MatchVersion, tokenizer);

        return new TokenStreamComponents(tokenizer, lowerFilter);
    }
}
