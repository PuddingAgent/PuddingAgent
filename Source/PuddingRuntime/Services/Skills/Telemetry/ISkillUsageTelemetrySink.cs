namespace PuddingRuntime.Services.Skills.Telemetry;

/// <summary>
/// 技能使用遥测落点契约。
/// 为什么存在：SkillEnforcerService 是技能注入的唯一决策点，但注入事件此前只有一条
/// LogDebug，无法回答「哪些技能真被用过 / 哪些从未命中 / 命中后是否有害」。
/// 本接口把「技能被注入」变成可持久记录的事实，供后续切片做打分与淘汰。
/// 实现必须 fail-open：任何异常自行消化（仅记日志），绝不向调用方抛出 ——
/// 遥测是旁路信号，故障不得影响技能注入与对话主链路。
/// </summary>
public interface ISkillUsageTelemetrySink
{
    ValueTask RecordAsync(SkillUsageRecord record, CancellationToken ct = default);
}
