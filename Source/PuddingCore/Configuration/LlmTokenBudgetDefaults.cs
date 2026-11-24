namespace PuddingCode.Configuration;

/// <summary>
/// 输入预算的两项产品口径。
///
/// 之所以单独成类：运行期推导（<c>LlmRequestBudgetGuard</c>）与配置加载期校验
/// （<c>PuddingFileConfigLoader.ValidateLlmProviders</c>）必须用**同一份**数字。
/// 若校验自己写一遍常量，就会出现「加载期通过、运行期算出的上限为 1」这种漂移，
/// 而那正是 2026-08-24 那次 <c>limit=1</c> 失败的表现形态。
///
/// 依赖方向：PuddingRuntime → PuddingCore，故常量归属 Core，运行期引用它。
/// </summary>
public static class LlmTokenBudgetDefaults
{
    /// <summary>
    /// 单轮回复预留上限（≈ 十万量级字符，远超任何单轮答复）。
    /// 1M 窗口下预留占比 6.4%；用登记的最大输出能力（deepseek-flash 393,216）当预留会白锁 39%。
    /// </summary>
    public const int PerTurnOutputCapTokens = 64_000;

    /// <summary>为 token 估算误差预留的缓冲。</summary>
    public const int SafetyBufferTokens = 1_024;

    /// <summary>
    /// 结构矛盾判据：窗口减去输出预留与安全缓冲后不足 1，即该模型连自己的预留输出都装不下。
    ///
    /// 运行期（把上限夹到 1 前的诊断）与配置加载期（拒绝加载）共用这一个比较，
    /// 因为**边界值**才是最容易漂移的部分。传参而非传配置，是为了让运行期能用
    /// 已经解析好的预留（可能被调用方传入的 safetyBuffer 覆盖），而不必重算一遍。
    /// </summary>
    public static bool LeavesNoInputRoom(int contextWindow, int reservedOutput, int safetyBuffer)
        => contextWindow - reservedOutput - safetyBuffer < 1;
}
