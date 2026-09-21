namespace PuddingCode.Skills;

/// <summary>SKILL Hub feature flags（进程内直连开关；配置节命名与 PuddingCore 既有 Options 一致）。</summary>
public sealed class SkillHubFeatureOptions
{
    public const string SectionName = "SkillHub";

    public bool Enabled { get; init; } = true;
}
