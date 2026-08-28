using PuddingCode.Configuration;

namespace PuddingCoreTests.Configuration;

[TestClass]
public sealed class PuddingDesktopBootstrapConfigTests
{
    [TestMethod]
    public void Defaults_Match_ManualApiTriggerDesign()
    {
        var config = new PuddingDesktopBootstrapConfig();

        // Polling is opt-in now; the HTTP endpoint is the primary trigger.
        Assert.IsFalse(config.Enabled);
        Assert.IsTrue(config.HttpEnabled);
        Assert.AreEqual(8199, config.HttpPort);
        Assert.AreEqual("desktop-build", config.DefaultDeploymentMode);
        Assert.IsNull(config.BuildProjectPath);

        // Existing defaults must stay untouched.
        Assert.AreEqual("Source/PuddingAgent/PuddingAgent.csproj", config.BuildProjectRelativePath);
        Assert.IsTrue(config.AutoYolo);
        Assert.AreEqual(300, config.BuildTimeoutSeconds);
        Assert.IsNull(config.SignalPath);
        Assert.AreEqual(string.Empty, config.BuildArguments);
    }

    [TestMethod]
    public void SystemJson_CamelCase_Binds_NewFields()
    {
        // Mirrors how SystemConfigurationService.LoadAsync deserializes
        // system.json → desktop.bootstrap (case-insensitive web defaults).
        const string json = """
            {
              "desktop": {
                "bootstrap": {
                  "enabled": true,
                  "httpEnabled": false,
                  "httpPort": 9001,
                  "defaultDeploymentMode": "prebuilt-artifact",
                  "buildProjectPath": "D:\\repos\\PuddingAgent\\Source\\PuddingAgent\\PuddingAgent.csproj"
                  }
              }
            }
            """;

        var system = System.Text.Json.JsonSerializer.Deserialize<PuddingSystemConfig>(
            json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.IsNotNull(system);
        var bootstrap = system.Desktop.Bootstrap;
        Assert.IsTrue(bootstrap.Enabled);
        Assert.IsFalse(bootstrap.HttpEnabled);
        Assert.AreEqual(9001, bootstrap.HttpPort);
        Assert.AreEqual("prebuilt-artifact", bootstrap.DefaultDeploymentMode);
        Assert.AreEqual("D:\\repos\\PuddingAgent\\Source\\PuddingAgent\\PuddingAgent.csproj", bootstrap.BuildProjectPath);
        Assert.AreEqual("Source/PuddingAgent/PuddingAgent.csproj", bootstrap.BuildProjectRelativePath);
    }
}
