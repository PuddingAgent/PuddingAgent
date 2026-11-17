using HarnessAgent.Core.Connectors.Feishu;

namespace HarnessAgent.Core.Tests.Feishu;

/// <summary>
/// 2026-11-17：兜底收件人的判定。
///
/// <para>
/// 缺陷背景：语音 / 图片 / 文件三条发送路径曾各自硬编码
/// <c>receive_id_type = "chat_id"</c>，只有文本那条读 <c>metadata["open_id"]</c>。
/// 而派发器在显式收件人场景会写入 <c>open_id</c> 并删掉 <c>chat_id</c>，
/// 于是 open_id 会被当成 chat_id 提交给飞书。
/// </para>
///
/// <para>
/// 这些用例锁的是「open_id 优先」这条判定本身；判定收拢成一个纯函数，
/// 新增发送路径就无法再各写一份而漏读。
/// </para>
/// </summary>
[TestClass]
public sealed class FeishuRecipientResolverTests
{
    [TestMethod]
    public void Explicit_Open_Id_Wins_Over_Chat_Target()
    {
        // 派发器的显式收件人分支会同时留下 open_id 与 Target，此时必须按用户发。
        var metadata = new Dictionary<string, string>
        {
            [FeishuRecipientResolver.OpenIdMetadataKey] = "ou_selected",
        };

        var (target, receiveIdType) = FeishuRecipientResolver.Resolve(metadata, "oc_group");

        Assert.AreEqual("ou_selected", target);
        Assert.AreEqual("open_id", receiveIdType);
    }

    [TestMethod]
    public void Falls_Back_To_Chat_Id_When_Open_Id_Is_Absent()
    {
        var metadata = new Dictionary<string, string>();

        var (target, receiveIdType) = FeishuRecipientResolver.Resolve(metadata, "oc_group");

        Assert.AreEqual("oc_group", target);
        Assert.AreEqual("chat_id", receiveIdType);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void Blank_Open_Id_Does_Not_Shadow_The_Chat_Target(string openId)
    {
        // 空串若被当成 open_id，会把真正的群聊目标挤掉。
        var metadata = new Dictionary<string, string>
        {
            [FeishuRecipientResolver.OpenIdMetadataKey] = openId,
        };

        var (target, receiveIdType) = FeishuRecipientResolver.Resolve(metadata, "oc_group");

        Assert.AreEqual("oc_group", target);
        Assert.AreEqual("chat_id", receiveIdType);
    }

    [TestMethod]
    public void Null_Metadata_Still_Resolves_The_Chat_Target()
    {
        var (target, receiveIdType) = FeishuRecipientResolver.Resolve(null, "oc_group");

        Assert.AreEqual("oc_group", target);
        Assert.AreEqual("chat_id", receiveIdType);
    }

    [TestMethod]
    public void Missing_Both_Is_Rejected_Rather_Than_Guessed()
    {
        // 宁可抛，也不要用空目标去撞服务端。
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => FeishuRecipientResolver.Resolve(null, null));
        Assert.AreEqual(
            "No open_id or chat target specified for Feishu message",
            ex.Message);
    }

    [TestMethod]
    public void Blank_Target_Is_Not_A_Recipient()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => FeishuRecipientResolver.Resolve(new Dictionary<string, string>(), "  "));
    }

    [TestMethod]
    public void Constants_Match_The_Feishu_Api_Values()
    {
        // 这两个字面量是飞书约定的取主键值，改成别的会 400。
        Assert.AreEqual("open_id", FeishuRecipientResolver.OpenIdReceiveIdType);
        Assert.AreEqual("chat_id", FeishuRecipientResolver.ChatIdReceiveIdType);
        Assert.AreEqual("open_id", FeishuRecipientResolver.OpenIdMetadataKey);
    }
}
