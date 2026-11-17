using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PuddingSecretReferences;

/// <summary>DPAPI cache for credentials needed before Core/KeyVault can start. Never a plaintext cache.</summary>
public static class BootstrapSecretFile
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PuddingAgent.BootstrapCredentials.v1");
    private static readonly Regex Reference = new(@"^\{\{vault:(?<name>[a-zA-Z0-9._-]+)\}\}$");
    /// <summary>引用前缀。配置值以它开头即表示「此处存的是引用，不是值」。</summary>
    public const string ReferenceMarker = "{{vault:";

    public static string FilePath(string dataRoot) => Path.Combine(dataRoot, "secrets", "bootstrap.credentials.dpapi");

    public static string Resolve(string dataRoot, string configured)
    {
        if (!configured.Contains(ReferenceMarker, StringComparison.OrdinalIgnoreCase)) return configured;
        var match = Reference.Match(configured);
        if (!match.Success) throw new InvalidOperationException("Invalid bootstrap credential reference.");
        try
        {
            var secrets = Read(dataRoot);
            if (secrets.TryGetValue(match.Groups["name"].Value, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }
        catch { throw new InvalidOperationException("Bootstrap credential cache cannot be decrypted. Restore it for this Windows account."); }
        throw new InvalidOperationException("Bootstrap credential cache entry is missing.");
    }

    public static void Store(string dataRoot, string name, string value)
    {
        if (!Reference.IsMatch("{{vault:" + name + "}}") || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Invalid bootstrap credential.");
        var file = FilePath(dataRoot);
        var entries = File.Exists(file) ? Read(dataRoot) : new Dictionary<string, string>();
        entries[name] = value;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
        byte[] protectedBytes;
        try { protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, protectedBytes); File.Move(temporary, file, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Dictionary<string, string> Read(string dataRoot)
    {
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath(dataRoot)), Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(bytes) ?? throw new InvalidDataException(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
