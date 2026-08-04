# [Obsolete] 属性残留扫描报告

> 扫描日期：2026-08-04 | 执行者：sub-9445690b (Flash)  
> 扫描范围：`Source\` 下全部 .cs 文件（排除 bin/obj/node_modules，共 1363 个文件）

---

## 真实 [Obsolete] 属性清单（8 处）

所有 8 处均为警告级（无 `[Obsolete(..., true)]` 强废弃用法），不影响编译。

| # | 文件 | 行号 | 所在成员 | Obsolete 消息 |
|---|------|:--:|------|------|
| 1 | `Source\PuddingCore\Abstractions\ILLMConfigResolver.cs` | 26 | `ILLMConfigResolver.ResolveConsciousAsync(string, string?, CancellationToken)` | "Use ResolveAsync(AgentLlmBinding) instead. Template-based resolution is deprecated." |
| 2 | `Source\PuddingCore\Abstractions\ILLMConfigResolver.cs` | 43 | `ILLMConfigResolver.ResolveMemoryAsync(string, string?, CancellationToken)` | "Use ResolveMemoryAsync(AgentLlmBinding) instead." |
| 3 | `Source\PuddingCore\Configuration\PuddingConfigModels.cs` | 205 | `PuddingLlmRoleConfig`（sealed record 类型声明） | "Agent should define preferredProviderId/preferredModelId directly in manifest.json. Global roles are no longer required." |
| 4 | `Source\PuddingCore\Configuration\PuddingConfigModels.cs` | 341 | `AgentInstanceManifest.Feishu`（属性） | "Use ChannelIds and ChannelInstanceManifest instead." |
| 5 | `Source\PuddingCore\Platform\MessageContracts.cs` | 39 | `LlmConfig.ApiKey`（属性） | "请改用 KeyVaultId 在 Runtime 侧注入密钥；此字段仅为向后兼容保留。" |
| 6 | `Source\PuddingHost\Services\FeishuConnectorIdentity.cs` | 7 | `FeishuConnectorIdentity.ForAgent(string agentId)`（静态方法） | "Connector identity is channel-owned." |
| 7 | `Source\PuddingPlatform\Data\Entities\ChatMessageEntity.cs` | 27 | `ChatMessageEntity.AgentTemplateId`（属性） | "AgentTemplateId 已迁移到 AgentTemplate 文件配置。" |
| 8 | `Source\PuddingPlatform\Services\AgentTemplateProvider.cs` | 17 | `AgentTemplateProvider`（sealed class） | "Template config is now embedded in agent instance manifest. Use WorkspaceAgentDto fields directly." |

---

## 仅文档注释含 [Obsolete] 字样（非属性，4 处）

| # | 文件 | 行号 | 内容 |
|---|------|:--:|------|
| 1 | `Source\PuddingCore\Abstractions\ILLMConfigResolver.cs` | 24 | `/// [Obsolete] 解析显意识 LLM 配置...` |
| 2 | `Source\PuddingCore\Abstractions\ILLMConfigResolver.cs` | 41 | `/// [Obsolete] 解析潜意识 LLM 配置...` |
| 3 | `Source\PuddingCore\Configuration\LlmProfileResolver.cs` | 8 | `/// [Obsolete] 全局 roles` |
| 4 | `Source\PuddingCore\Configuration\PuddingConfigModels.cs` | 201 | `/// [Obsolete] LLM 角色→Profile 映射...` |

---

## 普通文本 'obsolete'（非属性，3 处）

| # | 文件 | 行号 | 上下文 |
|---|------|:--:|------|
| 1 | `Source\PuddingCoreTests\Memory\MemoryMaintenancePlanValidatorTests.cs` | 148 | 测试数据字符串 "Delete obsolete memory." |
| 2 | `Source\PuddingMemoryEngine\Services\SubconsciousOrchestrator.cs` | 1644 | Prompt 文本 "clearly obsolete instructions" |
| 3 | `Source\PuddingRuntimeTests\Services\ContextPipelineLayerTests.cs` | 108 | 断言字符串 "The obsolete voice metadata protocol must not reach the Agent." |

---

## 备注

1. 所有真实属性均为警告级，无编译阻断
2. 覆盖 `[Obsolete]` / `[System.Obsolete]` / `[ObsoleteAttribute]` 所有变体
3. 扫描脚本留存：`D:\data\workspaces\default\scripts\find_obsolete.ps1`
