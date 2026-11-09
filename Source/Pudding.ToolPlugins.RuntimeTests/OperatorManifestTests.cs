using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;

namespace Pudding.ToolPlugins.RuntimeTests;

[TestClass]
public sealed class OperatorManifestTests
{
    [TestMethod]
    public void ModelCapabilitiesRequireCore13_AndCannotGrantArbitraryModelOrWorkAccess()
    {
        var manifest = new PluginManifest { Id = "sample.model", Name = "Model", Version = "1.0.0", CoreApi = "1.3", Entry = new("plugin.dll", "Fixture"),
            RequestedCapabilities = ["operators.model.list", "operators.text.classify", "operators.delivery.review"],
            Tools = [new() { Id = "evaluate", Name = "evaluate", Description = "Evaluate", InputSchema = PluginJson.Value(new { type = "object" }), OutputSchema = PluginJson.Value(new { type = "object" }) }] };
        PluginValidation.Manifest(manifest);
        foreach (var version in new[] { "1.0", "1.1", "1.2" }) Assert.ThrowsExactly<PluginException>(() => PluginValidation.Manifest(manifest with { CoreApi = version }));
        foreach (var capability in new[] { "model.invoke", "agent.work.submit", "operators.override", "execution.create" })
            Assert.ThrowsExactly<PluginException>(() => PluginValidation.Manifest(manifest with { RequestedCapabilities = [capability] }));
    }
    [TestMethod]
    public void OperatorCapabilitiesRequireCore12_AndDoNotPermitModelsOrOverrides()
    {
        var manifest = new PluginManifest { Id = "sample.op", Name = "Operator", Version = "1.0.0", CoreApi = "1.2", Entry = new("plugin.dll", "Fixture"),
            RequestedCapabilities = ["operators.list", "operators.skill-value.score"],
            Tools = [new() { Id = "score", Name = "score", Description = "Score", InputSchema = PluginJson.Value(new { type = "object" }), OutputSchema = PluginJson.Value(new { type = "object" }) }] };
        PluginValidation.Manifest(manifest);
        foreach (var version in new[] { "1.0", "1.1" }) Assert.ThrowsExactly<PluginException>(() => PluginValidation.Manifest(manifest with { CoreApi = version }));
        foreach (var capability in new[] { "operators.classify", "operators.override", "model.invoke", "agent.work.submit" })
            Assert.ThrowsExactly<PluginException>(() => PluginValidation.Manifest(manifest with { RequestedCapabilities = [capability] }));
    }
}
