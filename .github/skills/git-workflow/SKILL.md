---
name: git-workflow
description: Use when working with git — before commit, before push, when creating branches, resolving conflicts, merging, rolling back, or any git operation. Enforces clean history, TASK-ID traceability, branch naming conventions, push gating, multi-collaborator coordination via bulletin board, and local multi-agent isolation via git worktree. Triggers: git, commit, branch, merge, push, rebase, rollback, revert, conflict, fetch, pull, worktree.
argument-hint: "描述 Git 操作，例如 '创建功能分支'、'合并回主干前检查'、'多协作者同步' 或 '多 Agent 本地并行隔离'"
---

# Git Workflow — 版本控制工作流

## 概述

定义 Pudding 项目从**协作感知 → 开工前检查 → 分支管理 → 提交 → 合并 → 推送 → 回退**的完整 Git 规范。
**核心原则：Git 历史是代码的审计日志，必须干净、可追溯、不可破坏。多协作者通过公告板实时协调，避免冲突。多 Agent 本地并行通过 git worktree 实现工作区隔离，互不干扰。**

## 角色与职责

| 角色 | Git 职责 | 公告板职责 |
|------|---------|-----------|
| **@lead** | 在主 worktree 审批分支合并回主干，推送前确认 QA 门禁通过 | 接收所有公告，协调冲突，发布合并通知 |
| **@dev / @lightweight-developer / @super-dev** | 在自己的 worktree 上开发、提交、自检；完成后清理 worktree | 发布进度/预警/交接公告，开工前查阅未读公告 |
| **@qa / @qa-sonnet** | 审阅通过后标记 `qa_gate`，解锁推送门禁 | 发布审阅结果公告 |
| **@doc** | 归档时核对任务卡已登记分支名、worktree 路径和提交历史 | 发布文档同步完成公告 |
| **人类协作者** | 推送个人分支，通过 Agent 代理 Git 操作；本地多任务用 worktree 隔离 | 通过 Agent 代理通信 |
| **外部协作者** | Fork/PR 模式，推送个人分支 | 通过公告板协调 |

---

## 0. 多协作者协作规范

> **适用对象：所有参与者（人类 + Agent + 外部协作者）。本节的规则不仅是我们的规则，也是所有远端协作者共同遵守的约定。**

### 0.1 协作模型

项目支持多人类 + 多 Agent 并行开发。每个参与者独立领取任务卡，在自己的功能分支上工作。协作通过以下五层机制保证不冲突：

| 协作机制 | 工具 | 用途 |
|---------|------|------|
| **任务卡认领** | `todo-api` claim | 声明"我正在做这个"，防止多人同时做同一任务 |
| **公告板** | `todo-api` bulletin | **实时**通知其他参与者：开始/完成/阻塞/交接/修改共享文件 |
| **远端同步** | `git fetch/pull` | 保持本地与远端一致，及时发现他人提交 |
| **分支隔离** | `git branch` | 每人/每任务独立分支，互不干扰 |
| **工作区隔离** | `git worktree` | 每个 Agent/任务独立工作目录，避免分支切换互相干扰 |

### 0.2 拉取（fetch/pull）规则

远端同步是协作的基石。**不及时拉取 = 在过时的代码上开发 = 冲突风险。**

**必须 `git fetch` 的时机：**

| 时机 | 原因 |
|------|------|
| 每日开工时 | 了解 overnight 的远端变更 |
| 开始新任务前 | 确认基准 master 是最新的 |
| 推送前 | 检查是否有他人新推送，避免推送被拒 |
| 合并回 master 前 | 确保本地 master 包含所有远端提交 |

**`git fetch` vs `git pull`：**

| 命令 | 行为 | 何时用 |
|------|------|--------|
| `git fetch origin` | 仅下载远端信息，不修改本地文件 | **每次**想看远端状态时（安全，无副作用） |
| `git pull origin master` | fetch + merge，直接修改工作区 | 确认要同步远端变更时 |

```bash
# 标准远端检查流程（只在 master 分支执行 pull）
git fetch origin                          # 拉取所有远端信息（不修改本地文件）
git log HEAD..origin/master --oneline     # 查看远端领先的提交（别人做了什么）
git log origin/master..HEAD --oneline     # 查看本地领先的提交（我还有什么没推送）
```

> **铁律：先 fetch 查看，再决定是否 pull。禁止盲目 pull。禁止在功能分支上 pull master（应切回 master 再 pull）。**

