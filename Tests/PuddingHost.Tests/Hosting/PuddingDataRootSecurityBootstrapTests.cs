using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using PuddingCode.Configuration;
using PuddingHost.Hosting;
using PuddingPlatform.Services;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// 引导期 JWT 配置落盘契约：<c>&lt;DataRoot&gt;/config/security.json</c> 是登录态 JWT
/// 密钥与有效期的唯一配置来源（代码已无硬编码密钥兜底），文件缺失/占位/过短时由引导期生成。
/// </summary>
public sealed class PuddingDataRootSecurityBootstrapTests
{
    [Fact]
    public void Bootstrap_CreatesSecurityJsonWithGeneratedKeyAndSevenDayExpiry()
    {
        var dataRoot = CreateTempDataRoot();
        try
        {
            var paths = PuddingDataRootBootstrapper.Bootstrap(dataRoot);
            var securityPath = paths.SystemConfigFile("security.json");

            Assert.True(File.Exists(securityPath), $"未生成 security.json：{securityPath}");

            var root = JsonNode.Parse(File.ReadAllText(securityPath))!.AsObject();
            var jwt = root["jwt"]!.AsObject();
            var key = jwt["key"]!.GetValue<string>();

            Assert.True(key.Length >= 32, $"生成的密钥过短：{key.Length}");
            Assert.Equal(168, jwt["expiryHours"]!.GetValue<int>());
            Assert.Equal("pudding-platform", jwt["issuer"]!.GetValue<string>());
            Assert.Equal("pudding-admin", jwt["audience"]!.GetValue<string>());

            // 幂等：再次引导不得换密钥（换密钥会让所有已签发令牌与登录态失效）。
            PuddingDataRootBootstrapper.Bootstrap(dataRoot);
            var secondKey = JsonNode.Parse(File.ReadAllText(securityPath))!
                .AsObject()["jwt"]!.AsObject()["key"]!.GetValue<string>();

            Assert.Equal(key, secondKey);
        }
        finally
        {
            DeleteTempDataRoot(dataRoot);
        }
    }

    [Fact]
    public void Bootstrap_ReplacesShippedPlaceholderKey_PreservingOtherFields()
    {
        var dataRoot = CreateTempDataRoot();
        try
        {
            var configDir = Path.Combine(dataRoot, "config");
            Directory.CreateDirectory(configDir);
            var securityPath = Path.Combine(configDir, "security.json");
            File.WriteAllText(securityPath, """
            {
              "jwt": {
                "issuer": "custom-issuer",
                "audience": "pudding-admin",
                "expiryHours": 8,
                "key": "local-dev-key-change-me-32plus"
              },
              "keyVault": {
                "mode": "local-file",
                "masterKeyRef": "local"
              }
            }
            """);

            PuddingDataRootBootstrapper.Bootstrap(dataRoot);

            var root = JsonNode.Parse(File.ReadAllText(securityPath))!.AsObject();
            var jwt = root["jwt"]!.AsObject();

            Assert.NotEqual("local-dev-key-change-me-32plus", jwt["key"]!.GetValue<string>());
            Assert.True(jwt["key"]!.GetValue<string>().Length >= 32);
            // 只补密钥，其余字段原样保留（含用户显式配置的小时数）。
            Assert.Equal(8, jwt["expiryHours"]!.GetValue<int>());
            Assert.Equal("custom-issuer", jwt["issuer"]!.GetValue<string>());
            Assert.Equal("local-file", root["keyVault"]!.AsObject()["mode"]!.GetValue<string>());
        }
        finally
        {
            DeleteTempDataRoot(dataRoot);
        }
    }

    [Fact]
    public void Bootstrap_InvalidSecurityJson_FailsClosed()
    {
        var dataRoot = CreateTempDataRoot();
        try
        {
            var configDir = Path.Combine(dataRoot, "config");
            Directory.CreateDirectory(configDir);
            File.WriteAllText(Path.Combine(configDir, "security.json"), "{ not-json ");

            var error = Assert.Throws<InvalidOperationException>(
                () => PuddingDataRootBootstrapper.Bootstrap(dataRoot));

            Assert.Contains("security.json", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDataRoot(dataRoot);
        }
    }

    /// <summary>
    /// 端到端接线断言：引导期落盘的 security.json 经配置链解析后，就是
    /// <see cref="PuddingJwtSettings"/> 的密钥与有效期来源（此前该文件从未进入配置链）。
    /// </summary>
    [Fact]
    public void BootstrappedSecurityJson_IsTheJwtConfigurationSource()
    {
        var dataRoot = CreateTempDataRoot();
        try
        {
            var paths = PuddingDataRootBootstrapper.Bootstrap(dataRoot);
            var securityPath = paths.SystemConfigFile("security.json");
            var fileKey = JsonNode.Parse(File.ReadAllText(securityPath))!
                .AsObject()["jwt"]!.AsObject()["key"]!.GetValue<string>();

            var configuration = new ConfigurationBuilder()
                .AddJsonFile(securityPath, optional: false, reloadOnChange: false)
                .Build();

            var settings = PuddingJwtSettings.FromConfiguration(configuration);

            Assert.Equal(fileKey, settings.Key);
            Assert.Equal(168, settings.ExpiryHours);
            Assert.Equal("pudding-platform", settings.Issuer);
            Assert.Equal("pudding-admin", settings.Audience);
        }
        finally
        {
            DeleteTempDataRoot(dataRoot);
        }
    }

    private static string CreateTempDataRoot()
        => Path.Combine(Path.GetTempPath(), "PuddingAgent", $"security-bootstrap-{Guid.NewGuid():N}");

    private static void DeleteTempDataRoot(string dataRoot)
    {
        try
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
