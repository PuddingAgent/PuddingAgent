---
name: todo-api
description: 与本地 Todo 任务管理系统交互。当用户要求查询、创建、更新、删除任务、切换项目、进行任务生命周期操作（认领、开始、交付、验收、完成等）、看板视图、QA 门禁、Agent 协作管理时，使用此 skill。
---

## 服务信息

- **地址**: `https://todo.morenote.top/`
- **版本**: v9.0（C# ASP.NET Core 10，SQLite 本地文件存储，无外部数据库依赖）
- **认证**: 参与者鉴权已开启。写操作及协作接口必须携带参与者身份；未注册会被拒绝并提示先完成注册流程。
- **CLI 版本**: `todo_api.py` v9.0（新增 `setup` 一键注册+持久化环境变量，Agent 无需手拼认证命令）
- **JSON 格式**: 全局 snake_case（C# PascalCase 自动转换），null 字段默认省略。

## 安装文件下载入口（推荐）

服务端提供纯文本安装说明入口：`/install/get.md?participant_id=...&token=...`

- 这是同源相对路径，不写死域名/IP，适配 localhost、内网地址和公网域名。
- 访问与下载都需要鉴权参数（`participant_id` + `token`）。
- 建议先在前端登录，然后点击“下载 SKILL”按钮打开带鉴权参数的 `get.md`。

常用方式：

```bash
# 打印安装说明（纯文本，示例）
curl -L "/install/get.md?participant_id=<ID>&token=<TOKEN>"

# 下载最新 SKILL 和 CLI
curl -L -o SKILL.md "/install/files/SKILL.md?participant_id=<ID>&token=<TOKEN>"
curl -L -o todo_api.py "/install/files/todo_api.py?participant_id=<ID>&token=<TOKEN>"

# 或下载压缩包
curl -L -o todo-api.bundle.zip "/install/files/todo-api.bundle.zip?participant_id=<ID>&token=<TOKEN>"
```

## 参与者注册与鉴权（必须先做 — v9.0 推荐一键 setup）

> 任何 Agent 或 Human 在调用写接口前，先完成注册与登录/密钥配置。否则会返回 401/403。
>
> 注册需要 `registration_secret`（由服务端管理员配置，未提供或错误会拒绝注册）。
>
> 若使用 `/install/get.md` 链接里的 `token` 作为临时 `registration_secret`，该 token 需处于服务端“最近一次登录后约 60 分钟”的临时注册窗口内；这与 human 业务登录 token 的长期有效期（默认 30 天）不同。
>
> **v9.0 新增**: `setup` 命令一次性完成注册 + 登录 + 环境变量持久化 + 自检验证。Agent 只需要按下面示例执行一条命令即可。

### 1) Agent 一键 setup（推荐）

```bash
# Windows（自动用 setx 持久化到用户级环境变量，同时打印 $env: 即时生效命令）
python todo-api/todo_api.py setup --agent \
  --id copilot-dev \
  --name "小青龙" \
  --role developer \
  --intro "负责 Tasks-List 前后端改造与联调" \
  --key "your-strong-agent-key" \
  --registration-secret "your-registration-secret"
```

执行后脚本会自动：
1. 调用 API 注册 agent
2. 使用 `setx` 将 `TODO_API_PARTICIPANT_ID`/`TODO_API_PARTICIPANT_NAME`/`TODO_API_PARTICIPANT_KEY` 写入 Windows 用户级环境变量
3. 打印 `$env:TODO_API_XXX="value"` 命令供当前终端立即生效
4. 调用 `/api/participants/me` 验证凭据可用

> 如已注册过，加 `--force` 可跳过重复注册（仅持久化+自检）。

### 2) Human 一键 setup（推荐）

```bash
python todo-api/todo_api.py setup --human \
  --username alice \
  --password "your-password" \
  --display-name "王小明" \
  --registration-secret "your-registration-secret"
```

执行后自动完成：注册 → 登录获取 token → 持久化 token 到环境变量 → 自检。

### 3) 分步操作（兼容 v8.0，也可加 --persist 持久化）

```bash
# Agent 分步（加 --persist 自动写环境变量）
python todo-api/todo_api.py participant-register-agent \
  --id copilot-dev --name "小青龙" --role developer \
  --intro "负责前后端" --key "your-key" \
  --registration-secret "your-registration-secret" --persist

# Human 分步
python todo-api/todo_api.py participant-register-human \
  --username alice --password "your-password" --display-name "王小明" \
  --registration-secret "your-registration-secret" --persist
python todo-api/todo_api.py participant-login \
  --username alice --password "your-password" --persist
```

