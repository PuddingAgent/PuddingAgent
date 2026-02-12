# Agent 模板与客户端

> **2026-06-12**：客户端 = 内嵌 Web UI。Agent 模板统一为全局模板库；Workspace 只管理 Agent 实例，不再暴露 Workspace 级模板管理。

## Agent 模板

Agent 模板只有一个全局层级，配置主源为 `data/agent-templates/{templateId}/`：

### 系统预制模板（AgentTemplatePreset）

系统预制模板是软件随包资源，不属于用户数据目录的主源：

- 源码位置：`Source/PuddingAgent/default-data/agent-template-presets/*.json`
- 运行位置：应用输出目录 `default-data/agent-template-presets/*.json`
- 读取方式：`/api/global-agent-templates/presets` 直接读取软件输出物
- 导入方式：用户在 `/global-agent-template` 点击“导入预制”，系统把 JSON 转成正式全局模板并写入 `data/agent-templates/{templateId}/`

预制模板不复制到启动参数指定的 `data` 目录。`data` 目录只保存已导入、可编辑、可备份的正式模板。后续新增软件内置助手时，只需要新增一个 preset JSON 文件并随软件发布。

首批系统预制模板：

- `general-assistant`：通用助手，Service
- `research-assistant`：研究助手，Service
- `code-assistant`：代码助手，Service
- `workspace-audit-assistant`：审计助手，Audit；用于审计工作空间内其他 Agent 的计划、执行过程、工具调用、证据链和风险，不是代码审计模板

### 全局 Agent 模板（GlobalAgentTemplate）

定义系统内置 Agent 的角色、系统提示词、默认能力和偏好，对所有 Workspace 可见：

- 角色类型：Service / Task / Audit / Custom
- 系统提示词
- 首选模型与提供商
- 能力（Capability）引用
- Skill Package 引用
- 记忆策略与 Token 限制

前端页面：`/global-agent-template`

Workspace 内只创建和管理 Agent 实例。实例可引用全局模板，并按实例保存运行期覆盖项，例如名称、描述、首选模型和系统提示词覆盖。

> **2026-06-12 变更**：Workspace 详情页移除“模板管理”Tab，`/workspace-agent-template` 旧入口保留隐藏重定向用于兼容旧链接。旧版 `/agent-template` 统一模板页面已移除，其 API（`listAgentTemplates`、`getAgentTemplate`、`AgentTemplateType`、`AgentTemplateDefinition`）同步清理。详见 [QA-2026-05-03-RemoveOldAgentTemplate](../QA/QA-2026-05-03-RemoveOldAgentTemplate.md)。

### 模板与 Agent 实例字段归属

全局模板负责可复用的蓝图字段：

- 模板 ID、模板名称、角色类型、模板描述
- 模板角色定义、默认语气与边界、工具使用约定
- 能力与 Skill 授权、执行护栏
- 默认模型策略、默认记忆搜索模式
- 默认头像、模板启用状态、排序权重

Workspace Agent 实例负责场景化与个性化字段：

- Agent 名称、实例职责、所属 Workspace
- 来源全局模板
- 覆盖头像、高级 Prompt 覆盖、模型覆盖
- 实例启用状态、运行状态、记忆归属

未填写实例覆盖项时，运行时应继承来源模板默认值；实例配置不复制整份模板。

## 内嵌 Web UI

- 前端使用 React/TypeScript 开发
- 构建产物嵌入 ASP.NET Core 的 wwwroot
- 一个进程同时提供 API 和 UI
- 用户双击启动后浏览器直接打开

### 交互入口分层

> **2026-05-03**：Chat 页从后台 ProLayout 中剥离，作为 Pudding 的主交互界面独立呈现。

- `/chat` 是用户登录后的主界面，使用独立 Chat Shell，不继承后台侧栏、顶栏、Footer 或水印。
- 后台管理区定位为 Pudding Console，仅承载 Agent、工作空间、技能、模型资源和运行时配置。
- Workspace 详情页只保留配置与管理 Tab，不再内嵌 Chat；后台对话统一进入 `/admin/chat`（前端路由 `/chat`）。
- Chat 页通过轻量“控制台”入口进入管理区，避免后台菜单干扰日常对话。
- 登录页、Chat 页和后台应共享 Pudding 品牌风格，但信息密度按“主交互 → 管理工具箱”递增。

## 客户端

不再有独立的 CLI/Web 客户端项目。Web UI 就是唯一的客户端。
