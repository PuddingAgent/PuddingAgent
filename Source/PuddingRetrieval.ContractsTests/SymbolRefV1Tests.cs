using System.Text;
using PuddingRetrieval.Contracts;

namespace PuddingRetrieval.ContractsTests;

/// <summary>
/// <see cref="SymbolRefV1"/> 契约测试（symbol_ref 提案 §4 / §5 / §6）。
/// <para>
/// 守的是三件事：①token 往返不丢字段；②**伪造的 ref 与非法参数走同一条拒绝路径**
/// （ref 不签名，因此解析结果必须当不可信输入）；③可重定位性按 id 形态分流，
/// 位置相关的 id 不允许被当成能重定位的键。
/// </para>
/// </summary>
[TestClass]
public sealed class SymbolRefV1Tests
{
    private const string ProjectId = "proj-1";
    private const string FilePath = "Source/A.cs";
    private const string SymbolId = "M:Ns.Type.Run";
    private const string ContentHash = "9f3c8a1b";
    private const string ProviderGeneration = "roslyn-v1";

    private static SymbolRefV1 CreateValid(int startLine = 10, int endLine = 20) =>
        SymbolRefV1.Create(ProjectId, FilePath, SymbolId, startLine, endLine, ContentHash, ProviderGeneration);

    /// <summary>绕开 Create 直接拼 token —— 模拟"调用方伪造 ref"。</summary>
    private static string Forge(string json) =>
        SymbolRefV1.TokenPrefix +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [TestMethod]
    public void Token_RoundTrip_PreservesAllFields()
    {
        var token = CreateValid(startLine: 101, endLine: 140).ToToken();

        Assert.IsTrue(SymbolRefV1.TryParse(token, out var parsed, out var error), error);

        Assert.AreEqual(ProjectId, parsed!.ProjectId);
        Assert.AreEqual("source/a.cs", parsed.FilePath);
        Assert.AreEqual(SymbolId, parsed.SymbolId);
        Assert.AreEqual(101, parsed.StartLine);
        Assert.AreEqual(140, parsed.EndLine);
        Assert.AreEqual(ContentHash, parsed.ContentHash);
        Assert.AreEqual(ProviderGeneration, parsed.ProviderGeneration);
    }

    /// <summary>归一化必须在构造时完成，不能等到使用方各自处理 —— 否则同一符号会有多种写法。</summary>
    [TestMethod]
    public void Create_NormalizesPathAndSymbolId()
    {
        var reference = SymbolRefV1.Create(
            ProjectId, @"Source\PuddingRuntime\\Services\Tools\CodeOutlineService.cs", "Ns.Type  .  Run", 5, 9, ContentHash, ProviderGeneration);

        Assert.AreEqual("source/puddingruntime/services/tools/codeoutlineservice.cs", reference.FilePath);
        Assert.AreEqual("Ns.Type . Run", reference.SymbolId);
    }

