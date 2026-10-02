# Pudding Agent 项目指令

> **本文件只写规则**：强制约束、约定与门禁。
> **日志一律不进本文件**——进度、提交号、测试/验收数字、部署结论、已知问题、状态清单统一写 [`Docs/00_changelog/`](Docs/00_changelog/README.md)。
> 相关入口：[`Docs/README.md`](Docs/README.md)（目录地图与放什么→去哪）、[`Docs/10_conventions/Agents-Hygiene.md`](Docs/10_conventions/Agents-Hygiene.md)（仓库卫生完整规范）、[`Docs/00_changelog/README.md`](Docs/00_changelog/README.md)（日志规则）。

**导航**

| 组 | 章节 |
|----|------|
| 一、项目与架构 | 项目概述与产品形态 · 架构第一原则 · 组件交付顺序 · PuddingDesktop 产品边界 · 兼容性和补丁约定 |
| 二、目录与文档 | code_map.md · 修改日志 · Docs 目录与文档规范 · Markdown Front Matter · 调试与诊断手册 |
| 三、开发与构建 | 开发环境与路径 · 代码修改与构建约定 · 前端包管理 · 前端版本管理 · dev-up 开发脚本 · 运行时配置 |
| 四、仓库纪律 | 仓库卫生与提交纪律 |

## 项目概述与产品形态（强制）

Pudding 是 Windows First 的 .NET 10 桌面智能助手与 IDE：六层记忆体系、Skill 系统、子代理委派、潜意识后台管道。

- 最终产品入口是 `PuddingDesktop.exe` = **WinUI 3 Shell + WebView2 承载既有 Web UI + 独立 ASP.NET Core 子进程**。
- Core 由 Desktop 以 `core/PuddingAgent.exe --desktop-child` 子进程方式启动与监督；业务逻辑、Agent、Connector、数据库与 Runtime 全在 Core，**不得迁入 Shell**。
- **不得装配进程内 `PuddingHost`**，不继续原生聊天或业务设置迁移；业务界面保留 Web，Desktop 只负责窗口、系统集成与 Core 启动器。
- 配置沿用 `DesktopHome/desktop.json` 与 `<DataRoot>/config/system.json`；数据目录设置界面默认建议 `D:\data`，已保存目录优先。
- `Source/PuddingDesktop.WpfArchive` 只是**旧 UI 验证基线**：其无 UI 的启动器/协议源文件由 WinUI 项目链接编译、共用逻辑；**不将 WPF 放入 WinUI 产品进程**。
- `dev-up.py` 只用于源码开发与调试（开发态后端、前端开发服务器、代理与工具进程）；它不是产品守护进程，不进入交付包，也不能替代 Desktop 的产品进程主管职责。
- **分支纪律**：分支 `B` 保存原桌面开发的完整已提交历史、未提交代码与文档快照；**不得整体合并 B**，只能按必要修复与 UI 样式逐项移植。
- 权威决策：[Shell / Web / Core ADR](Docs/12_features/ADR-Desktop-Shell-WebUI-Separate-Core-2026-09-29.md)。

## 架构第一原则：架构整洁性（强制）

**组件独立 · 依赖方向由编译期强制 · 可测性优先 · 边界显式。**

- 能做成独立组件就做成独立组件；拆分的**第一目的是可测试**（例：让索引管线的测试进程不必加载 Roslyn / MSBuild 这类必须**进程级一次性注册**的重依赖）。
- 依赖方向**不得靠文档约定**，要由**编译器强制**：新抽出的组件**不得反向引用**其调用方；判据是「**编译期失败**」，而不是写在文档里等人遵守。
- 归属存疑的文件留在**引用方**一侧（宁可少拆），并逐条记录回退原因；禁止在抽取时顺手重构或改变行为。
- 落地实例：`PuddingCodeIndex`（索引组件：契约/管线/存储/调度）独立于 `PuddingCodeIntelligence`（语言智能与查询），设计见 `Docs/12_features/ADR-089-索引组件拆分设计-2026-09-23.md`。

## 组件交付顺序（强制）

**先独立测试，再接入 PuddingAgent。** 组件先在自己的边界内完成构建与测试，通过门禁后才登记进解决方案与 DI；顺序不可颠倒。收益：① 不重启宿主即可开发调试（独立程序集不碰运行中 Core 的文件锁）；② 保护接入后质量（接入只剩「登记+装配」）；③ 边界由**编译期**强制；④ PuddingAgent = 组件的组合。

