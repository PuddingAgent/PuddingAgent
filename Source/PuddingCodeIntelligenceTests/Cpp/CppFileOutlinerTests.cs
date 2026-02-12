using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Cpp;

namespace PuddingCodeIntelligenceTests.Cpp;

[TestClass]
public sealed class CppFileOutlinerTests
{
    private readonly CppFileOutliner _outliner = new();

    [TestMethod]
    public void SupportedExtensions_Include_C_And_Cpp_Header_Forms()
    {
        CollectionAssert.IsSubsetOf(
            new[] { ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh", ".hxx" },
            _outliner.SupportedExtensions.ToArray());
    }

    [TestMethod]
    public async Task OutlineAsync_ClassStructEnumAndFunctions_AreExtracted()
    {
        var source = """
            #pragma once

            namespace engine {
            enum class Mode {
                Idle,
                Running
            };

            struct Vec2 {
                float x;
                float y;
            };

            class Renderer {
            public:
                Renderer();
                ~Renderer();
                void draw(const Vec2& position);

            private:
                int frameCount;
            };

            int add(int left, int right);
            }

            static bool is_ready(const engine::Renderer& renderer) {
                return true;
            }
            """;

        var result = await _outliner.OutlineAsync("renderer.hpp", source);

        Assert.IsTrue(result.Success, result.Error);

        var names = result.Nodes.Select(n => n.Name).ToArray();
        CollectionAssert.Contains(names, "engine");
        CollectionAssert.Contains(names, "Mode");
        CollectionAssert.Contains(names, "Vec2");
        CollectionAssert.Contains(names, "Renderer");
        CollectionAssert.Contains(names, "is_ready");

        var renderer = result.Nodes.Single(n => n.Name == "Renderer");
        Assert.AreEqual(CodeSymbolKind.Class, renderer.Kind);
        Assert.IsNotNull(renderer.Children);

        var childNames = renderer.Children!.Select(n => n.Name).ToArray();
        CollectionAssert.Contains(childNames, "Renderer");
        CollectionAssert.Contains(childNames, "~Renderer");
        CollectionAssert.Contains(childNames, "draw");
        CollectionAssert.Contains(childNames, "frameCount");

        var add = result.Nodes.Single(n => n.Name == "add");
        Assert.AreEqual(CodeSymbolKind.Method, add.Kind);
        StringAssert.Contains(add.Signature, "int add(int left, int right)");
    }

    [TestMethod]
    public async Task OutlineAsync_Ignores_ControlFlow_Inside_FunctionBodies()
    {
        var source = """
            int compute(int value) {
                if (value > 0) {
                    return value;
                }

                for (int i = 0; i < 3; ++i) {
                    value += i;
                }

                return value;
            }
            """;

        var result = await _outliner.OutlineAsync("math.cpp", source);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(1, result.Nodes.Count);
        Assert.AreEqual("compute", result.Nodes[0].Name);
    }
}
