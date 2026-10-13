---
title: Pudding Agent Network 文档索引
author: hyfree
date: 2026-02-12
last_reviewed: 2026-10-02
status: active
description: "这里是 Pudding Agent 的设计入口。当前产品主线是 Windows First 的 PuddingDesktop.exe：WinUI 3 Shell 承载窗口/托盘/WebView2 与独立 ASP.NET Core 子进程监督，Core 继续承载 API、Controller、Runtime、Connector 与 SQLite。"
categories: [docs]
tags: [readme]
related_docs: [Docs/00_changelog/README.md, code_map.md, Agents.md, Docs/07_architecture/架构.md, Docs/07_architecture/README.md]
related_files: [code_map.md, Agents.md, Tools/Docs/front_matter.py, Docs/00_changelog/README.md]
slug: docs-readme
draft: false
---

# Pudding Agent Network 文档索引

> 本文件是 **Docs 目录地图与规范入口**：放什么、在哪、怎么命名、怎么校验。
> **修改日志写 [`00_changelog/`](00_changelog/README.md)，不写进本文件**；代码索引见根 [`code_map.md`](../code_map.md)；强制规则见 [`Agents.md`](../Agents.md) 的「Docs 目录与文档规范」与「Markdown Front Matter 规则」。

## 目录地图

一级目录统一 `NN_english_snake`（新增目录必须编号 + 英文小写，并在此表登记）。

| 目录 | 放什么 | 入口 |
|------|--------|------|
| `00_changelog/` | 修改日志（唯一去处，按 `<YYYY>Year/<MM>/` 分层） | [README](00_changelog/README.md) |
| `01_message_channels/` | 消息渠道（飞书等） | — |
| `02_agent_runtime/` | 智能体与智能体运行时 | — |
| `03_multi_agent/` | 多智能体协作 | — |
| `04_tools_and_skills/` | 工具系统与技能 | — |
| `05_providers_and_models/` | 服务商与模型 | — |
| `06_config/` | 配置说明（hooks、pudding-yaml） | [hooks](06_config/hooks.md) · [pudding-yaml](06_config/pudding-yaml.md) |
| `07_architecture/` | 架构分册与 ADR（含 `design/`） | [架构总览](07_architecture/架构.md) · [分册入口](07_architecture/README.md) |
| `08_how_debuge/` | 调试与诊断手册（主索引 + 01～15 分册） | [README](08_how_debuge/README.md) |
| `09_audit/` | 审计清单 | — |
| `10_conventions/` | 规程与约定（组件化交付、仓库卫生、SUBAGENTS、协作协议） | [组件化交付规程](10_conventions/组件化交付规程.md) · [Agents-Hygiene](10_conventions/Agents-Hygiene.md) |
| `11_design/` | 设计规格与视觉/UI 设计 | — |
| `12_features/` | 设计方案、施工计划、任务书（面向未来施工） | — |
| `13_runbooks/` | 运行手册、部署/回滚/一次性操作 | — |
| `14_reports/` | 诊断 / 验收 / 评测报告（一次性证据） | — |
| `15_tasks/` | 任务、待办、路线图、历史任务卡 | [Tasks](15_tasks/Tasks.md) · [待办](15_tasks/待办.md) · [路线图](15_tasks/路线图.md) |
| `16_qa/` | 验收记录与审阅索引 | [Review](16_qa/Review.md) |
| `17_memory/` | 记忆快照 | — |
| `18_superpowers/` | 外部方法论资料（`plans/`、`specs/`） | — |
| `19_references/` | 外部参考项目研究（`deepseek_harness/` 等） | — |
| `20_resources/` | 图片等静态资源（`Images/`） | — |
| `90_archive/` | 归档（只加不改） | — |

**Docs 根目录只允许本文件（`README.md`）**；新文档按性质进入上表目录，禁止新增根文件。
**第三方参考仓库**统一放在 `external/references/<name>` 并以 git submodule 管理（禁止修改其内容）。

