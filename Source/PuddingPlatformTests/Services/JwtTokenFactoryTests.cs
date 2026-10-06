using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 登录态 JWT 的配置解析与签发口径：密钥必须来自配置（无硬编码兜底）、
/// 有效期为 7 天（168 小时）、签发统一走 <see cref="JwtTokenFactory"/>。
/// </summary>
[TestClass]
public sealed class JwtTokenFactoryTests
{
    private const string ValidKey = "pudding-test-jwt-signing-key-0123456789abcdef";

    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
            values[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static Sm2JwtSigner CreateSigner()
        => new(Config(), NullLogger<Sm2JwtSigner>.Instance);

    [TestMethod]
    public void FromConfiguration_WithoutKey_FailsClosed()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => PuddingJwtSettings.FromConfiguration(Config(("Jwt:Issuer", "pudding-platform"))));

        StringAssert.Contains(error.Message, "Jwt:Key");
        StringAssert.Contains(error.Message, "security.json");
        StringAssert.Contains(error.Message, "硬编码");
    }

    [TestMethod]
    public void FromConfiguration_TooShortKey_FailsClosed()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => PuddingJwtSettings.FromConfiguration(Config(("Jwt:Key", "short-key"))));

        StringAssert.Contains(error.Message, "过短");
    }

    [TestMethod]
    public void FromConfiguration_DefaultsExpiryToSevenDays()
    {
        var settings = PuddingJwtSettings.FromConfiguration(Config(("Jwt:Key", ValidKey)));

        Assert.AreEqual(168, PuddingJwtSettings.DefaultExpiryHours);
        Assert.AreEqual(168, settings.ExpiryHours);
        Assert.AreEqual("pudding-platform", settings.Issuer);
        Assert.AreEqual("pudding-admin", settings.Audience);
        Assert.AreEqual(ValidKey, settings.Key);
    }

    [TestMethod]
    public void FromConfiguration_ExplicitExpiryHours_Wins()
    {
        var settings = PuddingJwtSettings.FromConfiguration(
            Config(("Jwt:Key", ValidKey), ("Jwt:ExpiryHours", "24")));

        Assert.AreEqual(24, settings.ExpiryHours);
    }

    [TestMethod]
    public void FromConfiguration_NonPositiveOrNonNumericExpiryHours_FailsClosed()
    {
        foreach (var raw in new[] { "0", "-5", "seven-days" })
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(
                () => PuddingJwtSettings.FromConfiguration(
                    Config(("Jwt:Key", ValidKey), ("Jwt:ExpiryHours", raw))));

            StringAssert.Contains(error.Message, "Jwt:ExpiryHours");
        }
    }

    [TestMethod]
    public void CreateToken_UsesConfiguredIssuerAudienceAndSevenDayLifetime()
    {
        var settings = new PuddingJwtSettings(ValidKey, "pudding-platform", "pudding-admin", 168);
        var before = DateTime.UtcNow;

        var token = JwtTokenFactory.CreateToken(
            settings,
            CreateSigner(),
            userId: "admin",
            displayName: "管理员",
            email: "admin@pudding.local",
            authority: "admin");

        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.AreEqual("pudding-platform", parsed.Issuer);
        Assert.IsTrue(parsed.Audiences.Contains("pudding-admin"));
        // 未设置 nbf，故 ValidFrom 为 DateTime.MinValue：以签发时刻为基准核对有效期。
        Assert.AreEqual(
            168d,
            Math.Round((parsed.ValidTo - before).TotalHours, 3),
            0.01,
            "登录态 JWT 有效期应为 168 小时（7 天）");
        Assert.IsTrue(parsed.Claims.Any(c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == "admin"));
        Assert.IsTrue(parsed.Claims.Any(c => c.Type.EndsWith("/role", StringComparison.Ordinal) && c.Value == "admin"));
        Assert.IsTrue(parsed.Claims.Any(c => c.Type == "sm2_sig" && c.Value.Length > 0));
    }
}
