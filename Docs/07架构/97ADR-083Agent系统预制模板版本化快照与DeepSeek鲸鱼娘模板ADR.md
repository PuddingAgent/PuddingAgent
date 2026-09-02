# 97 ADR-083 Agent 系统预制模板版本化快照与 DeepSeek 鲸鱼娘模板

> 状态：**Proposed**  
> 日期：2026-09-02  
> 决策范围：系统预制模板格式、导入/升级生命周期、Workspace Agent 创建快照、DeepSeek Whale-chan 社区角色模板  
> 关联：[Agent 系统预制模板完整快照与 DeepSeek 鲸鱼娘模板设计方案](../Features/Agent系统预制模板完整快照与DeepSeek鲸鱼娘模板设计方案.md)、[ADR-036 系统级配置文件唯一来源](37ADR-036系统级配置文件唯一来源ADR.md)、[ADR-040 Agent 模板编辑导航](41ADR-040Agent模板编辑SettingsSidebarNavigationADR.md)、[ADR-044 Agent 模板存储链路归一化](45ADR-044Agent模板存储链路归一化ADR.md)、[ADR-052 插件化工具系统契约](53ADR-052插件化工具系统契约冻结ADR.md)、[ADR-077 主代理原生视觉](92ADR-077主代理原生视觉理解与多模态消息链路ADR.md)

## 1. 背景

Pudding 已经确立三层配置边界：

1. 软件输出中的系统预制模板；
2. `data/agent-templates` 中已导入、可编辑的全局模板；
3. `data/agents` 中自包含、独立演进的 Workspace Agent。

现有“默认助手”实例的配置和 Prompt 已持续演进，出现了 Embedding、七类 Smart 子代理模型、`visionHelperModel`、heartbeat、完整 Markdown persona、能力与护栏等字段。当前系统预制仍使用单个扁平 JSON，并且 `AgentTemplatePreset`、`GlobalAgentTemplateDto`、创建页映射和实例 manifest 的字段集合不一致。

Workspace 创建页已经可以在选择模板后调用 `applyTemplateSnapshot()`，但只能应用 DTO 当前拥有的字段。系统预制导入后也没有版本和显式刷新机制；软件中预制变更不会反映到已导入模板，用户无法区分“已是最新”“可更新”和“已漂移”。

