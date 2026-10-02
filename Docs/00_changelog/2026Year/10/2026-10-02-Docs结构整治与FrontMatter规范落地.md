---
title: Docs 结构整治与 Front Matter 规范落地
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: active
description: "按用户 5 项要求整治 Docs：① 一级目录统一 NN_english_snake 并全仓库同步引用；② Docs 根文件按性质分类归位；③ 在 Agents.md 写入 Docs 使用规则与 Markdown Front Matter 规则；④ 两个参考子仓库迁至 external/references 并以 submodule 管理；⑤ 新增 Front Matter 校验/补齐工具并全量补齐 Docs 622 个 md。"
categories: [docs, changelog]
tags: [docs, structure, front-matter, submodule, conventions]
related_docs: [Docs/README.md, Agents.md, code_map.md, Docs/00_changelog/README.md]
related_files: [Tools/Docs/front_matter.py, Tools/Docs/check_md_links.py, .gitmodules, PuddingAgentNetwork.slnx]
slug: changelog-2026-10-02-docs-structure-and-front-matter
draft: false
---

# 2026-10-02 Docs 结构整治与 Front Matter 规范落地

## 目标 / 背景

用户提出 5 项要求：① 一级子目录按「数字_名称」重命名并递归检查；② Docs 下文件分类整理与移动；
③ 规划 Docs 使用规则并写入 `Agents.md`；④ 把 `Docs/claude-reviews-claude`、`Docs/IndepthCoding-Guide`
两个子仓库放到合理的「参考资料」位置并继续以子仓库方式管理；⑤ 编写 `Markdown Front Matter`
校验 py 脚本，批量扫描不合规并修复，同时把规则写入 `Agents.md`。

方案先经用户确认（5 个决策点全部选推荐项）：目录全部编号英文名、文件名保留语义名、
Front Matter 覆盖 Docs 全部 md（含日志与归档）、子仓库放 `external/references/<name>`、
删除过期 VS 共享工程。

## 改动（4 个提交）

### 1. 结构整治（`d942ea6`，791 文件）

- **24 个一级目录重命名**为 `NN_english_snake`：`01消息渠道→01_message_channels`、
  `02智能体与智能体运行时→02_agent_runtime`、`03多智能体→03_multi_agent`、
  `04工具与技能→04_tools_and_skills`、`05服务商与模型→05_providers_and_models`、
  `07架构→07_architecture`、`09审计→09_audit`、`00Changelog→00_changelog`、`Config→06_config`、
  `How-Debuge→08_how_debuge`、`Conventions→10_conventions`、`Design→11_design`、
  `Features→12_features`、`Reports→14_reports`、`Tasks→15_tasks`、`QA→16_qa`、`Memory→17_memory`、
  `superpowers→18_superpowers`、`Resources→20_resources`、`Archive→90_archive`；
  新建 `13_runbooks`、`19_references`（`deepseek-*` 研究与 `openhanako` 归入）。
- **Docs 根 22 个文件归位**（文件名不改）：`架构.md→07_architecture`；`Tasks/待办/路线图→15_tasks`；
  `Review.md→16_qa`；`SUBAGENTS.md`、`agent-collaboration-agreement.md→10_conventions`；
  `context.md→90_archive`；deepseek-harness 系列 5 份→`19_references[+deepseek_harness]`；
  两份 UI 设计→`11_design`；`event-driven-wake-design`、`model-visible-*`、`lease-ack-语义说明→12_features`；
  两份运行手册→`13_runbooks`；`thanks.md→90_archive`。此后 **Docs 根只留 `README.md`**。
- `Docs/Temp/check-circular-deps.ps1→TestScripts/`；删除路径已全部过期的 VS 共享工程
  `Docs/Docs.projitems`、`Docs/Docs.shproj` 并从 `PuddingAgentNetwork.slnx` 摘除。
- **全局引用同步**：455 个文件改写（719 处前缀替换 + 463 处按新位置重算相对链接），
  覆盖 `code_map.md`、`Agents.md`、`README*.md`、`TestScripts`、`Tools`、`Source` 内注释引用与 `.pudding` 记忆。

### 2. 子仓库归位（`e83ec46`，7 文件）

- `Docs/claude-reviews-claude → external/references/claude-reviews-claude`；
  `Docs/IndepthCoding-Guide → external/references/IndepthCoding-Guide`（与 `external/github.hyfree.GM` 同级）。
- 同步：`.gitmodules` 的 path 与段名、`git mv` gitlink 与工作树、
  `.git/modules/Docs/<name> → .git/modules/external/references/<name>`、
  工作树 `.git` 的 gitdir（需先 `attrib -r -h -s` 才能写）、模块 config 的 `core.worktree`（5 层相对路径）、
  `.git/config` 段名（`git submodule sync --recursive`）。

### 3. Front Matter（`f69a839`，622 文件）

- 新增 `Tools/Docs/front_matter.py`（纯标准库，零依赖；PyYAML 在本机不可用）：
  12 字段（`title/author/date/last_reviewed/status/description/categories/tags/related_docs/related_files/slug/draft`）；
  `--check`（非零退出）/`--fix`/`--dry-run`/`--force`/`--paths`/`--report`/`--all-repo`；
  只补缺失、不覆盖已有非空值；幂等。