⇒ 完整门禁、自检清单与反例见 **`Docs/10_conventions/组件化交付规程.md`**（S1~S5 五步与接入前 checklist）。**S5（接入）之前的任何一步都不得改动宿主。**

## PuddingDesktop 产品边界（强制）

**进程与依赖边界**

- `PuddingDesktop` 保持 `Microsoft.NET.Sdk`，不引用 `PuddingHost` 或 ASP.NET Core；Desktop 与 Core 只通过子进程协议、动态 Loopback HTTP 与认证 WebSocket Bridge 通信。
- Desktop 必须在 Core 启动失败、配置缺失或 DataRoot 未设置时仍可启动，并允许用户进入设置与运行中心执行修复、启动、停止或重启。
- Desktop 是单实例产品进程：关闭按钮默认隐藏到系统托盘并保持 Core 运行；只有「退出 Pudding」、Windows 会话结束或配置为 `ExitAndStopCore` 时才停止 Core 并释放 WebView2。
- `desktop.json` 保存 DesktopHome 范围的 DataRoot、Core 路径、窗口与关闭行为；`<DataRoot>/config/system.json` 保存 Core 端口、ControlToken、启动超时与自动恢复策略。**Token 不放环境变量，不在 UI 或诊断包回显。**
- `dev-up.py` 与 Desktop **不共享 PID、端口所有权**：使用同一个 `D:\data` 验证 Desktop 前必须先停止 dev-up 管理的 Core，反之亦然，避免两个 Core 同时访问数据库。

**Browser 与上层能力**

- DesktopChild 在启用 Browser Automation 时提供 `browser_context`、`browser_tabs`、`browser_navigate`、`browser_snapshot`、`browser_locate`、`browser_interact`、`browser_wait_for` 七项工具。
- **Snapshot ref 必须携带 PageVersion；交互提交后不得重查旧 Locator**，后续状态用 Wait 或新 Snapshot 获取。
- Douyin Adapter 准入：先用用户明确选择的测试 Agent/DataRoot 完成真实 DeepSeek 可见 smoke；**不得读取或复制 `D:\data` 中的 LLM Secret 绕过该项目准入**。底层始终保持通用，抖音能力只位于上层适配器。

**验证边界（两段式验收，强制）**

- 运行在 Pudding 内部的 Agent 可以测试当前进程**已经加载**的成品代码与工具（真实模型调用、Browser Tools、TestSite 页面操作、Bridge Activity、免重启行为）；但它**不能**证明刚改的代码已被当前进程加载，也**不能**独立验收承载自身的 Desktop/Core 生命周期（单实例会把第二次启动转发给旧进程，退出后无法观察子进程回收）。
- 因此：内部开发 Agent 先交付 `ready-for-external-deploy` → 进程外控制器重启到明确的新构建 → 进程内新测试会话执行功能 smoke 并交付 `in-product-functional-complete` → **启动/重启/崩溃恢复/退出回收结论仍由外部控制器判定**。

**构建与验证隔离**

- 使用 `--artifacts-path temp/build/recovery`，先 restore/build，再在同目录 `--no-restore`。
- Desktop 的构建、测试与发布**串行**执行。
- 生命周期验证脚本 `TestScripts/test-pudding-desktop-launcher.ps1` 使用隔离的 DesktopHome、DataRoot 与端口。
- 数据兼容性只在 `temp/test-out` 下的 SQLite 在线备份副本上验证；**不直接在 `D:\data` 试跑旧代码**。
- Console / DesktopChild 共用 `.pudding-host.lock` 文件句柄租约；**不得删除锁文件绕过互斥**。

## 兼容性和补丁约定

- 不要为兼容性牺牲性能与可维护性（旧数据格式、旧 API 版本同理），除非有明确的业务需求。
- 开发阶段**不引入兼容性层**：不为 SQL 迁移增加兼容代码（重置数据库比迁移代码更简单）；配置类数据优先**配置文件**而非数据库——LLM 服务商/模型、Agent、系统预置配置放程序所在目录，用户自定义配置放用户指定的数据目录（见 `PathHelper`）。
- 就地升级优先：直接对 `D:\data` 的数据库与配置做原地升级/修补，而不是加兼容层。
- 可以清理 `D:\data` 下的存储与缓存以还原干净开发环境，但**先备份 `llm.providers.json`**（含服务商信息）。重置后需访问 Bootstrap 页面完成初始化；初始化判定依据配置项 `Bootstrap.Initialized=true`，因此重置后需重新配置。

