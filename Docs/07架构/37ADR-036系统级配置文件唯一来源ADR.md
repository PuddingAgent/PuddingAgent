# 37 ADR-036 系统级配置文件唯一来源

> 状态：**accepted**  
> 日期：2026-05-23  
> 范围：系统配置、LLM 服务商、LLM 模型、LLM profile、全局 Agent 配置、工作区 Agent 配置、能力注册、连接器配置、头像/模板类配置、开发环境下旧 SQLite 配置数据的直接丢弃策略  
> 关联：[08数据模型与配置](08数据模型与配置.md)、[33ADR-032构建门禁与运行态漂移修复方案](33ADR-032构建门禁与运行态漂移修复方案.md)、[35ADR-034Agent头像服务端管理与模板绑定ADR](35ADR-034Agent头像服务端管理与模板绑定ADR.md)、[../Features/系统级配置文件化实施方案](../Features/系统级配置文件化实施方案.md)、[../superpowers/specs/2026-05-18-data-config-e2e-foundation-design.md](../superpowers/specs/2026-05-18-data-config-e2e-foundation-design.md)

---

## 1. 背景

Pudding 已经引入 `data/config/*.json`、`PuddingDataPaths`、`PuddingFileConfigLoader`、`AgentProfileProvider` 和 `LlmProfileResolver`，但平台内仍存在大量数据库主源配置：

- `LlmProviders` / `LlmModels` / `LlmProviderQuotas` 中同时保存服务商、模型、密钥、价格、配额和运行用量；
- `GlobalAgentTemplates` / `WorkspaceAgentTemplates` / `WorkspaceAgents` 中保存 Agent 模板、工作区覆盖和实例配置；
- `PlatformDbContext.SeedBuiltInData()` 仍 seed LLM provider/model、能力、默认工作区和模板；
- 管理端 API 直接读写 `PlatformDbContext`，导致“修改配置”变成“修改 SQLite 数据”；
- Docker 挂载的旧 `data/pudding_platform.db` 可能包含配置数据，与 `data/config` 和 `data/agent-templates` 之间没有清晰主从关系。当前施工环境已重置为干净新环境，因此旧配置库内容不需要迁移。

这已经导致两个具体问题：

1. 用户无法通过检查 `data/` 文件明确知道系统真实配置。
2. 配置表迁移会影响运行时启动，例如 SQLite 对某些 schema mutation 支持有限，配置表结构变化不应阻断软件启动。

本 ADR 决定：**所有系统级、设计级、配置类数据的唯一加载来源必须是本地文本配置文件，数据库不再保存这些数据的 canonical copy。**

---

## 2. 决策

### ADR-036-A：配置类数据只从 `data/` 文件加载

以下数据必须以 `json`、`md`、`jsonl` 或其他本地文本文件作为唯一事实来源：

| 配置类别 | 唯一来源 |
|----------|----------|
| 系统配置 | `data/config/system.json` |
| 安全配置 | `data/config/security.json` |
| 连接器配置 | `data/config/connectors.json` |
| LLM 服务商 | `data/config/llm.providers.json` |
| LLM 模型 | `data/config/llm.providers.json` 中 provider.models |
| LLM profiles / role defaults | `data/config/llm.providers.json` 中 profiles / roles |
| 全局 Agent 模板 | `data/agent-templates/{templateId}/manifest.json` + Markdown |
| 工作区 Agent 实例 | `data/agents/{agentInstanceId}/manifest.json` + `config/*.json` |
| 工作区到 Agent 绑定 | `data/workspaces/{workspaceId}/agents/{agentInstanceId}/ref.json` |
| Agent prompt/persona/tools/memory | `SOUL.md`、`AGENTS.md`、`TOOLS.md`、`BOOTSTRAP.md`、`MEMORY.md` |
| 能力/工具注册 | `data/config/capabilities.json` 或 template `permissions.json` |
| 系统头像/视觉资源目录 | `data/config/avatars.json` + `data/assets/...`，或 packaged default-data |

SQLite 中已存在的同类配置数据视为 **discarded data**。开发环境直接丢弃，不导出、不迁移、不兼容读取。