- **取值可追溯**：git 历史（author/date/last_reviewed；文件被移动时用 `git log --follow`，
  再无记录才回落文件 mtime）、首个 H1（title）、首段落/引用块/表首行（description）、
  目录（categories）、文件名与目录（tags）、文内链接（related_docs）、反引号内真实文件（related_files）。
- **Docs 下 622 个 md 全部补齐**，`--check` 通过（`related_docs`/`related_files` 允许空列表，
  `categories`/`tags` 不得为空；时间序日志与 `90_archive` 用 `status: archived`）。
- 顺带修复迁移引入的 23 处相对链接（指向已迁走子仓库的 `../claude-reviews-claude/...`）。

### 4. 规则与常驻工具（`57b6074`，4 文件）

- `Agents.md` 新增两节：**「Docs 目录与文档规范（强制）」**（目录地图、每目录放什么、
  根目录只允许 README.md、文件名保留语义名、引用必须可解析、第三方仓库只在
  `external/references/<name>` 且禁止改上游、日志/归档纪律）与
  **「Markdown Front Matter 规则（强制）」**（字段表、示例、取值来源、status 取值域、门禁命令）。
- `Docs/README.md` 重写为目录地图与规范入口（22 个目录 + 放什么→去哪决策表 + 门禁命令 +
  阅读顺序 + 修正后的架构基线：WinUI 3 Shell，删除失效的 P2P/Todo-API/06智能体网关 表述）。
- 新增常驻工具 `Tools/Docs/check_md_links.py`：仓库级 Markdown 链接/锚点检查
  （GitHub slug 口径、跳过子仓库与 `temp/`、支持 `file.cs:123` 行号、非零退出可作门禁）。

## 验证

| 检查 | 命令 / 方式 | 结果 |
|------|-------------|------|
| 结构迁移无损 | `temp/docs_restructure.py` 报告 | 690 个文件级移动，0 错误 |
| 引用改写 | `temp/rewrite_docs_paths.py`（先 dry-run 再 apply） | 455 文件 / 1182 处 |
| 旧目录名残留 | 全仓库扫描 31 个旧前缀 | 仅 2 处非引用命中（构建产物 `dist-dev/*.js`、测试夹具 JSON） |
| 子模块健康 | `git submodule status` + 子模块内 `status/rev-parse/log` | 3 个子模块 SHA 不变；工作树 0 改动；文件数 148 / 11 与迁移前一致 |
| Front Matter 门禁 | `python Tools/Docs/front_matter.py --check` | 623 个 md，不合规 **1**（他方本窗口新建文件，见留白） |
| 链接/锚点门禁 | `python Tools/Docs/check_md_links.py` | 623 个 md，**失效锚点 0**，失效目标 27（既有残留，见下） |
| 幂等 | `--fix --dry-run` | 无实际待补 |

**未验证**：未运行 .NET 构建/测试（纯文档、脚本与 git 结构改动）。

## 影响与回滚

- 影响面：`Docs/**`（结构 + Front Matter）、`.gitmodules`/子模块位置、`Agents.md`、`Docs/README.md`、
  `PuddingAgentNetwork.slnx`、`Tools/Docs/*`、若干引用 Docs 路径的源码注释与脚本。
- 无代码逻辑、配置、数据、部署影响；`Docs.projitems`/`Docs.shproj` 删除后解决方案少了过期的共享工程条目。
- 回滚：`git revert` 4 个提交；备份留存于 `temp/backup/`（改写前文件）、`temp/backup2/`（Front Matter 前的整棵 Docs）。
- 子模块回滚需同时还原 `.gitmodules`、`.git/modules/<path>` 与工作树 `.git` 文件（gitdir）。

## 关联

- 规则：`Agents.md`（Docs 目录规范 / Front Matter 规则 / 日志规则 / code_map 规则）、`Docs/README.md`、`Docs/00_changelog/README.md`
- 工具：`Tools/Docs/front_matter.py`、`Tools/Docs/check_md_links.py`
- 前序：`Docs/00_changelog/2026Year/10/2026-10-02-code_map瘦身与日志归档规范落地.md`、`2026-10-02-存量日志迁移与失效链接修复.md`

## 留白与风险

- **剩余失效链接 27 处**（均为既有残留，非本次引入）：已删除的历史 QA 文件（`15_tasks/Tasks.md`、`16_qa/Review.md`、
  3 份 ADR）、被移出版本控制的 `memory/plans/frontend-chat-ui-audit-plan.md`、2 处 `…` 伪链接。
  相关文件头部已加「链接核查（2026-10-02）」说明。
- **1 个他方新建文件缺 Front Matter**：`Docs/00_changelog/2026Year/10/` 下本窗口新写入的日志；
  下次 `--fix` 即会补齐（工具幂等）。
- **`.pudding/` 未纳入**：该目录已移出版本控制，其中 `skills/code_map-incremental-update/SKILL.md`
  仍含旧格式说明与 1 处模板占位链接；建议其版本化后按新规则更新。
- 本轮与另一位 Agent 并行写 Docs；全程用 `--pathspec-from-file` 精确暂存，未提交他方未完成改动
  （唯一例外：`Source/PuddingHost/code_map.md` 含他方一行路径修正，已单独说明）。
