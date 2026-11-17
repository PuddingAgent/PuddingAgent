using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 派发前工具参数不得被改写：历史上这里会做 vault 展开，把明文密钥送进
/// <c>file_write</c> / <c>shell</c>。这些用例锁住「原样派发」这件事本身。
/// </summary>
[TestClass]
public sealed class ToolArgumentPolicyTests
{
    [TestMethod]
    public void Vault_Reference_Is_Dispatched_Verbatim()
    {
        const string json = "{\"path\":\"notes.md\",\"content\":\"key={{vault:cfg.test.apiKey}}\"}";

        var actual = ToolArgumentPolicy.ForDispatch(json);

        Assert.AreEqual(json, actual,
            "工具参数被改写了：派发值必须与模型给出的参数逐字相同。");
    }

    [TestMethod]
    public void Resolvable_Reference_Is_Not_Replaced()
    {
        var actual = ToolArgumentPolicy.ForDispatch("{\"k\":\"{{vault:search.anysearch}}\"}");

        Assert.IsFalse(actual.Contains("sk-", StringComparison.Ordinal), "参数里不应出现任何已解析的明文形态。");
        StringAssert.Contains(actual, "{{vault:", "引用必须原样保留，交给工具自己的凭据边界处理。");
    }

    [TestMethod]
    public void Null_Or_Empty_Becomes_Empty_Object()
    {
        Assert.AreEqual("{}", ToolArgumentPolicy.ForDispatch(null));
        Assert.AreEqual("{}", ToolArgumentPolicy.ForDispatch(string.Empty));
    }

    [TestMethod]
    public void Plain_Arguments_Pass_Through_Unchanged()
    {
        const string json = "{\"query\":\"hello\"}";

        Assert.AreEqual(json, ToolArgumentPolicy.ForDispatch(json));
    }
}
