using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PuddingCode.Configuration;

namespace PuddingCoreTests.Configuration;

[TestClass]
public sealed class AtomicFileWriterTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pudding-utf8-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task Json_RoundTripsReadableUnicode_WithoutMutatingCallerOptions()
    {
        var path = Path.Combine(_root, "config.json");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var value = new { DisplayName = "默认助手：中文、日本語 🥧", Prompt = "  # 规则\r\n路径 C:\\代码\\文件\n\"引号\" <tag> & \\u4e2d  " };
        JsonSerializer.Serialize(value, options); // 模拟已被读取端冻结的共享选项。

        await AtomicFileWriter.WriteJsonAsync(path, value, options);

        var bytes = await File.ReadAllBytesAsync(path);
        var text = new UTF8Encoding(false, true).GetString(bytes);
        Assert.IsFalse(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        StringAssert.Contains(text, "默认助手：中文、日本語");
        using var json = JsonDocument.Parse(bytes);
        Assert.AreEqual(value.DisplayName, json.RootElement.GetProperty("displayName").GetString());
        Assert.AreEqual(value.Prompt, json.RootElement.GetProperty("prompt").GetString());
        Assert.IsNull(options.Encoder);
    }

    [TestMethod]
    public async Task Json_DefaultOptionsAreReadable_ExplicitEncoderIsRespected()
    {
        var path = Path.Combine(_root, "config.json");
        await AtomicFileWriter.WriteJsonAsync(path, new { Name = "中文" });
        StringAssert.Contains(await File.ReadAllTextAsync(path), "中文");
        await AtomicFileWriter.WriteJsonAsync(path, new { Name = "中文" }, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.Default,
        });
        StringAssert.Contains(await File.ReadAllTextAsync(path), "\\u4E2D");
    }

    [TestMethod]
    public async Task Markdown_PreservesWhitespaceAndLiteralEscapes_AndCanBeCleared()
    {
        var path = Path.Combine(_root, "AGENTS.md");
        const string text = "\r\n  # 协作规则 🥧\r\n保留 \\u4e2d 和 {{变量}}  \n";
        await AtomicFileWriter.WriteAsync(path, text);
        CollectionAssert.AreEqual(new UTF8Encoding(false, true).GetBytes(text), await File.ReadAllBytesAsync(path));
        await AtomicFileWriter.WriteAsync(path, "");
        Assert.AreEqual(0L, new FileInfo(path).Length);
    }

    [TestMethod]
    public async Task InvalidUnicode_DoesNotReplaceExistingFile_OrLeaveTemporaryFiles()
    {
        var path = Path.Combine(_root, "SOUL.md");
        await AtomicFileWriter.WriteAsync(path, "原始内容");
        await Assert.ThrowsAsync<EncoderFallbackException>(() => AtomicFileWriter.WriteAsync(path, "\uD800"));
        Assert.AreEqual("原始内容", await File.ReadAllTextAsync(path));
        Assert.HasCount(1, Directory.GetFiles(_root));
    }
}
