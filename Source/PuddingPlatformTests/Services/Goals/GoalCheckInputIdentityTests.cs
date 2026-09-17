using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// ADR-092 决策 10 · P1：GoalCheckInputIdentity 只读采集组件的行为验收。
/// 全部用例在临时目录自建最小项目树，测后清理；不触碰仓库内任何既有文件。
/// </summary>
[TestClass]
public sealed class GoalCheckInputIdentityTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "goal-check-input-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (!Directory.Exists(_root))
            return;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清理失败不影响测试结论（Windows 句柄延迟释放时的已知噪音）。
        }
    }

    [TestMethod]
    public void T1_Collect_SameTree_IsDeterministic()
    {
        CreateMinimalProject("App");

        var first = GoalCheckInputIdentity.Collect(_root, "App");
        var second = GoalCheckInputIdentity.Collect(_root, "App");

        Assert.AreEqual(first.Identity, second.Identity, "同一树连续采集必须得到完全相同的 Identity。");
        Assert.IsTrue(first.Identity.StartsWith("sha256:", StringComparison.Ordinal), "Identity 必须是 sha256:<lowerhex> 形态。");
        Assert.IsTrue(first.Entries.Any(e => e.RelativePath == "App/A.cs"), "源码分量必须包含 App/A.cs。");
    }

    [TestMethod]
    public void T2_Collect_RelatedSourceChange_ChangesIdentity()
    {
        CreateMinimalProject("App");
        var before = GoalCheckInputIdentity.Collect(_root, "App");

        File.WriteAllText(Path.Combine(_root, "App", "A.cs"), "class A { int Changed = 1; }\n");
        var after = GoalCheckInputIdentity.Collect(_root, "App");

        Assert.AreNotEqual(before.Identity, after.Identity, "相关源码内容变化必须使旧结论失效。");
    }

    [TestMethod]
    public void T3_Collect_UnrelatedDocOutsideProject_KeepsIdentity()
    {
        CreateMinimalProject("App");
        var before = GoalCheckInputIdentity.Collect(_root, "App");

        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "docs", "README.md"), "# 与检查无关的文档\n");
        var after = GoalCheckInputIdentity.Collect(_root, "App");

        Assert.AreEqual(before.Identity, after.Identity, "不在依赖范围内的无关变化不得使旧结论失效。");
    }

    [TestMethod]
    public void T4_Collect_CsprojConfigChange_ChangesIdentity()
    {
        CreateMinimalProject("App");
        var before = GoalCheckInputIdentity.Collect(_root, "App");

        File.WriteAllText(
            Path.Combine(_root, "App", "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n");
        var after = GoalCheckInputIdentity.Collect(_root, "App");

        Assert.AreNotEqual(before.Identity, after.Identity, "构建/运行配置变化必须使旧结论失效。");
    }

    [TestMethod]
    public void T5_Collect_CheckDefinitionRefChange_ChangesIdentity()
    {
        CreateMinimalProject("App");
        var before = GoalCheckInputIdentity.Collect(_root, "App", ["checks/build.md#dotnet-build"]);
        var after = GoalCheckInputIdentity.Collect(_root, "App", ["checks/test.md#dotnet-test"]);

        Assert.AreNotEqual(before.Identity, after.Identity, "检查定义变化必须使旧结论失效。");
        Assert.IsTrue(before.Entries.Any(e => e.RelativePath == "check:checks/build.md#dotnet-build"));
        Assert.IsTrue(after.Entries.Any(e => e.RelativePath == "check:checks/test.md#dotnet-test"));
    }

    [TestMethod]
    public void T6_Collect_ObjArtifactInsideProject_KeepsIdentity()
    {
        CreateMinimalProject("App");
        var before = GoalCheckInputIdentity.Collect(_root, "App");

        Directory.CreateDirectory(Path.Combine(_root, "App", "obj"));
        File.WriteAllText(Path.Combine(_root, "App", "obj", "x.cs"), "// 生成物，不属于检查输入。\n");
        var after = GoalCheckInputIdentity.Collect(_root, "App");

        Assert.AreEqual(before.Identity, after.Identity, "排除路径段（obj）下的文件不得进入清单。");
        Assert.IsFalse(after.Entries.Any(e => e.RelativePath.Contains("/obj/", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void T7_Collect_PathEscape_Throws()
    {
        CreateMinimalProject("App");

        Assert.ThrowsExactly<ArgumentException>(
            () => GoalCheckInputIdentity.Collect(_root, "../outside"));
    }

    [TestMethod]
    public void T8_Collect_ProvenanceHead_DoesNotAffectIdentity()
    {
        CreateMinimalProject("App");
        var withHeadA = GoalCheckInputIdentity.Collect(_root, "App", provenanceHead: "aaa");
        var withHeadB = GoalCheckInputIdentity.Collect(_root, "App", provenanceHead: "bbb");

        Assert.AreEqual(withHeadA.Identity, withHeadB.Identity, "HEAD 仅作来源信息，不得参与 Identity。");
        Assert.AreEqual("aaa", withHeadA.ProvenanceHead);
        Assert.AreEqual("bbb", withHeadB.ProvenanceHead);
    }

    [TestMethod]
    public void T9_Collect_ProjectReferenceChange_ChangesIdentity()
    {
        CreateMinimalProject("App", withProjectReference: true);
        var before = GoalCheckInputIdentity.Collect(_root, "App", ["checks/build.md#dotnet-build"]);

        File.WriteAllText(Path.Combine(_root, "Lib", "Lib.cs"), "class Lib { int Changed = 2; }\n");
        var after = GoalCheckInputIdentity.Collect(_root, "App", ["checks/build.md#dotnet-build"]);

        Assert.AreNotEqual(before.Identity, after.Identity, "一层 ProjectReference 解析出的被引用项目源码必须计入身份。");
    }

    [TestMethod]
    public void T10_Collect_TransitiveProjectReferenceChange_ChangesIdentity()
    {
        CreateTransitiveProjectTree();
        var before = GoalCheckInputIdentity.Collect(_root, "App");

        // 二层引用（App → Lib → Core）：Core 的源码不在一层范围内，必须靠传递闭合才能进入身份。
        File.WriteAllText(Path.Combine(_root, "Core", "Core.cs"), "class Core { int Changed = 3; }\n");
        var after = GoalCheckInputIdentity.Collect(_root, "App");

        Assert.AreNotEqual(before.Identity, after.Identity, "传递引用的被引用项目源码变化必须使旧结论失效。");
        Assert.IsTrue(after.Entries.Any(e => e.RelativePath == "Core/Core.cs"), "二层引用项目的源码必须进入清单。");
    }

    [TestMethod]
    public void T11_Collect_CyclicProjectReferences_Terminates()
    {
        CreateCyclicProjectTree();

        // 环保护：A ↔ B 互相引用时必须终止且产出稳定身份，不得无限展开。
        var first = GoalCheckInputIdentity.Collect(_root, "A");
        var second = GoalCheckInputIdentity.Collect(_root, "A");

        Assert.IsTrue(first.Identity.StartsWith("sha256:", StringComparison.Ordinal));
        Assert.AreEqual(first.Identity, second.Identity, "含环的项目图仍必须得到确定性身份。");
        Assert.IsTrue(first.Entries.Any(e => e.RelativePath == "B/B.cs"), "环中另一项目仍须被展开一次。");
    }

    [TestMethod]
    public void T12_Collect_DeclaredRelevantFileChange_ChangesIdentity()
    {
        CreateMinimalProject("App");
        Directory.CreateDirectory(Path.Combine(_root, "spec"));
        File.WriteAllText(Path.Combine(_root, "spec", "acceptance.md"), "v1\n");

        var before = GoalCheckInputIdentity.Collect(_root, "App", declaredRelevantPaths: ["spec/acceptance.md"]);
        File.WriteAllText(Path.Combine(_root, "spec", "acceptance.md"), "v2\n");
        var after = GoalCheckInputIdentity.Collect(_root, "App", declaredRelevantPaths: ["spec/acceptance.md"]);

        Assert.AreNotEqual(before.Identity, after.Identity, "检查声明的相关文件变化必须使旧结论失效。");
        Assert.IsTrue(before.Entries.Any(e => e.RelativePath == "spec/acceptance.md"), "声明的相关文件必须进入清单。");
    }

    [TestMethod]
    public void T13_Collect_DeclaredButAbsentPath_BecomingPresent_ChangesIdentity()
    {
        CreateMinimalProject("App");

        var before = GoalCheckInputIdentity.Collect(_root, "App", declaredRelevantPaths: ["spec/missing.md"]);
        Assert.IsTrue(
            before.Entries.Any(e => e.RelativePath == "declared-absent:spec/missing.md"),
            "声明但缺席的路径必须留下可追踪的「缺席」条目。");

        Directory.CreateDirectory(Path.Combine(_root, "spec"));
        File.WriteAllText(Path.Combine(_root, "spec", "missing.md"), "now here\n");
        var after = GoalCheckInputIdentity.Collect(_root, "App", declaredRelevantPaths: ["spec/missing.md"]);

        Assert.AreNotEqual(before.Identity, after.Identity, "声明但缺席的路径随后出现必须改变身份（保守而不静默忽略）。");
    }

    [TestMethod]
    public void T14_Collect_BuildIdChange_ChangesIdentity_WithoutLeakingValue()
    {
        CreateMinimalProject("App");

        var a = GoalCheckInputIdentity.Collect(_root, "App", buildId: "build-aaa");
        var b = GoalCheckInputIdentity.Collect(_root, "App", buildId: "build-bbb");

        Assert.AreNotEqual(a.Identity, b.Identity, "适用环境 / BuildId 变化必须使旧结论失效。");
        Assert.IsTrue(a.Entries.Any(e => e.RelativePath == "build-id"), "BuildId 必须以虚拟条目计入清单。");
        Assert.IsFalse(
            a.Entries.Any(e => e.RelativePath.Contains("build-aaa", StringComparison.Ordinal)),
            "不得把 BuildId 原始值写进清单（只记 SHA-256）。");
    }

    [TestMethod]
    public void T15_Collect_DeclaredPathEscapesRoot_IsIgnored()
    {
        CreateMinimalProject("App");

        var plain = GoalCheckInputIdentity.Collect(_root, "App");
        var withEscape = GoalCheckInputIdentity.Collect(
            _root,
            "App",
            declaredRelevantPaths: ["../outside.md", "\\absolute.md"]);

        Assert.AreEqual(plain.Identity, withEscape.Identity, "越界或绝对的声明必须被忽略，不得进入身份。");
    }

    /// <summary>在临时仓库根下创建最小项目树：App.csproj、A.cs、Sub/B.cs，可选带一层对 Lib 的 ProjectReference。</summary>
    private string CreateMinimalProject(string projectName, bool withProjectReference = false)
    {
        var projectDir = Path.Combine(_root, projectName);
        Directory.CreateDirectory(Path.Combine(projectDir, "Sub"));

        var csprojContent = "<Project Sdk=\"Microsoft.NET.Sdk\" />\n";
        if (withProjectReference)
        {
            var libDir = Path.Combine(_root, "Lib");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(libDir, "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
            File.WriteAllText(Path.Combine(libDir, "Lib.cs"), "class Lib { }\n");
            csprojContent =
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"..\\Lib\\Lib.csproj\" />\n  </ItemGroup>\n</Project>\n";
        }

        File.WriteAllText(Path.Combine(projectDir, projectName + ".csproj"), csprojContent);
        File.WriteAllText(Path.Combine(projectDir, "A.cs"), "class A { }\n");
        File.WriteAllText(Path.Combine(projectDir, "Sub", "B.cs"), "class B { }\n");
        return projectDir;
    }

    /// <summary>创建 App → Lib → Core 的三层项目链，用于验证传递引用闭合。</summary>
    private void CreateTransitiveProjectTree()
    {
        WriteProject("App", reference: "../Lib/Lib.csproj");
        WriteProject("Lib", reference: "../Core/Core.csproj");
        WriteProject("Core", reference: null);
    }

    /// <summary>创建 A ↔ B 互相引用，用于验证环保护（必须终止）。</summary>
    private void CreateCyclicProjectTree()
    {
        WriteProject("A", reference: "../B/B.csproj");
        WriteProject("B", reference: "../A/A.csproj");
    }

    /// <summary>写一个最小项目：&lt;name&gt;.csproj（可选带一条 ProjectReference）+ &lt;name&gt;.cs。</summary>
    private void WriteProject(string name, string? reference)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var body = reference is null
            ? "<Project Sdk=\"Microsoft.NET.Sdk\" />\n"
            : "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\""
              + reference + "\" />\n  </ItemGroup>\n</Project>\n";
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), body);
        File.WriteAllText(Path.Combine(directory, name + ".cs"), "class " + name + " { }\n");
    }
}
