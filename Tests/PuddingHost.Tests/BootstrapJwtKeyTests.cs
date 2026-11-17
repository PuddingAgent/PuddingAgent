using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PuddingCode.Configuration;
using PuddingHost.Hosting;
using PuddingSecretReferences;

namespace PuddingHost.Tests;

/// <summary>
/// 引导期 JWT 签名密钥的落盘形态：<b>配置文件里只能存引用，值只能存在启动前凭据缓存里</b>。
/// </summary>
/// <remarks>
/// 为什么值得单独锁一条：同一把密钥曾经同时存在两份（security.json 明文 + 引导缓存），
/// 两份不一致时，把配置改成引用会静默换掉签名密钥，让所有已签发管理员会话立即失效。
/// 下面四条分别对应这条不变量的四种起点：明文、缓存陈旧、已是引用、缺失/过短。
/// </remarks>
public sealed class BootstrapJwtKeyTests
{
    private const string FileKeyPath = "config/security.json";

    private static readonly string Reference =
        "{{vault:" + PuddingDataRootBootstrapper.JwtKeySecretName + "}}";

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-jwtkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        return root;
    }

    private static void WriteSecurity(string root, string key)
    {
        var document = new JsonObject
        {
            ["jwt"] = new JsonObject
            {
                ["issuer"] = "pudding-platform",
                ["audience"] = "pudding-admin",
                ["expiryHours"] = 168,
                ["key"] = key,
            },
        };

        File.WriteAllText(
            Path.Combine(root, FileKeyPath.Replace('/', Path.DirectorySeparatorChar)),
            document.ToJsonString());
    }

    private static string ReadSecurityKey(string root) =>
        (JsonNode.Parse(File.ReadAllText(
            Path.Combine(root, FileKeyPath.Replace('/', Path.DirectorySeparatorChar))))!["jwt"]!["key"]!)
        .GetValue<string>();

    private static string RandomKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    /// <summary>明文密钥必须被搬进引导缓存，配置文件改为引用。</summary>
    [Fact]
    public void PlaintextKey_IsMovedIntoBootstrapCache_AndReplacedByReference()
    {
        var root = NewTempRoot();
        var key = RandomKey();
        WriteSecurity(root, key);

        PuddingDataRootBootstrapper.EnsureSecurityJwtKey(PuddingDataPaths.FromRoot(root));

        var persisted = ReadSecurityKey(root);
        Assert.Equal(Reference, persisted);

        var resolved = BootstrapSecretFile.Resolve(root, persisted);
        Assert.Equal(key, resolved);
    }

    /// <summary>
    /// 生产上真实出现过的状态：缓存里躺着另一个更早的值。对齐必须以<b>配置文件</b>为准，
    /// 否则一旦切到引用就会把生效密钥换回旧值。
    /// </summary>
    [Fact]
    public void StaleBootstrapCacheEntry_IsRealignedToTheFileValue()
    {
        var root = NewTempRoot();
        var liveKey = RandomKey();
        var staleKey = RandomKey();
        WriteSecurity(root, liveKey);
        BootstrapSecretFile.Store(root, PuddingDataRootBootstrapper.JwtKeySecretName, staleKey);

        Assert.NotEqual(liveKey, BootstrapSecretFile.Resolve(root, Reference));

        PuddingDataRootBootstrapper.EnsureSecurityJwtKey(PuddingDataPaths.FromRoot(root));

        var persisted = ReadSecurityKey(root);
        Assert.Equal(Reference, persisted);
        Assert.Equal(liveKey, BootstrapSecretFile.Resolve(root, persisted));
    }

    /// <summary>已经是「引用 + 可解析」就别再碰文件：避免每次启动都改一次配置。</summary>
    [Fact]
    public void ExistingResolvableReference_LeavesTheFileByteIdentical()
    {
        var root = NewTempRoot();
        var key = RandomKey();
        WriteSecurity(root, Reference);
        BootstrapSecretFile.Store(root, PuddingDataRootBootstrapper.JwtKeySecretName, key);

        var path = Path.Combine(root, FileKeyPath.Replace('/', Path.DirectorySeparatorChar));
        var before = File.ReadAllBytes(path);

        PuddingDataRootBootstrapper.EnsureSecurityJwtKey(PuddingDataPaths.FromRoot(root));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(key, BootstrapSecretFile.Resolve(root, ReadSecurityKey(root)));
    }

    /// <summary>缺失/过短时生成新密钥，同样只落引用，并且其余字段原样保留。</summary>
    [Fact]
    public void MissingKey_GeneratesAKeyThatIsStoredInTheCacheNotInTheFile()
    {
        var root = NewTempRoot();
        WriteSecurity(root, string.Empty);

        PuddingDataRootBootstrapper.EnsureSecurityJwtKey(PuddingDataPaths.FromRoot(root));

        var persisted = ReadSecurityKey(root);
        Assert.Equal(Reference, persisted);

        var generated = BootstrapSecretFile.Resolve(root, persisted);
        Assert.True(
            PuddingDataRootBootstrapper.IsUsableJwtKey(generated),
            "生成的密钥必须通过可用性判定（长度 + 非占位符）");

        var document = JsonNode.Parse(File.ReadAllText(
            Path.Combine(root, FileKeyPath.Replace('/', Path.DirectorySeparatorChar))))!;
        Assert.Equal("pudding-platform", document["jwt"]!["issuer"]!.GetValue<string>());
        Assert.Equal(168, document["jwt"]!["expiryHours"]!.GetValue<int>());
    }
}
