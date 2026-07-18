# 45 ADR-044 Agent 模板存储链路归一化

> 状态：**accepted / P0-P3 implemented, P4 partial**  
> 日期：2026-05-24  
> 触发条件：全局 Agent 模板编辑页出现能力授权保存丢失；进一步审计发现 Admin UI、DB API、file-backed service、运行时读取链路存在事实来源漂移。  
> 关联：[37ADR-036系统级配置文件唯一来源ADR](./37ADR-036系统级配置文件唯一来源ADR.md)、[41ADR-040Agent模板编辑SettingsSidebarNavigationADR](./41ADR-040Agent模板编辑SettingsSidebarNavigationADR.md)、[35ADR-034Agent头像服务端管理与模板绑定ADR](./35ADR-034Agent头像服务端管理与模板绑定ADR.md)

---

## 1. 背景与问题

ADR-036 已经决定全局 Agent 模板属于系统级配置，唯一事实来源应为：

```text
data/agent-templates/{templateId}/manifest.json
data/agent-templates/{templateId}/SOUL.md
data/agent-templates/{templateId}/TOOLS.md
data/agent-templates/{templateId}/BOOTSTRAP.md
data/agent-templates/{templateId}/AGENTS.md
data/agent-templates/{templateId}/MEMORY.md
```

但当前实现仍然存在三条并行链路：

| 链路 | 当前事实 | 问题 |
|------|----------|------|
| Admin API | `GlobalAgentTemplateApiController` 直接读写 `PlatformDbContext.GlobalAgentTemplates` | 与 ADR-036 file-only 决策冲突，保存结果不一定被运行时读取。 |
| File service | `AgentTemplateFileService` 已能读写 `data/agent-templates` | 未被 `/api/global-agent-templates` 使用，且字段覆盖不完整。 |
| Runtime | `AgentTemplateProvider` 优先读文件，失败才回退 DB；`ChatApiController` 能力和 Skill 仍读 DB | 同一模板的 persona、能力、Skill、LLM 路由可能来自不同事实来源。 |

一次具体缺陷暴露了这个问题：前端 Transfer 修改高权限能力后，因为 `selectedCapabilityIds` 没有注册为 Form 字段，`validateFields()` 保存时丢字段，后端写成 `[]`。虽然这个单点已经修复，但更大的问题是模板存储链路没有统一契约。

---

## 2. 审计结论

### 2.1 前端字段链路

| 字段组 | 字段 | 状态 | 问题 |
|--------|------|------|------|
| 基础信息 | `templateId`, `name`, `role`, `description`, `avatarId`, `isEnabled`, `sortOrder` | 基本注册完整 | `openEdit` 未先 `resetFields()`，可选字段存在脏值残留风险。 |
| 能力与 Skill | `selectedCapabilityIds`, `selectedSkillPackageIds` | 已补隐藏字段注册 | `defaultCapIds` 依赖异步能力加载，创建过早保存可能漏默认能力。 |
| Prompt 与个性 | `systemPrompt`, `personaPrompt`, `toolsDescription`, `bootstrapTemplate`, `userPromptTemplate` | 已注册 | `AGENTS.md` / `MEMORY.md` 对应的 `agentsPrompt` / `memoryPrompt` 后端存在，前端不可编辑。 |
| 模型与记忆 | `preferredProviderId`, `preferredModelId`, `memoryLlmProviderId`, `memoryLlmModelId`, `memorySearchMode`, `reasoningEffort` | 已注册 | DTO 有 `consciousProfileId` / `subconsciousProfileId`，前端类型缺失，语义与 provider/model 混用。 |
| 执行护栏 | `maxRounds`, `maxElapsedSeconds`, `maxToolCallsTotal`, `containerImage`, `maxContextTokens`, `maxReplyTokens` | 已注册 | `maxContextTokens` / `maxReplyTokens` API 必填，但 UI 缺 required 校验。 |

### 2.2 后端/API 字段链路