### 4) 环境变量说明

| 变量名 | 用途 | 谁需要 |
|--------|------|--------|
| `TODO_API_PARTICIPANT_ID` | 参与者唯一标识 | Agent / Human |
| `TODO_API_PARTICIPANT_NAME` | 显示名字（可中文） | Agent / Human |
| `TODO_API_PARTICIPANT_KEY` | Agent API 密钥 | Agent |
| `TODO_API_AUTH_TOKEN` | Human 登录 token | Human |
| `TODO_API_REGISTRATION_SECRET` | 注册密钥 | Agent / Human（仅注册时） |

> **v9.0 持久化机制（Windows）**: `setup` 命令内部使用 `setx` 将变量写入用户级注册表（`HKCU\Environment`），新终端自动生效；同时打印 `$env:` 命令供当前终端即时使用。
>
> **强烈建议**: 密钥/token 存在操作系统环境变量中，不要写入仓库文件。
>
> **测试建议**: 回归前请优先显式传入 `--participant-id` 与 `--auth-token`；或先清理当前终端的 `TODO_API_AUTH_TOKEN`，避免旧 token 干扰 install/participant-me 等命令验证。

## Python 脚本入口

当前技能目录已内置可复用 Python CLI：

- **Pudding 仓库路径**: `.github/skills/todo-api/todo_api.py`（从仓库根目录调用）
- **独立安装路径**: `todo_api.py`（当前目录就是本 skill 目录时调用）
- **实现方式**: Python 标准库，无需额外安装第三方依赖
- **默认服务地址**: `https://todo.morenote.top/`

> **Pudding 仓推荐包装脚本**: `.\Doc\Scripts\todo.ps1 health` 一键自动加载鉴权 + 正确定位脚本路径，免去每次手写 `$env:TODO_API_XXX`。用法与 `todo_api.py` 完全一致。
>
> 给 Agent 的路径规则：本文档命令示例中的 `todo-api/todo_api.py` 路径仅适用于独立安装场景。如果在 Pudding 仓库根目录，**实际路径是 `.github/skills/todo-api/todo_api.py`**。先用 `file_search` 确认路径后再调用。

推荐优先使用该脚本，而不是重复手写 `curl` 或临时 PowerShell 命令。

### 常用调用

> **Pudding 仓内执行**：将下面示例中的 `todo-api/todo_api.py` 替换为 `.github/skills/todo-api/todo_api.py`，或直接使用 `.\Doc\Scripts\todo.ps1` 包装脚本。

```bash
# 拉取服务端安装说明（纯文本）
python todo-api/todo_api.py install-guide

# 下载最新 SKILL + todo_api.py 到当前目录（覆盖需加 --overwrite）
python todo-api/todo_api.py install-download --output-dir . --overwrite

# 子命令级覆盖服务地址（也支持全局 --base-url）
python todo-api/todo_api.py install-download --base-url https://todo.morenote.top/ --output-dir . --overwrite

# 当前身份自检
python todo-api/todo_api.py participant-me

# 查看参与者列表（Agent 可用于协作发现）
python todo-api/todo_api.py participant-list

# 健康检查
python todo-api/todo_api.py health

# 读取单个任务
python todo-api/todo_api.py get-task task-20260320-029

# 按条件筛选任务
python todo-api/todo_api.py list-tasks --status progress --project MPCAL

# 多标签筛选（OR 逻辑）
python todo-api/todo_api.py list-tasks --tag UI --tag frontend

# 搜索任务
python todo-api/todo_api.py search 架构评审

# 查看看板（默认按状态分组，可切换为研发阶段等）
python todo-api/todo_api.py kanban --group-by stage

# 按标签分组看板
python todo-api/todo_api.py kanban --group-by tag

# 看板按标签分组，并叠加筛选条件
python todo-api/todo_api.py kanban --group-by tag --project MPCAL --stage ready_for_qa --tag Tasks-List

# 查看 QA 状态
python todo-api/todo_api.py qa-view task-20260320-029
```

### 写操作示例

> ⚠️ **PowerShell 用户注意**：PowerShell 的引号处理与 bash 不同，`--data '{"key":"value"}'` 在 PowerShell 中极易因引号/花括号转义失败。**强烈建议改用 `--file` 模式或命名参数**。bash/zsh 用户用单引号包 JSON 正常。

