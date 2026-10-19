using PuddingHomeAssistant.Diagnostics;

namespace PuddingHomeAssistantTests;

[TestClass]
public sealed class HaErrorCodesTests
{
    [DataTestMethod]
    [DataRow(400, HaErrorCodes.InvalidRequest)]
    [DataRow(401, HaErrorCodes.AuthFailed)]
    [DataRow(403, HaErrorCodes.Forbidden)]
    [DataRow(404, HaErrorCodes.NotFound)]
    [DataRow(405, HaErrorCodes.InvalidRequest)]
    [DataRow(429, HaErrorCodes.RateLimited)]
    [DataRow(500, HaErrorCodes.ServiceError)]
    [DataRow(502, HaErrorCodes.Unavailable)]
    [DataRow(503, HaErrorCodes.Unavailable)]
    [DataRow(504, HaErrorCodes.Unavailable)]
    [DataRow(418, HaErrorCodes.ServiceError)]
    [DataRow(0, HaErrorCodes.ServiceError)]
    public void FromHttpStatus_MapsToStableCodes(int status, string expected)
    {
        Assert.AreEqual(expected, HaErrorCodes.FromHttpStatus(status));
    }

    [DataTestMethod]
    [DataRow("unauthorized", HaErrorCodes.AuthFailed)]
    [DataRow("not_allowed", HaErrorCodes.Forbidden)]
    [DataRow("not_found", HaErrorCodes.NotFound)]
    [DataRow("invalid_format", HaErrorCodes.InvalidRequest)]
    [DataRow("service_validation_error", HaErrorCodes.InvalidRequest)]
    [DataRow("timeout", HaErrorCodes.Timeout)]
    [DataRow("something_new", HaErrorCodes.ServiceError)]
    [DataRow(null, HaErrorCodes.ServiceError)]
    public void FromWebSocketCode_MapsToStableCodes(string? code, string expected)
    {
        Assert.AreEqual(expected, HaErrorCodes.FromWebSocketCode(code));
    }

    [TestMethod]
    public void AllCodes_AreNamespacedAndUnique()
    {
        var codes = typeof(HaErrorCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.AreEqual(codes.Length, codes.Distinct().Count(), "错误码存在重复");
        foreach (var code in codes)
        {
            Assert.IsTrue(code.StartsWith("ha.", StringComparison.Ordinal), "错误码缺少 ha. 前缀: " + code);
        }
    }
}
