using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PuddingAgent.Services;
using PuddingCode.Configuration;

namespace PuddingWebApiTests;

/// <summary>
/// 需求 3 配置界面的服务端出口：<see cref="SubAgentDelegationSettingsStore"/> 与
/// <c>SubAgentDelegationAdminController</c> 的契约测试。
///
/// <para>
/// 这里要证明三件事，缺一不可：
/// ① 校验能取红（非法组合被拒，且**一个字节都不写**）；
/// ② 写入只动 <c>SubAgentDelegation</c> 段，同文件的其它键原样保留；
/// ③ 写入后**配置热重载真的把新值读出来** —— 这条决定界面要不要提示「重启后生效」。
/// </para>
/// <para>
/// 第 ③ 条用真实的 <c>AddJsonFile(..., reloadOnChange: true)</c> 配置根，
/// 而不是调用 <c>Reload()</c> 手动刷新：手动刷新会绕过 watcher，证明不了生产路径。
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SubAgentDelegationSettingsStoreTests
{
    private string _dataRoot = null!;
    private string _configFile = null!;

    [TestInitialize]
    public void TestInit()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "pudding-subagent-settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dataRoot, "config"));
        _configFile = Path.Combine(_dataRoot, "config", "system.json");
    }

    [TestCleanup]
    public void TestCleanup()
    {
        try
        {
            if (Directory.Exists(_dataRoot))
                Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响断言结论。
        }
    }

    /// <summary>
    /// 校验矩阵：每条非法输入都必须被拒。若 <see cref="SubAgentDelegationSettingsStore.Validate"/>
    /// 的任一分支被删掉（变异），对应的 <c>Assert</c> 会先红。
    /// </summary>
    [TestMethod]
    public void Validate_Rejects_OutOfRange_And_Inconsistent_Combinations()
    {
        // 基线：全默认值合法。
        Assert.AreEqual(0, SubAgentDelegationSettingsStore.Validate(1, 0, true, null).Count);

        Assert.AreEqual(1, SubAgentDelegationSettingsStore.Validate(-1, 0, true, null).Count, "MaxDepth 负数应被拒");
        Assert.AreEqual(1, SubAgentDelegationSettingsStore.Validate(SubAgentDelegationSettingsStore.MaxAllowedDepth + 1, 0, true, null).Count, "MaxDepth 超上界应被拒");
        Assert.AreEqual(1, SubAgentDelegationSettingsStore.Validate(1, -1, true, null).Count, "并发上限负数应被拒");
        Assert.AreEqual(1, SubAgentDelegationSettingsStore.Validate(1, SubAgentDelegationSettingsStore.MaxAllowedConcurrent + 1, true, null).Count, "并发上限超上界应被拒");

        // 裸 modelId 会被拒：多 provider 注册时解析必然失败。
        Assert.AreEqual(1, SubAgentDelegationSettingsStore.Validate(1, 0, true, "gpt-6.2-sol").Count, "裸 modelId 应被拒");
        Assert.AreEqual(1, SubAgentDelegationSettingsStore.Validate(1, 0, true, "fastrouter/").Count, "空 modelId 应被拒");
        Assert.AreEqual(0, SubAgentDelegationSettingsStore.Validate(1, 0, true, "fastrouter/gpt-6.2-sol").Count, "provider/model 应通过");

        // 联动约束：关闭模型选择却不给默认路由 ⇒ 每次派发都会失败，属于必须提前拦的组合。
        var errors = SubAgentDelegationSettingsStore.Validate(1, 0, allowModelSelection: false, defaultModelRoute: null);
        Assert.AreEqual(1, errors.Count, "关闭模型选择但无默认路由应被拒");
        StringAssert.Contains(errors[0], "DefaultModelRoute");
    }

    /// <summary>
    /// 写入后：只改目标段、其它键保留、值落盘正确。
    /// 「其它键保留」是真正的风险点 —— 整文件重写很容易把用户手写的配置抹掉。
    /// </summary>
    [TestMethod]
    public async Task UpdateAsync_PreservesUnrelatedKeys_And_WritesOnlyTargetSection()
    {
        await File.WriteAllTextAsync(_configFile, """
            {
              "environment": "development",
              "http": { "port": 8080 },
              "GoalRuns": { "CheckTimeoutSeconds": 600 }
            }
            """);

        var (store, _) = CreateStore();

        var snapshot = await store.UpdateAsync(new SubAgentDelegationUpdate(MaxDepth: 2, MaxConcurrentSubAgents: 3));

        Assert.AreEqual(2, snapshot.MaxDepth);
        Assert.AreEqual(3, snapshot.MaxConcurrentSubAgents);
        Assert.IsTrue(snapshot.MaxDepthFromConfigFile, "刚写入的值必须标记为来自配置文件");

        var written = JsonDocument.Parse(await File.ReadAllTextAsync(_configFile));
        var root = written.RootElement;

        Assert.AreEqual("development", root.GetProperty("environment").GetString(), "无关顶层键必须保留");
        Assert.AreEqual(8080, root.GetProperty("http").GetProperty("port").GetInt32(), "无关嵌套键必须保留");
        Assert.AreEqual(600, root.GetProperty("GoalRuns").GetProperty("CheckTimeoutSeconds").GetInt32(), "无关嵌套键必须保留");

        var section = root.GetProperty(SubAgentDelegationSettingsStore.SectionName);
        Assert.AreEqual(2, section.GetProperty("MaxDepth").GetInt32());
        Assert.AreEqual(3, section.GetProperty("MaxConcurrentSubAgents").GetInt32());
        Assert.IsFalse(section.TryGetProperty("AllowModelSelection", out _), "未提供的字段不应被写入");

        Assert.IsFalse(File.Exists(_configFile + ".tmp"), "临时文件必须已被原子替换掉");
    }

    /// <summary>
    /// 校验失败时不得留下任何痕迹：文件内容逐字不变。
    /// </summary>
    [TestMethod]
    public async Task UpdateAsync_RejectsInvalidValue_WithoutTouchingTheFile()
    {
        var original = """
            {
              "environment": "development"
            }
            """;
        await File.WriteAllTextAsync(_configFile, original);

        var (store, _) = CreateStore();

        await Assert.ThrowsExactlyAsync<SubAgentDelegationValidationException>(() =>
            store.UpdateAsync(new SubAgentDelegationUpdate(MaxDepth: 99)));

        Assert.AreEqual(original, await File.ReadAllTextAsync(_configFile), "校验失败必须一个字节都不写");
    }

    /// <summary>
    /// 配置热重载取真：写入后不重启、不手动 Reload，只靠文件 watcher，
    /// 新的配置根必须读到新值。这条红掉就说明界面必须提示「重启后生效」。
    /// </summary>
    [TestMethod]
    public async Task WrittenValue_IsObservedByAReloadOnChangeConfigurationRoot()
    {
        await File.WriteAllTextAsync(_configFile, "{ \"environment\": \"development\" }");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(_configFile, optional: true, reloadOnChange: true)
            .Build();

        Assert.AreEqual("1", configuration["SubAgentDelegation:MaxDepth"] ?? "1", "写入前应当取代码默认");

        var (store, _) = CreateStore(configuration);
        await store.UpdateAsync(new SubAgentDelegationUpdate(MaxDepth: 4));

        var observed = await WaitForValueAsync(
            () => configuration["SubAgentDelegation:MaxDepth"],
            expected: "4",
            timeout: TimeSpan.FromSeconds(5));

        Assert.AreEqual("4", observed, "reloadOnChange 下配置文件写入必须被自动观察到");
    }

    /// <summary>
    /// HTTP 端到端：GET 给默认值、PUT 后 GET 拿到新值、非法值 400 且状态不变。
    /// 使用独立 factory，避免把 MaxDepth=2 带到别的测试类。
    /// </summary>
    [TestMethod]
    public async Task AdminEndpoint_Reads_Defaults_Writes_And_Rejects_Invalid()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        JwtHelper.SetBearerToken(client);

        var initial = await client.GetFromJsonAsync<SubAgentDelegationSnapshot>("api/admin/sub-agent-delegation");
        Assert.IsNotNull(initial);
        Assert.AreEqual(SubAgentDelegationSettingsStore.DefaultMaxDepth, initial.MaxDepth);
        Assert.IsFalse(initial.MaxDepthFromConfigFile, "测试实例的 system.json 没有该段，应标记为代码默认");

        using var put = await client.PutAsJsonAsync(
            "api/admin/sub-agent-delegation",
            new SubAgentDelegationUpdate(MaxDepth: 2));
        Assert.AreEqual(HttpStatusCode.OK, put.StatusCode);

        var afterPut = await put.Content.ReadFromJsonAsync<SubAgentDelegationSnapshot>();
        Assert.IsNotNull(afterPut);
        Assert.AreEqual(2, afterPut.MaxDepth);

        // 配置重载是文件变更后的**异步**行为：PUT 的响应已经是落盘的目标值，
        // 但紧接着的 GET 可能仍读到最后一次重载前的快照（实测毫秒级窗口）。
        // 所以这里轮询到截止时间 —— 断言的是「最终能被 GET 读到」，而不是「立刻可见」。
        SubAgentDelegationSnapshot? reread = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        do
        {
            reread = await client.GetFromJsonAsync<SubAgentDelegationSnapshot>("api/admin/sub-agent-delegation");
            if (reread is { MaxDepthFromConfigFile: true, MaxDepth: 2 })
                break;
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        Assert.IsNotNull(reread);
        Assert.IsTrue(reread.MaxDepthFromConfigFile, "PUT 之后该值必须最终被配置重载观察到");

        using var bad = await client.PutAsJsonAsync(
            "api/admin/sub-agent-delegation",
            new SubAgentDelegationUpdate(MaxDepth: SubAgentDelegationSettingsStore.MaxAllowedDepth + 1));
        Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);

        var afterBad = await client.GetFromJsonAsync<SubAgentDelegationSnapshot>("api/admin/sub-agent-delegation");
        Assert.IsNotNull(afterBad);
        Assert.AreEqual(2, afterBad.MaxDepth, "400 不得改变已有配置");
    }

    private (SubAgentDelegationSettingsStore Store, IConfiguration Configuration) CreateStore(
        IConfiguration? configuration = null)
    {
        configuration ??= new ConfigurationBuilder()
            .AddJsonFile(_configFile, optional: true, reloadOnChange: true)
            .Build();

        var store = new SubAgentDelegationSettingsStore(
            configuration,
            PuddingDataPaths.FromRoot(_dataRoot),
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<SubAgentDelegationSettingsStore>());

        return (store, configuration);
    }

    private static async Task<string?> WaitForValueAsync(Func<string?> read, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? observed;
        do
        {
            observed = read();
            if (string.Equals(observed, expected, StringComparison.Ordinal))
                return observed;
            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);

        return read();
    }
}
