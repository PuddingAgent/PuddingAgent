using Microsoft.VisualStudio.TestTools.UnitTesting;

using PuddingCodeIntelligence.Bicep;
using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligenceTests.Bicep;

[TestClass]
public class BicepFileOutlinerTests
{
    private readonly BicepFileOutliner _outliner = new();

    [TestMethod]
    public async Task OutlineAsync_SupportedExtensions_ReturnsBicep()
    {
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".bicep"));
        Assert.IsTrue(_outliner.SupportedExtensions.Contains(".bicepparam"));
    }

    [TestMethod]
    public async Task OutlineAsync_Parameters_Extracted()
    {
        var source = """
            param location string = resourceGroup().location
            param appName string
            param sku string = 'Standard'
            """;

        var result = await _outliner.OutlineAsync("main.bicep", source);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(3, result.Nodes.Count);
        Assert.AreEqual("location", result.Nodes[0].Name);
        Assert.AreEqual("appName", result.Nodes[1].Name);
        Assert.AreEqual("sku", result.Nodes[2].Name);
    }

    [TestMethod]
    public async Task OutlineAsync_Resource_Extracted()
    {
        var source = """
            param location string

            resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
              name: 'mystorageaccount'
              location: location
              kind: 'StorageV2'
              sku: {
                name: 'Standard_LRS'
              }
            }
            """;

        var result = await _outliner.OutlineAsync("storage.bicep", source);

        Assert.IsTrue(result.Success);
        var names = result.Nodes.Select(n => n.Name).ToList();
        Assert.IsTrue(names.Contains("location"));
        Assert.IsTrue(names.Contains("storageAccount"));
    }

    [TestMethod]
    public async Task OutlineAsync_VarsAndOutputs_Extracted()
    {
        var source = """
            param env string = 'dev'
            var prefix = 'myapp-${env}'
            var connectionString = 'Server=tcp:...'

            output resourceId string = resourceGroup().id
            output name string = prefix
            """;

        var result = await _outliner.OutlineAsync("vars.bicep", source);

        Assert.IsTrue(result.Success);
        var names = result.Nodes.Select(n => n.Name).ToList();
        Assert.IsTrue(names.Contains("env"));
        Assert.IsTrue(names.Contains("prefix"));
        Assert.IsTrue(names.Contains("connectionString"));
        Assert.IsTrue(names.Contains("resourceId"));
        Assert.IsTrue(names.Contains("name"));
    }

    [TestMethod]
    public async Task OutlineAsync_Module_Extracted()
    {
        var source = """
            module appService './modules/appService.bicep' = {
              name: 'appServiceDeploy'
              params: {
                appName: 'myapp'
              }
            }
            """;

        var result = await _outliner.OutlineAsync("main.bicep", source);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, result.Nodes.Count);
        Assert.AreEqual("appService", result.Nodes[0].Name);
    }

    [TestMethod]
    public async Task OutlineAsync_Type_Extracted()
    {
        var source = """
            type AppConfig = {
              name: string
              port: int
            }

            param config AppConfig
            """;

        var result = await _outliner.OutlineAsync("types.bicep", source);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Nodes.Any(n => n.Name == "AppConfig"));
        Assert.IsTrue(result.Nodes.Any(n => n.Name == "config"));
    }

    [TestMethod]
    public async Task OutlineAsync_ComplexFile()
    {
        var source = """
            targetScope = 'resourceGroup'

            param location string = resourceGroup().location
            param env string = 'prod'

            var tags = {
              environment: env
              managedBy: 'bicep'
            }

            resource vnet 'Microsoft.Network/virtualNetworks@2023-05-01' = {
              name: 'myVnet'
              location: location
              tags: tags
            }

            module app './modules/app.bicep' = {
              name: 'appDeploy'
              params: {
                location: location
              }
            }

            output vnetId string = vnet.id
            """;

        var result = await _outliner.OutlineAsync("main.bicep", source);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Nodes.Count >= 6);
    }

    [TestMethod]
    public async Task OutlineAsync_EmptySource_ReturnsEmpty()
    {
        var result = await _outliner.OutlineAsync("empty.bicep", "");

        Assert.IsTrue(result.Success);
        Assert.AreEqual(0, result.Nodes.Count);
    }
}
