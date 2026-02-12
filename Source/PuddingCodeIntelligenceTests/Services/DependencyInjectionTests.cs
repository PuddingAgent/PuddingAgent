using Microsoft.Extensions.DependencyInjection;
using PuddingCodeIntelligence;
using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligenceTests.Services;

[TestClass]
public sealed class DependencyInjectionTests
{
    [TestMethod]
    public void AddPuddingCodeIntelligence_Registers_Core_Services_Without_Registering_Store()
    {
        using var fixture = CodeIntelligenceFixture.Create();
        var services = new ServiceCollection();
        services.AddSingleton<ICodeIndexStore>(fixture.Store);

        services.AddPuddingCodeIntelligence();

        using var provider = services.BuildServiceProvider();
        Assert.IsNotNull(provider.GetRequiredService<ICodeProjectRegistry>());
        Assert.IsNotNull(provider.GetRequiredService<ICodeWorkspaceResolver>());
        Assert.IsNotNull(provider.GetRequiredService<ICodeQueryService>());
        Assert.IsNotNull(provider.GetRequiredService<ILanguageServerService>());
    }

    [TestMethod]
    public void AddPuddingCodeIntelligence_Registers_All_File_Outliners()
    {
        using var fixture = CodeIntelligenceFixture.Create();
        var services = new ServiceCollection();
        services.AddSingleton<ICodeIndexStore>(fixture.Store);

        services.AddPuddingCodeIntelligence();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IFileOutlinerRegistry>();

        Assert.IsTrue(registry.IsSupported("component.tsx"));
        Assert.IsTrue(registry.IsSupported("README.md"));
        Assert.IsTrue(registry.IsSupported("deploy.ps1"));
        Assert.IsTrue(registry.IsSupported("main.cpp"));
        Assert.IsTrue(registry.IsSupported("renderer.hpp"));
        Assert.IsTrue(registry.IsSupported("worker.py"));
    }
}