### 0.3 开工前的协作检查

每个参与者（人或 Agent）在开始任何编码工作前，**必须**完成以下协作检查：

```bash
# 1. 检查公告板，了解其他参与者的最新动态
python todo-api/todo_api.py bulletins --unacknowledged-by <my-agent-id>

# 2. 查看当前所有进行中的任务，避免重复工作
python todo-api/todo_api.py list-tasks --stage in_progress

# 3. 认领自己的任务（声明归属，防止其他人误领）
python todo-api/todo_api.py claim TASK-xxx \
  --agent-id <my-agent-id> \
  --lease-minutes 120

# 4. 查看当前所有 worktree，了解其他参与者的工作区占用
git worktree list

# 5. 远端同步，确保基于最新代码开工
git fetch origin
git log HEAD..origin/master --oneline
```

### 0.4 公告板通信协议

公告板是**实时**的跨参与者通信通道。所有参与者（人类通过 Agent 代理）必须遵守：

| 场景 | 公告类型 | 触发时机 | 必须？ |
|------|---------|---------|--------|
| **开始任务** | `progress` | 认领并开始编码前 | ✅ 必须 |
| **完成阶段** | `handoff` | 提交 QA / 合并完成时，通知下游接手 | ✅ 必须 |
| **遇到阻塞** | `blocked` | 发现依赖他人任务或外部问题时 | ✅ 必须 |
| **修改共享文件** | `warning` | 即将修改影响他人的文件（.csproj、公共接口、架构层） | ✅ 必须 |
| **紧急回退** | `alert` | revert 了已推送的提交 | ✅ 必须 |

```bash
# 开始任务
python todo-api/todo_api.py bulletin-post \
  --type progress \
  --author-agent dev-agent \
  --text "开始处理 TASK-123，涉及文件: Core/IPasswordHasher.cs, WebApplication/Services/AuthService.cs" \
  --related-task-id TASK-123

# 修改共享接口前发出预警
python todo-api/todo_api.py bulletin-post \
  --type warning \
  --author-agent dev-agent \
  --text "即将修改 Core/Interfaces/IPasswordHasher.cs，所有实现者请注意" \
  --related-task-id TASK-123

# 任务完成，交接给 QA
python todo-api/todo_api.py bulletin-post \
  --type handoff \
  --author-agent dev-agent \
  --target-agent qa-agent \
  --text "TASK-123 已推送至远端 feature/TASK-123-xxx，请审阅" \
  --related-task-id TASK-123
```

### 0.5 冲突预防铁律

<HARD-GATE>
以下规则适用于所有参与者，违反即阻塞合并。
</HARD-GATE>

| # | 规则 | 原因 |
|---|------|------|
| 1 | **一任务一分支**：每个 TASK 对应独立分支，禁止多人共用分支 | 避免 force push 和提交覆盖 |
| 2 | **先认领再开工**：开始编码前必须在 todo-api 认领任务 | 防止多人同时做同一任务 |
| 3 | **修改共享文件前预警**：修改 .csproj / 公共接口 / 架构层文件前发 warning 公告 | 让其他人有心理预期，减少合并冲突 |
| 4 | **每日至少推送一次**：防止本地代码丢失，也让他人感知进度 | 代码在本地 = 只有你知道 |
| 5 | **合并前必须 pull**：合并回 master 前 `git pull origin master` | 不 pull 就合并 = 必然冲突或覆盖他人代码 |
| 6 | **开工必读公告板**：开工前检查未读公告，完成关键步骤后发公告 | 公告是唯一的实时协调通道 |
| 7 | **同一文件串行修改**：如果公告板显示他人在修改相同文件，等待其完成后再开始 | 并行改同一文件 = 合并冲突地狱 |
| 8 | **一 Agent 一 worktree**：每个 Agent 在自己的 worktree 中工作，禁止多 Agent 共享同一 worktree | 切换分支会破坏其他 Agent 的工作上下文 |
| 9 | **主 worktree 仅用于合并**：原始 clone 目录（主 worktree）只用于 `git fetch/pull/merge` 和推送 master，不用于日常开发 | 避免合并操作与开发分支切换冲突 |

---

### 0.6 多 Agent 本地并行 — git worktree 规范

> **为什么需要 worktree？** 当多个 Agent 在同一台机器上并行开发时，如果共用一个工作目录，A Agent 切换分支会导致 B Agent 的工作上下文被破坏。`git worktree` 让每个 Agent 拥有独立的工作目录，共享同一份 `.git/objects`，既省磁盘又互不干扰。

