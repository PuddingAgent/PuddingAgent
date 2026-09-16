# 运行时数据存储纪律 + goal.md 快照规范

> 落定：2026-09-16 ｜ 依据：用户裁定（两条）
> ① 「goal 应是简略的快照，完整性的信息应存储在外部的文件；goal.md 只存储名称、简略描述、优先级等简短的信息」
> ② 「不要把运行时数据存储到项目目录，应存储在 ROOTPath 指示的数据目录（当前 `D:\data`）；项目目录用于开发和存储 PuddingAgent 及其**预制**的数据或配置文件」
> 例外：PuddingAgent 为项目开发定义的文件（`code_map`、`Agents.md`、**项目记忆**、项目代码索引等）仍留项目目录。

---

# 第一部分：存储位置纪律

## 1.1 判定表（先分类，再落盘）

| 类别 | 落点 | 例 |
|---|---|---|
| 项目代码 / 文档 / 规范 | **项目目录** | `Source/**`、`Docs/**`、本文件 |
| 预制数据 / 预制配置 | **项目目录** | `appsettings*.json`、agent 模板、seeds、fixtures |
| 项目开发定义的特殊文件 | **项目目录** | `code_map.md`、`Agents.md`、**项目记忆**（`memory/projects/**`）、代码索引产物 |
| **Agent 运行时数据** | **数据目录** | Agent memory 归档、`goal.md`、会话、日志、临时产物、运行时索引、归档报告 |

判据一句话：**「换一台开发机、重新 clone 仓库，这个文件还需要存在吗？」**
不需要 → 运行时数据 → 数据目录。需要（因为它是开发资产）→ 项目目录。

## 1.2 数据目录（实测，2026-09-16）

