using HarnessAgent.Core.Connectors.Feishu;

namespace HarnessAgent.Core.Tests.Feishu;

/// <summary>
/// 凭据判定的单一真源。
///
/// <para>连接器在真正读取字节之前要用同一套规则再复核一次：排队与投递之间存在时间窗，
/// 路径可能已被替换成凭据文件。若复核另写一套名单，两处就会漂移，
/// 所以判定逻辑只暴露 <see cref="FeishuFileSourcePolicy.IsCredentialLike"/> 一个入口。</para>
/// </summary>
[TestClass]
public sealed class FeishuFileCredentialProbeTests
{
    [DataTestMethod]
    [DataRow("prod.env", true)]
    [DataRow(".env.local", true)]
    [DataRow("credentials.json", true)]
    [DataRow("credential-store.yaml", true)]
    [DataRow("secret-token.txt", true)]
    [DataRow("id_rsa", true)]
    [DataRow("id_ed25519.pub", true)]
    [DataRow("notes.md", false)]
    [DataRow("HowToLiveBetter-20261116-v10.pdf", false)]
    [DataRow("report.csv", false)]
    public void IsCredentialLike_UsesTheSameRulesAsTheSourcePolicy(string fileName, bool expected)
    {
        Assert.AreEqual(
            expected,
            FeishuFileSourcePolicy.IsCredentialLike(fileName),
            fileName);
    }
}
