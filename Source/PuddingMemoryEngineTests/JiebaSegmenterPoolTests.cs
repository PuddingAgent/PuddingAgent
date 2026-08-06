using PuddingFullTextIndex.Infrastructure.Text;

namespace PuddingMemoryEngineTests;

/// <summary>
/// 验证 Jieba 分词资源在测试/运行环境中可正常加载。
/// 防止新增测试项目或拆分项目后 Resources/ 丢失。
/// </summary>
[TestClass]
public sealed class JiebaSegmenterPoolTests
{
    /// <summary>
    /// 构建输出验证：确认 Resources/dict.txt 存在于测试运行目录。
    /// 依赖方案 A 的 MSBuild 资源传递。
    /// </summary>
    [TestMethod]
    public void Resources_DictTxt_ShouldExistInOutputDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        var dictPath = Path.Combine(baseDir, "Resources", "dict.txt");

        Assert.IsTrue(File.Exists(dictPath),
            $"dict.txt 未在输出目录找到。预期路径: {dictPath}。" +
            $"AppContext.BaseDirectory: {baseDir}");
    }

    /// <summary>
    /// 初始化验证：确认 JiebaSegmenterPool 能正常创建分词器而不抛出资源异常。
    /// 依赖方案 B 的多策略资源解析。
    /// </summary>
    [TestMethod]
    public void JiebaSegmenterPool_Initialize_ShouldNotThrow()
    {
        try
        {
            var segmenter = JiebaSegmenterPool.Instance;
            Assert.IsNotNull(segmenter);

            // 验证分词功能正常
            var words = segmenter.Cut("这是一个测试句子");
            Assert.IsTrue(words.Any(), "分词结果不应为空");
        }
        catch (InvalidOperationException ex)
        {
            Assert.Fail($"JiebaSegmenterPool 初始化失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 验证"无 dict.txt 的 Resources 目录不被策略1-3 命中"。
    /// 策略1-3 统一委托 TryResolveResourceDir 判定（要求 Resources/dict.txt
    /// 存在），因此本测试用临时目录直接验证该判定：仅创建空的 Resources
    /// 目录应返回 null（不命中），补齐 dict.txt 后应命中。
    /// </summary>
    [TestMethod]
    public void TryResolveResourceDir_MissingDictTxt_ShouldNotMatchDirectStrategies()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "PuddingJiebaRes_" + Guid.NewGuid().ToString("N"));
        var resourcesDir = Path.Combine(tempRoot, "Resources");
        try
        {
            Directory.CreateDirectory(resourcesDir);

            // 仅有 Resources 目录、缺少 dict.txt：策略1-3 的统一判定不命中。
            Assert.IsNull(JiebaSegmenterPool.TryResolveResourceDir(tempRoot),
                "仅存在 Resources 目录（无 dict.txt）不应被策略1-3 命中");

            // 补齐 dict.txt 后命中。
            File.WriteAllText(Path.Combine(resourcesDir, "dict.txt"), "dummy");
            Assert.AreEqual(resourcesDir, JiebaSegmenterPool.TryResolveResourceDir(tempRoot),
                "补齐 dict.txt 后 Resources 目录应被命中");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// 验证 ResolveResourceDirectory 在当前运行环境中能解析出包含
    /// dict.txt 的 Resources 目录（正向命中，与上面的负向用例互补）。
    /// </summary>
    [TestMethod]
    public void ResolveResourceDirectory_ShouldResolveDirectoryWithDictTxt()
    {
        var resolved = JiebaSegmenterPool.ResolveResourceDirectory();
        Assert.IsNotNull(resolved, "ResolveResourceDirectory 不应返回 null");
        Assert.IsTrue(
            File.Exists(Path.Combine(resolved, "dict.txt")),
            $"解析出的 Resources 目录缺少 dict.txt。解析结果: {resolved}");
    }

    /// <summary>
    /// 验证成功初始化后 LastInitError 被清空（失败不缓存、成功自愈的
    /// 诊断状态约定）。
    /// </summary>
    [TestMethod]
    public void LastInitError_ShouldBeCleared_AfterSuccessfulInitialization()
    {
        var segmenter = JiebaSegmenterPool.Instance;
        Assert.IsNotNull(segmenter);
        Assert.IsNull(JiebaSegmenterPool.LastInitError,
            "初始化成功后 LastInitError 应为 null");
    }
}
