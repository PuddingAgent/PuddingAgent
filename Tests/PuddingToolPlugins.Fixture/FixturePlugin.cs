using System.Diagnostics;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Sdk;

namespace Sample.PluginFixture;

public class FixturePlugin : ToolPlugin
{
    public override IReadOnlyCollection<string> ToolIds => ["echo", "read_file", "hang", "flood", "crash", "child_hang"];
    public override IReadOnlyCollection<string> HookIds => ["advice"];
    public override async ValueTask<PluginResult> ExecuteAsync(PluginCall call, CancellationToken ct)
    {
        switch (call.ToolId)
        {
            case "echo": return PluginResult.Ok(call.Arguments.GetProperty("text").GetString());
            case "read_file": return PluginResult.Ok(await Context.Core.ReadWorkspaceTextAsync(call.Arguments.GetProperty("text").GetString()!, ct));
            case "hang": await Task.Delay(Timeout.Infinite); return PluginResult.Ok("");
            case "flood": return PluginResult.Ok(new string('x', 2 * 1024 * 1024));
            case "crash": Environment.Exit(37); return PluginResult.Ok("");
            case "child_hang":
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("/c"); start.ArgumentList.Add("ping -n 90 127.0.0.1 >nul");
                using (var child = Process.Start(start)!)
                    await File.WriteAllTextAsync(call.Arguments.GetProperty("text").GetString()!, child.Id.ToString());
                await Task.Delay(Timeout.Infinite); return PluginResult.Ok("");
            default: return PluginResult.Fail("Unknown fixture tool.");
        }
    }
    public override ValueTask<PluginHookResult> OnToolResultAsync(PluginHookCall call, CancellationToken ct)
        => ValueTask.FromResult(new PluginHookResult("Fixture advice: preserve original evidence."));
}
public sealed class FailedInitializationPlugin : FixturePlugin
{
    public override ValueTask InitializeAsync(IPluginContext context, CancellationToken ct)
        => throw new InvalidOperationException("Injected initialization failure.");
}