```bash
# 创建任务（内联 JSON — bash/zsh 用单引号，PowerShell 请改用 --file 或命名参数）
python todo-api/todo_api.py create --data '{"title":"示例任务","project":"MPCAL","owner":"wangxianqiang","priority":"P1"}'

# 更新任务（内联 JSON — 同上 PowerShell 请用 --file 或命名参数）
python todo-api/todo_api.py update task-20260320-029 --data '{"stage":"verifying","last_agent_summary":"已完成脚本补充"}'

# 添加备注
python todo-api/todo_api.py note task-20260320-029 --text "已补充 Python CLI 用法文档"

# QA 快捷通过
python todo-api/todo_api.py qa-approve task-20260320-029

# 快捷完成任务
python todo-api/todo_api.py complete task-20260320-029

# 推荐：完成任务或里程碑，并写入 agent 总结（避免手写 JSON）
python todo-api/todo_api.py finish task-20260320-029 --agent-id copilot --summary "实现完成并通过自测" --release --reason "交付给 QA"

# Agent 心跳续租
python todo-api/todo_api.py heartbeat --agent-id copilot

# 查看未确认公告
python todo-api/todo_api.py bulletins --unacknowledged-by copilot
```

### 命名参数模式（推荐，无需 JSON 转义）

create 和 update 支持直接用 `--key value` 命名参数，Python 层自动组装为 JSON，**完全避免 PowerShell/Shell 的 JSON 转义问题**。

三种传参模式优先级：`--data` > `--file` > 命名参数（互斥）。

```bash
# 创建任务（命名参数，推荐）
python todo-api/todo_api.py create \
  --title "修复用户-角色多对多关系" \
  --project MPCAL \
  --task-owner wangxianqiang \
  --priority P0 \
  --stage draft \
  --tag server --tag RBAC --tag database \
  --goal "实现用户-角色多对多关系" \
  --out-of-scope "项目级角色不在范围" \
  --acceptance-criteria "UserRole 联接表创建成功" \
  --acceptance-criteria "用户可分配多个角色" \
  --impact-scope "UserEntity / DbContext / UserService" \
  --entry-point "Source/Pudding.WebServer/Data/Entities/" \
  --entry-point "Source/Pudding.Agent/Services/UserService.cs" \
  --risk-notes "Breaking Change：需同步前端" \
  --executor-type hybrid \
  --human-owner WangXianQiang \
  --doc-path "Doc/设计文档/02服务端设计/01用户体系.md"

# 更新任务（命名参数）
python todo-api/todo_api.py update task-20260415-001 \
  --stage verifying \
  --last-agent-summary "已完成编码，等待 QA"
```

**可用的命名参数**：

字符串字段：`--title`、`--summary`、`--description`、`--detail`、`--project`、`--task-owner`（注意不是 --owner）、`--priority`、`--status`、`--stage`、`--due-date`、`--start-date`、`--goal`、`--out-of-scope`、`--impact-scope`、`--test-requirements`、`--risk-notes`、`--rollback-plan`、`--executor-type`、`--assigned-agent`、`--claimed-by`、`--claimed-at`、`--human-owner`、`--doc-path`、`--task-doc-path`、`--parent-id`、`--prerequisite-task-id`、`--last-agent-summary`

数值字段：`--lease-minutes`

列表字段（可重复）：`--tag`、`--acceptance-criteria`、`--entry-point`、`--depends-on`、`--blocks`

布尔字段：`--requires-human-confirmation`、`--reflection-required`、`--docs-synced`

### 使用 JSON 文件作为请求体

```bash
python todo-api/todo_api.py create --file create-task.json
python todo-api/todo_api.py update task-20260320-029 --file update-task.json
python todo-api/todo_api.py qa task-20260320-029 --file qa-result.json
```

### 可用命令

`install-guide`、`install-download`、`participant-register-agent`、`participant-register-human`、`participant-login`、`participant-list`、`participant-me`、`health`、`projects`、`tags`、`owners`、`kanban`、`get-task`、`list-tasks`、`search`、`create`、`update`、`delete`、`note`、`qa`、`qa-view`、`qa-approve`、`qa-reject`、`qa-retest`、`complete`、`finish`、`cancel`、`claim`、`release`、`agents`、`heartbeat`、`bulletins`、`bulletin-post`、`bulletin-ack`、`bulletin-delete`

### 查看帮助

```bash
python todo-api/todo_api.py --help
python todo-api/todo_api.py get-task --help
python todo-api/todo_api.py finish --help
```

### Agent 友好行为

- CLI 成功执行 `claim`、`release`、`heartbeat`、`complete`、`finish`、`qa-approve`、`bulletin-post`、`bulletin-ack` 时，会在输出 JSON 中追加 `agent_message`，用于给 Agent 明确的鼓励和下一步提示。
- CLI 出错时会尽量给出恢复建议，例如服务未启动、任务 ID 不存在、JSON 格式错误、非法状态流转等。
- 若不确定如何构造请求体，先运行对应命令的 `--help`，并优先使用命名参数/快捷命令，不要硬拼 JSON。

