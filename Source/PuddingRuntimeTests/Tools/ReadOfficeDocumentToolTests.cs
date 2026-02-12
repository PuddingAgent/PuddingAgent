using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;
using System.Text;
using System.Text.Json;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class ReadOfficeDocumentToolTests
{
    // ── PDF 测试 ──

    [TestMethod]
    public async Task ReadPdf_Basic_Returns_Content()
    {
        var pdfPath = CreateMinimalPdf("Hello PDF World!\nThis is page one.");
        try
        {
            var tool = new ReadOfficeDocumentTool();
            var result = await ExecuteAsync(tool, pdfPath, action: "auto");

            Assert.IsTrue(result.Success, FailDetail(result));
            StringAssert.Contains(result.Output, "Hello PDF World!");
        }
        finally { File.Delete(pdfPath); }
    }

    [TestMethod]
    public async Task ReadPdf_Pages_Selects_Range()
    {
        var pdfPath = CreateMultiPagePdf(new[] { "PageOne", "PageTwo", "PageThree" });
        try
        {
            var tool = new ReadOfficeDocumentTool();
            var result = await ExecuteAsync(tool, pdfPath, action: "pages", pages: "1-2");

            Assert.IsTrue(result.Success, FailDetail(result));
            StringAssert.Contains(result.Output, "PageOne");
            StringAssert.Contains(result.Output, "PageTwo");
            Assert.IsFalse(result.Output.Contains("PageThree"));
        }
        finally { File.Delete(pdfPath); }
    }

    [TestMethod]
    public async Task ReadPdf_NonexistentFile_Fails()
    {
        var tool = new ReadOfficeDocumentTool();
        var result = await ExecuteAsync(tool, @"C:\nonexistent\file.pdf");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Output, "不存在");
    }

    [TestMethod]
    public async Task ReadPdf_PathTraversal_Blocked()
    {
        var tool = new ReadOfficeDocumentTool();
        var result = await ExecuteAsync(tool, @"..\..\secret.pdf");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Output, "不允许");
    }

    [TestMethod]
    public async Task ReadPdf_Toc_Shows_Structure()
    {
        var pdfPath = CreateMinimalPdf("Just plain text without headers.");
        try
        {
            var tool = new ReadOfficeDocumentTool();
            var result = await ExecuteAsync(tool, pdfPath, action: "toc");

            Assert.IsTrue(result.Success, FailDetail(result));
            StringAssert.Contains(result.Output, "目录");
        }
        finally { File.Delete(pdfPath); }
    }

    [TestMethod]
    public async Task ReadPdf_Large_Document_Truncates()
    {
        // 创建 25 页 PDF (>20 页阈值)，验证自动截断提示
        var pages = Enumerable.Range(1, 25).Select(i => $"Page {i} content").ToArray();
        var pdfPath = CreateMultiPagePdf(pages);
        try
        {
            var tool = new ReadOfficeDocumentTool();
            var result = await ExecuteAsync(tool, pdfPath, action: "auto");

            Assert.IsTrue(result.Success, FailDetail(result));
            StringAssert.Contains(result.Output, "截断");
            StringAssert.Contains(result.Output, "pages");
        }
        finally { File.Delete(pdfPath); }
    }

    // ── TXT 测试 ──

    [TestMethod]
    public async Task ReadTxt_Auto_Returns_Content()
    {
        var txtPath = Path.Combine(Path.GetTempPath(), $"pudding-rt-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(txtPath, "# Test Title\n\nHello from txt file.\nLine three.");
            var tool = new ReadOfficeDocumentTool();
            var result = await ExecuteAsync(tool, txtPath, action: "auto");

            Assert.IsTrue(result.Success, FailDetail(result));
            StringAssert.Contains(result.Output, "Hello from txt file.");
        }
        finally { File.Delete(txtPath); }
    }

    [TestMethod]
    public async Task ReadTxt_Search_Finds_Match()
    {
        var txtPath = Path.Combine(Path.GetTempPath(), $"pudding-rt-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(txtPath, "alpha\nNeedleTarget\nomega\n");
            var tool = new ReadOfficeDocumentTool();
            var result = await ExecuteAsync(tool, txtPath, action: "search", query: "NeedleTarget");

            Assert.IsTrue(result.Success, FailDetail(result));
            StringAssert.Contains(result.Output, "NeedleTarget");
        }
        finally { File.Delete(txtPath); }
    }

    [TestMethod]
    public async Task ReadMd_Toc_Extracts_Headings()
    {
        var mdPath = Path.Combine(Path.GetTempPath(), $"pudding-rt-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(mdPath, "# Title\n\n## Section A\nContent\n### Sub A.1\nMore\n## Section B\nDone.\n");
            var tool = new ReadOfficeDocumentTool();
            var result = await ExecuteAsync(tool, mdPath, action: "toc");

            Assert.IsTrue(result.Success, FailDetail(result));
            StringAssert.Contains(result.Output, "Section A");
            StringAssert.Contains(result.Output, "Section B");
            StringAssert.Contains(result.Output, "Sub A.1");
        }
        finally { File.Delete(mdPath); }
    }

    // ── 参数缺失 ──

    [TestMethod]
    public async Task ExecuteAsync_MissingPath_Fails()
    {
        var tool = new ReadOfficeDocumentTool();
        var result = await ExecuteAsync(tool, "", action: "auto");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Output, "path");
    }

    [TestMethod]
    public async Task ExecuteAsync_UnsupportedExtension_Fails()
    {
        var tmpPath = Path.Combine(Path.GetTempPath(), $"pudding-rt-{Guid.NewGuid():N}.xyz");
        try
        {
            await File.WriteAllTextAsync(tmpPath, "data");
            var tool = new ReadOfficeDocumentTool();
            var result = await ExecuteAsync(tool, tmpPath, action: "auto");

                    Assert.IsTrue(result.Success, FailDetail(result));
        StringAssert.Contains(result.Output, "不支持");
        }
        finally { File.Delete(tmpPath); }
    }

    // ══════════════════════════════════════════════════════
    // 辅助方法
    // ══════════════════════════════════════════════════════

    private static string FailDetail(ToolExecutionResult r)
        => $"Output={r.Output}, Error={r.Error}";

    private static Task<ToolExecutionResult> ExecuteAsync(
        ReadOfficeDocumentTool tool,
        string path,
        string? action = null,
        string? pages = null,
        string? chapter = null,
        string? query = null,
        string? sheet = null)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["path"] = path,
        };
        if (action != null) args["action"] = action;
        if (pages != null) args["pages"] = pages;
        if (chapter != null) args["chapter"] = chapter;
        if (query != null) args["query"] = query;
        if (sheet != null) args["sheet"] = sheet;

        return tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = JsonSerializer.Serialize(args),
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent",
                WorkspaceId = "workspace",
                SessionId = "session",
            },
        });
    }

    private static string CreateMinimalPdf(string text) => CreateMultiPagePdf(new[] { text });

    private static string CreateMultiPagePdf(string[] pageTexts)
    {
        var pdf = BuildPdf(pageTexts);
        var path = Path.Combine(Path.GetTempPath(), $"pudding-rt-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    private static byte[] BuildPdf(string[] pageTexts)
    {
        using var ms = new MemoryStream();
        WriteMinimalPdf(ms, pageTexts);
        return ms.ToArray();
    }

    private static void WriteMinimalPdf(Stream stream, string[] pageTexts)
    {
        var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };
        var offsets = new Dictionary<int, long>();

        writer.WriteLine("%PDF-1.4");
        writer.WriteLine("%¿¿¿¿");

        offsets[1] = stream.Position; writer.Write("1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n");

        offsets[2] = stream.Position;
        writer.Write("2 0 obj<</Type/Pages/Kids[");
        for (int i = 0; i < pageTexts.Length; i++)
            writer.Write($"{3 + i * 2} 0 R ");
        writer.Write($"]/Count {pageTexts.Length}>>endobj\n");

        for (int i = 0; i < pageTexts.Length; i++)
        {
            var pageId = 3 + i * 2;
            var contentId = 4 + i * 2;

            offsets[contentId] = stream.Position;
            var contentBytes = Encoding.ASCII.GetBytes(
                $"BT /F1 24 Tf 100 700 Td ({EscapePdfString(pageTexts[i])}) Tj ET");
            writer.Write($"{contentId} 0 obj<</Length {contentBytes.Length}>>stream\n");
            writer.Flush();
            stream.Write(contentBytes);
            writer.Write("\nendstream\nendobj\n");

            offsets[pageId] = stream.Position;
            writer.Write($"{pageId} 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents {contentId} 0 R/Resources<</Font<</F1 {999} 0 R>>>>>>endobj\n");
        }

        offsets[999] = stream.Position;
        writer.Write("999 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\n");

        var xrefPos = stream.Position;
        writer.Write("xref\n");
        writer.Write($"0 {1000}\n");
        writer.Write("0000000000 65535 f \n");
        for (int i = 1; i < 1000; i++)
        {
            if (offsets.TryGetValue(i, out var pos))
                writer.Write($"{pos:0000000000} 00000 n \n");
            else
                writer.Write("0000000000 65535 f \n");
        }

        writer.Write($"trailer<</Size 1000/Root 1 0 R>>\n");
        writer.Write($"startxref\n{xrefPos}\n%%EOF\n");
        writer.Flush();
    }

    private static string EscapePdfString(string s)
        => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", "").Replace("\n", " ");

}
