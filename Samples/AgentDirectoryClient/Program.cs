namespace Pudding.Samples.AgentDirectoryClient;

/// <summary>
/// Deliberately offline: this executable has no credential loading and no online connection.
/// It only explains how to supply an already-authorized <c>IAgentDirectoryApi</c> to
/// <see cref="DirectoryDemo"/>.
/// </summary>
internal static class Program
{
    private const string OfflineNotice = "这是可注入 IAgentDirectoryApi 的离线示例；请由上层提供已授权的 API。";

    private static int Main(string[] args)
    {
        Console.Error.WriteLine(OfflineNotice);
        return 2;
    }
}