### 完成任务/里程碑推荐命令

`finish` 是给 Agent 准备的快捷命令：会自动处理常见状态流转，写入 `last_agent_summary`，可选追加备注、释放租约、发布交接公告。

```bash
# 最小用法：完成并写入总结
python todo-api/todo_api.py finish task-20260320-029 --summary "实现完成并通过自测"

# 完成后释放租约
python todo-api/todo_api.py finish task-20260320-029 \
  --agent-id copilot \
  --summary "完成接口实现和冒烟验证" \
  --release \
  --reason "交付给 QA"

# 完成后通知另一个 Agent 接手
python todo-api/todo_api.py finish task-20260320-029 \
  --agent-id dev-agent \
  --summary "开发完成，待验证" \
  --stage ready_for_qa \
  --announce-target qa-agent
```

## 任务数据结构

```json
{
  "id": "task-20260320-001",
  "title": "任务标题",
  "summary": "一句话摘要",
  "description": "任务详细描述",
  "detail": "补充技术细节",
  "project": "项目名称",
  "owner": "wangcai",
  "priority": "P1",
  "status": "pending",
  "stage": "draft",
  "tag": "工作",
  "tags": ["alpha", "beta"],
  "completed": false,
  "created_at": "2026-03-20T00:00:00Z",
  "updated_at": "2026-03-20T00:00:00Z",
  "due_date": "2026-04-01",
  "start_date": null,
  "completed_at": null,
  "notes": [],
  "depends_on": [],
  "prerequisite_task_id": null,
  "blocks": [],
  "parent_id": null,
  "subtask_ids": [],
  "qa_status": "pending",
  "qa_gate": null,
  "qa_report_path": null,

  "goal": "本任务要达成的目标",
  "out_of_scope": "不包含的内容",
  "acceptance_criteria": ["标准1", "标准2"],
  "impact_scope": "影响范围说明",
  "test_requirements": "测试要求",
  "entry_points": ["文件路径或函数名"],
  "risk_notes": "风险备注",
  "rollback_plan": "回滚方案",

  "executor_type": "human",
  "assigned_agent": null,
  "claimed_by": null,
  "claimed_at": null,
  "lease_minutes": 60,
  "human_owner": null,
  "requires_human_confirmation": false,
  "last_agent_update_at": null,
  "last_agent_summary": null,

  "doc_path": "Doc/Tasks/example.md",
  "task_doc_path": "Doc/Tasks/example.md",
  "context_doc_path": "Doc/Context.md",
  "review_index_path": "Doc/Review.md",
  "daily_log_path": "Doc/Log/2026-03-20.md",
  "reflection_required": false,
  "docs_synced": false
}
```

字段说明：

**基础字段**
- `priority`：`P0`（紧急）/ `P1`（重要）/ `P2`（普通）/ `P3`（低）。支持别名映射：`high` → `P0`、`medium` → `P1`、`low` → `P2`
- `status`：`pending` → `progress` → `completed` → `auditing` → `closed`；`cancelled` 可从任意状态进入
- `stage`：流程阶段，**严格校验**，允许值为 `draft` / `ready` / `in_progress` / `blocked` / `implemented` / `verifying` / `ready_for_qa` / `qa_failed` / `done` / `cancelled`
- `owner`：任意文本，表示负责人
- `project`：自由文本，按项目过滤，为空归入"未分配"
- `tag` / `tags`：单标签字符串 / 标签数组，两种形式兼容
- `due_date` / `start_date`：`YYYY-MM-DD` 格式

**关系字段**
- `notes`：备注数组，每项 `{author, text, created_at}`
- `depends_on`：前置依赖的任务 ID 列表
- `prerequisite_task_id`：前置依赖任务 ID（字符串，可空），用于直接指向一个上游任务
- `blocks`：被本任务阻塞的后续任务 ID 列表
- `parent_id`：父任务 ID（子任务场景）
- `subtask_ids`：直属子任务 ID 列表（创建子任务时自动维护）

**QA 字段**
- `qa_status`：`pending` / `passed` / `failed` / `blocked`
- `qa_gate`：QA 门禁详细对象（含 checked_by、criteria、findings、evidence 等）

