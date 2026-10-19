using PuddingHomeAssistant.Contracts;
using PuddingHomeAssistant.Policy;

namespace PuddingHomeAssistantTests;

[TestClass]
public sealed class HaDomainPolicyTests
{
    private static HaInstance Instance(params string[] optIn) => new()
    {
        Alias = "test",
        BaseUrl = new Uri("http://127.0.0.1:8123/"),
        Token = "token",
        OptInDomains = new HashSet<string>(optIn, StringComparer.Ordinal),
    };

    [DataTestMethod]
    [DataRow("shell_command")]
    [DataRow("hassio")]
    [DataRow("homeassistant")]
    [DataRow("script")]
    [DataRow("automation")]
    [DataRow("python_script")]
    [DataRow("lock")]
    [DataRow("alarm_control_panel")]
    [DataRow("camera")]
    [DataRow("rest_command")]
    public void PermanentlyBlockedDomains_AreBlocked_EvenWhenOptedIn(string domain)
    {
        // 关键：即使用户把它写进 OptInDomains，也必须仍然被拒绝。
        var instance = Instance(domain);
        Assert.AreEqual(HaDomainDecision.Blocked, HaDomainPolicy.Decide(domain, instance), domain);
    }

    [DataTestMethod]
    [DataRow("light")]
    [DataRow("switch")]
    [DataRow("scene")]
    [DataRow("input_boolean")]
    [DataRow("notify")]
    [DataRow("tts")]
    [DataRow("button")]
    public void DefaultAllowedDomains_NeedNoOptIn(string domain)
    {
        Assert.AreEqual(HaDomainDecision.Allowed, HaDomainPolicy.Decide(domain, Instance()), domain);
    }

    [DataTestMethod]
    [DataRow("media_player")]
    [DataRow("fan")]
    [DataRow("climate")]
    [DataRow("cover")]
    [DataRow("vacuum")]
    [DataRow("water_heater")]
    public void OptInDomains_AreRejectedUntilEnabled(string domain)
    {
        Assert.AreEqual(HaDomainDecision.OptInRequired, HaDomainPolicy.Decide(domain, Instance()), domain);
        Assert.AreEqual(HaDomainDecision.Allowed, HaDomainPolicy.Decide(domain, Instance(domain)), domain);
    }

    [TestMethod]
    public void UnknownDomain_FailsClosed()
    {
        Assert.AreEqual(HaDomainDecision.OptInRequired, HaDomainPolicy.Decide("brand_new_integration", Instance()));
        Assert.AreEqual(HaDomainDecision.Allowed, HaDomainPolicy.Decide("brand_new_integration", Instance("brand_new_integration")));
    }

    [DataTestMethod]
    [DataRow("light/../shell_command")]
    [DataRow("light.turn_on")]
    [DataRow("light turn_on")]
    [DataRow("light:on")]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    public void MalformedDomain_IsBlocked(string? domain)
    {
        Assert.AreEqual(HaDomainDecision.Blocked, HaDomainPolicy.Decide(domain, Instance()));
    }

    [DataTestMethod]
    [DataRow("LIGHT", "light")]
    [DataRow("  Switch  ", "switch")]
    [DataRow("input_boolean", "input_boolean")]
    public void Normalize_LowercasesAndTrims(string input, string expected)
    {
        Assert.IsTrue(HaDomainPolicy.TryNormalize(input, out var normalized));
        Assert.AreEqual(expected, normalized);
    }

    [TestMethod]
    public void OptInCandidates_DoNotOverlapWithBlockedOrDefault()
    {
        var blocked = HaDomainPolicy.AlwaysBlockedDomains().ToHashSet(StringComparer.Ordinal);
        var allowed = HaDomainPolicy.DefaultAllowedDomains().ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in HaDomainPolicy.OptInCandidateDomains())
        {
            Assert.IsFalse(blocked.Contains(candidate), "opt-in 与黑名单重叠: " + candidate);
            Assert.IsFalse(allowed.Contains(candidate), "opt-in 与默认允许重叠: " + candidate);
        }
    }
}