    [TestMethod]
    public void Create_RejectsPathTraversal()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            SymbolRefV1.Create(ProjectId, "../secret/A.cs", SymbolId, 1, 2, ContentHash, ProviderGeneration));

        StringAssert.Contains(error.Message, "'..'");
    }

    [TestMethod]
    public void Create_RejectsAbsoluteAndRootedPaths()
    {
        var drive = Assert.Throws<ArgumentException>(() =>
            SymbolRefV1.Create(ProjectId, "C:/src/A.cs", SymbolId, 1, 2, ContentHash, ProviderGeneration));
        StringAssert.Contains(drive.Message, "':'");

        var rooted = Assert.Throws<ArgumentException>(() =>
            SymbolRefV1.Create(ProjectId, "/etc/passwd", SymbolId, 1, 2, ContentHash, ProviderGeneration));
        StringAssert.Contains(rooted.Message, "根路径");
    }

    /// <summary>没有源版本就不许签发 —— 否则会造出一类永远无法判陈旧的 ref。</summary>
    [TestMethod]
    public void Create_RejectsMissingContentHash()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            SymbolRefV1.Create(ProjectId, FilePath, SymbolId, 1, 2, "   ", ProviderGeneration));

        StringAssert.Contains(error.Message, "content_hash");
    }

    [TestMethod]
    public void Create_RejectsMissingSymbolIdAndInvertedLines()
    {
        Assert.Throws<ArgumentException>(() =>
            SymbolRefV1.Create(ProjectId, FilePath, " ", 1, 2, ContentHash, ProviderGeneration));

        var inverted = Assert.Throws<ArgumentException>(() =>
            SymbolRefV1.Create(ProjectId, FilePath, SymbolId, 20, 10, ContentHash, ProviderGeneration));
        StringAssert.Contains(inverted.Message, "end_line");
    }

    /// <summary>旧行号/文件名不能冒充 ref（§8.3 明禁按文件名猜符号）。</summary>
    [TestMethod]
    public void Parse_RejectsNonRefText()
    {
        Assert.IsFalse(SymbolRefV1.TryParse("Source/A.cs:12", out _, out var error));
        StringAssert.Contains(error, SymbolRefV1.TokenPrefix);
    }

    /// <summary>
    /// 核心安全用例：合法 base64url + 合法 JSON + 合法 schema，但**路径逃逸**。
    /// 证明校验发生在解析之后，而不是"前缀对了就信"。
    /// </summary>
    [TestMethod]
    public void Parse_RejectsForgedPayloadWithTraversal()
    {
        var forged = Forge(
            "{\"s\":\"1\",\"p\":\"proj-1\",\"f\":\"../secret/a.cs\",\"i\":\"M:Ns.Type.Run\",\"a\":1,\"b\":2,\"h\":\"abc\",\"g\":\"roslyn-v1\"}");

        Assert.IsFalse(SymbolRefV1.TryParse(forged, out var parsed, out var error));
        Assert.IsNull(parsed);
        StringAssert.Contains(error, "'..'");
    }

    [TestMethod]
    public void Parse_RejectsUnsupportedSchemaAndMissingFields()
    {
        var wrongSchema = Forge(
            "{\"s\":\"2\",\"p\":\"proj-1\",\"f\":\"source/a.cs\",\"i\":\"M:Ns.Type.Run\",\"a\":1,\"b\":2,\"h\":\"abc\",\"g\":\"gen\"}");
        Assert.IsFalse(SymbolRefV1.TryParse(wrongSchema, out _, out var schemaError));
        StringAssert.Contains(schemaError, "schema");

        var missingHash = Forge("{\"s\":\"1\",\"p\":\"proj-1\",\"f\":\"source/a.cs\",\"i\":\"M:Ns.Type.Run\",\"a\":1,\"b\":2,\"g\":\"gen\"}");
        Assert.IsFalse(SymbolRefV1.TryParse(missingHash, out _, out var missingError));
        StringAssert.Contains(missingError, "'h'");
    }

    [TestMethod]
    public void Parse_RejectsOversizedAndMalformedTokens()
    {
        Assert.IsFalse(SymbolRefV1.TryParse(new string('a', SymbolRefV1.MaxTokenLength + 1), out _, out var oversize));
        StringAssert.Contains(oversize, "超长");

        Assert.IsFalse(SymbolRefV1.TryParse(SymbolRefV1.TokenPrefix + "!!!", out _, out var base64Error));
        StringAssert.Contains(base64Error, "base64url");

        Assert.IsFalse(SymbolRefV1.TryParse(SymbolRefV1.TokenPrefix + Forge("not json")[SymbolRefV1.TokenPrefix.Length..], out _, out var jsonError));
        StringAssert.Contains(jsonError, "JSON");
    }

    /// <summary>签发侧同样有界：字段过长时宁可抛错，也不签发一个下游用不上的 ref。</summary>
    [TestMethod]
    public void ToToken_RejectsWhenTokenExceedsLimit()
    {
        var longPath = "source/" + new string('a', 400) + ".cs";

        var error = Assert.Throws<ArgumentException>(() =>
            SymbolRefV1.Create(ProjectId, longPath, SymbolId, 1, 2, ContentHash, ProviderGeneration).ToToken());

        StringAssert.Contains(error.Message, SymbolRefV1.MaxTokenLength.ToString());
    }

    /// <summary>
    /// 提案 §6：可重定位性按 id 形态分流。位置相关的回退形态**不得**被当成可重定位键，
    /// 否则会在文件前部改动后"重新定位"到错误位置且看起来成功。
    /// </summary>
    [TestMethod]
    public void ClassifySymbolId_SeparatesDeclarativeFromPositionDependent()
    {
        Assert.AreEqual(SymbolIdStability.Declarative, SymbolRefV1.ClassifySymbolId("M:Ns.Type.Run(System.String)"));
        Assert.AreEqual(SymbolIdStability.Declarative, SymbolRefV1.ClassifySymbolId("T:Ns.Type"));
        Assert.AreEqual(SymbolIdStability.PositionDependent, SymbolRefV1.ClassifySymbolId("Method:(T:Ns.Type)Run@1234"));
        Assert.AreEqual(SymbolIdStability.Unknown, SymbolRefV1.ClassifySymbolId("py:module.func"));
        Assert.AreEqual(SymbolIdStability.Unknown, SymbolRefV1.ClassifySymbolId(null));
    }
}