**研发任务卡（v8.0）**
- `goal`：任务目标一句话描述
- `out_of_scope`：此次不做的内容（避免范围蔓延）
- `acceptance_criteria`：验收标准列表
- `impact_scope`：改动影响范围
- `test_requirements`：测试要求
- `entry_points`：关键文件/函数入口
- `risk_notes`：风险备注
- `rollback_plan`：回滚方案

**Agent 协作（v8.0）**
- `executor_type`：`human` / `agent` / `hybrid`
- `assigned_agent`：执行的 agent 标识
- `claimed_by`：当前认领该任务的 agent ID（用于多 agent 协作与任务归属）
- `claimed_at`：认领时间（UTC ISO）
- `lease_minutes`：租约时长（分钟），到期后会在读取时自动释放
- `requires_human_confirmation`：是否需要人工确认才能继续
- `last_agent_summary`：agent 最近一次进度汇报

**文档关联（v8.0）**
- `doc_path`：主关联文档相对路径
- `task_doc_path`、`context_doc_path`、`review_index_path`、`daily_log_path`：细分文档路径
- `reflection_required`：任务完成后是否需要撰写复盘
- `docs_synced`：文档是否已同步

---

## 项目列表

```bash
curl https://todo.morenote.top/api/projects
```

响应：
```json
{
  "success": true,
  "projects": [
    {"project": "todo-web", "total": 12, "completed": 5, "active": 7},
    {"project": "narrative-engine", "total": 8, "completed": 3, "active": 5}
  ]
}
```

`active` = 非 `completed` 且非 `cancelled` 的任务数。

---

## 任务 CRUD

### 获取任务列表

```bash
# 获取所有任务
curl https://todo.morenote.top/api/tasks

# 按项目过滤
curl "https://todo.morenote.top/api/tasks?project=todo-web"

# 多条件过滤（同一参数可重复表示 OR 逻辑）
curl "https://todo.morenote.top/api/tasks?status=pending&priority=P0&owner=wangcai"
curl "https://todo.morenote.top/api/tasks?status=pending&status=progress"
curl "https://todo.morenote.top/api/tasks?status=auditing&status=closed"

# 按阶段/执行者类型过滤
curl "https://todo.morenote.top/api/tasks?stage=in_progress&executor_type=agent"
```

支持的过滤参数（均可多值）：`status`、`priority`、`owner`、`project`、`stage`、`executor_type`、`tag`、`qa_status`、`claimed_by`、`search`

搜索会同时覆盖 `title`、`summary`、`description`、`detail`、`project`、`owner`、`assigned_agent`、`claimed_by`、`human_owner`、`tag` 以及 `tags[]`。

> **过滤逻辑**：同一参数多值为 **OR**（如 `status=pending&status=progress`）；不同参数之间为 **AND**。

响应：`{ "success": true, "tasks": [...] }`

> **错误响应格式**：所有 API 错误均返回 `{ "success": false, "error": "错误描述" }`，HTTP 状态码 400/404/500。

### 获取单个任务

```bash
curl https://todo.morenote.top/api/tasks/{id}
```

### 创建任务

```bash
# 基础创建
curl -X POST https://todo.morenote.top/api/tasks \
  -H "Content-Type: application/json" \
  -d '{
    "title": "任务标题",
    "description": "详细描述",
    "project": "todo-web",
    "priority": "P1",
    "owner": "wangcai",
    "tag": "工作",
    "due_date": "2026-03-30"
  }'

# 带研发任务卡字段的创建
curl -X POST https://todo.morenote.top/api/tasks \
  -H "Content-Type: application/json" \
  -d '{
    "title": "实现登录功能",
    "project": "todo-web",
    "priority": "P0",
    "owner": "wangcai",
    "executor_type": "agent",
    "assigned_agent": "dev-agent",
    "stage": "in_progress",
    "goal": "实现 JWT 登录接口",
    "out_of_scope": "OAuth 暂不做",
    "acceptance_criteria": ["POST /auth/login 返回 token", "token 有效期 7 天"],
    "impact_scope": "AuthController + JWT 中间件",
    "entry_points": ["Controllers/AuthController.cs"],
    "requires_human_confirmation": false,
    "task_doc_path": "Doc/Tasks/login.md"
  }'
```

响应：`{ "success": true, "task": {...} }` HTTP 201

### 创建子任务

```bash
curl -X POST https://todo.morenote.top/api/tasks \
  -H "Content-Type: application/json" \
  -d '{
    "title": "子任务",
    "parent_id": "task-20260320-001",
    "owner": "wangcai"
  }'
```

创建后父任务的 `subtask_ids` 自动更新。

### 更新任务