### ADR-036-B：数据库只保存运行态、索引态和业务态

SQLite 仍然可以保存非配置数据：

| 数据类别 | 是否可进数据库 | 说明 |
|----------|----------------|------|
| 用户、角色、登录认证 | 可以 | 属于平台业务态，不是系统设计配置。 |
| Session、ChatMessage、事件日志 | 可以 | 运行态和历史记录。 |
| RuntimeActivity、diagnostics、trace | 可以 | 可观测性数据。 |
| EventQueue、SubAgentRun | 可以 | 运行调度状态。 |
| Memory 数据库和检索索引 | 可以 | 运行时知识/记忆，不是静态配置。 |
| Token 使用量 | 可以 | 运行计量。 |
| LLM provider/model 定义 | 不可以 | 必须来自 `llm.providers.json`。 |
| LLM quota limit | 不可以 | 配额上限是配置，写在文件；实际用量可进 DB。 |
| Agent 模板/实例定义 | 不可以 | 必须来自 `data/agent-templates` / `data/agents`。 |
| 能力定义 | 不可以 | 能力 registry 是配置；授权/执行记录可进 DB。 |

如果为了列表查询性能需要索引，可以建立 `ConfigProjection` 或内存 cache，但它只能是 derived projection，可删除、可重建、不可作为配置来源。

### ADR-036-C：禁止配置双写

管理端 API 不得同时写 DB 和文件来维护配置。所有配置修改必须写入文件：

```text
Admin UI/API -> File-backed Config Service -> atomic file write -> reload/invalidate cache
```

允许产生以下附属文件：

- `.bak` 或 `data/backups/config/{timestamp}/...`；
- `data/runtime/config-index/*.json` 派生索引；
- `data/logs/system/config-changes.jsonl` 审计日志。

但这些都不是 canonical source。

### ADR-036-D：旧 SQLite 配置数据直接丢弃

已有 SQLite 中的配置数据不再迁入新运行路径。施工不提供 best-effort export，也不提供“从旧 SQLite 生成配置文件”的命令。

运行时加载顺序中不得出现“如果文件缺失则从 SQLite 读取配置”的 fallback。

如果文件缺失，系统只能：

1. 从 `Source/PuddingAgent/default-data` 复制安全默认模板；
2. 或启动失败并给出明确错误；
3. 或在 bootstrap 初始化流程中生成文件。

### ADR-036-E：配置加载必须 fail fast

启动顺序必须改为：

1. 解析 `PUDDING_DATA_ROOT`，默认 Docker `/app/data`，本地 repo `data`；
2. 创建 `data/` 标准目录；
3. 复制缺失的 `default-data` 模板；
4. 加载并验证 `data/config/*.json`；
5. 加载并验证 `data/agent-templates`、`data/agents`、`data/workspaces`；
6. 构建内存 registry / derived projection；
7. 启动 Web/API/Runtime。

任何必需配置缺失、JSON 损坏、profile 指向不存在 provider/model、agent 指向不存在 template，都应阻断启动并输出可操作错误。

### ADR-036-F：Admin 配置 API 改为 file-backed

以下 API 族需要从 DB-backed 改为 file-backed：

- `/api/llm/providers`
- `/api/llm/providers/{providerId}/models`
- `/api/global-agent-templates`
- `/api/workspace-agent-templates`
- `/api/workspaces/{workspaceId}/agents`
- 能力、头像、连接器、系统设置相关 API

写操作必须采用原子写：

```text
write temp file -> fsync/flush -> validate -> replace target -> append audit log -> invalidate cache
```

Windows 和 Linux 下都必须避免半写文件成为下一次启动的配置来源。

### ADR-036-G：迁移不应再通过 DB schema 删除配置列

开发环境允许直接删除旧 SQLite 数据库文件并重建运行态数据库。代码层面不要求通过 EF migration 去 drop 配置表或 drop column。

原因：

- SQLite 对复杂 schema mutation 支持弱；
- 配置数据已被弃用，不值得用 destructive migration 阻断启动；
- 干净环境可以直接重建数据库，删除表列不是完成文件化的必要条件。