## 放什么 → 去哪（决策表）

| 内容 | 去处 |
|------|------|
| 「这轮改了什么、验证到哪」 | `00_changelog/`（追加式，不抹除） |
| 长期有效的架构决策 | `07_architecture/`（ADR） |
| 面向未来的设计方案 / 施工计划 | `12_features/` |
| 设计规格、视觉/UI 设计 | `11_design/` |
| 一次性诊断 / 验收 / 评测证据 | `14_reports/`、`16_qa/` |
| 操作手册、部署/回滚步骤 | `13_runbooks/` |
| 规程、门禁、交付纪律 | `10_conventions/` |
| 任务、待办、路线图 | `15_tasks/` |
| 外部项目的对照研究 | `19_references/` |
| 过期但需留痕的材料 | `90_archive/` |

## 命名与 Front Matter

- **文件名保留语义名**（中文主题、日期均可）；重命名/移动必须脚本化同步全仓库引用。
- **每个 md 开头必须有 Front Matter**（12 个字段：`title / author / date / last_reviewed / status / description / categories / tags / related_docs / related_files / slug / draft`），规则与示例见 [`Agents.md`](../Agents.md)「Markdown Front Matter 规则（强制）」。
- 门禁命令：

```bash
python Tools/Docs/front_matter.py --check          # 不合规非零退出
python Tools/Docs/front_matter.py --fix --dry-run  # 预览将补齐的内容
python Tools/Docs/front_matter.py --fix            # 补齐（只补缺失，不覆盖已有值）
```

同为只读门禁（互为补充）：

```bash
python Tools/Docs/check_md_links.py                # 断链检查
python Tools/Docs/code_map_check.py --check        # code_map 格式门禁（行长/单元格长度等）
python Tools/Docs/verify_codemap_wave.py --old <改动前备份> --new <当前文件>
                                                   # code_map **内容守恒**门禁：抓“改格式时把原文截断”
python Tools/Docs/verify_codemap_wave_selftest.py  # 上者的自测（自带夹具，含 4 个取红用例）
python Tools/Docs/verify_codemap_global.py --old <改动前备份> --new <当前文件>
                                                   # code_map **全局守恒**门禁：覆盖要更宽（短行/围栏/被改写行）
python Tools/Docs/verify_codemap_global.py --selftest   # 自带夹具自测（3 用例，含 2 个取红）
```

> `code_map_check.py` 只看**格式**；一次实测中某个 wave 格式门禁 `error 0 / PASS`，但
> 3 条长纯段落尾部被截断（370/365/444 → 289/283/287）。`verify_codemap_wave.py` 专补这个盲区：
> 用“**滑动窗口命中率**”（去掉空白后按 32 字符窗、步长 16 滑动）判定内容是否守恒——
> 该判据对软折行鲁棒，且不依赖“前缀包含”的单调性。
>
> **两条实测注意**：
> 1. **必须对真实仓库路径跑检查器**。把待检文件复制到 `temp/` 下再检查，会出现 `link-broken: 1` 的
>    **假阳性**（相对链接从 `temp/` 解析失败）；真实路径下同一文件 `link-broken: 0`。
> 2. `verify_codemap_wave.py` 只覆盖「长表格单元格(>80)」与「长纯段落(>300)」；
>    **围栏内内容与短行不在覆盖内** —— 遇到 `fence-forbidden` 这类改动时需额外人工/脚本自检。
>
> **两个守恒门禁的分工**：`verify_codemap_wave.py` 是**定向**检查（长单元格/长段落）；
> `verify_codemap_global.py` 是**全局**检查，按改动形态分型——表格行逐**单元格**查
> （行被改写时整行已变，只有单元格原文会被迁移）、非表行逐**行**查、围栏标记行跳过计数。
> 两者互补，建议波次验收**两个都跑**：定向门禁快且判据明确，全局门禁能拦下短行/围栏间的静默丢失。

## 建议阅读顺序