```bash
# 更新任意字段（只传需要改的字段）
curl -X PUT https://todo.morenote.top/api/tasks/{id} \
  -H "Content-Type: application/json" \
  -d '{"title": "新标题", "priority": "P0", "project": "new-project"}'

# Agent 更新进度汇报
curl -X PUT https://todo.morenote.top/api/tasks/{id} \
  -H "Content-Type: application/json" \
  -d '{
    "stage": "verifying",
    "last_agent_summary": "已完成 AuthController，单元测试通过，等待人工 review",
    "last_agent_update_at": "2026-03-20T12:00:00Z"
  }'

# 标记文档已同步
curl -X PUT https://todo.morenote.top/api/tasks/{id} \
  -H "Content-Type: application/json" \
  -d '{"docs_synced": true}'
```

### 删除任务

```bash
curl -X DELETE https://todo.morenote.top/api/tasks/{id}
```

### 搜索任务

```bash
curl "https://todo.morenote.top/api/tasks/search?q=关键词"
```

---

## 任务生命周期

```bash
# 直接更新 status 字段（最通用）
curl -X PUT https://todo.morenote.top/api/tasks/{id} \
  -H "Content-Type: application/json" \
  -d '{"status": "progress"}'

# 提交审计（已完成 -> 待审计）
curl -X PUT https://todo.morenote.top/api/tasks/{id} \
  -H "Content-Type: application/json" \
  -d '{"status": "auditing"}'

# 关闭任务（仅当 qa_status=passed 时允许）
curl -X PUT https://todo.morenote.top/api/tasks/{id} \
  -H "Content-Type: application/json" \
  -d '{"status": "closed"}'

# 快捷完成（等同于 status=completed）
curl -X POST https://todo.morenote.top/api/tasks/{id}/complete

# 取消任务（等同于 status=cancelled）
curl -X POST https://todo.morenote.top/api/tasks/{id}/cancel
```

`status` 流转：`pending` → `progress` → `completed` → `auditing` → `closed`；`cancelled` 可从任意状态进入。  
限制：仅当 `qa_status=passed` 时允许进入 `closed`。  
`stage` 建议流转：`draft` → `ready` → `in_progress` → `implemented` → `verifying` → `ready_for_qa` → `done`

**自动填充字段**：当 `status` 变为 `completed` / `cancelled` / `closed` 时，系统自动设置 `completed=true` 和 `completed_at`；反之自动清空。备注的 `created_at` 由服务端自动填充。

### 任务认领与租约

```bash
# 认领任务（可选租约时长，默认 60 分钟）
curl -X POST https://todo.morenote.top/api/tasks/{id}/claim \
  -H "Content-Type: application/json" \
  -d '{"agent_id":"dev-agent","lease_minutes":60}'

# 释放任务
curl -X POST https://todo.morenote.top/api/tasks/{id}/release \
  -H "Content-Type: application/json" \
  -d '{"agent_id":"dev-agent","reason":"本轮完成，交给 QA"}'
```

说明：租约过期会在任务读取/列表查询时自动释放，避免 Agent 异常退出导致任务长期悬空。

### 公告板（Agent 间通信）

```bash
# 发布公告
curl -X POST https://todo.morenote.top/api/bulletins \
  -H "Content-Type: application/json" \
  -d '{"type":"handoff","author_agent":"dev-agent","target_agent":"qa-agent","text":"请接手验证","related_task_id":"task-20260320-029"}'

# 查看公告（支持按未确认 Agent 过滤）
curl "https://todo.morenote.top/api/bulletins?unacknowledged_by=qa-agent"

# 确认公告
curl -X POST https://todo.morenote.top/api/bulletins/{id}/ack \
  -H "Content-Type: application/json" \
  -d '{"agent_id":"qa-agent"}'
```

**公告类型（`type` 字段）**：API 仅接受以下 5 种值，非法值会被拒绝（HTTP 400）：

| 类型 | 用途 | 典型场景 |
|------|------|----------|
| `info` | 一般信息 | 开始任务、状态更新、进度同步等日常通知 |
| `warning` | 预警/风险提示 | 修改共享文件、发现潜在冲突、即将到期提醒 |
| `request_help` | 请求协助 | 遇到阻塞需人工或其他 Agent 介入、权限不足 |
| `handoff` | 任务交接 | 完成阶段性工作后交付给下一环节（如开发→QA） |
| `announcement` | 全组通告 | 紧急回退、重要变更通知、里程碑达成等需全员周知的事项 |

**场景映射速查**：

| 通信场景 | 推荐类型 |
|----------|----------|
| 开始任务、日常进度同步 | `info` |
| 修改共享文件、潜在冲突预警 | `warning` |
| 遇到阻塞需协助 | `request_help` |
| 阶段性完成交接 | `handoff` |
| 紧急回退、重要通告 | `announcement` |

