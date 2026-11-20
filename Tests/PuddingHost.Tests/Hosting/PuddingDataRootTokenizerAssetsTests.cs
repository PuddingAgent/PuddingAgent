using PuddingHost.Hosting;
using PuddingTokenization;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// 引导期把数据根下的词表接入运行时。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由是一个真实事故：<c>PUDDING_TOKENIZER_ASSETS</c> 已在用户级登记（注册表有值），
/// 但运行中的进程看不到——Windows 的环境块在进程创建时从父进程继承，而 Core 是 Desktop 的
/// 长寿子进程，只重启 Core 不会重读用户级变量。于是「配置过了」与「没生效」可以同时成立，
/// 而诊断只会说「资产缺失」，看起来像文件被删了。
/// </para>
/// <para>
/// 这组用例钉住四条边界：有资产则接入、无资产不接入、空目录不算有资产、用户显式配置不被覆盖。
/// </para>
/// </remarks>
public sealed class PuddingDataRootTokenizerAssetsTests
{
    [Fact]
    public void Bootstrap_PointsTheProcessVariableAtDataRootAssets()
    {
        WithCleanEnvironment(() =>
        {
            var dataRoot = CreateTempDataRoot();
            try
            {
                var paths = WriteAsset(dataRoot);
                PuddingDataRootBootstrapper.Bootstrap(dataRoot);

                Assert.Equal(
                    TokenizerAssetDirectory.DataRootAssetDirectory(paths.DataRoot),
                    Environment.GetEnvironmentVariable(
                        TokenizerAssetDirectory.EnvironmentVariableName,
                        EnvironmentVariableTarget.Process));
            }
            finally
            {
                DeleteTempDataRoot(dataRoot);
            }
        });
    }

    [Fact]
    public void Bootstrap_LeavesTheVariableUnsetWhenDataRootHasNoAssets()
    {
        WithCleanEnvironment(() =>
        {
            var dataRoot = CreateTempDataRoot();
            try
            {
                PuddingDataRootBootstrapper.Bootstrap(dataRoot);

                Assert.True(string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable(
                        TokenizerAssetDirectory.EnvironmentVariableName,
                        EnvironmentVariableTarget.Process)));
            }
            finally
            {
                DeleteTempDataRoot(dataRoot);
            }
        });
    }

    /// <summary>
    /// 回归：目录存在但零资产时不得接入。
    /// </summary>
    /// <remarks>
    /// 若这里改用 <c>Directory.Exists</c>，空目录会被当成「已配置」——正好是诊断报了整整一个月的
    /// 那个误判。判据必须与 <see cref="TokenizerAssetDirectory"/> 共用同一份定义。
    /// </remarks>
    [Fact]
    public void Bootstrap_DoesNotTreatAnEmptyDirectoryAsConfigured()
    {
        WithCleanEnvironment(() =>
        {
            var dataRoot = CreateTempDataRoot();
            try
            {
                var tokenizerRoot = Path.Combine(dataRoot, "models", "tokenizers");
                Directory.CreateDirectory(tokenizerRoot);

                PuddingDataRootBootstrapper.Bootstrap(dataRoot);

                Assert.True(string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable(
                        TokenizerAssetDirectory.EnvironmentVariableName,
                        EnvironmentVariableTarget.Process)));
            }
            finally
            {
                DeleteTempDataRoot(dataRoot);
            }
        });
    }

    [Fact]
    public void Bootstrap_DoesNotOverrideAnExplicitSetting()
    {
        var explicitDirectory = Path.Combine(Path.GetTempPath(), "explicit-tokenizers-" + Guid.NewGuid().ToString("N"));
        WithCleanEnvironment(() =>
        {
            Environment.SetEnvironmentVariable(
                TokenizerAssetDirectory.EnvironmentVariableName,
                explicitDirectory,
                EnvironmentVariableTarget.Process);

            var dataRoot = CreateTempDataRoot();
            try
            {
                WriteAsset(dataRoot);
                PuddingDataRootBootstrapper.Bootstrap(dataRoot);

                Assert.Equal(
                    explicitDirectory,
                    Environment.GetEnvironmentVariable(
                        TokenizerAssetDirectory.EnvironmentVariableName,
                        EnvironmentVariableTarget.Process));
            }
            finally
            {
                DeleteTempDataRoot(dataRoot);
            }
        }, restoreTo: explicitDirectory);
    }

    /// <summary>
    /// 进程级环境变量是全局状态，用例之间会互相污染：进入前清空、退出后还原原值。
    /// </summary>
    private static void WithCleanEnvironment(Action body, string? restoreTo = null)
    {
        var original = Environment.GetEnvironmentVariable(
            TokenizerAssetDirectory.EnvironmentVariableName,
            EnvironmentVariableTarget.Process);

        try
        {
            Environment.SetEnvironmentVariable(
                TokenizerAssetDirectory.EnvironmentVariableName,
                null,
                EnvironmentVariableTarget.Process);

            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                TokenizerAssetDirectory.EnvironmentVariableName,
                restoreTo ?? original,
                EnvironmentVariableTarget.Process);
        }
    }

    private static PuddingCode.Configuration.PuddingDataPaths WriteAsset(string dataRoot)
    {
        var paths = PuddingCode.Configuration.PuddingDataPaths.FromRoot(dataRoot);
        var tokenizerRoot = TokenizerAssetDirectory.DataRootAssetDirectory(paths.DataRoot);
        Directory.CreateDirectory(tokenizerRoot);
        File.WriteAllText(
            Path.Combine(tokenizerRoot, ModelTokenizerCatalog.DeepSeekV41Flash + ".tokenizer.json"),
            "{}");
        return paths;
    }

    private static string CreateTempDataRoot()
        => Path.Combine(Path.GetTempPath(), "PuddingAgent", $"tokenizer-assets-{Guid.NewGuid():N}");

    private static void DeleteTempDataRoot(string dataRoot)
    {
        try
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
