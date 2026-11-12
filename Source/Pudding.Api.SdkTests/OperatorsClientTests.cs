using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;

namespace Pudding.Api.SdkTests;

[TestClass]
public sealed class OperatorsClientTests
{
    [TestMethod]
    public async Task TypedScorer_PreservesInputAndExplicitOptions_WithoutRetry()
    {
        var api = new Fake(); var client = new OperatorsClient(api);
        var input = new SkillValueInput(" 中文技能 ", 2, 3, 4096);
        var options = new OperatorCallOptions("call-id", 250);
        var error = await Assert.ThrowsExactlyAsync<OperatorApiException>(() => client.SkillValue.ScoreAsync(input, options));
        Assert.AreEqual("denied", error.Code); Assert.AreEqual(1, api.Calls);
        Assert.AreEqual(SkillValueApi.SceneKey, api.Request!.SceneKey); Assert.AreEqual(SkillValueApi.InputSchema, api.Request.InputSchema);
        Assert.AreEqual(options, api.Request.Options);
        Assert.AreEqual(input, api.Request.Input.Deserialize<SkillValueInput>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    private sealed class Fake : IOperatorApi
    {
        public int Calls; public OperatorScoreRequest? Request;
        public Task<OperatorCatalog> ListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<OperatorScoreReceipt> ScoreAsync(OperatorScoreRequest request, CancellationToken ct)
        { Calls++; Request = request; throw new OperatorApiException("denied", "test"); }
    }
}