同时，用户要求根据社区项目 [Neko3000/deepseek-whalechan](https://github.com/Neko3000/deepseek-whalechan) 增加一个预制模板。该项目明确为非官方社区项目；代码采用 MIT，而规范文档与 Skill 模板采用 CC-BY-NC-SA 4.0，角色图片还涉及原作者和品牌权利。

## 2. 决策

### ADR-083-A：系统预制使用目录包，不再使用扁平单 JSON

系统预制的权威格式改为：

```text
default-data/agent-template-presets/{presetId}/
  manifest.json
  SYSTEM.md
  SOUL.md
  AGENTS.md
  TOOLS.md
  BOOTSTRAP.md
  MEMORY.md
  heartbeatPrompt.md
  NOTICE.md (optional)
```

`manifest.json` 使用 `pudding.agent-template-preset/v2`，包含语义版本、来源/许可、模型、能力、Skill、记忆与护栏。Prompt 长文本只保存在 Markdown 文件中。

实施时一次迁移现有预制，不保留 `*.json` 与目录包双读兼容层。

### ADR-083-B：模板字段以统一 Creation Snapshot 为合同

增加 `AgentTemplateCreationSnapshot` 作为系统预制、全局模板、Admin 创建表单和 `WorkspaceAgentFileService` 之间的唯一复制合同。

它必须覆盖：

- 角色、描述、头像；
- System/User Prompt、SOUL/AGENTS/TOOLS/BOOTSTRAP/MEMORY/heartbeat；
- 主模型、记忆模型、Embedding、七类 Smart 模型与 `visionHelperModel`；
- 记忆模式、能力、Skill 和执行护栏；
- 来源 preset ID、版本、内容 SHA-256、许可和 attribution；
- 不可用模型、能力和 Skill 的诊断。

每个 `AgentInstanceManifest` 字段必须在自动测试中分类为：模板可复制、实例专属或运行派生。新增未分类字段时构建失败。

### ADR-083-C：`maxContextTokens` 不属于模板或 Agent 实例

模型上下文容量只由选中 Provider Model 的配置提供。预制 v2、Creation Snapshot、Workspace Agent 请求和实例 manifest 不复制 `maxContextTokens`。

Agent 只保存用于收紧执行的 `maxReplyTokens`；有效上下文与输入预算继续由 provider/model 能力解析。

### ADR-083-D：预制权限只保存 capability selection

预制 v2 不再保存 `allowFileWrite`、`allowShellExecution`、`allowNetworkAccess` 和 `allowedToolNames`。工具可见性、风险属性和审批由 capability catalog 与 Runtime 策略决定。

模板可以选择高风险 capability，使能力在适当授权后可用；模板本身不能绕过运行时审批。

### ADR-083-E：导入全局模板保存来源版本和哈希

已导入全局模板保存：

```text
originPresetId
originPresetVersion
originPresetContentHash
lastSyncedPresetContentHash
```

系统预制列表区分：`available/current/update_available/drifted/invalid`。

预制升级必须先展示差异：

- 未漂移模板可显式更新；
- 已漂移模板默认另存为副本；
- 覆盖用户编辑必须二次确认；
- 更新全局模板永远不修改既有 Agent。

### ADR-083-F：选择模板立即原子填充全部创建字段

Workspace Agent 创建页选择模板后，按需获取完整 Creation Snapshot，并一次性更新六个设置分组。保存按钮在快照应用完成前禁用。

若表单已有手工修改，切换模板前确认覆盖；异步请求以最后一次选择为准，旧响应不得回写。创建请求携带来源版本和内容哈希，模板在选择后发生变化时返回 `409 template_snapshot_changed`。

Agent 创建后：

- `sourceTemplateId/version/hash` 只用于审计；
- 来源模板在编辑模式只读；
- 运行时不回查模板；
- Agent 的 Prompt、能力、模型、Skill 和护栏均以实例目录为准。

### ADR-083-G：通用助手只吸收可复用原则，不复制默认实例全文

`general-assistant` 的新 Prompt 吸收默认助手的证据优先、先读后改、委派整合、结果验证、记忆分层和状态区分经验。

必须删除或禁止进入系统预制的内容：

- 本机绝对路径；
- 固定 provider/model；
- 固定飞书收件人和免打扰时间；
- 强制每次委派、提交、推送或主动修复某个仓库；
- 对当前不可见工具或 Skill 的硬编码假设。

系统预制模型字段默认 `null`，由平台或 Workspace 决定。

### ADR-083-H：新增 `deepseek-whalechan` 用户可见系统预制

新增：

```text
templateId: deepseek-whalechan
name: DeepSeek 鲸鱼娘（社区角色）
role: Service
```

该模板是日常协作与创意/图片助手，不是 Runtime 内置 fallback，也不加入 `BuiltInAgentTemplates`。

人格合同：

- 聪明、元气、稍微傲娇，使用鲸鱼、海洋和白米饭式轻松表达；
- 嘴上可以嫌麻烦，实际必须认真完成任务；
- 人格只影响语气，不影响事实、权限、状态和验收；
- 不得把失败包装成成功；
- 不得以“语义偷换”扩大授权、绕过确认或误导用户；
- 严肃、安全或高风险场景立即收起玩笑。

首版默认使用平台模型和中性头像，授予只读、记忆、浏览器和多模态能力，不默认授予 Shell、文件写入、Git 写入或部署。

### ADR-083-I：首版不分发上游受限内容

基于上游许可与权利说明，首版：

- 只包含 Pudding 原创 Prompt；
- 在 NOTICE 中记录上游 URL、固定 revision、非官方声明和灵感来源；
- 不复制上游角色图片、参考资产、Skill 文本、Prompt 模板或脚本；
- 不暗示 DeepSeek 官方背书；
- 使用 Pudding 自有中性头像。

未来若分发上游 Skill 或资产，必须作为独立 Skill Package，经法律/商业使用审查或书面授权，固定 revision 与哈希，并携带完整许可证和 attribution。模板只能把它声明为可选能力，不能自动安装。

### ADR-083-J：两个“内置”概念保持边界

- `default-data/agent-template-presets` 是用户可见、可导入的系统预制权威。
- `BuiltInAgentTemplates` 是 Runtime 低层内置角色/fallback 的代码权威。

不得为同一 template ID 在两处维护不同定义。`deepseek-whalechan` 只属于系统预制。若未来 Runtime 需要按全局模板启动子代理，应读取统一模板服务的不可变快照，不复制到另一个同名静态定义。

## 3. 被否决方案

### 3.1 把当前默认助手 manifest 原样复制成预制

否决。它包含本机路径、固定模型、飞书行为、仓库专属规则和过宽能力，不具备跨 Workspace/机器复用性。

### 3.2 只往现有 `general-assistant.json` 追加字段

否决。长 Prompt JSON 可维护性差，且不能解决字段覆盖、版本、哈希、刷新和前端竞态。

### 3.3 软件升级时自动覆盖 `D:\data\agent-templates`

否决。全局模板是用户数据；自动覆盖会破坏用户定制，也混淆软件预制与正式模板主源。

### 3.4 更新全局模板后让 Agent 运行时继续继承

否决。会破坏运行可复现性和实例独立演进合同。

### 3.5 直接打包 deepseek-whalechan 上游 Skill 和图片

否决。规范/Skill 是 CC-BY-NC-SA，角色图片还涉及原作者和品牌权利；未完成授权审查前不能作为默认产品资产分发。

### 3.6 把鲸鱼娘的“语义偷换”用于实际工具行为

否决。角色梗不能改变权限解释、任务完成判定或错误报告。

## 4. 后果

正向：

- 新增 Agent 字段不会再悄悄从模板链路丢失；
- 选择模板即可真正完整填充；
- 系统预制可以随软件演进，同时保护用户全局模板和既有 Agent；
- Prompt 结构更易审阅、测试和版本化；
- Whale-chan 模板具有鲜明人格，但不降低 Pudding 的执行可信度；
- 第三方来源、许可和非官方声明有明确落点。

成本：

- 需要迁移四个现有预制及相关测试；
- DTO/API 与 Workspace 创建服务需要一次字段收敛；
- Admin 需要版本/漂移 UI 和异步快照围栏；
- 上游 Skill/资产集成被推迟到独立许可审查之后。

## 5. 实施门禁

1. 字段分类测试覆盖所有 Agent 实例字段。
2. 系统预制目录包解析、内容哈希和发布输出测试通过。
3. Import/GET/Create 的字段与七个 Prompt 文件完整往返。
4. UI 模板选择自动填充全部六组配置，并通过 A→B 异步竞态测试。
5. 预制升级不改动既有 Agent 的测试通过。
6. Whale-chan NOTICE、非官方声明和执行真实性 Prompt 断言通过。
7. 不包含上游角色图片、Skill 文本或 Prompt 原文。
8. 外部部署后完成 UI 与真实 Agent smoke，才能将 ADR 从 Proposed 更新为 Accepted/Implemented。

## 6. 当前状态

本 ADR 仅冻结设计。2026-09-02 本轮除按用户要求通过外部任务 API 新增一条任务看板记录外，未修改源码、配置、业务数据库内容或 `D:\data` 运行数据；尚未完成构建、部署或产品验收。
