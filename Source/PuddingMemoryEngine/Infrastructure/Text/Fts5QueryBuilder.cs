namespace PuddingMemoryEngine.Infrastructure.Text;

/// <summary>
/// FTS5 查询构建器 — 将用户查询转为 SQLite FTS5 MATCH 表达式。
/// 纯静态函数，零 Pudding 依赖，可独立单元测试。
/// </summary>
public static class Fts5QueryBuilder
{
    /// <summary>
    /// 构建 FTS5 查询字符串：jieba 中文分词 + 空格英文分词，
    /// 每个词元用双引号包裹，以 OR 连接。
    /// </summary>
    /// <param name="query">用户原始查询文本</param>
    /// <param name="segmenter">jieba 分词器实例</param>
    /// <param name="isStopWord">停用词判定函数</param>
    /// <returns>FTS5 MATCH 表达式</returns>
    public static string Build(JiebaNet.Segmenter.JiebaSegmenter segmenter, Func<string, bool> isStopWord, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "\"\"";

        // ① 检测是否含中文字符
        bool hasChinese = query.Any(c => c >= 0x4E00 && c <= 0x9FFF);

        // ② jieba 分词（中文路径）
        var tokens = new List<string>();
        if (hasChinese)
        {
            var jiebaTokens = segmenter.Cut(query)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Where(t => !isStopWord(t))
                .ToList();
            tokens.AddRange(jiebaTokens);
        }

        // ③ 空格分词（英文路径）
        var spaceTokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in spaceTokens)
        {
            if (!tokens.Any(existing => string.Equals(existing, t, StringComparison.OrdinalIgnoreCase)))
                tokens.Add(t);
        }

        // ④ 兜底：无词元时返回原始查询
        if (tokens.Count == 0)
            return $"\"{query.Replace("\"", "\"\"")}\"";

        // ⑤ 智能连接策略：
        //    - 2 个以下词元 → OR（宽松匹配）
        //    - 3 个以上词元 + 中文 → 前 2 个 AND，其余 OR（精确优先，减少误召回）
        //    - 纯英文 → 保持 OR
        var escapedTokens = tokens.Select(t => $"\"{t.Replace("\"", "\"\"")}\"").ToList();
        if (tokens.Count <= 2 || !hasChinese)
            return string.Join(" OR ", escapedTokens);

        var andPart = string.Join(" AND ", escapedTokens.Take(2));
        var orPart = string.Join(" OR ", escapedTokens.Skip(2));
        return $"({andPart}) OR ({orPart})";
    }
}
