using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// <see cref="ImportantMemoryService"/> 的编码安全（2026-11-25）。
/// <para>
/// 背景：这是 <c>goal_update</c> 写坏 goal.md、<c>file_write</c>/<c>file_patch</c>
/// 静默替换字节之后的同一类缺陷的第三处。它的独特危险在于
/// <b>「损失性解码 + 整文件回写」</b>：文件一旦含非法 UTF-8 字节，
/// 读出来的 lenient 文本里那个字节已经变成 U+FFFD，若此时因为超长而回写，
/// 非法字节就被永久固化成 U+FFFD —— 原数据不可逆丢失。
/// </para>
/// <para>
/// 所以这里锁住两条互相独立的性质：
/// ①非法字节时<b>绝不回写</b>（负向）；②合法但超长时截断回写<b>照旧</b>（正向对照）。
/// 只做 ① 的话，一个「永远不写」的实现也能过测。
/// </para>
/// </summary>
[TestClass]
public sealed class ImportantMemoryEncodingTests
{
    private const string AgentId = "agent-encoding-probe";

    private string _root = string.Empty;
    private ImportantMemoryService _service = null!;
    private PuddingDataPaths _paths = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-important-mem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = PuddingDataPaths.FromRoot(_root);
        _service = new ImportantMemoryService(_paths, NullLogger<ImportantMemoryService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响结论
        }
    }

    private string MemoryFile => _paths.AgentInstanceImportantMemoryFile(AgentId);

    private void SeedBytes(params byte[] bytes)
    {
        var dir = Path.GetDirectoryName(MemoryFile)!;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(MemoryFile, bytes);
    }

    /// <summary>合法但超长：1100 个 ASCII + 末尾一个非法字节 0xFF。</summary>
    private static byte[] OverlongWithInvalidByte()
    {
        var bytes = new List<byte>(capacity: 1101);
        for (var i = 0; i < 1100; i++)
            bytes.Add((byte)'A');
        bytes.Add(0xFF);
        return bytes.ToArray();
    }

    // ═══════════════════════════════════════════════════════
    // 1. 负向：非法 UTF-8 时读路径不得回写
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task ReadAsync_InvalidUtf8_ReturnsMarkerAndLeavesBytesUntouched()
    {
        var seeded = OverlongWithInvalidByte();
        SeedBytes(seeded);

        var content = await _service.ReadAsync(AgentId);

        Assert.IsNotNull(content, "读路径应返回内容而不是 null，否则 Pinned 层静默消失更难排查。");
        StringAssert.StartsWith(content, "⚠️", "非法字节必须对 Agent 显式可见。");
        CollectionAssert.AreEqual(seeded, File.ReadAllBytes(MemoryFile),
            "长度超限也不能回写：损失性解码后回写会把 0xFF 永久固化成 U+FFFD。");
    }

    [TestMethod]
    public void ReadOrNull_InvalidUtf8_LeavesBytesUntouched()
    {
        var seeded = OverlongWithInvalidByte();
        SeedBytes(seeded);

        var content = _service.ReadOrNull(AgentId);

        Assert.IsNotNull(content);
        StringAssert.StartsWith(content, "⚠️");
        CollectionAssert.AreEqual(seeded, File.ReadAllBytes(MemoryFile),
            "同步热路径同样不得回写。");
    }

    // ═══════════════════════════════════════════════════════
    // 2. 正向对照：合法 UTF-8 的截断回写照旧，且无 BOM
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task ReadAsync_ValidButOverlong_StillTruncatesAndRewritesWithoutBom()
    {
        var seeded = Encoding.UTF8.GetBytes(new string('A', 1100));
        SeedBytes(seeded);

        var content = await _service.ReadAsync(AgentId);

        Assert.IsNotNull(content);
        StringAssert.Contains(content, "重要记忆已超过", "超长时必须带出原引导警告。");

        var after = File.ReadAllBytes(MemoryFile);
        Assert.AreNotEqual(0xEF, after[0], "回写必须无 BOM：首字节不能是 0xEF。");

        // 回写后的文件长度**会变长**（截断到 1000 字后追加了中文警告），
        // 所以不能拿长度做断言 —— 要直接锁「内容确实被截到了 1000」这件事。
        var rewritten = Encoding.UTF8.GetString(after);
        var longestRun = 0;
        var current = 0;
        foreach (var ch in rewritten)
        {
            current = ch == 'A' ? current + 1 : 0;
            if (current > longestRun)
                longestRun = current;
        }

        Assert.AreEqual(ImportantMemoryService.MaxChars, longestRun,
            "合法超长文件仍应被截断回写（既有行为）：应恰好保留 MaxChars 个连续字符。");
        Assert.AreNotEqual(seeded.Length, after.Length, "回写后字节长度必然变化，证明真的发生了重写。");
    }

    // ═══════════════════════════════════════════════════════
    // 3. 写路径一律无 BOM
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task WriteAsync_WritesNoBom()
    {
        var result = await _service.WriteAsync(AgentId, "- 一条记忆");

        Assert.IsTrue(result.Success, result.Error);
        var bytes = File.ReadAllBytes(MemoryFile);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("- 一条记忆"), bytes,
            "写出的字节必须与纯 UTF-8 完全一致（多一个 EF BB BF 就不是）。");
    }

    [TestMethod]
    public async Task EnsureInitializedAsync_WritesNoBom()
    {
        var created = await _service.EnsureInitializedAsync(AgentId);

        Assert.IsTrue(created, "文件不存在时应初始化。");
        var bytes = File.ReadAllBytes(MemoryFile);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes(ImportantMemoryService.DefaultContent), bytes,
            "冷启动种子同样不能带 BOM。");

        Assert.IsFalse(await _service.EnsureInitializedAsync(AgentId), "已存在时幂等跳过。");
    }
}