1. [`07_architecture/架构.md`](07_architecture/架构.md) — 架构总览、分层边界与阅读地图。
2. [`07_architecture/README.md`](07_architecture/README.md) — 模块级分册入口（Runtime / Controller / Platform / 治理 / 数据模型 / V1 落地）。
3. [`15_tasks/Tasks.md`](15_tasks/Tasks.md) — 全局任务入口；任务状态以任务看板为准，不硬编码。
4. [`08_how_debuge/README.md`](08_how_debuge/README.md) — 需要调试、找日志埋点时从这里进。

## 主题文档分组

### 1. 渠道、网关与接入

- [`01_message_channels/`](01_message_channels/)
- [`06_config/hooks.md`](06_config/hooks.md) · [`06_config/pudding-yaml.md`](06_config/pudding-yaml.md)
- [`07_architecture/63ADR-063飞书Agent绑定与可靠消息网关ADR.md`](07_architecture/63ADR-063飞书Agent绑定与可靠消息网关ADR.md)
- Browser / WebView2 系列规格：`07_architecture/67`～`79`（ADR-066 抖音接入、Phase 2A-1/2/3 工作指令与验收报告）

### 2. 智能体、运行时与协作

- [`02_agent_runtime/`](02_agent_runtime/) · [`03_multi_agent/`](03_multi_agent/) · [`04_tools_and_skills/`](04_tools_and_skills/) · [`07_architecture/`](07_architecture/)

### 2.1 上下文、缓存与输入压缩

- [`07_architecture/18上下文缓存可观测性ADR.md`](07_architecture/18上下文缓存可观测性ADR.md)
- [`07_architecture/43ADR-042上下文自动压缩与主动Compact命令ADR.md`](07_architecture/43ADR-042上下文自动压缩与主动Compact命令ADR.md)
- [`07_architecture/44ADR-043缓存统计闭环ADR.md`](07_architecture/44ADR-043缓存统计闭环ADR.md)
- [`07_architecture/104ADR-090上下文压缩触发口径来源标注与收益准入收敛ADR.md`](07_architecture/104ADR-090上下文压缩触发口径来源标注与收益准入收敛ADR.md)
- [`12_features/上下文自动压缩与Compact命令设计方案.md`](12_features/上下文自动压缩与Compact命令设计方案.md)
- [`12_features/上下文Token效率缓存命中与分级压缩优化设计方案.md`](12_features/上下文Token效率缓存命中与分级压缩优化设计方案.md)

用于跟踪 token 成本治理、服务商前缀缓存命中，以及工具输出/日志/文件/RAG 块进入 LLM 前的压缩策略。

### 3. 历史任务与设计演进记录

- [`15_tasks/`](15_tasks/) 下的 `task04-swarm.md` ～ `task23-central-lock-coordination.md` 等历史任务卡与阶段总结。

这些文档仍可参考，但要在当前 Platform / Runtime / Workspace 治理主线下阅读，不能单独代表产品方向。

## 当前架构基线

- 产品入口：`PuddingDesktop.exe`（**WinUI 3 Shell**）双击启动，负责窗口、托盘、系统集成与 Core 子进程监督。
- Core 作为独立 ASP.NET Core 子进程（`core/PuddingAgent.exe --desktop-child`）承载 Web UI 静态资源、API/Controller、Runtime、Connector 与 SQLite。
- Web 业务界面保留在 WebView2 中承载；DesktopHome/`desktop.json` 存 DataRoot/Core 路径/窗口与关闭行为，`<DataRoot>/config/system.json` 存端口、ControlToken、启动超时与恢复策略。
- `dev-up.py`（`Tools/Dev/dev-up.py`，根目录为转发 shim）只用于源码开发态，不进入交付包，也不替代 Desktop 的进程主管职责。
- Console Host 只作开发与诊断入口；Phase 1A/1B-R/1B-S 与 Phase 2A-1/2/3 的确定性实现已验收，真实模型 smoke 与进程外验收按需单独判定。
