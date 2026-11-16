using HarnessAgent.Core.Connectors.Feishu;

namespace HarnessAgent.Core.Tests.Feishu;

/// <summary>
/// 2026-11-16：扩展名到飞书 file_type 的映射。
/// 这张表是连接器与工具层共用的唯一来源，所以要对「白名单内」「白名单外」
/// 「大小写」「带路径」「无扩展名」四类边界都钉住。
/// </summary>
[TestClass]
public sealed class FeishuFileTypesTests
{
    [DataTestMethod]
    [DataRow("report.pdf", "pdf", "application/pdf")]
    [DataRow("REPORT.PDF", "pdf", "application/pdf")]
    [DataRow("memo.docx", "doc", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [DataRow("memo.doc", "doc", "application/msword")]
    [DataRow("sheet.xlsx", "xls", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [DataRow("deck.pptx", "ppt", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [DataRow("clip.mp4", "mp4", "video/mp4")]
    [DataRow("pudding-1.opus", "opus", "audio/ogg")]
    public void Whitelisted_Extensions_Map_To_Feishu_FileTypes(
        string fileName, string expectedFileType, string expectedContentType)
    {
        var descriptor = FeishuFileTypes.Resolve(fileName);

        Assert.AreEqual(expectedFileType, descriptor.FileType);
        Assert.AreEqual(expectedContentType, descriptor.ContentType);
        Assert.Contains(expectedFileType, FeishuFileTypes.Supported);
    }

    [DataTestMethod]
    [DataRow("archive.zip")]
    [DataRow("no-extension")]
    [DataRow("trailing.")]
    [DataRow("")]
    [DataRow(null)]
    public void Unknown_Or_Missing_Extensions_Fall_Back_To_Stream(string? fileName)
    {
        var descriptor = FeishuFileTypes.Resolve(fileName);

        Assert.AreEqual("stream", descriptor.FileType, "白名单之外的扩展名必须按未知二进制发");
        Assert.AreEqual("application/octet-stream", descriptor.ContentType);
    }

    [TestMethod]
    public void Directory_Dots_Are_Not_Treated_As_Extension()
    {
        // 若把目录里的点当扩展名，'v1.2' 会被误判；这里必须是 stream。
        var descriptor = FeishuFileTypes.Resolve(@"..\releases\v1.2\README");

        Assert.AreEqual("stream", descriptor.FileType);
    }

    [TestMethod]
    public void Path_Prefix_Does_Not_Hide_The_Real_Extension()
    {
        var descriptor = FeishuFileTypes.Resolve(@"D:\Data\output\HowToLiveBetter-20261116-v3.pdf");

        Assert.AreEqual("pdf", descriptor.FileType);
        Assert.AreEqual("application/pdf", descriptor.ContentType);
    }
}
