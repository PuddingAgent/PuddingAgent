using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingSecretReferences;

namespace PuddingSecretReferencesTests;

[TestClass]
public sealed class CredentialReferenceTests
{
    [TestMethod] public async Task Plaintext_Is_Unchanged() => Assert.AreEqual("  password  ", await CredentialReference.ResolveAsync("  password  ", null));
    [TestMethod] public async Task Resolves_Vault_Without_Sending_Placeholder() => Assert.AreEqual("test-key", await CredentialReference.ResolveAsync("{{vault:voice.qwen}}", (text, _) => Task.FromResult(text.Replace("{{vault:voice.qwen}}", "test-key"))));
    [TestMethod] public async Task Missing_Resolver_Is_An_Error() => await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CredentialReference.ResolveAsync("{{vault:missing}}", null));
    [TestMethod] public async Task Unresolved_Vault_Is_An_Error() => await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CredentialReference.ResolveAsync("{{vault:missing}}", (text, _) => Task.FromResult(text)));
    [TestMethod] public async Task Decryption_Failure_Is_Not_Missing_Credentials()
    {
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CredentialReference.ResolveAsync("{{vault:test}}", (_, _) => throw new Exception("private-test-value")));
        Assert.IsFalse(ex.ToString().Contains("private-test-value"));
    }
    [TestMethod] public async Task Environment_Then_Vault()
    {
        var name = "PUDDING_SECRET_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "{{vault:test}}");
        try { Assert.AreEqual("resolved", await CredentialReference.ResolveAsync("${" + name + "}", (_, _) => Task.FromResult("resolved"))); }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }
    [TestMethod] public async Task Missing_Environment_Is_An_Error() => await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CredentialReference.ResolveAsync("${PUDDING_NONEXISTENT_" + Guid.NewGuid().ToString("N") + "}", null));
    [TestMethod] public void Bootstrap_Roundtrip_And_Corruption_Fails_Closed()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-secrets-" + Guid.NewGuid().ToString("N"));
        try
        {
            BootstrapSecretFile.Store(root, "desktop.control", "test-control-token");
            Assert.IsFalse(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(BootstrapSecretFile.FilePath(root))).Contains("test-control-token"));
            Assert.AreEqual("test-control-token", BootstrapSecretFile.Resolve(root, "{{vault:desktop.control}}"));
            File.WriteAllText(BootstrapSecretFile.FilePath(root), "damaged");
            Assert.ThrowsExactly<InvalidOperationException>(() => BootstrapSecretFile.Resolve(root, "{{vault:desktop.control}}"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestMethod] public void Bootstrap_Missing_Is_An_Error() => Assert.ThrowsExactly<InvalidOperationException>(() => BootstrapSecretFile.Resolve(Path.GetTempPath(), "{{vault:missing}}"));
    [TestMethod] public void Boundary_Has_No_Product_References() => Assert.IsFalse(typeof(CredentialReference).Assembly.GetReferencedAssemblies().Any(x => x.Name!.StartsWith("Pudding")));
}
