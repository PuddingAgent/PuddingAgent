using PuddingCode.Diagnostics;

namespace PuddingDiagnosticsTests;

/// <summary>证据预算与脱敏门禁（可诊断基础设施设计 §5.4）。</summary>
[TestClass]
public sealed class DiagnosticEvidenceTests
{
    [TestMethod]
    public void SensitiveKeys_AreRedacted_BySubstringNotExactMatch()
    {
        // 现状实测缺口：既有 DiagnosticRedactor 用「键名整串相等」，llm.apiKey 之类会穿透。
        foreach (var key in new[] { "apiKey", "api_key", "llm.apiKey", "authorization_header", "myToken", "client_secret" })
        {
            Assert.IsTrue(DiagnosticEvidenceRedactor.ShouldRedactKey(key), $"{key} 必须被判定为敏感键");
            Assert.AreEqual(DiagnosticEvidenceRedactor.RedactedValue, DiagnosticEvidenceRedactor.Sanitize(key, "whatever"));
        }

        foreach (var key in new[] { "provider_id", "model_id", "request_bytes", "phase" })
            Assert.IsFalse(DiagnosticEvidenceRedactor.ShouldRedactKey(key), $"{key} 不应被判定为敏感键");
    }

    [TestMethod]
    public void SecretShapedValues_AreRedacted_EvenUnderInnocentKeys()
    {
        // 夹具用一眼可辨的假值（形如 sk-…），避免与真实密钥混淆。
        Assert.AreEqual(DiagnosticEvidenceRedactor.RedactedValue, DiagnosticEvidenceRedactor.Sanitize("note", "sk-FIXTURE-not-a-real-key"));
        Assert.AreEqual(DiagnosticEvidenceRedactor.RedactedValue, DiagnosticEvidenceRedactor.Sanitize("note", "Bearer FIXTURE.not-a-real.token"));
        Assert.IsFalse(DiagnosticEvidenceRedactor.LooksLikeSecret("deepseek-flash"));
    }

    [TestMethod]
    public void UrlQueryStrings_AreStripped()
    {
        var sanitized = DiagnosticEvidenceRedactor.Sanitize("endpoint", "https://api.example.com/v1/responses?q=FIXTUREVALUE&x=1");

        Assert.AreEqual("https://api.example.com/v1/responses?<redacted>", sanitized);
        StringAssert.DoesNotMatch(sanitized, new System.Text.RegularExpressions.Regex("FIXTUREVALUE"));
    }

    [TestMethod]
    public void Value_IsTruncatedAtTheDocumentedLimit()
    {
        var value = new string('x', DiagnosticEvidenceBuilder.MaxValueChars + 50);

        var sanitized = DiagnosticEvidenceRedactor.Sanitize("message", value);

        Assert.AreEqual(DiagnosticEvidenceBuilder.MaxValueChars + DiagnosticEvidenceRedactor.TruncatedSuffix.Length, sanitized.Length);
        StringAssert.EndsWith(sanitized, DiagnosticEvidenceRedactor.TruncatedSuffix);
    }

    [TestMethod]
    public void Builder_StopsAtTheKeyBudget_AndSaysSoInsteadOfSilentlyDropping()
    {
        var builder = new DiagnosticEvidenceBuilder();

        for (var index = 0; index < DiagnosticEvidenceBuilder.MaxKeys + 10; index++)
            builder.Add($"key_{index:00}", "v");

        var evidence = builder.Build();

        Assert.IsTrue(builder.IsTruncated);
        Assert.AreEqual("true", evidence[DiagnosticEvidenceKeys.EvidenceTruncated]);
        Assert.HasCount(DiagnosticEvidenceBuilder.MaxKeys + 1, evidence);
    }

    [TestMethod]
    public void Builder_FirstValueWins_AndOutputOrderIsDeterministic()
    {
        var builder = new DiagnosticEvidenceBuilder();
        builder.Add("b", "first").Add("a", "1").Add("b", "second");

        var evidence = builder.Build();

        Assert.AreEqual("first", evidence["b"]);
        CollectionAssert.AreEqual(new[] { "a", "b" }, evidence.Keys.ToArray());
    }

    [TestMethod]
    public void Builder_NeverEmitsRedactedContentThroughAnyPath()
    {
        var builder = new DiagnosticEvidenceBuilder();
        builder
            .Add(DiagnosticEvidenceKeys.ProviderId, "deepseek")
            .Add("authorization", "Bearer sk-FIXTURE-not-a-real-key")
            .Add("endpoint", "https://api.example.com/v1?q=sk-FIXTURE-not-a-real-key");

        var evidence = builder.Build();
        var joined = string.Join("|", evidence.Values);

        StringAssert.DoesNotMatch(joined, new System.Text.RegularExpressions.Regex("sk-FIXTURE"));
        Assert.AreEqual(DiagnosticEvidenceRedactor.RedactedValue, evidence["authorization"]);
    }
}
