using System.Collections.Immutable;
using Pudding.ToolPlugins.AgentSdk;

namespace Pudding.ToolPlugins.AgentSdkTests;

/// <summary>PA-07 07B plugin extension contract: declaration bounds and explicit transform/guard result forms.</summary>
[TestClass]
public sealed class PluginExtensionContractTests
{
    [TestMethod]
    public void Declarations_RequireBoundedIdentityPointPriorityAndVersion()
    {
        Assert.IsTrue(new PluginExtensionDeclaration("ext-a", "tool.result", PluginExtensionKind.Transform, 10, "1.0",
            false).IsValid);
        Assert.IsFalse(new PluginExtensionDeclaration("", "tool.result", PluginExtensionKind.Observer, 0, "1.0", false)
            .IsValid);
        Assert.IsFalse(new PluginExtensionDeclaration("ext-a", "tool.result", PluginExtensionKind.Observer, 2000, "1.0",
            false).IsValid);
        Assert.IsFalse(new PluginExtensionDeclaration("ext-a", "tool.result", PluginExtensionKind.Observer, 0, " ",
            false).IsValid);
    }

    [TestMethod]
    public void TransformAndGuardResults_HaveExplicitSuccessFailureForms()
    {
        var fields = ImmutableDictionary<string, string>.Empty.Add("text", "rewritten");
        Assert.IsTrue(new PluginTransformResult(true, fields, null).IsValid);
        Assert.IsFalse(new PluginTransformResult(true, fields, "check.failed").IsValid);
        Assert.IsFalse(new PluginTransformResult(false, fields, null).IsValid);
        Assert.IsTrue(new PluginTransformResult(false, fields, "plugin.rejected").IsValid);

        Assert.IsTrue(new PluginGuardDecision(true, null).IsValid);
        Assert.IsFalse(new PluginGuardDecision(true, "deny").IsValid);
        Assert.IsTrue(new PluginGuardDecision(false, "policy.denied").IsValid);
        Assert.IsFalse(new PluginGuardDecision(false, null).IsValid);
    }
}