如果后续存在生产/长期数据环境，再单独制定数据保留策略；本 ADR 的当前施工假设是开发环境可丢弃旧 SQLite 配置数据。

---

## 3. 目标目录

```text
data/
  config/
    system.json
    security.json
    connectors.json
    llm.providers.json
    capabilities.json
    avatars.json
  agent-templates/
    {templateId}/
      manifest.json
      permissions.json
      SOUL.md
      AGENTS.md
      TOOLS.md
      BOOTSTRAP.md
      MEMORY.md
  agents/
    {agentInstanceId}/
      manifest.json
      config/
        llm.json
        capabilities.json
        memory.json
      workspace/
      state/
      logs/
  workspaces/
    {workspaceId}/
      manifest.json
      agents/
        {agentInstanceId}/
          ref.json
  assets/
    avatars/
  runtime/
    config-index/
    migrations/
    traces/
  logs/
    system/
    diagnostics/
    sessions/
  databases/
  backups/
```

---

## 4. 影响范围

### 必改服务

```text
Source/PuddingCore/Configuration/PuddingDataPaths.cs
Source/PuddingCore/Configuration/PuddingConfigModels.cs
Source/PuddingCore/Configuration/PuddingFileConfigLoader.cs
Source/PuddingCore/Agents/AgentProfileProvider.cs
Source/PuddingCore/Configuration/LlmProfileResolver.cs
Source/PuddingPlatform/Controllers/Api/LlmProviderApiController.cs
Source/PuddingPlatform/Controllers/Api/LlmModelApiController.cs
Source/PuddingPlatform/Controllers/Api/GlobalAgentTemplateApiController.cs
Source/PuddingPlatform/Controllers/Api/WorkspaceAgentTemplateApiController.cs
Source/PuddingPlatform/Controllers/Api/WorkspaceAgentApiController.cs
Source/PuddingPlatform/Services/AgentTemplateProvider.cs
Source/PuddingPlatform/Services/AgentLLMConfigResolver.cs
Source/PuddingPlatform/Data/PlatformDbContext.cs
Source/PuddingAgent/Program.cs
build-and-up.ps1
```

### 保留数据库但重新界定语义

```text
AppUsers / AppRoles / Team / Workspace
ChatMessages / SessionEventLogs / SessionSubAgents
RuntimeActivities / EventQueue / SubAgentRuns
TokenUsageStats
Memory database
```

### Discarded 配置数据

```text
LlmProviders / LlmModels
GlobalAgentTemplates / WorkspaceAgentTemplates
WorkspaceAgents
Capabilities
AgentAvatars
```

开发环境可以直接删除旧 SQLite 文件或清空这些表。即使表仍存在，也不得再被运行时配置解析读取。

---

## 5. 验收标准

1. 删除旧 SQLite 文件或清空其中 LLM provider/model/template/agent 表后，系统仍能从 `data/` 文件启动。
2. 修改 `data/config/llm.providers.json` 后，Admin LLM 页面和 Runtime 解析结果一致。
3. 修改 `data/agent-templates/{id}/manifest.json` 或 Markdown 后，Agent 行为解析使用文件内容。
4. 新建/编辑工作区 Agent 时，写入 `data/agents` 和 `data/workspaces`，不写配置表。
5. `build-and-up.ps1` 不依赖 `.env`，并能报告缺失配置文件。
6. 常规启动迁移不再因为配置表 drop column/drop table 失败而阻断服务。
7. 配置写操作有审计和 JSON validation；是否生成配置文件备份由实现决定，不为旧 SQLite 数据提供备份。
8. 运行态数据仍正常写入 SQLite / JSONL / memory 存储。

---

## 6. 后续

本 ADR 生效后，Dev 按 [系统级配置文件化实施方案](../Features/系统级配置文件化实施方案.md) 分阶段施工。当前开发环境按干净环境处理：旧 SQLite 配置数据可直接丢弃，重点是让运行时、API 和 Admin UI 完全脱离配置表。
