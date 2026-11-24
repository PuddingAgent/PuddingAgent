using System.Text;
using System.Text.Json;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 守卫 file_write(file) / file_patch 的编码契约：当目标文件不是合法 UTF-8 时必须**拒绝**。
///
/// 动机（现场实测的静默数据销毁）：这两个工具都是「整文件读入 → 改 → 整文件重写」，
/// 旧实现用 <c>Encoding.UTF8</c> 宽松解码，于是文件里的非法字节先被替换成 U+FFFD、再落盘，
/// 工具回报成功。实测把 <c>41 42 FF 43 44</c> 追加一个 "Z" 后变成
/// <c>41 42 EF BF BD 43 44 5A</c> —— 单个 0xFF 被永久替换成三字节替换字符，且无任何提示。
///
/// 因此断言分两半：拒绝（Success=false）**且**原文件字节未被改动。
/// 另配正向对照：「合法 UTF-8 + 同样操作」必须照常成功 —— 否则一个「拒绝一切写入」的
/// 实现同样能通过负向断言，测试就是假门禁。
/// </summary>
[TestClass]
public sealed class FileToolUtf8StrictnessTests
{
    private string _tempDir = null!;
    private string _originalRepoRoot = null!;

    /// <summary>合法 UTF-8 前缀 + 单个非法字节 0xFF + 合法后缀。</summary>
    private static readonly byte[] InvalidUtf8Bytes = [0x41, 0x42, 0xFF, 0x43, 0x44];

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pudding-utf8-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalRepoRoot = Environment.GetEnvironmentVariable("PUDDING_REPOSITORY_ROOT") ?? string.Empty;
        Environment.SetEnvironmentVariable("PUDDING_REPOSITORY_ROOT", _tempDir);
        HostFileToolPaths.InvalidateWorkspaceRootCache();
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("PUDDING_REPOSITORY_ROOT", _originalRepoRoot);
        HostFileToolPaths.InvalidateWorkspaceRootCache();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ── file_write append ──

    [TestMethod]
    public async Task FileWrite_Append_RefusesNonUtf8File_AndLeavesBytesUntouched()
    {
        var path = Path.Combine(_tempDir, "legacy.txt");
        File.WriteAllBytes(path, InvalidUtf8Bytes);

        var result = await ExecuteAsync(new FileWriteTool(), new Dictionary<string, object?>
        {
            ["path"] = "legacy.txt",
            ["content"] = "Z",
            ["append"] = true,
        });

        Assert.IsFalse(result.Success, "appending to a non-UTF-8 file must fail rather than mangle it");
        StringAssert.Contains(result.Error, "UTF-8");
        CollectionAssert.AreEqual(InvalidUtf8Bytes, File.ReadAllBytes(path),
            "a refused append must not touch a single byte of the original file");
    }