#### 0.6.1 目录约定

所有 worktree 统一放在仓库根目录旁的 `.wt/` 下：

```
Pudding/                  # 主仓库（主 worktree，仅用于 master 的 fetch/pull/merge）
└── .wt/                   # 所有 Agent worktree 容器（已在 .gitignore 中）
    ├── dev-TASK-123/      # @dev Agent 的 worktree
    ├── lightweight-TASK-456/  # @lightweight-developer Agent 的 worktree
    └── qa-TASK-123/       # @qa Agent 的审阅 worktree（临时）
```

**命名规范：** `.wt/<agent-role>-<TASK-id>/`

| 部分 | 说明 |
|------|------|
| `agent-role` | 使用 Agent 简称：`lead`、`dev`、`lightweight`、`super-dev`、`qa`、`qa-sonnet`、`explore` |
| `TASK-id` | 对应任务卡 ID，如 `TASK-123` |

#### 0.6.2 核心约束

<HARD-GATE>
以下 worktree 规则为硬性门禁，违反即阻断。
</HARD-GATE>

| # | 规则 | 原因 |
|---|------|------|
| 1 | **同一分支同一时刻只能在一个 worktree 检出** | Git 的硬约束，强行检出会报错 |
| 2 | **主 worktree（原始目录）始终停留在 `master` 分支** | 保证合并/推送操作不受干扰 |
| 3 | **每个 Agent 创建独立 worktree，只做自己的任务** | 隔离工作上下文，互不干扰 |
| 4 | **任务完成后必须清理 worktree**：`git worktree remove` + `git worktree prune` | 防止残留 worktree 堆积 |
| 5 | **创建 worktree 前必须先 `git fetch origin`** | 确保基于最新远端状态创建 |
| 6 | **禁止在 worktree 中执行 `git checkout` 切换分支** | worktree 绑定分支是固定的；如需换分支，删除重建 |

#### 0.6.3 常用命令速查

```bash
# === 查看所有 worktree ===
git worktree list                        # 人类可读
git worktree list --porcelain            # 脚本友好

# === 创建 worktree（基于远端 master 新建功能分支） ===
git fetch origin
git worktree add ../.wt/dev-TASK-123 -b feature/TASK-123-xxx origin/master

# === 创建 worktree（基于已有本地分支） ===
git worktree add ../.wt/dev-TASK-456 feature/TASK-456-xxx

# === 创建 worktree（临时查看某个提交，不占用分支） ===
git worktree add --detach ../.wt/qa-TASK-123-review abc1234

# === 删除 worktree ===
git worktree remove ../.wt/dev-TASK-123

# === 强制删除（有未提交改动时慎用） ===
git worktree remove -f ../.wt/dev-TASK-123

# === 清理无效 worktree 记录（目录已手动删除时） ===
git worktree prune -n -v                  # 预演
git worktree prune                        # 实际清理

# === 锁定/解锁 worktree（防止误删） ===
git worktree lock ../.wt/dev-TASK-123 --reason "hotfix in progress"
git worktree unlock ../.wt/dev-TASK-123

# === 对指定 worktree 执行 Git 命令 ===
git -C ../.wt/dev-TASK-123 status
git -C ../.wt/dev-TASK-123 add -A
git -C ../.wt/dev-TASK-123 commit -m "feat: xxx (TASK-123)"
git -C ../.wt/dev-TASK-123 push -u origin feature/TASK-123-xxx
```

#### 0.6.4 Agent 使用 worktree 的标准流程

```
1. @lead 在主 worktree 执行 git fetch origin（保持 master 最新）
2. Agent 在自己的 worktree 中开发：
   a. 基于 origin/master 创建 worktree + 功能分支
   b. 在 worktree 中编码、提交
   c. 从 worktree 推送到远端
3. @lead 在主 worktree 执行合并回 master
4. Agent（或 @lead）清理 worktree
```

#### 0.6.5 常见坑与排查

| 问题 | 原因 | 解决 |
|------|------|------|
| `branch already checked out` | 同一分支已在其他 worktree 检出 | 使用不同分支名，或用 `--detach` |
| worktree 目录残留但 `git worktree list` 不显示 | 目录被手动删除但记录还在 | `git worktree prune` |
| `worktree is locked` | 被 `git worktree lock` 锁定或异常退出残留 | `git worktree unlock <path>` 后重试 |
| worktree 中 `git log` 看不到最新提交 | 未在 worktree 中执行 `git fetch` | 在主 worktree fetch，或 `git -C <wt> fetch origin` |