| 字段 | DTO | Entity | DB API | File service | Runtime |
|------|-----|--------|--------|--------------|---------|
| 基础信息 | 有 | 有 | 保存 | 部分保存 | 部分读取 |
| 头像 | `avatarId/avatarUrl/avatarName` | `AvatarId` | 保存并回显 URL | 保存并回显 | workspace agent 解析使用 |
| `systemPrompt/userPromptTemplate` | 有 | 有 | 保存 | 不保存 | DB fallback 可读，file 主路径不可读 |
| `persona/tools/bootstrap` | 有 | 有 | 保存 | 保存 Markdown | file 主路径可读 |
| `agentsPrompt/memoryPrompt` | 有 | 有 | 保存 | 不保存 | file 主路径未读 |
| 能力 | `selectedCapabilityIds` | JSON | 保存 | 保存到 `Capabilities.AllowedToolIds` | `ChatApiController` DB 路径读 DB，file 路径未统一 |
| Skill 包 | `selectedSkillPackageIds` | JSON | 保存 | 不保存 | `ChatApiController` DB 路径读 DB |
| LLM provider/model | 有 | 有 | 保存 | 保存 | resolver 读 DB |
| profile alias | `consciousProfileId/subconsciousProfileId` | 无独立列 | 被错误映射为 provider id | 保存到 manifest `DefaultLlmProfiles` | profile resolver 路径可用但 DB API 语义不成立 |
| 护栏 | `maxRounds/maxElapsedSeconds/maxToolCallsTotal` | 有 | 保存 | 不保存 | subagent/loop 读取链路不完整 |
| 容器镜像 | 有 | 有 | 保存 | 不保存 | 运行时使用链路不完整 |

### 2.3 根因

1. `/api/global-agent-templates` 仍以 DB 为主源，违背 ADR-036。
2. `GlobalAgentTemplateDto` 同时承载 DB 字段和 file manifest 字段，但两边映射不等价。
3. `AgentTemplateFileService` 未覆盖完整 DTO 字段，且未接入当前 Admin API。
4. Runtime 不同子系统分别读取 file、DB、内置 fallback，导致保存结果和运行结果可能不同。
5. 前端表单依赖可选字段和异步数据，缺少统一的 request normalization。

---

## 3. 决策

### ADR-044-A：全局 Agent 模板 API 改为 file-backed

`/api/global-agent-templates` 的 List/Get/Create/Update/Delete 必须委托 `AgentTemplateFileService`。`GlobalAgentTemplates` DB 表只允许作为历史兼容 fallback 或可删除派生投影，不再作为保存主源。

### ADR-044-B：DTO 字段必须有唯一落点

每个 `UpsertGlobalAgentTemplateRequest` 字段必须明确落到以下位置之一：

| 字段类别 | 落点 |
|----------|------|
| manifest 元数据 | `manifest.json` |
| persona/prompt 文本 | `SOUL.md`, `TOOLS.md`, `BOOTSTRAP.md`, `AGENTS.md`, `MEMORY.md` |
| 能力授权 | `manifest.json.capabilities.allowedToolIds` |
| Skill 包 | `manifest.json.skillPackageIds` 或独立 `skills.json`，首选 manifest |
| 模型路由 | `manifest.json.preferredProviderId/preferredModelId/memoryLlmProviderId/memoryLlmModelId/defaultLlmProfiles` |
| 执行护栏 | `manifest.json.maxRounds/maxElapsedSeconds/maxToolCallsTotal/containerImage` |

禁止新增“前端有字段、DTO 有字段、file service 不保存”的半链路字段。

### ADR-044-C：Runtime 读取必须收敛到模板配置服务

运行时解析 persona、能力、Skill、模型和护栏时，不得在 Controller 中分别查询 `GlobalAgentTemplates`。需要通过一个统一的模板配置读取服务获得 resolved template。DB fallback 只能用于迁移过渡，并必须记录 warning。

### ADR-044-D：Admin 表单保存前必须 normalize

前端保存不得直接发送裸 `validateFields()` 结果。保存前必须执行：

```text
normalize(values, grantTargetKeys, skillTargetKeys, defaultCapIds)
```

保证：

- `selectedCapabilityIds = unique(defaultCapIds + grantTargetKeys)`
- `selectedSkillPackageIds = skillTargetKeys`
- token、护栏、启用状态、排序字段有默认值
- create/edit 都先 reset，再 set 完整初始值
- 异步依赖未加载时禁用保存或阻止创建

### ADR-044-E：测试以字段往返为验收核心

每个可编辑字段必须有至少一层 round-trip 测试覆盖：

1. 前端：编辑表单 normalize 后请求体字段完整。
2. API：PUT 后 GET 返回相同字段。
3. File service：manifest/Markdown 写入后读取一致。
4. Runtime：resolved template 使用保存后的能力、Skill、模型和 persona。

### ADR-044-F：Agent 自维护必须经过实例配置写入权威

Agent 可以通过 Low 风险 `agent_state` 工具检查、诊断、读取和更新自己的实例级
Markdown，但必须满足：

1. Agent 身份只取自 `ToolExecutionContext.AgentInstanceId`，参数不能指定其他 Agent。
2. 只允许 `SOUL.md`、`AGENTS.md`、`TOOLS.md`、`BOOTSTRAP.md`、
   `MEMORY.md`、`heartbeatPrompt.md` 六个白名单文件。
