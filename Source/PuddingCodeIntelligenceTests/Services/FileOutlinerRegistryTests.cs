using Microsoft.VisualStudio.TestTools.UnitTesting;

using PuddingCodeIntelligence.Markdown;
using PuddingCodeIntelligence.Cpp;
using PuddingCodeIntelligence.Python;
using PuddingCodeIntelligence.Services;
using PuddingCodeIntelligence.TypeScript;

namespace PuddingCodeIntelligenceTests.Services;

[TestClass]
public class FileOutlinerRegistryTests
{
    private readonly FileOutlinerRegistry _registry;

    public FileOutlinerRegistryTests()
    {
        _registry = new FileOutlinerRegistry([
            new TypeScriptFileOutliner(),
            new MarkdownFileOutliner(),
            new CppFileOutliner(),
            new PythonFileOutliner()
        ]);
    }

    [TestMethod]
    public void IsSupported_TypeScriptFile_ReturnsTrue()
    {
        Assert.IsTrue(_registry.IsSupported("app.ts"));
        Assert.IsTrue(_registry.IsSupported("component.tsx"));
        Assert.IsTrue(_registry.IsSupported("script.js"));
        Assert.IsTrue(_registry.IsSupported("page.jsx"));
    }

    [TestMethod]
    public void IsSupported_MarkdownFile_ReturnsTrue()
    {
        Assert.IsTrue(_registry.IsSupported("README.md"));
        Assert.IsTrue(_registry.IsSupported("docs.mdx"));
        Assert.IsTrue(_registry.IsSupported("notes.markdown"));
    }

    [TestMethod]
    public void IsSupported_UnsupportedFile_ReturnsFalse()
    {
        Assert.IsFalse(_registry.IsSupported("program.cs"));
        Assert.IsFalse(_registry.IsSupported("style.css"));
        Assert.IsFalse(_registry.IsSupported("image.png"));
    }

    [TestMethod]
    public void IsSupported_CppFile_ReturnsTrue()
    {
        Assert.IsTrue(_registry.IsSupported("main.c"));
        Assert.IsTrue(_registry.IsSupported("renderer.cpp"));
        Assert.IsTrue(_registry.IsSupported("renderer.hpp"));
        Assert.IsTrue(_registry.IsSupported("renderer.hxx"));
    }

    [TestMethod]
    public void IsSupported_PythonFile_ReturnsTrue()
    {
        Assert.IsTrue(_registry.IsSupported("main.py"));
        Assert.IsTrue(_registry.IsSupported("app.pyw"));
        Assert.IsTrue(_registry.IsSupported("typing.pyi"));
    }

    [TestMethod]
    public void GetOutliner_TypeScriptFile_ReturnsTypeScriptOutliner()
    {
        var outliner = _registry.GetOutliner("app.ts");
        Assert.IsNotNull(outliner);
        Assert.IsInstanceOfType(outliner, typeof(TypeScriptFileOutliner));
    }

    [TestMethod]
    public void GetOutliner_MarkdownFile_ReturnsMarkdownOutliner()
    {
        var outliner = _registry.GetOutliner("README.md");
        Assert.IsNotNull(outliner);
        Assert.IsInstanceOfType(outliner, typeof(MarkdownFileOutliner));
    }

    [TestMethod]
    public void GetOutliner_UnsupportedFile_ReturnsNull()
    {
        var outliner = _registry.GetOutliner("program.cs");
        Assert.IsNull(outliner);
    }

    [TestMethod]
    public void GetOutliner_CaseInsensitive_Works()
    {
        var outliner = _registry.GetOutliner("README.MD");
        Assert.IsNotNull(outliner);
        Assert.IsInstanceOfType(outliner, typeof(MarkdownFileOutliner));
    }
}