    [TestMethod]
    public async Task FileWrite_Append_StillWorksOnValidUtf8()
    {
        var path = Path.Combine(_tempDir, "plain.txt");
        File.WriteAllText(path, "AB", new UTF8Encoding(false));

        var result = await ExecuteAsync(new FileWriteTool(), new Dictionary<string, object?>
        {
            ["path"] = "plain.txt",
            ["content"] = "Z",
            ["append"] = true,
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("ABZ", File.ReadAllText(path, Encoding.UTF8));
        // 顺带守住「不写 BOM」：追加不得改变文件头。
        Assert.AreNotEqual(0xEF, File.ReadAllBytes(path)[0], "append must not introduce a UTF-8 BOM");
    }

    // ── file_patch ──

    [TestMethod]
    public async Task FilePatch_RefusesNonUtf8File_AndLeavesBytesUntouched()
    {
        var path = Path.Combine(_tempDir, "legacy.txt");
        File.WriteAllBytes(path, InvalidUtf8Bytes);

        var result = await ExecuteAsync(new FilePatchTool(), new Dictionary<string, object?>
        {
            ["path"] = "legacy.txt",
            ["operations"] = new object[]
            {
                // 宽松解码下 "AB" 是能匹配到的 —— 正因如此旧实现才会「成功」地毁掉 0xFF。
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "AB", ["new_text"] = "XY" },
            },
        });

        Assert.IsFalse(result.Success, "patching a non-UTF-8 file must fail rather than mangle it");
        StringAssert.Contains(result.Error, "UTF-8");
        CollectionAssert.AreEqual(InvalidUtf8Bytes, File.ReadAllBytes(path),
            "a refused patch must not touch a single byte of the original file");
    }

    [TestMethod]
    public async Task FilePatch_StillWorksOnValidUtf8()
    {
        var path = Path.Combine(_tempDir, "plain.txt");
        File.WriteAllText(path, "AB", new UTF8Encoding(false));

        var result = await ExecuteAsync(new FilePatchTool(), new Dictionary<string, object?>
        {
            ["path"] = "plain.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "AB", ["new_text"] = "XY" },
            },
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("XY", File.ReadAllText(path, Encoding.UTF8));
    }

    // ── file_patch 统一 diff 路径（patch_text，与 apply_patch 共用）──

    /// <summary>
    /// 统一 diff 路径的坏件：非法字节放在第 2 行，第 1 行保持合法且可匹配的 "alpha"，
    /// 与 FilePatchToolTests 的 patch_text 用例同一形状。
    ///
    /// 变异取红的实测结论（不要改写成更漂亮的说法）：把严格解码换回 Encoding.UTF8 后本用例确实转红，
    /// 但红在「Hunk ... did not match」而不是「UTF-8」—— 说明宽松解码本身<b>不会</b>拒绝这个文件，
    /// 拦住它的是编码守卫而非 diff 匹配。因此本用例证明的是「解码边界确实在起作用」，
    /// 不证明「该固件在宽松解码下一定会被改写落盘」；后者需要一个 hunk 在替换字符文本上仍能匹配的固件，
    /// 迄今没构造出来（两次尝试均以 hunk 不匹配告终）。
    /// </summary>
    // "alpha\n" + 0xFF + "tail\n"
    private static readonly byte[] InvalidUtf8BytesMultiLine =
        [0x61, 0x6C, 0x70, 0x68, 0x61, 0x0A, 0xFF, 0x74, 0x61, 0x69, 0x6C, 0x0A];

    [TestMethod]
    public async Task FilePatch_PatchText_RefusesNonUtf8File_AndLeavesBytesUntouched()
    {
        var path = Path.Combine(_tempDir, "legacy.txt");
        File.WriteAllBytes(path, InvalidUtf8BytesMultiLine);

        var result = await ExecuteAsync(new FilePatchTool(), new Dictionary<string, object?>
        {
            ["path"] = "legacy.txt",
            ["patch_text"] = "--- a/legacy.txt\n+++ b/legacy.txt\n@@ -1 +1 @@\n-alpha\n+beta\n",
        });

        Assert.IsFalse(result.Success,
            "applying a unified diff to a non-UTF-8 file must fail rather than mangle it");
        StringAssert.Contains(result.Error, "UTF-8");
        CollectionAssert.AreEqual(InvalidUtf8BytesMultiLine, File.ReadAllBytes(path),
            "a refused unified diff must not touch a single byte of the original file");
    }

    [TestMethod]
    public async Task FilePatch_PatchText_StillWorksOnValidUtf8()
    {
        var path = Path.Combine(_tempDir, "plain.txt");
        File.WriteAllText(path, "alpha\n", new UTF8Encoding(false));

        var result = await ExecuteAsync(new FilePatchTool(), new Dictionary<string, object?>
        {
            ["path"] = "plain.txt",
            ["patch_text"] = "--- a/plain.txt\n+++ b/plain.txt\n@@ -1 +1 @@\n-alpha\n+beta\n",
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("beta\n", File.ReadAllText(path, Encoding.UTF8));
    }

    // ── 带 BOM 的合法文件：必须照常打补丁 ──

    /// <summary>
    /// 回归守卫：把严格解码写成 <c>Strict.GetString(File.ReadAllBytes(..))</c> 时**不剥 BOM**，
    /// 首行变成 "\uFEFFalpha"，逐行比对的 hunk 全部失配。
    /// 提交门禁就是用 4 个既有用例（FilePatchToolDryRunDefaultTests 写文件时传了 Encoding.UTF8，会写 BOM）
    /// 把这个回归抓出来的。
    ///
    /// 只留 patch_text 一侧的用例：实测把 BOM 剥除变异掉之后，operations 路径的可替换旧字符串
    /// 仍然能作为子串命中（"alpha" 就在 "\uFEFFalpha" 里），所以那种用例突变后依然绿，是个假门禁。
    /// 逐行比对的统一 diff 路径才能真取红。
    /// </summary>
    [TestMethod]
    public async Task FilePatch_PatchText_StillWorksOnUtf8WithBom()
    {
        var path = Path.Combine(_tempDir, "bom2.txt");
        File.WriteAllText(path, "alpha\n", Encoding.UTF8);
        Assert.AreEqual(0xEF, File.ReadAllBytes(path)[0], "fixture must really start with a UTF-8 BOM");

        var result = await ExecuteAsync(new FilePatchTool(), new Dictionary<string, object?>
        {
            ["path"] = "bom2.txt",
            ["patch_text"] = "--- a/bom2.txt\n+++ b/bom2.txt\n@@ -1 +1 @@\n-alpha\n+beta\n",
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("beta\n", File.ReadAllText(path, Encoding.UTF8));
    }

    // ── BOM 往返：读→改→写不得静默改变编码形态 ──

    /// <summary>
    /// 实测缺陷（2026-11-25 运行时验收发现）：<c>StrictUtf8Text.ReadAllText</c> 剥掉 BOM，
    /// 而写回用 <c>new UTF8Encoding(false)</c> —— 于是带 BOM 的文件被 patch 一次就<b>永久丢掉 BOM</b>
    /// （fixture <c>ef bb bf 61 6c 70 68 61 0a 6f 6d 65 67 61 0a</c>，15 字节 → 12 字节）。
    /// 这与宽松解码同属「读→改→写」静默改变文件的缺陷，只是丢的是开头三字节。
    ///
    /// 上面的 <c>FilePatch_PatchText_StillWorksOnUtf8WithBom</c> 只断言「能 patch 成功」，
    /// 断言不到 BOM 是否被保留 —— 所以它<b>拦不住</b>这个缺陷。两个用例是互补的，不是重复的。
    /// </summary>
    [TestMethod]
    public async Task FilePatch_Operations_PreservesBom()
    {
        var path = Path.Combine(_tempDir, "bom-ops.txt");
        File.WriteAllText(path, "omega\n", Encoding.UTF8);
        Assert.IsTrue(StartsWithUtf8Bom(path), "fixture must really start with a UTF-8 BOM");

        var result = await ExecuteAsync(new FilePatchTool(), new Dictionary<string, object?>
        {
            ["path"] = "bom-ops.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "omega", ["new_text"] = "OMEGA" },
            },
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("OMEGA\n", File.ReadAllText(path, Encoding.UTF8));
        Assert.IsTrue(StartsWithUtf8Bom(path),
            "patching a BOM file must not silently drop its BOM (read→edit→write must round-trip the encoding)");
    }

    [TestMethod]
    public async Task FilePatch_PatchText_PreservesBom()
    {
        var path = Path.Combine(_tempDir, "bom-diff.txt");
        File.WriteAllText(path, "alpha\n", Encoding.UTF8);
        Assert.IsTrue(StartsWithUtf8Bom(path), "fixture must really start with a UTF-8 BOM");

        var result = await ExecuteAsync(new FilePatchTool(), new Dictionary<string, object?>
        {
            ["path"] = "bom-diff.txt",
            ["patch_text"] = "--- a/bom-diff.txt\n+++ b/bom-diff.txt\n@@ -1 +1 @@\n-alpha\n+beta\n",
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("beta\n", File.ReadAllText(path, Encoding.UTF8));
        Assert.IsTrue(StartsWithUtf8Bom(path),
            "the unified-diff path writes through a temp file, whose BOM must inherit the target's");
    }

    /// <summary>
    /// 反向对照：无 BOM 的文件 patch 后也不得<b>新增</b> BOM。
    /// 否则「一律写 BOM」的实现能通过上面两条用例 —— 那同样是静默改变编码。
    /// </summary>
    [TestMethod]
    public async Task FilePatch_DoesNotIntroduceBom_OnPlainFile()
    {
        var path = Path.Combine(_tempDir, "plain-nobom.txt");
        File.WriteAllText(path, "omega\n", new UTF8Encoding(false));
        Assert.IsFalse(StartsWithUtf8Bom(path), "fixture must start without a BOM");

        var result = await ExecuteAsync(new FilePatchTool(), new Dictionary<string, object?>
        {
            ["path"] = "plain-nobom.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "omega", ["new_text"] = "OMEGA" },
            },
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(StartsWithUtf8Bom(path), "patching must not introduce a UTF-8 BOM that was not there");
    }

    private static bool StartsWithUtf8Bom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    // ── Helper ──

    private static Task<ToolExecutionResult> ExecuteAsync<TArgs>(
        PuddingToolBase<TArgs> tool, IReadOnlyDictionary<string, object?> parameters)
        where TArgs : class
    {
        return tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = JsonSerializer.Serialize(parameters),
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent",
                WorkspaceId = "workspace",
                SessionId = "session",
            },
        });
    }
}