---

## 1. 开工前检查 — 工作区门禁

<HARD-GATE>
主 worktree（原始目录）必须始终停留在 `master` 分支且保持干净。Agent 开发不直接在主 worktree 进行，而是创建独立 worktree。
开工前必须先完成 [0.3 协作检查](#03-开工前的协作检查)。
</HARD-GATE>

### 1.1 主 worktree 状态检查

```bash
# 确认当前在 master 分支
git branch --show-current   # 必须输出 master

# 确认工作区干净（仅主 worktree 有此要求）
git status --porcelain      # 应无输出

# 查看已有的 worktree（了解其他 Agent 在做什么）
git worktree list
```

### 1.2 同步主 worktree

```bash
git fetch origin
git log HEAD..origin/master --oneline  # 查看落后多少提交
# 如果有落后：
git merge origin/master                 # 或 git pull origin master
```

### 1.3 Agent 创建自己的 worktree

<HARD-GATE>
每个 Agent 开始编码前，必须从主 worktree 创建自己的 worktree。禁止在两个 worktree 中检出同一分支。
</HARD-GATE>

```bash
# 在主 worktree（Pudding/）中执行：

# 方式一：基于 origin/master 新建功能分支
git worktree add ../.wt/dev-TASK-123 -b feature/TASK-123-short-desc origin/master

# 方式二：基于已有本地分支
git worktree add ../.wt/dev-TASK-456 feature/TASK-456-short-desc

# 方式三：临时查看某个提交（不占用分支名，适合 QA 审阅）
git worktree add --detach ../.wt/qa-TASK-123-review abc1234
```

### 1.4 处理不干净的主 worktree

| 场景 | 处理方式 |
|------|---------|
| **有未提交的变更（当前任务相关）** | `git add -A && git commit -m "描述 (TASK-xxx)"` |
| **有未提交的变更（其他任务残留）** | 切回原分支提交，或 `git stash push -m "描述"`（需记录 stash 原因到 Context.md） |
| **有 stash 残留** | 检查是否仍需保留：如果不用则 `git stash drop`；如果需要则 `git stash pop` 后提交 |
| **主 worktree 不在 master** | `git checkout master`（如果当前分支有未提交变更，先提交或 stash） |

---

## 2. 分支管理

### 2.1 分支命名规范

```
<type>/TASK-<id>-<short-description>
```

| type | 用途 | 示例 |
|------|------|------|
| `feature` | 新功能 | `feature/TASK-123-password-export` |
| `fix` | Bug 修复 | `fix/TASK-456-null-reference` |
| `refactor` | 重构（功能不变） | `refactor/TASK-789-di-unify` |
| `hotfix` | 紧急热修复 | `hotfix/TASK-999-crash-on-startup` |
| `docs` | 纯文档 | `docs/TASK-100-update-readme` |

**规则：**
- 全小写，单词用 `-` 连接
- 必须包含 `TASK-<id>`（无任务卡则先创建任务卡）
- 简短描述不超过 4 个单词

### 2.2 创建分支（通过 worktree）

<HARD-GATE>
写任何代码前，必须通过 `git worktree add` 从最新 `origin/master` 创建功能分支和独立工作区。
</HARD-GATE>

```bash
# 1. 在主 worktree 同步远端（如果尚未同步）
git checkout master
git fetch origin
git merge origin/master

# 2. 创建 worktree + 功能分支（一步完成）
git worktree add ../.wt/<agent>-TASK-xxx -b <type>/TASK-xxx-short-desc origin/master

# 3. Agent 进入自己的 worktree 开始工作
cd ../.wt/<agent>-TASK-xxx
```

### 2.3 分支与任务卡绑定

创建 worktree 后**立即**在任务卡上登记：

```bash
python todo-api/todo_api.py update TASK-xxx \
  --stage in_progress \
  --last-agent-summary "从 origin/master 创建 worktree .wt/<agent>-TASK-xxx，分支 <type>/TASK-xxx-short-desc"
```

任务的 `last_agent_summary` 和备注中应持续记录分支名和 worktree 路径，作为完成证据。

### 2.4 禁止操作

| 禁止 | 原因 |
|------|------|
| `git push --force` / `git push -f` | 破坏远程历史，不可逆 |
| `git push --force-with-lease` 到共享分支 | 仍可能覆盖他人提交 |
| `git rebase` 已推送的分支 | 改变已共享的提交 SHA |
| `git commit --amend` 已推送的提交 | 同上 |
| `git reset --hard` 已推送的提交 | 同上 |
| 在 worktree 中 `git checkout` 切换分支 | worktree 与分支绑定，切换会破坏隔离；应删除重建 |
| 多 Agent 共享同一 worktree | 切换分支会破坏另一 Agent 的工作上下文 |

**例外**：`--force-with-lease` 仅在个人独占分支且确认无他人基于此分支工作时允许，需在 Context.md 中记录原因。

---

## 3. 提交规范

### 3.1 提交粒度

每次提交应该是一个**逻辑独立的原子变更**：
- 一个功能的完整实现 + 测试
- 一个 Bug 修复 + 回归测试
- 测试通过才能提交

**反模式：**
- "WIP"、"临时保存"、"下班提交" → 用 `git stash` 代替
- 一次提交包含 10 个不相关的修改 → 拆分为多个提交

### 3.2 提交信息格式

```
<type>: <简短中文描述> (TASK-xxx)

<可选的详细说明 — 为什么这样改，不是改了什么>
```

**type 类型：**

| type | 用法 |
|------|------|
| `feat` | 新功能 |
| `fix` | Bug 修复 |
| `refactor` | 重构（不改变功能） |
| `test` | 添加或修改测试 |
| `docs` | 文档变更 |
| `chore` | 构建/工具/配置变更 |
| `style` | 格式（空格、缩进等） |

**示例：**
```
feat: 实现密码模块 SM2 密钥对导出功能 (TASK-123)

导出格式符合 GMT 0015 规范，支持 PEM 和 DER 两种编码。
```

```
fix: 修复 ProjectManager.OpenProject 空引用异常 (TASK-456)

根因：EnsureProjectDefaults 在未初始化 context 时被调用。
修复：添加 null 检查并在初始化后调用。
```

### 3.3 提交前检查清单

- [ ] 代码编译通过：`dotnet build` 零错误
- [ ] 测试通过：`dotnet test` 全绿
- [ ] 无调试代码残留（`Console.WriteLine`、`Debug.WriteLine` 等）
- [ ] 关键链路已添加日志
- [ ] 遵守架构分层

---

## 4. 推送与合并

### 4.1 从 worktree 推送到远端

<HARD-GATE>
推送前必须检查 QA 门禁是否通过。QA FAIL → 禁止推送。
</HARD-GATE>

```bash
# 在 Agent 自己的 worktree 中执行推送

# 1. 确认 QA 已通过
python todo-api/todo_api.py get-task TASK-xxx  # 查看 stage 是否为 ready_for_qa 且 qa_gate 已通过

# 2. 获取远程最新
git fetch origin

# 3. 推送到远端
git push -u origin <type>/TASK-xxx-short-description
```

> **提示**：也可以从主 worktree 用 `git -C ../.wt/<agent>-TASK-xxx push` 操作，但直接在 worktree 中执行更直观。

**推送时机：**
- 日常开发中：每日至少推送一次（防止本地数据丢失）
- QA 审阅前：将最新代码推送至远端，供 QA 访问
- QA 通过后：推送最终版本，准备合并

### 4.2 在主 worktree 合并回 master

<HARD-GATE>
合并操作必须在主 worktree（原始 Pudding/ 目录）的 `master` 分支上执行。禁止在 Agent worktree 中合并。
</HARD-GATE>

```bash
# 在主 worktree（Pudding/）中执行：

# 1. 确保在主 worktree 且本地 master 是最新的
git checkout master
git pull origin master

# 2. 合并功能分支（使用 --no-ff 保留分支历史）
git merge --no-ff <type>/TASK-xxx-short-description

# 3. 确保合并后测试通过
dotnet build
dotnet test

# 4. 推送到远端
git push origin master

# 5. 删除本地和远程功能分支
git branch -d <type>/TASK-xxx-short-description
git push origin --delete <type>/TASK-xxx-short-description

# 6. 清理 worktree
git worktree remove ../.wt/<agent>-TASK-xxx
git worktree prune
```

**合并规则：**
- 始终使用 `--no-ff`（保留分支历史轨迹）
- 禁止直接向 `master` 提交（必须通过分支 + 合并）
- 合并前必须确认 QA 门禁通过且任务卡已 `done`
- 合并后必须清理对应的 worktree

### 4.3 冲突解决

```bash
# 1. 冲突发生时，先了解双方变更
git log --oneline master..feature/TASK-xxx   # 我的变更
git log --oneline feature/TASK-xxx..master   # master 新增的变更

# 2. 逐个文件解决冲突
# 编辑冲突文件 → 删除 <<<<<<< ======= >>>>>>> 标记

# 3. 标记已解决
git add <resolved-file>

# 4. 完成合并
git commit -m "merge: 合并 feature/TASK-xxx 解决冲突"

# 5. 运行测试验证
dotnet test
```

**冲突解决原则：**
- 理解双方的变更意图，不盲目选一边
- 解决后必须运行完整测试
- 如果冲突涉及架构级变更，拉 `@architect` 判断

---

## 5. 回退机制

### 5.1 决策树

```
需要回退？
├─ 未提交的修改 → git checkout -- <file> 或 git restore <file>
├─ 已提交但未推送 → git revert <commit>（推荐）或 git reset --soft HEAD~1
├─ 已推送到个人分支 → git revert <commit>（推荐，保留历史）
├─ 已推送到共享分支 → 只能用 git revert，禁止 reset
└─ 已合并到 master → git revert -m 1 <merge-commit>
```

### 5.2 回退操作

```bash
# ✅ 推荐：revert（安全，保留历史）
git revert <bad-commit-sha>
git commit -m "revert: 回退 <描述> (TASK-xxx)"

# ⚠️ 谨慎：reset（仅未推送的本地提交）
git reset --soft HEAD~1   # 保留修改在工作区
git reset --hard HEAD~1   # 完全丢弃修改

# ❌ 禁止：已推送后用 reset + force push
```

### 5.3 误操作恢复

```bash
# 查看所有操作历史（包括已删除的分支和 reset 的提交）
git reflog

# 恢复误删的提交
git checkout -b recovery-branch <lost-sha>

# 恢复误删的分支
git checkout -b <branch-name> <sha-from-reflog>
```

---

## 6. 历史记录规范

### 6.1 干净历史三原则

1. **可追溯**：每个提交通过 `TASK-xxx` 关联到任务卡
2. **可阅读**：提交信息清晰说明做了什么、为什么
3. **不可破坏**：已推送的历史不可变

### 6.2 反模式

| 反模式 | 后果 | 正确做法 |
|--------|------|---------|
| `git push -f` | 覆盖他人工作，历史丢失 | 使用 `git revert` |
| 提交信息 "fix"、"update"、"WIP" | 无法理解变更内容 | 写完整中文描述 + TASK ID |
| 一次提交包含多个不相关变更 | 无法单独回退 | 拆分为多个原子提交 |
| 直接向 master 提交 | 绕过审查 | 走分支 + PR + QA 流程 |
| 合并时使用 fast-forward | 丢失分支历史 | 使用 `--no-ff` |
| squash merge | 丢失任务级提交粒度 | 保持完整提交历史 |

### 6.3 提交历史关联到任务卡

任务完成后，在任务卡备注中记录完成的 Git 信息：

```bash
# 记录分支和提交信息
python todo-api/todo_api.py note TASK-xxx \
  --author dev \
  --text "Git 分支: feature/TASK-xxx-xxx | 提交: abc1234 feat: XXX (TASK-xxx) | 合并: def5678 merge --no-ff"
```

---

## 7. 推送门禁

### 7.1 门禁流程

```
代码完成 → 自测通过 → QA 审阅 PASS → 推送远端 → 合并 master → 任务卡 done
                                    ↑
                              QA FAIL 则阻断
```

### 7.2 推送前检查清单

- [ ] 工作区干净（`git status` 无未提交文件）
- [ ] QA 门禁通过（`qa_gate` 为 PASS 或 PASS_WITH_NOTES）
- [ ] 任务卡 stage 为 `ready_for_qa` 或 `done`
- [ ] 分支名符合命名规范
- [ ] 提交信息包含 TASK ID
- [ ] 没有 `--force` 推送
- [ ] 已拉取远端最新（避免冲突）

```bash
# 一键门禁检查脚本思路
python todo-api/todo_api.py get-task TASK-xxx | grep -E "(stage|qa_gate)"
git status --porcelain  # 应无输出
git log origin/master..HEAD --oneline  # 检查即将推送的提交
```

---

## 8. 场景化流程速查

### 场景 A: 正常功能开发（单 Agent）

```
# === @lead 在主 worktree 准备工作 ===
1. git checkout master && git fetch origin && git merge origin/master

# === 创建 Agent worktree ===
2. git worktree add ../.wt/dev-TASK-123 -b feature/TASK-123-xxx origin/master
3. python todo-api/todo_api.py update TASK-123 --stage in_progress  # 标记开始
4. python todo-api/todo_api.py bulletin-post --type progress ...    # 发开始公告

# === Agent 在 worktree 中开发 ===
5. cd ../.wt/dev-TASK-123
6. [编码 + TDD + 提交...]
7. git push -u origin feature/TASK-123-xxx                         # 日常推送

# === QA 审阅 ===
8. [QA 审阅通过]

# === @lead 在主 worktree 合并 ===
9. cd <back-to-main-worktree> && git checkout master
10. git pull origin master
11. git merge --no-ff feature/TASK-123-xxx
12. dotnet test && git push origin master

# === 清理 ===
13. git branch -d feature/TASK-123-xxx
14. git push origin --delete feature/TASK-123-xxx
15. git worktree remove ../.wt/dev-TASK-123 && git worktree prune
16. python todo-api/todo_api.py finish TASK-123 --summary "..." --release
```

### 场景 B: 紧急热修复

```
# === @lead 在主 worktree 准备工作 ===
1. git checkout master && git fetch origin && git merge origin/master

# === 创建热修 worktree ===
2. git worktree add ../.wt/dev-TASK-999 -b hotfix/TASK-999-xxx origin/master
3. git worktree lock ../.wt/dev-TASK-999 --reason "hotfix in progress"

# === Agent 在 worktree 中修复 ===
4. [修复 + 测试 + 提交]
5. git -C ../.wt/dev-TASK-999 push -u origin hotfix/TASK-999-xxx

# === 快速 QA 审阅 ===
6. [快速 QA 审阅 — 可适当加速但不可跳过]

# === @lead 在主 worktree 合并 ===
7. git checkout master && git pull origin master
8. git merge --no-ff hotfix/TASK-999-xxx
9. dotnet test && git push origin master

# === 清理 ===
10. git worktree unlock ../.wt/dev-TASK-999
11. git worktree remove ../.wt/dev-TASK-999 && git worktree prune
12. git branch -d hotfix/TASK-999-xxx && git push origin --delete hotfix/TASK-999-xxx
13. python todo-api/todo_api.py note TASK-999 --author dev --text "热修复 worktree .wt/dev-TASK-999 | 分支 hotfix/TASK-999-xxx"
```

### 场景 C: 多 Agent 本地并行开发（核心场景）

```
# === 前提：@lead 在主 worktree 完成远端同步 ===
0. git checkout master && git fetch origin && git merge origin/master

# === Agent A（@dev）开始 TASK-001 ===
1. python todo-api/todo_api.py claim TASK-001 --agent-id dev-agent
2. git worktree add ../.wt/dev-TASK-001 -b feature/TASK-001-foo origin/master
3. python todo-api/todo_api.py bulletin-post --type progress \
     --author-agent dev-agent --text "开始 TASK-001，worktree: .wt/dev-TASK-001"

# === Agent B（@lightweight-developer）同时开始 TASK-002 ===
4. python todo-api/todo_api.py claim TASK-002 --agent-id lightweight-agent
5. git worktree add ../.wt/lightweight-TASK-002 -b feature/TASK-002-bar origin/master
6. python todo-api/todo_api.py bulletin-post --type progress \
     --author-agent lightweight-agent --text "开始 TASK-002，worktree: .wt/lightweight-TASK-002"

# === 两个 Agent 在各自的 worktree 中并行开发，互不干扰 ===
7. # Agent A: cd ../.wt/dev-TASK-001 && 编码提交推送
8. # Agent B: cd ../.wt/lightweight-TASK-002 && 编码提交推送

# === Agent A 修改共享文件前发出预警 ===
9. python todo-api/todo_api.py bulletin-post --type warning \
     --author-agent dev-agent \
     --text "即将修改 Core/Interfaces/IFoo.cs，涉及 TASK-001"

# === Agent B 看到预警，确认自己不受影响，继续工作 ===

# === Agent A 完成，交 QA ===
10. python todo-api/todo_api.py bulletin-post --type handoff \
      --author-agent dev-agent --target-agent qa-agent \
      --text "TASK-001 已推送 feature/TASK-001-foo"

# === @lead 合并 TASK-001 ===
11. git checkout master && git pull origin master
12. git merge --no-ff feature/TASK-001-foo && git push origin master
13. git worktree remove ../.wt/dev-TASK-001 && git worktree prune

# === Agent B 同步 master（拉取 TASK-001 的变更） ===
14. # 在主 worktree 已是最新，Agent B 在自己的 worktree 中：
    git -C ../.wt/lightweight-TASK-002 fetch origin
    git -C ../.wt/lightweight-TASK-002 merge origin/master
15. # 继续开发，不会冲突
```

### 场景 D: QA 审阅用临时 worktree

```
# === @qa 创建临时 worktree 审阅某个 PR 的提交 ===
1. git fetch origin pull/123/head:pr-123
2. git worktree add --detach ../.wt/qa-pr-123-review pr-123

# === 在临时 worktree 中审阅 ===
3. cd ../.wt/qa-pr-123-review
4. [编译、运行测试、代码审阅...]

# === 审阅完成，清理 ===
5. cd <back-to-main-worktree>
6. git worktree remove ../.wt/qa-pr-123-review && git worktree prune
7. git branch -D pr-123  # 清理临时拉取的分支
```

### 场景 E: 回溯调试/复现（不污染当前目录）

```
# === 为某个历史提交创建临时 worktree ===
1. git worktree add --detach ../.wt/tmp-debug abc1234

# === 在临时 worktree 中调试 ===
2. cd ../.wt/tmp-debug
3. [编译、复现、排查...]

# === 用完清理 ===
4. cd <back-to-main-worktree>
5. git worktree remove ../.wt/tmp-debug && git worktree prune
```

### 场景 F: 需要回退已合并的错误提交

```
# === 在主 worktree 执行（不影响其他 Agent 的 worktree） ===
1. git checkout master && git pull origin master
2. git revert -m 1 <merge-commit-sha>              # 回退整个合并（保留历史）
3. git commit -m "revert: 回退 feature/TASK-xxx 因发现缺陷 (TASK-001)"
4. dotnet test → 确认回退后测试通过
5. git push origin master

# === 通知所有活跃 Agent ===
6. python todo-api/todo_api.py bulletin-post --type alert \
     --text "已回退 feature/TASK-xxx，所有 Agent 请同步 master"
```

### 场景 G: 日常维护 — 查看与清理 worktree

```
# === 查看所有 worktree 状态 ===
git worktree list

# === 清理无效记录 ===
git worktree prune -n -v     # 预演
git worktree prune           # 实际清理

# === 排查残留锁 ===
git worktree list --porcelain | grep locked
git worktree unlock <path>   # 确认无人在用后解锁
```

---

## 9. 关键约束总结

| 约束 | 规则 |
|------|------|
| **开工门禁** | 主 worktree 干净且在 `master`；Agent 通过 `git worktree add` 创建独立工作区 |
| **协作检查** | 查阅公告板 → 检查进行中任务 → 认领任务 → `git worktree list` → 远端同步 |
| **远端同步** | 开工/推送/合并前必须 `git fetch`；`git pull` 只在主 worktree 的 master 分支执行 |
| **分支命名** | `type/TASK-id-description`，全小写 |
| **分支绑定** | 分支名和 worktree 路径必须在任务卡 `last_agent_summary` 或备注中登记 |
| **提交信息** | `<type>: <中文描述> (TASK-xxx)` |
| **禁止操作** | `push -f`、`rebase` 已推送分支、直接向 master 提交、squash merge、在 worktree 中 checkout 切换分支、多 Agent 共享 worktree |
| **合并方式** | `--no-ff`，在主 worktree 执行，保留完整分支历史 |
| **推送门禁** | QA PASS 后从 worktree 推送，合并前主 worktree 的 `git status` 干净 |
| **回退方式** | `git revert`（不可逆历史），禁止 `reset` 已推送提交 |
| **完成证据** | 任务卡备注中记录 worktree 路径 + 分支名 + 关键提交 SHA |
| **公告板** | 开始任务/交接/修改共享文件/阻塞/回退 必须发公告 |
| **worktree 隔离** | 一 Agent 一 worktree，主 worktree 仅用于 fetch/pull/merge；任务完成后清理 worktree |
| **worktree 目录** | 统一放在 `.wt/<agent>-TASK-id/`，`.wt/` 加入 `.gitignore` |
