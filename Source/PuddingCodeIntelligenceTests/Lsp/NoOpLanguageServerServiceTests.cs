using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Lsp;

namespace PuddingCodeIntelligenceTests.Lsp;

[TestClass]
public sealed class NoOpLanguageServerServiceTests
{
    [TestMethod]
    public async Task ExecuteAsync_Returns_Unsupported_Response_With_Correlation_Id()
    {
        var service = new NoOpLanguageServerService();
        var request = new LanguageServerRequest(
            WorkspaceId: "workspace-one",
            Method: LanguageServerMethod.Definition,
            DocumentPath: "src/App.cs",
            CorrelationId: "corr-1");

        var response = await service.ExecuteAsync(request);

        Assert.IsFalse(response.IsSupported);
        Assert.AreEqual(LanguageServerMethod.Definition, response.Method);
        Assert.AreEqual("corr-1", response.CorrelationId);
    }
}