### Agent 心跳

```bash
# Agent 心跳（刷新在线状态 + 续租已认领任务）
curl -X POST https://todo.morenote.top/api/agents/heartbeat \
  -H "Content-Type: application/json" \
  -d '{"agent_id":"dev-agent"}'

# 查看 Agent 在线状态
curl "https://todo.morenote.top/api/agents?online_window_minutes=5"
```

---

## 备注

```bash
curl -X POST https://todo.morenote.top/api/tasks/{id}/notes \
  -H "Content-Type: application/json" \
  -d '{"author": "wangcai", "text": "备注内容"}'
```

---

## 标签与归属

```bash
curl https://todo.morenote.top/api/tasks/tags
curl https://todo.morenote.top/api/tasks/owners
```

**标签机制**：
- 每个任务支持 `tag`（单标签，向后兼容）和 `tags`（多标签数组），两者自动合并去重
- 前端命令栏的标签筛选为**多选下拉菜单**，支持搜索过滤和批量勾选
- 看板支持**按标签分组**（group_by=tag），一个任务可出现在多个标签列中

---

## Agent 执行状态

前端自动根据任务的 `status`、`stage`、`qa_status` 字段推导一个**综合执行状态**，在所有视图（列表、表格、看板）中显示：

| 状态 | 条件 | 语义色 |
|------|------|--------|
| 待开始 | stage=draft, status=pending | 灰色 |
| 已就绪 | stage=ready | 蓝色 |
| 执行中 | stage=in_progress 或 status=progress | 青色 |
| 阻塞中 | stage=blocked 或 qa_status=blocked | 红色 |
| 已实现待验证 | stage=implemented 或 status=completed | 紫色 |
| 验证中 | stage=verifying | 橙色 |
| 待 QA 受理 | stage=ready_for_qa | 品红 |
| QA 审核中 | stage=ready_for_qa + qa_status=pending，或 status=auditing | 黄色 |
| QA 驳回待修复 | stage=qa_failed 或 qa_status=failed | 红色 |
| 已交付 | stage=done 或 status=closed | 绿色 |
| 已取消 | status=cancelled | 灰色 |

每个状态还附带**角色徽章**，显示当前参与方：`QA`、`dev`(Agent)、`human`、`hybrid` 等。

---

## 看板视图

前端使用**统一看板**（`TaskUnifiedKanbanView`），支持 5 种分组方式切换，替代了原先独立的"状态看板"和"阶段看板"。

**分组方式**：`status`（任务状态，默认）/ `stage`（研发阶段）/ `priority`（优先级）/ `tag`（标签）/ `owner`（负责人）

- `stage` 和 `status` 分组支持**拖拽**改变字段值
- `priority`、`tag`、`owner` 分组为只读展示

**看板卡片展示**：
- 执行状态徽章：由 `deriveExecutionState()` 自动推导，如"执行中"、"QA 审核中"、"阻塞中"、"已交付"等
- 角色徽章：由 `getResponsibilitySummary()` 推导，显示 QA / Agent / Human 等参与方
- 优先级、多标签（最多显示 3 个）、负责人

```bash
# API：按状态分组（默认）
curl https://todo.morenote.top/api/kanban

# 可选 group_by：status / priority / owner / project / stage / executor_type / tag
curl "https://todo.morenote.top/api/kanban?group_by=priority"
curl "https://todo.morenote.top/api/kanban?group_by=executor_type"
curl "https://todo.morenote.top/api/kanban?group_by=tag&project=MPCAL&stage=ready_for_qa&tag=Tasks-List"

# 看板同样支持任务列表的筛选参数
curl "https://todo.morenote.top/api/kanban?group_by=status&search=标签&executor_type=hybrid"
```

响应：
```json
{
  "success": true,
  "kanban": {
    "group_by": "status",
    "total": 15,
    "columns": [
      {"key": "pending",   "count": 8, "tasks": [...]},
      {"key": "progress",  "count": 5, "tasks": [...]},
      {"key": "completed", "count": 2, "tasks": [...]},
      {"key": "auditing",  "count": 1, "tasks": [...]},
      {"key": "closed",    "count": 1, "tasks": [...]} 
    ]
  }
}
```

---

## QA 门禁