3. Tool 不接收物理路径，不直接使用通用文件工具，也不写历史 `persona/` 目录。
4. `WorkspaceAgentFileService : IAgentSelfMaintenanceService` 是写入权威；
   使用原子文件替换、共享写锁和可选 SHA-256 乐观并发检查。
5. manifest 文件引用缺失时，更新操作将其修复为规范文件名；读取不会跟随异常引用。
6. 不允许通过该工具修改 LLM 路由、能力授权、Skill、密钥、工作空间归属或其他 Agent。
7. 更新从下一轮上下文组装开始生效；当前正在执行的不可变快照不被回写。

---

## 4. 施工方案

### P0：前端保存链路硬化

状态：已实施。

- `openCreate/openEdit` 均先 `form.resetFields()`。
- 新增 `buildGlobalAgentTemplateRequest(values, state)` 纯函数。
- 保存时显式合成能力、Skill 和默认值。
- `maxContextTokens/maxReplyTokens` 增加必填校验。
- `api.ts` 补齐 `agentsPrompt`, `memoryPrompt`, `consciousProfileId`, `subconsciousProfileId` 类型。

### P1：File service 字段覆盖

状态：已实施。

- 扩展 `AgentTemplateManifest`，补齐：
  - `SkillPackageIds`
  - `MaxRounds`
  - `MaxElapsedSeconds`
  - `MaxToolCallsTotal`
  - `ContainerImage`
  - `SystemPrompt`
  - `UserPromptTemplate`
- `AgentTemplateFileService` Create/Update/MapToDto 写全字段。
- 写入/读取 `AGENTS.md` 和 `MEMORY.md`。

### P2：全局模板 API 切换 file-backed

状态：已实施。

- 将 `GlobalAgentTemplateApiController` 构造依赖改为 `AgentTemplateFileService`。
- List/Get/Create/Update/Delete 委托 file service。
- 保留 DB-backed 逻辑为临时 fallback 时必须记录 warning，并不得用于写操作。

### P3：Runtime 模板解析收敛

状态：已实施核心路径。`ChatApiController` 的全局模板能力、Skill 包、ReasoningEffort 解析已改为读取 `AgentTemplateFileService`；`AgentLLMConfigResolver` 的全局模板 LLM/记忆模型路由已改为读取文件模板。工作区模板仍保留 DB 覆盖；`AgentTemplateProvider` 保留 DB fallback 作为旧数据兼容路径。

- `ChatApiController.ResolveCapabilitiesAsync` 和 Skill 包解析改为从 file-backed template DTO / resolved template 读取。
- `AgentTemplateProvider` 保持 file-first，但去除 Global DB 作为普通路径。
- `AgentLLMConfigResolver` 增加 file-backed template 读取，DB 只作过渡 fallback。

### P4：回归与迁移保护

状态：部分实施。已覆盖前端 normalize、file service round-trip、API file-backed 往返；runtime resolved template 仍需补测试。

- 前端表单保存测试覆盖每个分组。
- File service round-trip 测试覆盖所有字段。
- API PUT/GET 测试覆盖能力、Skill、Prompt、模型、护栏。
- Runtime capability resolve 测试确认保存后的 `cap-python` 可进入工具策略。

### P5：Agent 私有状态自维护

状态：已实施。

- 用 `AgentStateTool` 替换未注册且写入旧 `persona/` 目录的 `AgentUpdateTool`。
- `inspect/diagnose` 返回 manifest 引用、文件存在性、长度、SHA-256 和健康问题。
- `read` 返回受控文档内容与截断元数据。
- `update` 全量替换单个白名单文档，支持 `expectedSha256` 防止陈旧覆盖。
- 权限为 Low，不设置 `RequiresFileWrite` 或 `Destructive`，由权限策略识别为
  `low-risk agent-private state tool`，无需运行时授权。

---

## 5. 验收标准

1. 在 `/admin/global-agent-template` 修改任一可编辑字段，保存后刷新页面仍可回显。
2. 修改高权限能力 `cap-python` 后，新会话运行时能获得 `python` 工具授权。
3. 修改 `SOUL.md`、`TOOLS.md`、`BOOTSTRAP.md`、`AGENTS.md`、`MEMORY.md` 对应字段后，文件落盘且 Runtime 使用新内容。
4. 修改模型、记忆模型、推理深度和护栏后，API GET 与运行时解析结果一致。
5. 删除或重建 SQLite 配置表不会导致全局 Agent 模板配置丢失。
6. `agent_state` 不能选择其他 Agent 或传入路径；更新后根目录 Markdown 和 manifest
   引用一致，下一轮运行时读取新内容。
