using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Python;

namespace PuddingCodeIntelligenceTests.Python;

[TestClass]
public sealed class PythonFileOutlinerTests
{
    private readonly PythonFileOutliner _outliner = new();

    [TestMethod]
    public void SupportedExtensions_Include_Python_File_Forms()
    {
        CollectionAssert.IsSubsetOf(
            new[] { ".py", ".pyw", ".pyi" },
            _outliner.SupportedExtensions.ToArray());
    }

    [TestMethod]
    public async Task OutlineAsync_ClassMethodsFunctionsAndVariables_AreExtracted()
    {
        var source = """
            from pathlib import Path

            VERSION = "1.0"
            config = {"debug": True}

            class Client:
                DEFAULT_TIMEOUT = 30

                def __init__(self, endpoint: str):
                    self.endpoint = endpoint

                async def fetch(self, path: str) -> dict:
                    return {}

                def _normalize(self, value: str) -> str:
                    return value.strip()

            async def main() -> None:
                pass

            def helper(left: int, right: int = 0) -> int:
                return left + right
            """;

        var result = await _outliner.OutlineAsync("client.py", source);

        Assert.IsTrue(result.Success, result.Error);

        var names = result.Nodes.Select(n => n.Name).ToArray();
        CollectionAssert.Contains(names, "VERSION");
        CollectionAssert.Contains(names, "config");
        CollectionAssert.Contains(names, "Client");
        CollectionAssert.Contains(names, "main");
        CollectionAssert.Contains(names, "helper");

        var version = result.Nodes.Single(n => n.Name == "VERSION");
        Assert.AreEqual(CodeSymbolKind.Constant, version.Kind);

        var client = result.Nodes.Single(n => n.Name == "Client");
        Assert.AreEqual(CodeSymbolKind.Class, client.Kind);
        Assert.IsNotNull(client.Children);

        var childNames = client.Children!.Select(n => n.Name).ToArray();
        CollectionAssert.Contains(childNames, "DEFAULT_TIMEOUT");
        CollectionAssert.Contains(childNames, "__init__");
        CollectionAssert.Contains(childNames, "fetch");
        CollectionAssert.Contains(childNames, "_normalize");

        var fetch = client.Children!.Single(n => n.Name == "fetch");
        Assert.AreEqual(CodeSymbolKind.Method, fetch.Kind);
        StringAssert.Contains(fetch.Signature, "async def fetch(self, path: str) -> dict");
    }

    [TestMethod]
    public async Task OutlineAsync_Ignores_Nested_Functions_Inside_FunctionBodies()
    {
        var source = """
            def outer(value: int) -> int:
                def inner() -> int:
                    return value + 1

                if value > 0:
                    return inner()

                return value
            """;

        var result = await _outliner.OutlineAsync("nested.py", source);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, result.Nodes.Count);
        Assert.AreEqual("outer", result.Nodes[0].Name);
    }
}
