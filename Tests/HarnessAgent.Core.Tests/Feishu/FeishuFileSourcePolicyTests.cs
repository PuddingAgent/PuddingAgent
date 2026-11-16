using HarnessAgent.Core.Connectors.Feishu;

namespace HarnessAgent.Core.Tests.Feishu;

/// <summary>
/// 2026-11-16：文件投递的取材判定。
/// 设计改过一次——最初要求「必须在工作区内」，那会把用户自己要发的
/// <c>D:\Data\output\*.pdf</c> 拦在外面。现在按「允许根可配置 + 凭据类拦截」判定。
/// </summary>
[TestClass]
public sealed class FeishuFileSourcePolicyTests
{
    private string _root = string.Empty;
    private string _otherRoot = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "pudding-feishu-source", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "output");
        _otherRoot = Path.Combine(baseDir, "output2");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_otherRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        var parent = Path.GetDirectoryName(_root);
        if (parent is not null && Directory.Exists(parent))
            Directory.Delete(parent, recursive: true);
    }

    [TestMethod]
    public void File_Inside_Allowed_Root_Is_Allowed()
    {
        var file = CreateFile(_root, "book.pdf");
        var policy = new FeishuFileSourcePolicy([_root]);

        var decision = policy.Evaluate(file);

        Assert.IsTrue(decision.Allowed, decision.Reason);
        Assert.AreEqual(Path.GetFullPath(file), decision.FullPath);
    }

    [TestMethod]
    public void Sibling_Root_Sharing_A_Prefix_Is_Not_Allowed()
    {
        // 经典前缀 bug：D:\...\output2 不能因为以 D:\...\output 开头就放行。
        var file = CreateFile(_otherRoot, "sneaky.pdf");
        var policy = new FeishuFileSourcePolicy([_root]);

        var decision = policy.Evaluate(file);

        Assert.IsFalse(decision.Allowed);
        StringAssert.Contains(decision.Reason, "outside the allowed roots");
    }

    [TestMethod]
    public void Directory_Is_Rejected()
    {
        var policy = new FeishuFileSourcePolicy([_root]);

        var decision = policy.Evaluate(_root);

        Assert.IsFalse(decision.Allowed);
        StringAssert.Contains(decision.Reason, "directory");
    }

    [TestMethod]
    public void Missing_File_Is_Rejected()
    {
        var policy = new FeishuFileSourcePolicy([_root]);

        var decision = policy.Evaluate(Path.Combine(_root, "nope.pdf"));

        Assert.IsFalse(decision.Allowed);
        StringAssert.Contains(decision.Reason, "not found");
    }

    [TestMethod]
    public void Traversal_Out_Of_The_Root_Is_Rejected()
    {
        var outside = CreateFile(Path.GetDirectoryName(_root)!, "secret.txt");
        var policy = new FeishuFileSourcePolicy([_root]);

        var decision = policy.Evaluate(Path.Combine(_root, "..", "secret.txt"));

        Assert.IsFalse(decision.Allowed);
        StringAssert.Contains(decision.Reason, "outside the allowed roots");
        Assert.IsTrue(File.Exists(outside));
    }

    [DataTestMethod]
    [DataRow("id_rsa")]
    [DataRow("credentials")]
    [DataRow(".env")]
    [DataRow("server.pem")]
    [DataRow("bundle.pfx")]
    [DataRow("vault.kdbx")]
    public void Credential_Like_Files_Are_Rejected_Even_Inside_The_Root(string fileName)
    {
        var file = CreateFile(_root, fileName);
        var policy = new FeishuFileSourcePolicy([_root]);

        var decision = policy.Evaluate(file);

        Assert.IsFalse(decision.Allowed, $"{fileName} 不应被当作普通文件发出");
        StringAssert.Contains(decision.Reason, "credential");
    }

    [TestMethod]
    public void Credential_Directory_Segment_Is_Rejected()
    {
        var sshDir = Path.Combine(_root, ".ssh");
        Directory.CreateDirectory(sshDir);
        var file = CreateFile(sshDir, "known_hosts");
        var policy = new FeishuFileSourcePolicy([_root]);

        var decision = policy.Evaluate(file);

        Assert.IsFalse(decision.Allowed);
        StringAssert.Contains(decision.Reason, "credential");
    }

    [TestMethod]
    public void Extra_Roots_Are_Honoured()
    {
        // 用户的实际需求：PDF 在 D:\Data\output 这类工作区之外的目录，必须能发。
        var file = CreateFile(_otherRoot, "report.pdf");
        var policy = new FeishuFileSourcePolicy([_root, _otherRoot]);

        var decision = policy.Evaluate(file);

        Assert.IsTrue(decision.Allowed, decision.Reason);
    }

    [TestMethod]
    public void Blank_Path_Is_Rejected()
    {
        var policy = new FeishuFileSourcePolicy([_root]);

        Assert.IsFalse(policy.Evaluate(" ").Allowed);
        Assert.IsFalse(policy.Evaluate(null).Allowed);
    }

    private static string CreateFile(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "x");
        return path;
    }
}

/// <summary>超过飞书 30 MB 上限时的切分边界。</summary>
[TestClass]
public sealed class FeishuFileSplitPlannerTests
{
    private const long Mb = 1024 * 1024;

    [TestMethod]
    public void Exactly_At_The_Limit_Stays_A_Single_Part()
    {
        var parts = FeishuFileSplitPlanner.Plan(30 * Mb);

        Assert.AreEqual(1, parts.Count, "恰好等于上限不是「超限」，不该切");
        Assert.AreEqual(string.Empty, parts[0].Suffix);
        Assert.AreEqual(30 * Mb, parts[0].Length);
    }

    [TestMethod]
    public void One_Byte_Over_Splits_Into_Two_Parts()
    {
        var parts = FeishuFileSplitPlanner.Plan(30 * Mb + 1);

        Assert.AreEqual(2, parts.Count);
        Assert.AreEqual(30 * Mb, parts[0].Length);
        Assert.AreEqual(1, parts[1].Length);
        Assert.AreEqual(".part02of02", parts[1].Suffix);
    }

    [TestMethod]
    public void Parts_Cover_The_Whole_File_Without_Gaps()
    {
        var total = 95 * Mb + 123;
        var parts = FeishuFileSplitPlanner.Plan(total);

        Assert.AreEqual(4, parts.Count);
        Assert.AreEqual(0, parts[0].Offset);
        long sum = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            Assert.AreEqual(sum, parts[i].Offset, $"第 {i + 1} 段的起点必须紧接上一段");
            Assert.IsTrue(parts[i].Length <= 30 * Mb);
            sum += parts[i].Length;
        }

        Assert.AreEqual(total, sum, "各段之和必须等于文件长度");
    }

    [TestMethod]
    public void Empty_File_Has_No_Parts()
    {
        Assert.AreEqual(0, FeishuFileSplitPlanner.Plan(0).Count);
    }
}