## code_map.md 使用规则（强制）

**定位**：`code_map.md` 是**代码地图/索引**，用来快速回答「这个概念、组件、文件在项目的哪里」。根目录 `code_map.md` 是**主索引**，登记每个子项目及其 `code_map.md`；子项目索引在 `Source/<项目>/code_map.md`，负责该项目内部的文件级索引。

**正向例子（只写这四类内容）**：

1. **关键概念**：概念 → 权威位置 → 用途/不变量。例：`canonical Turn 围栏 | Source/PuddingRuntime/Services/AgentExecution/ | 用 [CURRENT USER TURN input_sha256=…] 围住本轮输入，缺失即 fail-closed`。
2. **组件 / 子项目**：程序集 → 一句话用途 → 该项目 `code_map.md` 链接。例：`Source/PuddingCodeIndex/ | 🔑 索引组件：契约/存储/变更捕获管线/调度；不得引用 PuddingCodeIntelligence | [code_map](Source/PuddingCodeIndex/code_map.md)`。
3. **关键文件（相对路径）**：`相对路径` + 用途。路径必须是仓库内真实存在的相对路径，改完自查链接可解析。
4. **调用链路 / 测试工程 / 设计文档入口**：端到端数据流、契约边界、测试项目清单、关键 ADR 与设计文档链接。

**反向例子（严禁）**：把 `code_map.md` 当台账/日志用——不断追加「## YYYY-MM-DD：…」条目、轮次记录（「第 N 轮」）、提交号、测试与门禁数字、部署/验收结论、性能实测、缺陷排查时间线。**这些一律写 `Docs/00_changelog/`**，索引里只保留「现在是什么」。

**维护时机（强制）**：

- 开始任务前**必读**根 `code_map.md`，再进相关子项目的 `code_map.md` 定位文件。
- 任务结束前**必须维护**：新增/移动/删除关键文件 → 更新对应子项目 `code_map.md`；新增子项目 → 在根 `code_map.md` §2 登记并链接其 `code_map.md`；概念/链路变化 → 更新根 §3/§4。
- **只更新受影响的条目**（就地改写），不得在文件末尾追加时间线；过程记录走修改日志规则。
- 子项目 `code_map.md` 里已存在的「变更（YYYY-MM-DD）」小节属于历史日志，按 `Docs/00_changelog/README.md` §6 逐步迁出，不要继续追加。

## 修改日志使用规则（强制）

**唯一去处**：`Docs/00_changelog/<YYYY>Year/<MM>/`（例：`Docs/00_changelog/2026Year/10/2026-10-02-<主题>.md`）。完整规则、文件格式与目录边界见 **`Docs/00_changelog/README.md`**。

- **命名**：`YYYY-MM-DD-<主题>.md`，日期是**记录日期**；同一天多个主题就写多个文件，不要堆成一个巨型文件。
- **时机**：每个原子任务完成并通过验证后写一条，与该任务的代码改动放在**同一个 commit**。
- **必含**：日期主题、改了什么（含相对路径）、验证状态（实际跑了什么/结果数字；没验证就写「未验证」并说明原因）、未完成部分、关联 ADR/报告/任务 id。
- **禁止写入**：`code_map.md`（根与子项目）、`Docs/README.md`、ADR 正文、`Agents.md`，以及 `Docs/` 根目录。
- **日志是摘要不是证据仓库**：完整证据留在 `Docs/14_reports/`、`Docs/16_qa/`，日志只链接；敏感信息（apiKey、ControlToken、隐私数据）不入日志。
- 已写入的日志**追加式、可更正、不抹除**；更正写成「更正（YYYY-MM-DD）：…」。

## Docs 目录与文档规范（强制）

**目录地图**（一级目录统一 `NN_english_snake`；新增目录必须编号 + 英文小写，并在 `Docs/README.md` 登记）：