```bash
# 查看 QA 状态
curl https://todo.morenote.top/api/tasks/{id}/qa

# 写入 QA 结果
curl -X POST https://todo.morenote.top/api/tasks/{id}/qa \
  -H "Content-Type: application/json" \
  -d '{
    "status": "passed",
    "checked_by": "qa-eng",
    "notes": "LGTM",
    "criteria": [{"name": "单测覆盖率 ≥ 80%", "passed": true}],
    "findings": [],
    "evidence": ["测试报告链接"]
  }'

# 快捷通过
curl -X POST https://todo.morenote.top/api/tasks/{id}/qa/approve

# 快捷驳回（支持附加原因）
curl -X POST https://todo.morenote.top/api/tasks/{id}/qa/reject \
  -H "Content-Type: application/json" \
  -d '{"notes": "需要修改登录逻辑"}'

# 重新测试（设 retest_required=true，qa_status=pending）
curl -X POST https://todo.morenote.top/api/tasks/{id}/qa/retest
```

`qa_status`：`pending` / `passed` / `failed` / `blocked`

---

## 健康检查

```bash
curl https://todo.morenote.top/health
# 响应：{"status":"ok","timestamp":"2026-03-20T00:00:00Z"}
```

---

## 操作规范

1. 读取前先用 GET 确认当前状态，避免覆盖冲突
2. 只传需要修改的字段，PUT 为部分更新（null 不覆盖已有值）
3. `project` 字段区分项目，为空则任务归入"全部"
4. 创建子任务时设置 `parent_id`，系统自动维护 `subtask_ids`
5. `depends_on` / `prerequisite_task_id` / `blocks` 需按业务语义维护关系
6. Agent 执行任务时应定期更新 `last_agent_summary` 和 `stage`
7. 状态流转须遵循顺序规则，禁止直接从 `completed` 跳 `closed`
8. 关闭任务前应先完成 QA，确保 `qa_status=passed`
9. 任务完成后若 `reflection_required=true`，需撰写复盘并更新 `docs_synced`

---

## 架构说明

```
Tasks-List/
  Tasks-List-Server/         # C# ASP.NET Core 10 后端
    Program.cs               # 注册 DI、CORS、静态文件、SPA fallback、健康检查
    Controllers/
      TasksController.cs     # /api/tasks/* 任务 CRUD + notes + QA
      AgentsController.cs    # /api/agents/* Agent 心跳与在线状态
      BulletinsController.cs # /api/bulletins/* 公告板
      KanbanController.cs    # /api/kanban
      ProjectsController.cs  # /api/projects
      HomeController.cs      # MVC 视图（index.html fallback）
    Services/
      TaskService.cs         # 任务业务逻辑（CRUD、过滤、看板、QA、父子关系）
      AgentService.cs        # Agent 心跳、在线状态与任务续租
      BulletinService.cs     # 公告板 CRUD 与 ACK
      StorageService.cs      # SQLite 本地文件存储引擎（data/tasks.db，线程安全）
    Models/
      TaskItem.cs            # 任务完整模型（v8.0）
      Dtos.cs                # 请求 DTO：CreateTaskDto / UpdateTaskDto / QaUpdateDto
      AgentPresence.cs       # Agent 心跳记录
      AgentRuntimeStatus.cs  # Agent 运行状态视图
      BulletinItem.cs        # 公告板模型
      QaGate.cs              # QA 门禁模型
      TaskNote.cs            # 备注模型
    wwwroot/                 # 前端产物（由 frontend/ 编译生成）
  frontend/                  # Vue 3 + Vite + TypeScript + Pinia 前端
    src/
      App.vue                # 顶部栏、命令栏（搜索 + 多标签筛选 + 视图切换）
      components/
        TagMultiSelect.vue     # 多标签选择下拉组件（搜索 + 批量勾选）
        TaskUnifiedKanbanView.vue  # 统一看板（5种分组 + 拖拽 + Agent状态徽章）
        TaskList.vue           # 列表视图（含阶段、执行状态、标签列）
        TaskTableView.vue      # 表格视图（TanStack Table，含阶段、执行状态、标签列）
      utils/
        taskPresentation.ts    # 执行状态推导、角色徽章、标签处理、筛选逻辑
      stores/
        ui.ts                  # UI状态（viewMode、kanbanGroupBy、filters）
  data/
    tasks.db                 # SQLite 本地数据库文件（任务、公告、Agent 心跳）
  Dockerfile                 # 2 阶段：dotnet publish → aspnet runtime
  docker-compose.yml         # 端口 5003，挂载 data/
  builder.ps1                # 本地：pnpm build → docker build → docker compose up
```

**部署**：运行 `.\builder.ps1`（在 `Tasks-List/` 目录），约 10s 完成，访问 `https://todo.morenote.top/`。
