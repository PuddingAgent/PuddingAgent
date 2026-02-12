using PuddingMemoryEngine.Infrastructure.Text;

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
}
