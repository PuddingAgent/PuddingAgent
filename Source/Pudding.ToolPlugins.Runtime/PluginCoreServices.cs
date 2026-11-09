using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pudding.ToolPlugins.Contracts;

namespace Pudding.ToolPlugins.Runtime;

/// <summary>Bounded file services. Caller must enforce the live capability lease before entering.</summary>
public sealed class PluginCoreServices(string stateRoot)
{
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    public async Task<JsonElement> CallAsync(PluginPackage package, PluginScope scope, string? workspaceRoot,
        string method, JsonElement args, CancellationToken ct)
    {
        if (method is "workspace.read" or "assets.read")
        {
            var path = args.GetProperty("path").GetString()!;
            if (method == "assets.read" && !package.Manifest.Assets.Any(a => a.Path == path))
                throw new PluginException("plugin.core_callback_denied", "core_callback", "Asset is not declared by this package.");
            var root = method == "assets.read" ? package.Directory : workspaceRoot;
            if (string.IsNullOrWhiteSpace(root)) throw new PluginException("plugin.core_callback_denied", "core_callback", "No frozen workspace root is available.");
            return PluginJson.Value(await ReadTextAsync(PluginValidation.Inside(root, path), ct));
        }
        if (method is "state.read" or "state.write")
        {
            var key = args.GetProperty("key").GetString()!;
            if (!PluginValidation.ValidId(key)) throw new PluginException("plugin.core_callback_denied", "core_callback", "State key must be a short ASCII identifier.");
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { scope.WorkspaceId, scope.AgentId, package.Manifest.Id })))).ToLowerInvariant();
            var directory = PluginValidation.Inside(stateRoot, identity);
            var file = PluginValidation.Inside(directory, key + ".txt");
            await _stateGate.WaitAsync(ct);
            try
            {
                if (method == "state.read") return PluginJson.Value(File.Exists(file) ? await ReadTextAsync(file, ct) : null);
                var value = args.GetProperty("value").GetString()!;
                var bytes = Encoding.UTF8.GetBytes(value);
                if (bytes.Length > 65536) throw new PluginException("plugin.resource_limit", "core_callback", "State value exceeds 64 KiB.");
                Directory.CreateDirectory(directory);
                var files = Directory.GetFiles(directory, "*.txt");
                if (files.Length >= 64 && !File.Exists(file)
                    || files.Where(f => f != file).Sum(f => new FileInfo(f).Length) + bytes.Length > 256 * 1024)
                    throw new PluginException("plugin.resource_limit", "core_callback", "Plugin state quota exceeded.");
                var staging = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllBytesAsync(staging, bytes, ct); File.Move(staging, file, true); }
                finally { if (File.Exists(staging)) File.Delete(staging); }
                return PluginJson.Value(new { written = true });
            }
            finally { _stateGate.Release(); }
        }
        throw new PluginException("plugin.core_capability_unavailable", "core_callback", "Unsupported Core capability.");
    }
    private static async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            using var buffer = new MemoryStream();
            await PluginPackageStore.CopyBoundedAsync(input, buffer, 256 * 1024, ct);
            return new UTF8Encoding(false, true).GetString(buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { throw new PluginException("plugin.file_read_failed", "core_callback", "Cannot read UTF-8 text within the permitted root."); }
    }
}