| 目录 | 放什么 |
|------|--------|
| `Docs/00_changelog/` | 修改日志（唯一去处；规则见其 README） |
| `Docs/01_message_channels/` · `02_agent_runtime/` · `03_multi_agent/` · `04_tools_and_skills/` · `05_providers_and_models/` | 主题文档：渠道 / 智能体运行时 / 多智能体 / 工具与技能 / 服务商与模型 |
| `Docs/06_config/` | 配置说明（hooks、pudding-yaml 等） |
| `Docs/07_architecture/` | 架构分册与 ADR（含 `design/`） |
| `Docs/08_how_debuge/` | 调试与诊断手册（主索引 README + 分册） |
| `Docs/09_audit/` | 审计清单 |
| `Docs/10_conventions/` | 规程与约定（组件化交付、仓库卫生、SUBAGENTS、协作协议） |
| `Docs/11_design/` | 设计规格与视觉/UI 设计 |
| `Docs/12_features/` | 设计方案、施工计划、任务书（面向未来施工） |
| `Docs/13_runbooks/` | 运行手册、部署/回滚/一次性操作 |
| `Docs/14_reports/` | 诊断 / 验收 / 评测报告（一次性证据） |
| `Docs/15_tasks/` | 任务、待办、路线图、历史任务卡 |
| `Docs/16_qa/` | 验收记录与审阅索引 |
| `Docs/17_memory/` | 记忆快照 |
| `Docs/18_superpowers/` | 外部方法论资料（plans / specs） |
| `Docs/19_references/` | 外部参考项目研究（deepseek_harness 等） |
| `Docs/20_resources/` | 图片等静态资源 |
| `Docs/90_archive/` | 归档（只加不改） |

**硬性规则**：

1. **Docs 根目录只允许 `README.md`**（目录地图与规范入口）。新文档按性质放进上面的目录，禁止新增根文件。
2. **文件名保留语义名**（中文主题、日期均可）：重命名文件必须同步全仓库引用；重命名/移动目录必须脚本化改写同步引用，并用链接检查脚本验证。
3. **站内引用必须真实可解析**：优先相对路径；写完用 `python Tools/Docs/check_md_links.py`（检查 Docs/，`--all-repo` 可全仓库；有失效目标或锚点即非零退出）自查。
4. **第三方参考仓库只放 `external/references/<name>`，并以 git submodule 管理**（同步 `.gitmodules`）；**禁止修改子仓库内容**，其内部链接失效属上游问题。
5. **Front Matter**：每个 `Docs/**/*.md` 开头必须有标准 Front Matter（见下节），新增文档即刻补齐。
6. **归档与日志**：`00_changelog/` 追加式（不抹除历史）；`90_archive/` 只加不改。

## Markdown Front Matter 规则（强制）

**每个 `Docs/**/*.md` 开头必须有 Front Matter**，字段固定 12 项：

```yaml
---
title: <标题（取首个 H1）>
author: <作者（git 首次提交作者）>
date: 2026-10-02                 # 创建日期（git 首次提交）
last_reviewed: 2026-10-02        # 最近复核/改动日期（git 最后提交）
status: active                   # draft | proposed | active | deprecated | archived
description: "<概要：首个段落 / 引用块 / 表首行>"
categories: [docs, features]     # [docs, <所属目录>]
tags: [adr, desktop, shell]
related_docs: [Docs/14_reports/X.md]                            # 文内引用的其他文档（仓库相对路径）
related_files: [Source/PuddingDesktop/PuddingDesktop.csproj]     # 关联文件
slug: features-adr-desktop-shell-webui-separate-core-2026-09-29
draft: false
---
```

- **校验/补齐工具**：`python Tools/Docs/front_matter.py --check`（不合规非零退出，可作门禁）；`--fix` 只补缺失字段、不覆盖已有值（`--force` 才覆盖非法值），幂等可重复执行；另有 `--dry-run` 预览、`--paths` 限定范围、`--report` 输出 JSON 明细、`--all-repo` 扩展到全仓库。
- `related_docs` / `related_files` 允许为空列表（确实无引用）；`categories` / `tags` 不得为空。
- `status` 取值受限：时间序日志（`00_changelog/20xxYear/**`）与 `90_archive/**` 用 `archived`，其余默认 `active`。
- `Docs/**` 之外的 md（如 `.pudding/skills/**` 的技能文件，格式为 `manifest.json` + `SKILL.md`）按各自规范维护，不套用本规则。
- 新增/移动文档后**必须**让 `--check` 通过；门禁失败即视为任务未完成。

