using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 密钥库「使用情况」视图的回归测试：回答「这条现在谁在用、有没有重复、是不是死条目」。
/// </summary>
/// <remarks>
/// 这些断言刻意锁住三件容易悄悄退化的事：
/// ① 引用计数按出现次数算（同名在同一文件里出现两次必须是 2，不是 1）；
/// ② 孤儿判定必须在没有任何引用时才成立（否则改了不生效的密钥会被当成在用）；
/// ③ 报告里绝不能出现密钥值本身（这条最贵，一旦回归就是泄漏）。
/// </remarks>
[TestClass]
public sealed class KeyVaultUsageInspectorTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "kv-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响断言结论，交给操作系统临时目录回收。
        }
    }

    private void WriteConfig(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private KeyVaultUsageInspector CreateInspector(FakeKeyVaultService vault) =>
        new(PuddingDataPaths.FromRoot(_root), vault);

    [TestMethod]
    public async Task ReferenceCount_CountsOccurrences_ButFilesAreDeduplicated()
    {
        WriteConfig(
            "config/llm.providers.json",
            """{"a":"{{vault:dup}}","b":"{{vault:dup}}"}""");
        WriteConfig(
            "config/search.providers.json",
            """{"c":"{{vault:dup}}"}""");

        var vault = new FakeKeyVaultService()
            .Add("dup", "value-shared", "k1")
            .Build();

        var report = await CreateInspector(vault).BuildAsync();

        var entry = report.Entries.Single(e => e.Name == "dup");
        Assert.AreEqual(3, entry.ReferenceCount, "同一文件内出现两次必须计 2，再加另一个文件 1 次");
        Assert.AreEqual(2, entry.ReferencedBy.Count, "被引用文件必须是去重后的清单");
        CollectionAssert.Contains(entry.ReferencedBy.ToList(), "config/llm.providers.json");
        CollectionAssert.Contains(entry.ReferencedBy.ToList(), "config/search.providers.json");
    }

    [TestMethod]
    public async Task EntryWithoutAnyReference_IsReportedAsOrphan()
    {
        WriteConfig("config/system.json", """{"k":"{{vault:used}}"}""");

        var vault = new FakeKeyVaultService()
            .Add("used", "v-used", "k1")
            .Add("nobody-reads-me", "v-orphan", "k2")
            .Build();

        var report = await CreateInspector(vault).BuildAsync();

        var orphan = report.Entries.Single(e => e.Name == "nobody-reads-me");
        Assert.AreEqual(0, orphan.ReferenceCount);
        Assert.AreEqual(0, orphan.ReferencedBy.Count);
        Assert.AreEqual(1, report.OrphanCount, "孤儿计数必须恰好包含那条无人引用的条目");
        Assert.AreEqual(2, report.TotalEntries);
    }

    [TestMethod]
    public async Task SameValue_YieldsOneDuplicateGroup_SharedByBothNames()
    {
        var vault = new FakeKeyVaultService()
            .Add("alpha", "identical-secret", "k1")
            .Add("beta", "identical-secret", "k2")
            .Add("gamma", "different-secret", "k3")
            .Build();

        var report = await CreateInspector(vault).BuildAsync();

        var alpha = report.Entries.Single(e => e.Name == "alpha");
        var beta = report.Entries.Single(e => e.Name == "beta");
        var gamma = report.Entries.Single(e => e.Name == "gamma");

        Assert.IsNotNull(alpha.DuplicateGroupId, "同值条目必须被标出重复");
        Assert.AreEqual(alpha.DuplicateGroupId, beta.DuplicateGroupId, "同值条目必须落在同一组");
        Assert.IsNull(gamma.DuplicateGroupId, "不同值的条目不得被标成重复");
        Assert.AreEqual(1, report.DuplicateGroupCount, "两组相同值只能算一组");
    }

    [TestMethod]
    public async Task Report_NeverContainsSecretValue()
    {
        const string canary = "sk-plaintext-canary-0123456789abcdef";
        var vault = new FakeKeyVaultService()
            .Add("canary", canary, "k1")
            .Build();

        var report = await CreateInspector(vault).BuildAsync();
        var json = JsonSerializer.Serialize(report);

        Assert.IsFalse(
            json.Contains(canary, StringComparison.Ordinal),
            "使用情况报告序列化后不得包含密钥值（这条一旦回归就是泄漏）");
        Assert.IsFalse(
            json.Contains("DuplicateGroupId\":\"dup-0", StringComparison.Ordinal),
            "重复分组编号从 dup-1 起，且只为真正的重复分配");
    }

    [TestMethod]
    public async Task ChannelsAndAgentsRoots_AreScannedToo()
    {
        WriteConfig("config/a.json", """{"k":"{{vault:cfg-secret}}"}""");
        WriteConfig("channels/c1/manifest.json", """{"k":"{{vault:channel-secret}}"}""");
        WriteConfig("agents/x/home-assistant.json", """{"k":"{{vault:agent-secret}}"}""");

        var vault = new FakeKeyVaultService()
            .Add("cfg-secret", "v1", "k1")
            .Add("channel-secret", "v2", "k2")
            .Add("agent-secret", "v3", "k3")
            .Build();

        var report = await CreateInspector(vault).BuildAsync();

        Assert.AreEqual(0, report.OrphanCount, "三处扫描根都必须覆盖，否则会把在用密钥误报成孤儿");
        CollectionAssert.Contains(
            report.Entries.Single(e => e.Name == "agent-secret").ReferencedBy.ToList(),
            "agents/x/home-assistant.json");
    }

    /// <summary>
    /// 只实现使用情况视图真正用到的两个成员；其余方法故意抛异常，
    /// 这样一旦实现改成调用别的成员（例如解密全部条目），测试会立刻红。
    /// </summary>
    private sealed class FakeKeyVaultService : IKeyVaultService
    {
        private readonly List<KeyVaultSecretSummary> _summaries = [];
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public FakeKeyVaultService Add(string name, string value, string keyVaultId)
        {
            _summaries.Add(new KeyVaultSecretSummary
            {
                KeyVaultId = keyVaultId,
                Name = name,
                Description = "test",
                CreatedAt = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc),
            });
            _values[name] = value;
            return this;
        }

        public FakeKeyVaultService Build() => this;

        public Task<IReadOnlyList<KeyVaultSecretSummary>> ListSecretsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KeyVaultSecretSummary>>(_summaries);

        public Task<KeyVaultSecretDetail?> GetSecretAsync(
            string keyVaultId,
            bool includePlainText = false,
            CancellationToken ct = default)
        {
            var summary = _summaries.FirstOrDefault(s => s.KeyVaultId == keyVaultId);
            if (summary is null)
            {
                return Task.FromResult<KeyVaultSecretDetail?>(null);
            }

            return Task.FromResult<KeyVaultSecretDetail?>(new KeyVaultSecretDetail
            {
                KeyVaultId = summary.KeyVaultId,
                Name = summary.Name,
                Description = summary.Description,
                CreatedAt = summary.CreatedAt,
                Value = includePlainText ? _values[summary.Name] : null,
            });
        }

        public Task<string> EncryptAsync(string plainText, CancellationToken ct = default) =>
            throw new NotSupportedException("usage inspector must not encrypt");

        public Task<string> DecryptAsync(string encryptedValue, CancellationToken ct = default) =>
            throw new NotSupportedException("usage inspector must not decrypt");

        public Task<KeyVaultSecretSummary> CreateSecretAsync(
            CreateKeyVaultSecretCommand request,
            CancellationToken ct = default) =>
            throw new NotSupportedException("usage inspector is read-only");

        public Task<KeyVaultSecretSummary?> UpdateSecretAsync(
            string keyVaultId,
            UpdateKeyVaultSecretCommand request,
            CancellationToken ct = default) =>
            throw new NotSupportedException("usage inspector is read-only");

        public Task<bool> DeleteSecretAsync(string keyVaultId, CancellationToken ct = default) =>
            throw new NotSupportedException("usage inspector is read-only");

        public Task<string> InjectAsync(string text, CancellationToken ct = default) =>
            throw new NotSupportedException("usage inspector must not inject");

        public Task<string> StripAsync(string text, CancellationToken ct = default) =>
            throw new NotSupportedException("usage inspector must not strip");
    }
}
