using PuddingCode.Observability;

namespace PuddingCoreTests;

/// <summary>
/// 探针的唯一职责：把「报文里有没有 reasoning.effort」变成可读的事实。
/// 它出错的方式必须是"返回 null"，不能是抛异常——诊断代码不许让请求失败。
/// </summary>
[TestClass]
public sealed class LlmBodyReasoningProbeTests
{
    [TestMethod]
    public void ReadsEffortFromRequestBody()
    {
        const string body = "{\"model\":\"deepseek-flash\",\"reasoning\":{\"effort\":\"max\"},\"input\":[]}";

        Assert.AreEqual("max", LlmBodyReasoningProbe.TryReadReasoningEffort(body));
    }

    [TestMethod]
    public void ReturnsNullWhenReasoningSectionIsAbsent()
    {
        const string body = "{\"model\":\"deepseek-flash\",\"input\":[]}";

        Assert.IsNull(LlmBodyReasoningProbe.TryReadReasoningEffort(body));
    }

    [TestMethod]
    public void ReturnsNullWhenEffortIsNotAString()
    {
        const string body = "{\"reasoning\":{\"effort\":42}}";

        Assert.IsNull(LlmBodyReasoningProbe.TryReadReasoningEffort(body));
    }

    [TestMethod]
    public void ReturnsNullForMalformedOrEmptyInput()
    {
        Assert.IsNull(LlmBodyReasoningProbe.TryReadReasoningEffort(null));
        Assert.IsNull(LlmBodyReasoningProbe.TryReadReasoningEffort(string.Empty));
        Assert.IsNull(LlmBodyReasoningProbe.TryReadReasoningEffort("{not json"));
        Assert.IsNull(LlmBodyReasoningProbe.TryReadReasoningEffort("[1,2,3]"));
    }
}