## 调试与诊断手册维护（强制）

- 调试与日志诊断入口是 `Docs/08_how_debuge/README.md`（主索引）；新主题才新建分册（`01`～`15` 序列）并在主索引登记。
- 调试经验（关键日志埋点、Error 日志在哪找、如何过滤）写入**对应主题分册**，不要堆进单文件或 `Agents.md`。

## 开发环境与路径

- **登录**：用户名 `admin`，密码 `Admin@123`。
- **测试脚本**：`TestScripts/` 目录。
- **必读文件**：`Agents.md`（本文件）；文档入口 `Docs/README.md`。
- **代码目录**：本仓库根目录（以 `pwd` 为准，不要硬编码；作者主副本位于 `E:\github\AgentNetworkPlan\PuddingAgent`）。
- **数据存储**：`D:\data`（开发环境 DataRoot，见 `PathHelper`；由 dev-up 的环境变量或启动参数确定）。
- **工作空间**：`D:\data\workspaces\default`。
- **编译入口**：`dotnet build PuddingRuntime`。
- **代码地图**：`code_map.md`（根主索引 + 各子项目 `code_map.md`），规则见上文「code_map.md 使用规则（强制）」。
- **修改日志**：`Docs/00_changelog/<YYYY>Year/<MM>/`，规则见上文「修改日志使用规则（强制）」。
- **文档**：`Docs`（任务开始前必读、结束后维护）。
- **临时编译与重定向输出**：必须使用 `temp\builder` 目录；构建/测试产物目录约定见「仓库卫生与提交纪律」。

## 代码修改与构建约定

- `dry_run` 默认 `false` 直接写盘；仅当需要先看 diff 时显式传 `dry_run=true`。
- 编译命令：`dotnet build PuddingRuntime --no-restore`。
- Desktop 定向构建：`dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo`。
- Desktop 定向测试：`dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo`。
- Desktop Release 预览发布：`dotnet publish Source\PuddingDesktop\PuddingDesktop.csproj -c Release --no-restore -o temp\build\desktop-preview --nologo`。
- **Desktop build / test / publish 必须串行执行**：并行构建同一 Desktop 项目会共享 `obj`，可能产生重复 `mainwindow.baml` 的 `RG1000`。
- 构建、测试与发布输出只允许放在仓库 `temp\build`、`temp\test-out` 或系统 Temp，**不得放到 `D:\data`**。

## 前端包管理约定（强制）

前端（Web UI）在 `Source\PuddingPlatformAdmin`，**包管理器统一使用 pnpm**，不得使用 npm / yarn。

- 依据：该目录只有 `pnpm-lock.yaml`、`pnpm-workspace.yaml` 与 `.npmrc`，没有 `package-lock.json` / `yarn.lock`；`node_modules` 是 pnpm 的 `.pnpm` 链接结构。
- 安装与运行：`pnpm install`、`pnpm run start:dev`、`pnpm run build`、`pnpm run test`。`dev-up.py` 启动前端时同样执行 `pnpm install` 与 `pnpm run start:dev`，并强制要求 pnpm 存在。
- 禁止 `npm install` / `yarn install`：会生成 `package-lock.json`、破坏 `.pnpm` 链接结构，使 `node_modules` 与锁文件不一致。
- `package.json` 内部脚本仍写着 `npm run ...`（ant-design-pro 模板遗留），那只是脚本内部互调；对外入口一律走 pnpm。
- `node_modules` 与 `.pnpm-store` 不入库（见 `.gitignore`）；需要依赖时重新 `pnpm install`，不要跨机器/跨工作树直接复制 `node_modules`。

## 前端版本管理（强制）

前端（`Source/PuddingPlatformAdmin`）**独立于 Core/Desktop 管理自己的版本号**，因为它是被部署进 `wwwroot/admin` 的静态产物，运行中实例用的是哪一份构建必须能一眼看出来。

