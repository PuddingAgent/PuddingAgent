namespace Pudding.Samples.ExecutionBudgetClient;

/// <summary>
/// Deliberately offline: this executable has no credential loading and no online connection.
/// It only explains how to supply an already-authorized <c>IExecutionBudgetApi</c> to
/// <see cref="BudgetDemo"/>.
/// </summary>
internal static class Program
{
    private const string OfflineNotice = "这是可注入 IExecutionBudgetApi 的离线示例；请由上层提供已授权的 API。";

    private static int Main(string[] args)
    {
        Console.Error.WriteLine(OfflineNotice);
        return 2;
    }
}
