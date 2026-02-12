using System.Text;
using System.Text.RegularExpressions;
using NPOI.HSSF.UserModel;

using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.XWPF.UserModel;
using PuddingCode.Models;
using PuddingCode.Tools;

using UglyToad.PdfPig;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// 统一文档读取工具。支持 .docx / .xlsx / .xls / .pdf / .txt / .md
/// </summary>
[Tool(
    id: "read_office_document",
    name: "文档读取",
    description: "读取文档内容返回 Markdown。支持 .docx/.xlsx/.xls/.pdf/.txt/.md。action: auto(默认)/toc(目录)/pages(选页如1-5,10)/chapter(章节)/search(搜索)。例: path=\"/a.docx\" action=\"toc\"",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low)]
public sealed class ReadOfficeDocumentTool : PuddingToolBase<ReadOfficeDocumentArgs>
{
    private const int DefaultMaxChars = 200_000;
    private const int DefaultMaxRows = 1000;
    private const int LinesPerPage = 50;

    public ReadOfficeDocumentTool() { }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ReadOfficeDocumentArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var result = await ExecuteCore(args.Path, args.Action, args.Pages, args.Chapter, args.Query, args.Sheet, ct);
        return new ToolExecutionResult { Success = result.Success, Output = result.Output, Error = result.Error, ExitCode = result.ExitCode };
    }

    private Task<SkillResult> ExecuteCore(
        string path, string? action, string? pages, string? chapter, string? query, string? sheet, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return TaskFail("缺少必填参数：path");
        if (path.Contains("..")) return TaskFail("不允许路径遍历");
        if (!File.Exists(path)) return TaskFail($"文件不存在：{path}");

        var ext = Path.GetExtension(path).ToLowerInvariant();
        action = (action ?? "auto").ToLowerInvariant();

        try
        {
            var raw = ext switch
            {
                ".txt" or ".md" => File.ReadAllText(path, Encoding.UTF8),
                ".docx" => ReadDocx(path, ct),
                ".doc"  => "[不支持] .doc 暂不支持，请转换为 .docx 格式。",
                ".xlsx" or ".xls" => ReadExcel(path, sheet, ct),
                ".pdf"  => ReadPdf(path, pages, ct),
                ".pptx" => "[不支持] PowerPoint 暂未实现。PPTX 阅读器将在下一版本添加。",
                _       => $"[不支持] {ext} — 支持：.docx, .xlsx, .xls, .pdf, .txt, .md"
            };
            if (raw.StartsWith('[')) return TaskOk(raw);
            return TaskOk(ApplyAction(action!, raw, path, pages, chapter, query));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return TaskFail($"读取失败：{ex.Message}"); }
    }

    // ── Action methods ──

    private static string ApplyAction(string action, string content, string path,
        string? pages, string? chapter, string? query) => action switch
    {
        "toc"     => ExtractToc(content),
        "pages"   => ExtractPages(content, pages),
        "chapter" => ExtractChapter(content, chapter),
        "search"  => SearchContent(content, query),
        _         => AutoRead(content, path)
    };

    private static string AutoRead(string content, string path)
    {
        var sb = new StringBuilder(); sb.AppendLine($"# {Path.GetFileName(path)}");
        var lines = content.Split('\n');
        if (lines.Length <= 500) { sb.AppendLine(content); }
        else
        {
            sb.AppendLine($"（共 {lines.Length} 行，以下是前 {LinesPerPage * 2} 行预览 / 使用 toc/pages/chapter/search 模式查看）\n");
            sb.Append(ExtractToc(content)); sb.AppendLine("\n---\n");
            sb.Append(string.Join('\n', lines.Take(LinesPerPage * 2)));
        }
        return Truncate(sb.ToString(), DefaultMaxChars);
    }

    private static string ExtractToc(string content)
    {
        var sb = new StringBuilder(); sb.AppendLine("## 目录");
        var re = new Regex(@"^(#{2,6})\s+(.+)$", RegexOptions.Multiline);
        var n = 0;
        foreach (Match m in re.Matches(content))
        {
            if (++n > 50) break;
            var level = m.Groups[1].Value.Length - 1;
            sb.AppendLine($"{new string(' ', (level - 1) * 2)}- {m.Groups[2].Value}");
        }
        if (n == 0) sb.AppendLine("（未检测到 Markdown 标题）");
        return sb.ToString();
    }

    private static string ExtractPages(string content, string? pages)
    {
        if (string.IsNullOrWhiteSpace(pages)) return "请指定 pages=\"1-5\" 或 pages=\"1,3,5-8\"";
        var ranges = ParseRanges(pages);
        if (ranges.Count == 0) return $"无法解析页码范围：{pages}";
        var lines = content.Split('\n'); var sb = new StringBuilder();
        foreach (var (s, e) in ranges)
        {
            var start = (s - 1) * LinesPerPage; var end = Math.Min(e * LinesPerPage, lines.Length);
            if (start >= lines.Length) { sb.AppendLine($"### 第{s}-{e}页（超出）"); continue; }
            sb.AppendLine($"### 第{s}-{Math.Min(e, (int)Math.Ceiling((double)lines.Length / LinesPerPage))}页");
            sb.AppendLine(string.Join('\n', lines.Skip(start).Take(end - start)));
        }
        return Truncate(sb.ToString(), DefaultMaxChars);
    }

    private static string ExtractChapter(string content, string? chapter)
    {
        if (string.IsNullOrWhiteSpace(chapter)) return "请指定 chapter=\"章节名\"";
        var lines = content.Split('\n'); var re = new Regex(@"^(#{2,6})\s+(.+)$");
        int match = -1, level = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(chapter, StringComparison.OrdinalIgnoreCase)) continue;
            var m = re.Match(lines[i]); if (!m.Success) continue;
            match = i; level = m.Groups[1].Value.Length; break;
        }
        if (match < 0) return $"未找到章节：{chapter}";
        var sb = new StringBuilder();
        for (var i = match; i < lines.Length; i++)
        {
            var m = re.Match(lines[i]);
            if (m.Success && i > match && m.Groups[1].Value.Length <= level) break;
            sb.AppendLine(lines[i]); if (i - match > 400) break;
        }
        return Truncate(sb.ToString(), DefaultMaxChars);
    }

    private static string SearchContent(string content, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "请指定 query=\"关键词\"";
        var lines = content.Split('\n'); var sb = new StringBuilder(); sb.AppendLine($"## 搜索：{query}\n");
        var count = 0;
        for (var i = 0; i < lines.Length && count < 30; i++)
        {
            if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            count++;
            if (i > 0) sb.AppendLine($"  {lines[i - 1].Trim()}");
            sb.AppendLine($"> {lines[i].Trim()}  ← 行{i + 1}");
            if (i < lines.Length - 1) sb.AppendLine($"  {lines[i + 1].Trim()}"); sb.AppendLine();
        }
        sb.AppendLine(count == 0 ? $"(未找到：{query})" : $"(共 {count} 处匹配)");
        return Truncate(sb.ToString(), DefaultMaxChars);
    }

    // ── Readers ──

    private static string ReadDocx(string path, CancellationToken ct)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var d = new XWPFDocument(s);
        var sb = new StringBuilder(); sb.AppendLine($"# {Path.GetFileNameWithoutExtension(path)}\n");
        foreach (var p in d.Paragraphs)
        {
            ct.ThrowIfCancellationRequested();
            var t = p.ParagraphText?.Trim(); if (string.IsNullOrWhiteSpace(t)) continue;
            if (p.Style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true)
            {
                var lv = p.Style.Length > 7 && int.TryParse(p.Style[7..], out var l) ? Math.Clamp(l, 1, 6) : 1;
                sb.AppendLine(new string('#', lv + 1) + " " + t);
            }
            else sb.AppendLine(t);
            sb.AppendLine();
        }
        foreach (var t in d.Tables)
        {
            sb.AppendLine();
            foreach (var r in t.Rows)
                sb.AppendLine("| " + string.Join(" | ", r.GetTableCells().Select(c => c.GetText().Trim().Replace("\n", " "))) + " |");
            sb.AppendLine();
        }
        return sb.ToString();
    }


    private static string ReadExcel(string path, string? sheet, CancellationToken ct)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        using var wb = ext == ".xls" ? (IWorkbook)new HSSFWorkbook(s) : new XSSFWorkbook(s);
        var sb = new StringBuilder(); sb.AppendLine($"# {Path.GetFileNameWithoutExtension(path)}");
        for (var i = 0; i < wb.NumberOfSheets; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sh = wb.GetSheetAt(i);
            if (sheet != null && !sh.SheetName.Equals(sheet, StringComparison.OrdinalIgnoreCase)
                && !(int.TryParse(sheet, out var ii) && ii == i)) continue;
            sb.AppendLine($"\n## Sheet: {sh.SheetName}\n"); var rc = 0;
            foreach (IRow r in sh)
            {
                if (++rc > DefaultMaxRows) { sb.AppendLine($"...（截断，{sh.LastRowNum + 1}行）"); break; }
                var cells = new List<string>();
                for (var ci = 0; ci < r.LastCellNum; ci++)
                {
                    var c = r.GetCell(ci);
                    cells.Add(c == null ? "" : c.CellType switch
                    {
                        CellType.Numeric => DateUtil.IsCellDateFormatted(c) ? $"{c.DateCellValue:yyyy-MM-dd}" : $"{c.NumericCellValue}",
                        CellType.Boolean  => $"{c.BooleanCellValue}",
                        CellType.Formula  => c.CellFormula ?? "",
                        _                 => c.StringCellValue ?? ""
                    });
                }
                if (cells.All(string.IsNullOrWhiteSpace)) continue;
                sb.AppendLine("| " + string.Join(" | ", cells) + " |");
            }
        }
        return sb.ToString();
    }

    private static string ReadPdf(string path, string? pages, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(path);
        var sb = new StringBuilder();
        sb.AppendLine($"# {Path.GetFileNameWithoutExtension(path)}");
        sb.AppendLine($"（共 {pdf.NumberOfPages} 页）\n");

        HashSet<int> targetPages;
        bool hasExplicitRange = !string.IsNullOrWhiteSpace(pages);
        if (hasExplicitRange)
        {
            var ranges = ParseRanges(pages!); targetPages = new HashSet<int>();
            foreach (var (s, e) in ranges)
                for (int p = s; p <= e; p++)
                    if (p >= 1 && p <= pdf.NumberOfPages) targetPages.Add(p);
            if (targetPages.Count == 0) return $"页码范围 {pages} 超出文档范围（1-{pdf.NumberOfPages}）";
        }
        else if (pdf.NumberOfPages > 20) targetPages = new HashSet<int>(Enumerable.Range(1, Math.Min(10, pdf.NumberOfPages)));
        else targetPages = new HashSet<int>(Enumerable.Range(1, pdf.NumberOfPages));

        int scannedPages = 0, totalReadPages = 0;
        foreach (int i in targetPages.OrderBy(x => x))
        {
            ct.ThrowIfCancellationRequested();
            var text = pdf.GetPage(i).Text?.Trim() ?? ""; totalReadPages++;
            if (text.Length < 20)
            {
                scannedPages++; sb.AppendLine($"### 第 {i} 页 ⚠️ 疑似扫描件\n");
                if (text.Length > 0) sb.AppendLine(text); else sb.AppendLine("(无可提取文本 — 可能为扫描图片)");
                sb.AppendLine(); continue;
            }
            sb.AppendLine($"### 第 {i} 页\n"); sb.AppendLine(text); sb.AppendLine();
        }
        if (pdf.NumberOfPages > targetPages.Count && !hasExplicitRange)
            sb.AppendLine($"\n... （截断，共 {pdf.NumberOfPages} 页，使用 pages 参数读取其他页）");
        if (scannedPages > 0)
            sb.AppendLine($"\n⚠️ 检测到 {scannedPages}/{totalReadPages} 页疑似扫描件（文本 < 20 字符），建议配合 OCR 工具使用。");
        return Truncate(sb.ToString(), DefaultMaxChars);
    }

    // ── Helpers ──

    private static string Truncate(string t, int max) =>
        t.Length <= max ? t : t[..max] + $"\n\n... (截断，原始 {t.Length} 字符)";

    private static List<(int, int)> ParseRanges(string input)
    {
        var r = new List<(int, int)>();
        foreach (var p in input.Split(','))
        {
            if (int.TryParse(p.Trim(), out var s)) { r.Add((s, s)); continue; }
            var d = p.IndexOf('-');
            if (d > 0 && int.TryParse(p[..d], out var a) && int.TryParse(p[(d + 1)..], out var b))
                r.Add(a < b ? (a, b) : (b, a));
        }
        return r;
    }

    private static SkillResult Ok(string o) => new() { Success = true, Output = o };
    private static SkillResult Fail(string m) => new() { Success = false, Output = m };
    private static Task<SkillResult> TaskOk(string o) => Task.FromResult(Ok(o));
    private static Task<SkillResult> TaskFail(string m) => Task.FromResult(Fail(m));

    private static (string path, string? action, string? pages, string? chapter, string? query, string? sheet) ParseArgs(string input)
    {
        string path = "", action = null, pages = null, chapter = null, query = null, sheet = null;
        if (string.IsNullOrWhiteSpace(input)) return (path, action, pages, chapter, query, sheet);

        void Set(string k, string v)
        {
            v = v.Trim('"', '\'', ' ');
            switch (k) { case "path": path = v; break; case "action": action = v; break; case "pages": pages = v; break; case "chapter": chapter = v; break; case "query": query = v; break; case "sheet": sheet = v; break; }
        }

        var rem = input;
        while (rem.Length > 0)
        {
            var eq = rem.IndexOf('='); if (eq < 0) { Set("path", rem.Trim()); break; }
            var k = rem[..eq].TrimEnd(); rem = rem[(eq + 1)..].TrimStart();
            if (rem.Length == 0) break;
            if (rem[0] == '"') { var end = rem.IndexOf('"', 1); if (end >= 0) { Set(k, rem[1..end]); rem = rem[(end + 1)..].TrimStart(); continue; } }
            var sp = rem.IndexOf(' ');
            if (sp >= 0) { Set(k, rem[..sp]); rem = rem[(sp + 1)..]; } else { Set(k, rem); break; }
        }
        return (path, action, pages, chapter, query, sheet);
    }
}

public sealed record ReadOfficeDocumentArgs
{
    [ToolParam("文件路径")]
    public string? Path { get; init; }
    [ToolParam("操作模式：auto(默认)/toc(目录)/pages(选页)/chapter(章节)/search(搜索)")]
    public string? Action { get; init; }
    [ToolParam("页码范围，如 1-5,10")]
    public string? Pages { get; init; }
    [ToolParam("章节标题")]
    public string? Chapter { get; init; }
    [ToolParam("搜索关键词")]
    public string? Query { get; init; }
    [ToolParam("Excel sheet 名或索引")]
    public string? Sheet { get; init; }
}
