using System.Text;

namespace PuddingPlatform.Data.Diagnostics;

/// <summary>
/// 把一条具体 SQL 归一成"同一形状"的 key，使只有参数值不同的同一查询合并计数。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：没有归一化时，<c>WHERE id = 1</c> 与 <c>WHERE id = 2</c> 会各自成为一条
/// 统计项，几千行数据就能长出几千个 key，统计表本身变成内存问题，也看不出"哪条语句最频繁"。
/// </para>
/// <para>
/// 归一化必须**纯函数**：不做 IO、不读时钟、不依赖连接。这样"归一规则是否正确"可以用普通
/// 单元测试钉死——而这正是最容易被改坏、也最容易被忽视的部分。
/// </para>
/// <para>
/// 刻意保守：只替换**能被可靠识别**的四类片段，从不尝试完整解析 SQL。
/// 宁可少合并（多几个 key），也不要错合并（把两条不同的查询算成一条，那会误导诊断）。
/// </para>
/// </remarks>
internal static class SqlStatementKey
{
    /// <summary>归一化后为空时使用的占位 key；调用方应据此跳过统计。</summary>
    internal const string EmptyKey = "";

    /// <summary>
    /// 归一化一条 SQL。
    /// </summary>
    /// <param name="sql">原始 SQL 文本。</param>
    /// <returns>归一化后的 key；输入为 null/空白时返回 <see cref="EmptyKey"/>。</returns>
    internal static string Normalize(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return EmptyKey;

        var sb = new StringBuilder(sql.Length);
        var i = 0;
        var len = sql.Length;

        while (i < len)
        {
            var c = sql[i];

            // 1) 字符串字面量：'...'，内部 '' 表示一个单引号。
            //    必须最先处理：否则字符串里的 @name / 123 会被后面的规则误伤。
            if (c == '\'')
            {
                sb.Append('?');
                i++;
                while (i < len)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < len && sql[i + 1] == '\'')
                        {
                            i += 2;   // '' 是转义的引号，字符串还没结束
                            continue;
                        }
                        i++;          // 真正的结束引号
                        break;
                    }
                    i++;
                }
                continue;
            }

            // 2) 参数占位符：@name / $name / :name。
            //    只在后面紧跟标识符首字符时才替换，避免把 'a:@' 这种残留标点当成参数。
            if ((c == '@' || c == '$' || c == ':') && i + 1 < len && IsKeyStart(sql[i + 1]))
            {
                sb.Append('?');
                i++;
                while (i < len && IsKeyPart(sql[i]))
                    i++;
                continue;
            }

            // 3) 数字字面量，但只在它**不属于标识符**时替换。
            //    这条守卫是关键：table1 / col2 里的数字是名字的一部分，替换它们会把
            //    两条不同的语句合并成同一条。
            if (c >= '0' && c <= '9')
            {
                var previousIsIdentifierChar = i > 0 && IsKeyPart(sql[i - 1]);
                if (previousIsIdentifierChar)
                {
                    // 整个标识符原样抄过去
                    while (i < len && IsKeyPart(sql[i]))
                    {
                        sb.Append(sql[i]);
                        i++;
                    }
                    continue;
                }

                sb.Append('?');
                while (i < len && sql[i] >= '0' && sql[i] <= '9')
                    i++;
                // 小数部分一起吞掉，避免 '1.5' 变成 '?.5'
                if (i + 1 < len && sql[i] == '.' && sql[i + 1] >= '0' && sql[i + 1] <= '9')
                {
                    i++;
                    while (i < len && sql[i] >= '0' && sql[i] <= '9')
                        i++;
                }
                continue;
            }

            // 4) 其余字符：连续空白折叠成一个空格，其它原样保留（大小写不改，那会影响可读性）。
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                    sb.Append(' ');
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        // 去掉尾部空白（归一化产物用作字典 key，尾部空格会造成重复 key）
        var end = sb.Length;
        while (end > 0 && sb[end - 1] == ' ')
            end--;
        sb.Length = end;

        return sb.ToString();
    }

    private static bool IsKeyStart(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';

    private static bool IsKeyPart(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
}