- **唯一真源**：`Source/PuddingPlatformAdmin/package.json` 的 `version`。
- **每次修改前端都必须递增该版本号**，且与前端改动放在**同一个 commit** 里：样式/修复类改动 → 递增**修订号**（如 `6.1.0` → `6.1.1`）；新增功能/组件类改动 → 递增**次版本号**（如 `6.0.0` → `6.1.0`）；结构性重构或破坏性调整 → 递增**主版本号**。只改文档、测试或后端时**不要**动前端版本号。
- **展示**：每个前端页面右下角常驻一个很小的徽标，内容为 `v版本号[+dirty] · 短哈希 · 构建时间`（悬停显示全哈希、提交时间与构建时间）。实现：`config/config.ts` 构建期注入 `__PUDDING_FRONTEND__` → `src/utils/frontendBuild.ts` → `src/components/FrontendVersionBadge`（挂在 `app.tsx` 的 `rootContainer`）。
- **`+dirty`** 表示构建时工作树有未提交改动：本地调试可能正常出现，但**正式交付构建不应带 `+dirty`**；看到它就说明产物不可复现。
- **只重新构建前端不会生效**：产物需部署进 Core 读取的 `wwwroot/admin`（`Source/PuddingAgent` 的构建通过 `PuddingHostContent.props` 把 `PuddingPlatformAdmin/dist` 拷进去）。改完前端必须重新构建并部署，然后核对页角徽标上的版本号/哈希确实是新构建——只重启 Core 不会重新部署静态产物。

## dev-up 开发脚本

`dev-up` 是源码开发环境的调试与代理工具，用于快速启动前后端开发栈；修改开发态 Core/Workbench 代码后可用它重启或重新编译。**它只服务源码开发，最终用户不使用这些命令，也不进入交付包。**

```bash
python dev-up.py --frontend-only   # 只启动前端
python dev-up.py --down            # 关闭（手动启动后端前必须先 down，否则端口占用）
python dev-up.py --restart         # 重启
python dev-up.py --rebuild         # 重新编译（排除编译缓存问题）
python dev-up.py --status          # 查看状态
```

- 脚本本体在 `Tools/Dev/dev-up.py`；仓库根 `dev-up.py` / `dev-up.ps1` 是转发 shim，**两种入口等价**。

## 运行时配置

> 这里指 Pudding **运行时**的环境变量与工作目录，不是开发代码的运行配置。

- Shell：`pwsh`（PowerShell Core）
- OS：Windows
- 工作目录：`D:\data`（见 `PathHelper`；由 dev-up 的环境变量或启动参数确定）

## 仓库卫生与提交纪律（强制）

完整规范见 `Docs/10_conventions/Agents-Hygiene.md`；本节只列必须在每个任务里执行的动作。

1. **任务完成即提交**：每个原子任务完成并通过验证后立刻 `git commit`，禁止把多个任务的改动攒在一起；中断/转交前先提交已验证部分。**工作树不允许长期处于脏状态。**
2. **精确暂存**：使用 `git add <明确文件路径列表>`，禁止裸 `git add -A` / `git add .`（工作树常混有他方并行 WIP）。
3. **临时产物一律进 `temp/`**（已 ignore），且必须落在这三个子目录之一：编译/发布输出 → `temp\build\`，测试输出与结果 → `temp\test-out\`，临时脚本/草稿/报告 → `temp\` 根；禁止散落在仓库根或源码目录。
   - 一次性清理：`Remove-Item temp\build\*, temp\test-out\* -Recurse -Force -ErrorAction SilentlyContinue`（每次构建/测试跑完立即执行）。
   - `dev-up.py --clear` 已把这些路径纳入白名单；`temp\` 根下的人类可读笔记（`.md`/`.patch`）不受清理影响。
4. **提交前自检**：`git status` 只含本次预期文件 → `git diff --cached --stat` 无 `bin/`、`obj/`、`pub/`、`temp/build/` → 无 `??` 临时/密钥/大文件 → 一个 commit 只做一件事。
5. **只推自己的 commit**：不代推他方未完成的改动；推送前确认没有夹带。

> 对照断言：`Source/PuddingPlatformTests/Services/AgentTemplateFileServiceTests.cs` 的 `RepoGeneralAssistantPreset_AgentsPrompt_Contains_RepoHygieneClause` 会校验产品预设 `general-assistant.json` 的 `agentsPrompt` 含上述条款，两处需同步维护。
