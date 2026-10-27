using PuddingFullTextIndex.Infrastructure.Maintenance;

namespace PuddingFullTextIndexTests;

/// <summary>
/// S3d 接线切片：<see cref="FullTextMaintenanceScopePlanner"/> 的纯逻辑断言。
///
/// <para>
/// 为什么这层值得单独测：宿主把「配置里的字符串」翻译成「维护器要的 RootPath + ScopeKey」，
/// 中间任何一步静默出错，表现都是「维护启动了但什么都没索引」—— 与「维护根本没启动」在
/// 日志上几乎无法区分。所以这里逐条锚定三件事：接受、相对路径合成、拒绝必须带原因。
/// </para>
///
/// <para>
/// 用例只做 <see cref="Path.GetTempPath"/> 下的目录创建/清理，不写文件、不碰索引根。
/// </para>
/// </summary>
[TestClass]
public sealed class FullTextMaintenanceScopePlannerTests
{
    [TestMethod]
    public void Create_AbsoluteExistingScope_IsAcceptedWithNormalizedKeyAndBudget()
    {
        var root = CreateTempDirectory();
        try
        {
            var options = new MaintenanceOptions
            {
                Enabled = true,
                Scopes = new[] { root },
                MaxIndexBytes = 1_234_567,
            };

            var plan = FullTextMaintenanceScopePlanner.Create(options);

            Assert.AreEqual(0, plan.Rejections.Count, "存在的绝对目录不应被拒绝。");
            Assert.AreEqual(1, plan.Scopes.Count);

            var scope = plan.Scopes[0];
            Assert.AreEqual(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), scope.RootPath);

            // 规范键口径（分隔符统一 \ + 不变文化小写）由组件内 SupplyScopeNormalizer 唯一实现；
            // 这里按同一口径独立重算一次作为期望值，避免把「实现输出抄一遍」当成断言。
            var expectedKey = scope.RootPath.Replace('/', '\\').ToLowerInvariant();
            Assert.AreEqual(expectedKey, scope.ScopeKey);

            Assert.AreEqual(1_234_567, scope.MaxIndexBytes, "预算必须来自配置，不得被默认值顶掉。");
            Assert.IsNull(scope.IndexDirectory, "索引目录应留给维护器用同一真源推导，不得在此复刻命名哈希。");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void Create_RelativeScopeWithAbsoluteWorkspaceRoot_IsResolvedAgainstWorkspaceRoot()
    {
        var workspaceRoot = CreateTempDirectory();
        var sub = Path.Combine(workspaceRoot, "sub");
        Directory.CreateDirectory(sub);
        try
        {
            var options = new MaintenanceOptions
            {
                Enabled = true,
                WorkspaceRoot = workspaceRoot,
                Scopes = new[] { "sub" },
            };

            var plan = FullTextMaintenanceScopePlanner.Create(options);

            Assert.AreEqual(0, plan.Rejections.Count);
            Assert.AreEqual(1, plan.Scopes.Count);
            Assert.AreEqual(
                sub.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                plan.Scopes[0].RootPath,
                "相对 scope 必须解析到 WorkspaceRoot 之下，而不是进程当前目录。");
        }
        finally
        {
            TryDelete(workspaceRoot);
        }
    }

    [TestMethod]
    public void Create_RelativeScopeWithoutWorkspaceRoot_IsRejectedWithReason()
    {
        var options = new MaintenanceOptions
        {
            Enabled = true,
            Scopes = new[] { "relative-does-not-exist" },
        };

        var plan = FullTextMaintenanceScopePlanner.Create(options);

        Assert.AreEqual(0, plan.Scopes.Count, "无法确定的相对 scope 绝不能被猜成某个绝对路径。");
        Assert.AreEqual(1, plan.Rejections.Count, "拒绝必须留痕：静默丢弃会让配置错误表现成「没配置」。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(plan.Rejections[0]));
    }

    [TestMethod]
    public void Create_MissingDirectory_IsRejectedAndYieldsNoScope()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pudding-missing-" + Guid.NewGuid().ToString("N"));
        var options = new MaintenanceOptions
        {
            Enabled = true,
            Scopes = new[] { missing },
        };

        var plan = FullTextMaintenanceScopePlanner.Create(options);

        Assert.AreEqual(0, plan.Scopes.Count, "不存在的目录不得进入受维护 scope 清单。");
        Assert.AreEqual(1, plan.Rejections.Count);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pudding-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结论；临时目录由系统回收。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
