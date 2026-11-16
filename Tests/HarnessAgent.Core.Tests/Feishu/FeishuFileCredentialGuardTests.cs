using HarnessAgent.Core.Connectors.Feishu;

namespace HarnessAgent.Core.Tests.Feishu;

/// <summary>
/// 凭据拦截的变体覆盖。
///
/// <para>背景：原来的守卫是「精确文件名 + 扩展名」两份名单，挡不住最常见的凭据落盘形式——
/// <c>prod.env</c> 的扩展名不在名单里，<c>.env.local</c> 与 <c>credentials.json</c>
/// 的名字又不是精确命中，三者全部放行。而 <see cref="FeishuFileSourcePolicy"/> 自称是
/// 「模型被注入后唯一阻止把私钥发出去的那道关卡」，名单有缝等于没有关卡。</para>
/// </summary>
[TestClass]
public sealed class FeishuFileCredentialGuardTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "pudding-feishu-credential",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private FeishuFileSourceDecision Evaluate(string fileName)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, "content");
        return new FeishuFileSourcePolicy([_root]).Evaluate(path);
    }

    [DataTestMethod]
    [DataRow(".env")]
    [DataRow(".env.local")]
    [DataRow(".env.production")]
    [DataRow("prod.env")]
    [DataRow("credentials.json")]
    [DataRow("credential-store.yaml")]
    [DataRow("secret-token.txt")]
    [DataRow("id_rsa")]
    [DataRow("id_ed25519.pub")]
    public void CredentialVariantsAreDenied(string fileName)
    {
        var decision = Evaluate(fileName);

        Assert.IsFalse(decision.Allowed, fileName + " 必须被拒绝");
    }

    [DataTestMethod]
    [DataRow("notes.md")]
    [DataRow("report.pdf")]
    [DataRow("HowToLiveBetter-20261116-v10.pdf")]
    public void OrdinaryFilesAreStillAllowed(string fileName)
    {
        var decision = Evaluate(fileName);

        Assert.IsTrue(decision.Allowed, fileName + " 应当放行");
    }
}
