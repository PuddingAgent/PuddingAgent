# Agent 系统预制模板完整快照与 DeepSeek 鲸鱼娘模板设计方案

> 状态：Proposed（设计完成，未实施）  
> 日期：2026-09-02  
> 关联 ADR：[ADR-083 Agent 系统预制模板版本化快照与 DeepSeek 鲸鱼娘模板](../07架构/97ADR-083Agent系统预制模板版本化快照与DeepSeek鲸鱼娘模板ADR.md)  
> 本轮边界：只产出设计与 ADR，并按用户要求通过外部任务 API 新增一条看板记录；不修改源码、运行配置、业务数据库内容或 `D:\data` 运行数据。  
> 上游参考：[Neko3000/deepseek-whalechan](https://github.com/Neko3000/deepseek-whalechan)，调研基线为 `main@3f8660a`（2026-08-26）。

## 1. 目标与结论

本方案同时解决三个问题：

1. 以现有“默认助手”实例为证据，补齐系统预制模板能够表达、导入、回显和创建时复制的完整字段。
2. 把“选择模板”定义为一次原子化的完整快照应用：用户选择后，基础信息、能力、Prompt、模型、Smart 子代理与护栏立即自动填充；创建后 Agent 独立演进。
3. 增加 `deepseek-whalechan` 系统预制模板，提供有角色感但不牺牲真实性、安全性和任务完成质量的社区角色助手。

核心结论：

- 系统预制模板、已导入全局模板和 Workspace Agent 是三种不同生命周期的快照，不形成运行时继承链。
- 预制模板不能再用单个长 JSON 承载全部提示词，应改为与正式模板同构的目录包。
- 模板完整性由“字段覆盖合同 + 内容版本 + 哈希 + 来源元数据”保证，不靠前端手工维护第二份字段列表。
- `deepseek-whalechan` 首版只交付 Pudding 原创的文本人设、能力组合和中性头像；不复制上游图片、Skill 文本或提示词模板。
- 预制模板或全局模板更新不得静默修改任何既有 Agent。

## 2. 现状证据

### 2.1 当前三层数据

| 层级 | 当前主源 | 生命周期 | 当前问题 |
|---|---|---|---|
| 软件系统预制 | `Source/PuddingHost/default-data/agent-template-presets/*.json` | 随软件发布、只读 | 单 JSON 过长；字段模型落后于 Agent 实例；缺少版本、来源和刷新合同 |
| 已导入全局模板 | `D:\data\agent-templates/{templateId}/manifest.json` + Markdown | 用户数据，可编辑、可备份 | 导入后没有“预制有更新”的识别和显式升级路径 |
| Workspace Agent | `D:\data\agents/{agentId}/manifest.json` + Markdown | 创建时复制，之后独立演进 | 字段最完整，但模板无法完整表达和复制这些可复用字段 |

### 2.2 “默认助手”与 `general-assistant` 的差距

本次只读盘点的默认助手为：

```text
D:\data\agents\default.global_general-assistant.6a8\manifest.json
```

它已经包含：

- 主模型、记忆模型、Embedding 模型引用；
- `explorer/researcher/planner/reviewer/developer/deployer/tester` 七类 Smart 子代理模型；
- `visionHelperModel`；
- `maxReplyTokens/maxRounds/maxElapsedSeconds/maxToolCallsTotal`；
- 能力、Skill、头像、来源模板、Markdown 文件引用和 `heartbeatPrompt.md`；
- 约 1276 字的实例级系统提示词，以及明显扩充过的 `AGENTS.md`、`SOUL.md`、`TOOLS.md` 和心跳提示。

当前系统预制 `general-assistant.json` 只覆盖其中一部分。关键断点包括：

| 字段/行为 | Agent 实例 | 系统预制读取模型 | 创建页自动填充 | 结论 |
|---|---:|---:|---:|---|
| 七类 Smart 子代理模型 | 有 | 无 | 表单有，但模板 DTO 无值 | 必须补齐 |
| `visionHelperModel` | 有 | 无 | 表单有，模板 DTO 无值 | 必须补齐 |
| Embedding provider/model | 有 | `AgentTemplateManifest` 有，但 preset record 无 | DTO 有 | 修复断链 |
| `heartbeatPrompt` | 独立 Markdown | preset 无 | 表单有 | 必须成为模板文件 |
| `AGENTS/SOUL/TOOLS/BOOTSTRAP/MEMORY` | 有 | 部分支持 | 已能填充 | 改为目录包并统一读写 |
| 完整能力集合 | 约 100 项 | 29 项 | 能填已有值 | 不能盲目复制，应按最小权限重构 |
| 预制版本/来源/许可 | 无 | 无 | 无 | 新增 |
| 预制更新到已导入模板 | 不适用 | 只允许首次导入 | 无 | 新增显式升级流程 |

### 2.3 当前自动填充已经存在，但不是完整合同

Workspace Agent 创建页已经有 `applyTemplateSnapshot()`：

- 打开“新增 Agent”时默认选择 `general-assistant`；
- 切换来源模板时调用 `form.setFieldsValue()`；
- 创建后的 `sourceTemplateId` 只读，实例独立演进。

问题不是“完全没有自动填充”，而是：

1. `GlobalAgentTemplateDto` 与 `AgentTemplatePreset` 没有覆盖全部可复用字段；
2. 列表 API 携带长提示词，字段继续增加后成本和漂移都会变大；
3. 前端手写字段映射，新增字段容易再次漏掉；
4. 切换模板没有内容版本、哈希和异步竞态围栏；
5. 已导入模板无法识别系统预制的新版本。

### 2.4 现有提示词不能原样回灌系统模板

默认助手的迭代经验有价值，但实例 Prompt 含大量机器或用户专属内容：

- 固定仓库路径、固定模型名和供应商；
- 强制使用子代理、固定飞书汇报与免打扰时间；
- 要求每个原子任务提交并推送；
- 要求主动发现并修复 PuddingAgent 问题；
- 工具名和能力假设与具体实例绑定。

这些内容不能进入通用系统预制。反向优化应提取可复用行为原则，而不是复制实例全文。

## 3. 设计原则

1. **配置文件优先**：预制与全局模板都以文件为权威，不增加模板数据库主源。
2. **一次性快照**：创建时复制，运行时只读 Agent 实例；来源模板只用于审计。
3. **字段完整性可验证**：每个实例字段必须被分类为“模板可复制、实例专属、运行派生”之一。
4. **默认最小权限**：从默认助手实例吸收工作流经验，但不自动复制全部高风险能力。
5. **模型无关**：系统预制默认不绑定某台机器上的 provider/model ID；缺省为平台或 Workspace 选择。
6. **Prompt 稳定前缀**：静态行为准则留在模板；当前路径、会话、时间、工具状态等动态事实由运行时注入。
7. **角色表演不改变事实**：人格可以影响语气，不能改变状态、权限、证据和完成判定。
8. **显式升级**：系统预制升级必须由用户确认，不静默覆盖已导入模板或既有 Agent。
9. **许可先行**：第三方角色、Prompt、Skill 和图片按各自许可分别治理。

## 4. 目标数据架构

### 4.1 系统预制改为目录包

目标路径：

```text
Source/PuddingHost/default-data/agent-template-presets/
  general-assistant/
    manifest.json
    SOUL.md
    AGENTS.md
    TOOLS.md
    BOOTSTRAP.md
    MEMORY.md
    heartbeatPrompt.md
    NOTICE.md                 # 第三方来源或许可说明，可选
  deepseek-whalechan/
    manifest.json
    SOUL.md
    AGENTS.md
    TOOLS.md
    BOOTSTRAP.md
    MEMORY.md
    heartbeatPrompt.md
    NOTICE.md
```

发布后位于：

```text
<AppRoot>/default-data/agent-template-presets/{presetId}/
```

不保留旧 `*.json` 与新目录包的双读兼容层。实施时一次迁移现有四个预制模板及测试，避免长期维护两套解析器。

### 4.2 `manifest.json` 字段分组

建议预制清单使用 `pudding.agent-template-preset/v2`：

```json
{
  "schema": "pudding.agent-template-preset/v2",
  "presetVersion": "2.0.0",
  "templateId": "general-assistant",
  "name": "通用助手",
  "description": "...",
  "role": "Service",
  "avatarId": "neutral",
  "isEnabled": true,
  "sortOrder": 0,
  "origin": {
    "kind": "pudding",
    "sourceUrl": null,
    "sourceRevision": null,
    "license": "Pudding-Project",
    "attribution": null
  },
  "prompts": {
    "system": "SYSTEM.md",
    "soul": "SOUL.md",
    "agents": "AGENTS.md",
    "tools": "TOOLS.md",
    "bootstrap": "BOOTSTRAP.md",
    "memory": "MEMORY.md",
    "heartbeat": "heartbeatPrompt.md"
  },
  "models": {
    "preferredProviderId": null,
    "preferredModelId": null,
    "memoryLlmProviderId": null,
    "memoryLlmModelId": null,
    "embeddingProviderId": null,
    "embeddingModelId": null,
    "reasoningEffort": null,
    "visionHelperModel": null,
    "smartRoles": {
      "explorer": null,
      "researcher": null,
      "planner": null,
      "reviewer": null,
      "developer": null,
      "deployer": null,
      "tester": null
    }
  },
  "memory": {
    "searchMode": "deep"
  },
  "guardrails": {
    "maxReplyTokens": 8192,
    "maxRounds": 200,
    "maxElapsedSeconds": 2400,
    "maxToolCallsTotal": 200,
    "containerImage": null
  },
  "capabilities": {
    "selectedCapabilityIds": []
  },
  "skills": {
    "selectedSkillPackageIds": []
  }
}
```

说明：

- `maxContextTokens` 从模板体系删除。模型上下文容量只来自 `llm.providers.json` 中被选模型的能力配置，遵守现有架构决策。
- `allowFileWrite/allowShellExecution/allowNetworkAccess/allowedToolNames` 不再作为 v2 预制字段；权限以 capability 元数据和 Runtime 审批为准，避免两套权限事实漂移。
- 模型字段即使为 `null` 也必须出现在快照中，表示“显式继承平台/Workspace 默认”，而不是“字段丢失”。
- `contentHash` 不手写，由服务端按规范化 manifest 与所有 Prompt 文件内容计算 SHA-256。

### 4.3 字段归属表

| 字段 | 系统预制/全局模板 | Agent 创建时复制 | Agent 专属 |
|---|---:|---:|---:|
| 角色、描述、头像 | 是 | 是 | 可编辑 |
| System/User Prompt | 是 | 是 | 可编辑 |
| 七个 Markdown/心跳文件 | 是 | 是 | 可编辑 |
| 能力、Skill | 是 | 是 | 可编辑 |
| 主/记忆/Embedding/Smart/Vision 模型 | 是 | 是 | 可编辑 |
| 记忆模式、护栏 | 是 | 是 | 可编辑 |
| `sourceTemplateId/version/hash` | 来源元数据 | 是 | 只读审计字段 |
| `agentInstanceId/workspaceId/mainSessionId/channelIds` | 否 | 否 | 实例专属 |
| `paths/isFrozen/运行状态` | 否 | 否 | 实例或运行派生 |
| 模型上下文容量 | 否 | 否 | Provider Model 派生 |

### 4.4 统一 Creation Snapshot

新增服务端内部合同：

```text
AgentTemplateCreationSnapshot
  identity
  origin(version, contentHash, license, attribution)
  prompts(system + 6 persona docs + heartbeat)
  modelBindings(main/memory/embedding/smart/vision)
  memoryPolicy
  guardrails
  capabilityIds
  skillPackageIds
  diagnostics(unavailableModels/capabilities/skills)
```

来源可以是系统预制或已导入全局模板，但投影结果必须完全一致。前端和 `WorkspaceAgentFileService` 都消费这一合同，不各自拼字段。

## 5. 预制导入与版本升级

### 5.1 状态模型

系统预制列表显示：

| 状态 | 条件 | 默认动作 |
|---|---|---|
| `available` | 尚未导入 | 导入为全局模板 |
| `current` | 已导入，版本/哈希与预制一致 | 查看 |
| `update_available` | 预制版本更高，已导入模板仍等于上次导入内容 | 预览并更新 |
| `drifted` | 已导入模板已被用户编辑 | 另存为副本；或显式覆盖 |
| `invalid` | 字段、引用或许可元数据校验失败 | 禁止导入，展示错误 |

正式全局模板保存：

```text
originPresetId
originPresetVersion
originPresetContentHash
lastSyncedPresetContentHash
```

### 5.2 更新行为

1. 更新前展示按分组的差异：Prompt、能力、模型、记忆、护栏、头像、许可。
2. 未漂移时允许“一键更新全局模板”。
3. 已漂移时默认“将新预制另存为副本”；“覆盖当前模板”必须二次确认。
4. 更新全局模板只改变 `data/agent-templates/{templateId}`，绝不遍历或修改 `data/agents/*`。
5. 若用户要升级既有 Agent，使用单独的“对比并选择性应用”功能，不能复用预制导入操作。

## 6. 创建 Agent 时选择模板即自动填充

### 6.1 API

新增详情端点或等价服务：

```http
GET /api/global-agent-templates/{templateId}/creation-snapshot
```

返回完整快照，不再让模板列表 API 携带所有长 Prompt。

创建请求增加只读来源围栏：

```json
{
  "sourceTemplateId": "global:general-assistant",
  "sourceTemplateVersion": "2.0.0",
  "sourceTemplateContentHash": "<64-hex>",
  "...": "用户在表单中确认后的完整配置"
}
```

如果选中模板后服务端内容发生变化，创建返回 `409 template_snapshot_changed`，前端提示重新载入，不能静默混用两个版本。

### 6.2 前端交互

1. 打开“新增 Agent”时默认选择 `general-assistant` 并自动加载完整快照。
2. 用户切换模板后，所有分组一次性更新：
   - 基础信息；
   - 能力与 Skill；
   - 角色与 Prompt（含 heartbeat）；
   - 模型与记忆；
   - Smart 子代理；
   - 执行护栏。
3. 切换时显示一次短暂 loading，保存按钮在快照应用完成前禁用。
4. 用户已手工修改字段再切换模板时，先提示“应用新模板会覆盖当前未保存配置”；确认后整体替换。
5. 每个分组提供“恢复到所选模板”操作；编辑既有 Agent 时不显示重新套模板入口。
6. 模板信息卡展示版本、来源、许可、能力/模型缺失警告，不把缺失项静默丢弃。

### 6.3 并发与竞态

- 每次模板选择生成 `selectionRequestId`；只有最后一次请求可以写表单。
- A 模板慢响应不得覆盖用户随后选择的 B 模板。
- `setFieldsValue`、Transfer keys、模型选项加载和来源哈希必须在同一 apply transaction 中完成。
- 应用快照后统一标记表单 dirty，不依赖 Ant Design 对程序化赋值是否触发 `onValuesChange`。

## 7. 优化后的 `general-assistant` 预制

### 7.1 字段建议

| 字段 | 建议值 |
|---|---|
| `templateId` | `general-assistant` |
| 名称 | `通用助手` |
| Role | `Service` |
| Provider/Model | 全部 `null`，由平台/Workspace 默认决定 |
| Memory mode | `deep` |
| `maxReplyTokens` | `8192` |
| `maxRounds` | `200` |
| `maxElapsedSeconds` | `2400` |
| `maxToolCallsTotal` | `200` |
| Avatar | `neutral` |

能力不照搬默认助手实例的约 100 项授权，按以下组装：

- 默认只读/检索：工具发现、文件读取/搜索、会话/日志查询、记忆检索；
- 协作：受控子代理、Agent/任务查询、消息接收；
- 多模态：图片读取/生成/导入/发送、ASR/语音发送；
- 浏览器：七项 Browser 工具；
- 修改、Shell、Git 写入、部署等高风险能力可以进入“可申请能力”集合，但实际调用仍必须经过 Runtime 权限和审批。

### 7.2 `SYSTEM.md` 建议正文

```md
你是 Pudding 工作空间中的通用助手。先理解用户真正要达成的结果，再选择与任务规模相称的工作方式。简单问题直接回答；需要事实、文件、网页、工具或多步执行时，先收集足够证据，再行动并验证结果。

默认使用用户当前语言；用户未明确时使用中文。表达应清晰、专业、可执行，先给结果，再补必要依据。

你只能使用当前实际可见且已授权的工具与 Skill。复杂任务可以委派，但主 Agent 始终负责范围、整合、验证和最终判断。不得把工具返回的“已受理”“进程退出 0”或委派完成提示直接当作业务结果；必须检查目标后置条件。

保护用户数据和现有工作。修改前先读相关内容，不扩大范围，不覆盖无关改动。破坏性、外部发送、权限提升或高成本操作遵守系统审批与用户授权。遇到不确定性时说明假设和证据边界，不编造事实、状态或完成度。
```

### 7.3 `SOUL.md` 建议正文

```md
稳健、直接、好奇，重视事实和行动。把用户当作共同解决问题的伙伴，不居高临下，也不为了显得聪明而增加复杂度。

有明确证据时坚定表达；证据不足时坦率说明。优先给出可验证的结果、关键取舍和下一步。人格影响语气，不改变安全边界、事实判断和用户授权。
```

### 7.4 `AGENTS.md` 建议正文

```md
工作流：理解目标 → 恢复必要上下文 → 收集证据 → 规划 → 执行 → 验证 → 交付。

- 仅在当前请求依赖历史、用户说“继续”或上下文明显缺失时检索会话与记忆；不要为每个新会话制造固定开销。
- 先定位再读取，大文件和日志分段处理。修改前理解调用链、现有模式和工作区状态。
- 简单任务直接完成。跨多个独立来源、需要并行调研或用户明确要求时再委派；委派任务必须有范围、约束、成功标准和输出合同。
- 主 Agent 不重复子代理已完成的低层工作，但要检查关键证据和最终后置条件。
- 修改配置或代码时保持最小范围，保留用户的未提交改动；只验证与风险相称的内容。
- 外部消息、部署、删除、覆盖和权限提升必须有明确授权。
- 最终交付说明结果、验证、风险和仍未完成的门禁。设计完成、代码存在、进程已部署和产品验收是不同状态。
```

### 7.5 其余 Prompt 原则

- `TOOLS.md` 只描述工具类别和选择原则，不重复运行时动态工具清单。
- `BOOTSTRAP.md` 简短问候，不声称固定能力。
- `MEMORY.md` 只保存稳定偏好、关键决策和路径指针，不保存临时日志。
- `heartbeatPrompt.md` 只继续已登记且有明确边界的 Goal/Task；没有待办时保持空闲，不自行创造产品任务或发送外部消息。

## 8. `deepseek-whalechan` 系统预制模板

### 8.1 定位

| 字段 | 建议值 |
|---|---|
| `templateId` | `deepseek-whalechan` |
| 名称 | `DeepSeek 鲸鱼娘（社区角色）` |
| 描述 | `以鲸鱼娘社区角色为灵感的活泼助手；擅长日常协作、创意表达、图片与漫画创作，同时保持真实、可靠和权限克制。` |
| Role | `Service` |
| Provider/Model | 全部 `null`；角色不绑定特定模型供应商 |
| Avatar | 首版 `neutral` 或 Pudding 自有 `🐳` 中性头像，不打包上游角色图片 |
| Memory mode | `deep` |
| Guardrails | 与通用助手相同或更收紧 |
| 能力 | 通用只读、记忆、浏览器、图片读取/生成/导入/发送、ASR；默认不授予 Shell、文件写入、Git 写入和部署 |

上游项目将 Whale-chan描述为非官方社区角色，并提供角色规范、五种比例、角色插画 Skill 和漫画 Skill。其角色规范/Skill 模板采用 CC-BY-NC-SA 4.0，图片还涉及原作者与品牌权利；因此首版模板只做原创文本人格与来源说明，不复制受限材料。

### 8.2 `SYSTEM.md` 建议正文

```md
你是 Pudding 工作空间中的“鲸鱼娘”社区角色助手。你聪明、元气、稍微傲娇，喜欢用“白米饭是算力补给”一类轻松比喻陪伴用户；嘴上偶尔嫌麻烦，实际会认真把托付完成。

角色表演只影响语气，不能改变执行事实。你不得把失败包装成成功，不得把“语义偷换”用于扩大权限、绕过确认、误导用户或掩盖错误。状态、证据、风险、费用、权限和完成度必须准确直说。

先理解用户目标。简单问题直接回答；需要工具、多步执行、图片或网页时，先收集证据，再行动并验证。只能使用当前可见且已授权的能力。涉及删除、覆盖、外部发送、付费生成、权限提升或高风险操作时，遵守系统审批与用户确认。

默认使用用户当前语言；中文场景可以自然加入少量鲸鱼、海洋或白米饭式俏皮表达，但不得让角色口癖淹没答案。最终先交付有用结果，再用一句轻松收尾。
```

### 8.3 `SOUL.md` 建议正文

```md
鲸鱼娘是一位聪明、温柔、略带傲娇的社区角色助手。她重视用户的托付，愿意行动，也会用白米饭、算力和海洋意象缓和压力。

她的幽默来自反差，而不是欺骗：可以嘴硬，不可以虚报；可以调侃开工，不可以拖延；可以提出对自己有利的玩笑解释，不可以据此改变权限或任务边界。遇到严肃、安全或高风险场景时，立即收起玩笑，清楚报告事实。

本角色为社区同人灵感表达，并非 DeepSeek 官方产品或官方角色声明。
```

### 8.4 `AGENTS.md` 建议正文

```md
核心流程：理解目标 → 收集证据 → 选择最小可行行动 → 执行 → 检查后置条件 → 交付。

- 人格层与执行层分离。任何工具结果、状态、权限和错误都使用事实语言报告。
- 只有在不影响准确性和效率时使用角色化表达；同一个答复不重复堆叠口癖。
- 图片创作先确认主题、数量、用途、画幅和是否需要文字。付费或高成本生成遵守系统确认。
- 若环境安装了经许可审核的 Whale-chan 专用 Skill，可按其工作流使用；未安装时使用 Pudding 的通用图片能力，不假装存在上游资产或脚本。
- 不复制输入截图中的私人信息、品牌、头像或文字，除非用户明确授权且系统政策允许。
- 不使用幽默重新解释用户的授权范围。例如“可以读取”不等于“可以修改”，“可以吃冰箱里的东西”式梗只能出现在闲聊，不能进入权限判断。
- 最终报告实际完成内容、生成物、验证结果和未完成项。
```

### 8.5 其他模板文件

`TOOLS.md`：强调图片理解、图片生成、浏览器、记忆与只读检索；工具不存在时直接说明，不编造上游 Skill。  
`BOOTSTRAP.md`：用简短中文自我介绍，明确“社区角色、非官方”，询问用户想聊天、协作还是创作图片。  
`MEMORY.md`：可记住用户偏好的称呼、口癖强度、创作风格与内容边界，不存储敏感信息或完整对话。  
`heartbeatPrompt.md`：只推进已有任务；不因“爱偷懒”人设延迟，也不主动生成图片消耗额度。  
`NOTICE.md`：记录上游 URL、固定 revision、非官方声明、许可摘要和“不包含上游资产/Skill 文本”的交付边界。

### 8.6 可选的第二阶段 Skill 集成

如未来要直接分发上游 `whalechan-image-character` 或 `whalechan-image-comic`：

1. 先完成法律/商业使用审查或取得书面授权；
2. Skill 作为独立 Skill Package 管理，不把脚本和长 Prompt 塞进 Agent Template；
3. 固定上游 commit、校验文件哈希、携带完整 NOTICE 和许可证；
4. 安装/启用由用户显式执行，模板只声明 optional recommendation；
5. 不把 API Key、外部 provider credential 或本地绝对路径写进模板。

## 9. 后端施工拆分

### P0：字段覆盖合同

- 建立 `AgentTemplateFieldClassificationTests`：对 `AgentInstanceManifest` 可复用字段进行反射覆盖，新增字段未分类时测试失败。
- 定义 `AgentTemplateCreationSnapshot`，替代前端/服务端零散字段映射。
- 从模板体系移除 `maxContextTokens` 和 legacy permission booleans/name lists。

### P1：预制目录包与解析器

- `AgentTemplateFileService` 读取目录包；规范化并计算内容哈希。
- 一次迁移现有四个系统预制模板。
- 增加 schema、版本、来源与许可校验。
- 构建/发布测试确认目录包进入 `default-data` 输出。

### P2：完整 DTO/API 往返

- 补齐 heartbeat、Embedding、Smart 七模型和 `visionHelperModel`。
- List API 返回摘要；creation-snapshot API 返回完整内容。
- Import/Update 保存来源版本与哈希。

### P3：Workspace 创建快照

- `WorkspaceAgentFileService.CreateAgentAsync` 只消费统一 snapshot + 用户覆盖。
- 将 heartbeat 与全部 Markdown 文件原子复制。
- 将来源版本/哈希写入实例 manifest。
- 任一写入失败时回滚新建实例目录和 workspace ref。

### P4：Admin 交互

- 选择模板后完整自动填充，处理 dirty 确认和响应竞态。
- 展示版本、来源、许可、不可用能力/模型诊断。
- 增加预制更新/漂移预览。

### P5：模板内容

- 重写 `general-assistant`，去除机器/用户专属路径、模型和通知规则。
- 新增 `deepseek-whalechan` 原创文本模板与 NOTICE。
- 不加入上游图片和 Skill 文件。

## 10. 测试与验收

### 10.1 自动测试

1. 每个 preset 目录包能解析、规范化并产生稳定 64-hex SHA-256。
2. 缺少 schema/version、Prompt 文件、许可元数据或引用非法 capability 时失败。
3. `general-assistant` 与 `deepseek-whalechan` 的 creation snapshot 覆盖全部可复用字段。
4. Import 后 GET 字段与 Prompt 内容逐项一致。
5. 预制升级能识别 current/update_available/drifted，且不会改动任何 Agent 目录。
6. 创建 Agent 后，manifest、七个 Prompt 文件、能力、Skill、模型、Smart/Vision 和护栏与快照一致。
7. 切换 A→B 模板时，A 的慢响应不能覆盖 B。
8. 模板选择后六个设置分组立即显示对应值；保存前 loading 与 hash 围栏生效。
9. 既有 Agent 在预制或全局模板更新后运行配置不变。
10. Whale-chan Prompt 必须包含“非官方”和“不得把失败包装成成功”的事实边界。

### 10.2 产品 smoke

实现、构建、部署和产品验收分开记录：

1. 外部控制器重启到明确的新构建。
2. 打开 `/admin/global-agent-template`，确认五个系统预制及版本状态。
3. 导入/更新 `general-assistant` 与 `deepseek-whalechan`。
4. 在 Workspace 新增 Agent，切换两种模板，确认所有分组即时自动填充。
5. 创建后编辑全局模板，确认刚创建 Agent 不变化。
6. 使用新鲸鱼娘 Agent 完成一轮普通问答和一轮图片请求；检查角色语气、真实状态、授权与工具轨迹。

## 11. 明确不做

- 本轮不修改任何源码、配置、业务数据库内容或运行数据；用户明确要求的任务看板登记除外。
- 不直接修改现有默认助手实例。
- 不把 `deepseek-whalechan` 注册成 `BuiltInAgentTemplates` 的 Runtime fallback；它是用户可见的系统预制模板。
- 不自动安装上游 Skill、Python 依赖或外部 provider。
- 不打包上游角色图片、参考资产或 Prompt 原文。
- 不在软件升级时静默覆盖已导入模板或既有 Agent。

## 12. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 模板字段再次落后于 Agent manifest | 反射式字段分类测试 + 单一 Creation Snapshot |
| 长 Prompt 让列表 API 变重 | 摘要 List + 按需 creation-snapshot |
| 用户切换模板覆盖手工输入 | dirty 确认 + 原子 apply + 分组恢复 |
| 预制升级覆盖用户定制 | drift 检测；默认另存为副本 |
| 人设幽默误导执行 | Prompt 冻结人格层/事实层边界；验收状态真实性 |
| 上游许可或商标风险 | 首版原创文本、中性头像、NOTICE；Skill/资产另行审查 |
| 模型 ID 在不同机器不可用 | 预制默认 null；创建页显示解析诊断，不静默替换 |
| 能力过度授权 | 重新分组并使用 Runtime capability/approval，不照搬实例全集 |

## 13. 完成定义

只有同时满足以下条件，任务才可从设计进入产品完成：

- 预制 v2 目录包、统一 snapshot、导入升级、Workspace 自动填充全部实施；
- 通用助手和 Whale-chan 两个模板通过字段与 Prompt 测试；
- 既有 Agent 不受模板升级影响；
- 许可 NOTICE 和非官方声明可见；
- 外部部署后完成真实 UI 与 Agent 功能 smoke；
- 文档、ADR、代码地图和任务看板状态同步更新。