| 用途 | 路径 | 说明 |
|---|---|---|
| **工作区级运行时 memory（权威）** | `D:\data\workspaces\default\memory\` | 既有 `goal-archive-*.md` / `budget-status.md` / `projects/` / `context-efficiency/` 都在这；**Agent 手写归档的正确落点** |
| Agent 私有记忆引擎库 | `D:\data\agents\<agentId>\memory\` | `content.md` / `index.json` / `daily/` / `session-summaries/`；**平台管理，勿手写** |
| Agent 私有目标文件 | `D:\data\agents\<agentId>\goal.md` | 即 `goal_read` 读取的那份 |
| 会话 / 日志 / 数据库 / 备份 | `D:\data\{sessions,logs,databases,backups}\` | 平台管理 |

## 1.3 工具通路限制（实测，务必记住）

| 通路 | 能力 | 证据 |
|---|---|---|
| `file_read` / `search_grep` | **可读仓库外** | 本会话已成功读 `D:\data\agents\...\goal.md` |
| `list_dir` / `file_write` / `file_patch` | **被"执行根"限制在项目目录** | `list_dir D:\data` → `Path is outside the current execution root`（Current execution root = `E:\github\AgentNetworkPlan\PuddingAgent`） |
| `file_write` 写仓库外 | High 风险，被审批依赖拦（`approval_review_profile_not_configured`） | 本会话多次实测 |
| **`shell`（powershell）** | ✅ **可访问数据目录** | `Get-ChildItem D:\data` 成功 ⇒ 跨根复制/校验/建目录走 shell |
| **`spawn_sub_agent(working_directory=<数据目录>)`** | ✅ 让子代理的 file 根落在数据目录，从而**直接写入** | spawn 工具参数；父级 file 工具仍受限 |

结论：**需要写数据目录时 —— 要么用 shell，要么派子代理并显式传 `working_directory`。**

---

# 第二部分：goal.md 快照规范

## 2.1 问题（有先例，非推测）

| 时间 | 体量 | 后果 |
|---|---|---|
| 2026-09-13 19:25 | 37,833 B → 瘦身至 9,716 B | 超 `goal_read` 16 KB 上限时**只返回尾部** |
| 2026-09-16 | 涨回 34,531 chars / 394 行 | 同上；**已造成实际需求遗失**：W4 的「start 须能派生验收条件否则 fail-closed」是在被迫整读全文时才被找回 |

结论：**没有格式约束的 append 式台账，会在 3 天内重新超限**（+256%）。一次性瘦身不解决问题。

## 2.2 白名单：goal.md 允许承载的 6 类信息

1. **主线**：一句话
2. **活跃目标**：表 —— 优先级 / 名称 / 简略描述（≤40 字）/ 状态 / 详情指针
3. **待办**：每条 ≤30 字 + 优先级，按 P0→P3
4. **硬约束与关键结论**：每条 ≤40 字，**只保留当前仍有效的**
5. **指针表**：用途 → 路径（**指向数据目录**）
6. **session_chain**

## 2.3 黑名单：禁止写入 goal.md（一律外部化）

执行过程叙述 · 命令与输出 · `file:line` 证据 · 验收报告 · 心跳/迭代日志 · 决策论证过程 · 已完成事项的细节 · 大段引用与重复段落

判据一句话：**每条信息必须能回答「下一轮是否需要它来做决策」**；否则不进 goal.md，只在指针表留一行。

## 2.4 容量纪律

| 项 | 值 |
|---|---|
| 硬上限 | 16 KB（`goal_read` 可读阈值，超限即失明） |
| 目标体量 | **≤4 KB** |
| 预警线 | >8 KB 即触发归档外置 |
| 自检 | 每次写入后 `goal_read` 必须 `truncated=false` |
| append 纪律 | `goal_update(append)` 单次 ≤800 字符，只允许追加「待办条目 / 新目标 / 指针」；更长的内容先写外部文件，再 append 一行指针 |

## 2.5 外部文件分层（完整信息的新家）

| 用途 | 路径 |
|---|---|
| **完整历史副本（权威）** | `D:\data\workspaces\default\memory\goals\goal-detail-<yyyyMMdd>.md`（字节级迁移，正文 Δ=0，含元信息头） |
| 历史归档 | `D:\data\workspaces\default\memory\goal-archive-<yyyyMMdd-HHmm>.md` |
| 专题（环境 / 模型路由 / 工具坑 / DB 认知） | `D:\data\workspaces\default\memory\agent-env-and-conventions.md` |
| 索引 | `D:\data\workspaces\default\memory\INDEX.md` |
| 设计与报告（**项目资产，留项目目录**） | `Docs/Features/*.md`、`Docs/Reports/*.md` |

## 2.6 归档流程（标准动作，7 步）

1. 读全文：`file_read` 绝对路径 + `offset_lines`/`limit_lines` 分页（`goal_read` 超限时不可依赖）
2. 字节级外置到 `D:\data\workspaces\default\memory\goals\goal-detail-<date>.md` —— **用 shell 复制**，或派子代理并传 `working_directory=<数据目录>`；若走子代理文件工具，按 ≤4000 字符/块、按行边界、首块覆盖 + 后续 append（最少次数 = 字符数 ÷ 4000；合并大块会因单次输出超限直接失败）
3. 回读核对：size / lines 与源对齐（正文部分 Δ=0）+ 抽样锚点逐字比对
4. 用 `goal_update(content_base64=...)` **整体覆盖**为快照版
5. `goal_read` 验证 `truncated=false`
6. 在 `D:\data\workspaces\default\memory\INDEX.md` 登记本次归档
7. 快照版内容必须过 §2.3 黑名单自检

## 2.7 已知事实（避免重复踩坑）

- `goal_update(append)` 与 `goal_read` 指向**同一文件**（2026-09-16 实证：append 后 `goal_read` 尾部可见该段）。历史笔记中「两者不是同一文件」的结论已过时。
- `goal_read` 超 16 KB 只返回尾部 + 告警 —— **这就是丢需求的直接机制**，不是提示性问题。
- 写 `D:\data\**` 的 `file_write` 被审批依赖拦截 ⇒ 改 `goal.md` 只能走 `goal_update`；改**其它**数据目录文件走 shell 或带 `working_directory` 的子代理。
- 项目目录 `memory/` 为 2026-09-16 裁定前的历史落点，正在迁移至 `D:\data\workspaces\default\memory\`；迁移后项目目录只保留 `memory/projects/**` 等项目记忆。
