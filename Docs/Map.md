# PuddingAssistant 架构总览

> **定位：** PuddingAssistant 是面向大众用户的**数字化布丁管家**，通过 Swarm 多智能体协作、本地环境控制和个人记忆图谱，为用户提供全方位的智能助手体验。
>
> **核心理念：** 从"程序员的生产力工具"转向"人类的数字化代理"。PuddingCode（编程能力）作为**专业形态 / Pro Mode** 保留为插件。
>
> **设计哲学：** **"AI 的大脑，绅士的手脚"** — 零配置、单机优先、情感交互。安静陪伴，确认执行，绝不破坏。
>
> **详细设计方案：** [task18定位.md](Tasks/task18定位.md)

---

## 一、系统架构

PuddingAssistant 采用 **双态交互 + Swarm 编排 + 插件化能力** 的分层架构，核心由 C#（.NET 10）驱动。

### 1. 架构分层

```text
┌──────────────────────────────────────────────────────────┐
│  交互层 — 双态架构                                        │
│  ├─ 桌面精灵态 (Desktop Spirit)  — 常驻轻量入口           │
│  └─ 窗口态 (Command Center)      — 深度配置与编排         │
├──────────────────────────────────────────────────────────┤
│  编排层 — Swarm 集群                                      │
│  ├─ Leader（总管）  — 意图识别、任务拆解、结果汇聚         │
│  ├─ Worker（动态）  — 按需生成、任务结束后消失              │
│  └─ SignalBus       — 消息总线、状态同步                   │
├──────────────────────────────────────────────────────────┤
│  记忆层 — MemoryHub                                       │
│  ├─ Profile.md      — 用户约束（偏好、过敏、预算）         │
│  ├─ Session_Log/    — 按日期存储任务流                     │
│  └─ Refinement      — 长期记忆提炼                        │
├──────────────────────────────────────────────────────────┤
│  能力层 — 插件化 (IPuddingPlugin)                         │
│  ├─ 意图执行  — 快捷指令、本地搜索、脚本唤醒              │
│  ├─ 环境感知  — 屏幕拾取、文本精修、剪贴板管家            │
│  ├─ 数字管家  — 记忆编辑、任务看板、行程提醒              │
│  ├─ 系统管理  — Swarm 视图、Agent 商店、设置              │
│  └─ Pro Mode  — PuddingCode 编程能力（原有 CLI 闭环）     │
├──────────────────────────────────────────────────────────┤
│  执行层 — LocalActionProvider                             │
│  ├─ FileSystemConnector  — 读写文件（PDF / Excel / 代码） │
│  ├─ CliWrap              — 执行本地命令                   │
│  └─ EnvironmentSensor    — 桌面文件感知                   │
└──────────────────────────────────────────────────────────┘
```

### 2. 双态交互

| 模式 | 载体 | 用途 |
| --- | --- | --- |
| **桌面精灵态** | Avalonia 异形透明窗口，置顶常驻 | 拖入文件、双击输入、右键环形菜单、闲置提醒 |
| **窗口态** | Avalonia 管理窗口 | Swarm 视图（指挥部）、Editor（记忆/脚本编辑）、成果墙、记忆抽屉 |

两态通过 `SwarmManager` 共享状态，窗口启动任务时"光球"飞向精灵，精灵吐出通知气泡。

**精灵态交互细节：**

* **常驻态（Quiet Mode）**：半透明（15%）置顶，呼吸感动画，自动避让活跃窗口。
* **感知态（Perception）**：鼠标悬停或拖拽文件靠近时变为不透明，产生"吸入"物理效果。
* **语音唤醒（Wake-up）**：通过"布丁布丁"唤醒，精灵产生光晕并切换至"倾听"Lottie 动画。
* **结果卡片**：任务反馈以精美的"物理卡片"弹出，可拖动、可钉选。

### 3. Swarm 编排

* **动态生成**：Leader 根据用户意图按需生成 Worker（如 Researcher、Auditor、Reporter），任务结束后角色消失。
* **顺序依赖链**：Leader → Researcher → Auditor → Reporter，严格遵守逻辑先后。
* **Leader 核心逻辑**：环境自省 → 约束检查（Profile.md）→ 任务拆解（优先并行）。

### 4. 记忆系统

采用 OpenClaw 风格的人类可读持久化记忆，管理 `~/Documents/Pudding/Memory/` 目录。

| 层级 | 内容 | 示例 |
| --- | --- | --- |
| **底层 — 用户约束** | `Profile.md` | 海鲜过敏、预算 500 |
| **中层 — 沉淀知识** | 任务中产生的事实 | 鸡蛋价格波动 |
| **顶层 — 情感链接** | 长期个人化记忆 | 用户不喜欢太甜的蛋挞 |

---

## 二、核心功能模块

### 1. LocalActionProvider（本地环境连接器）

Agent 的**手**（执行命令）、**眼**（读取文件/感知屏幕）：

* **读写文件**：代码、PDF 菜单、Excel 账单
* **执行指令**：`ls` / `curl` / `python script.py`
* **环境感知**：回答"桌面上有哪些文件？"

### 2. MemoryHub（记忆引擎）

* **Profile.md**：核心约束存储
* **Session_Log/**：按日期存储任务流
* **Refinement**：任务结束时自动提炼关键信息存入长期记忆

### 3. 插件系统

* **接口标准**：`IPuddingPlugin`（PluginIcon / OnAction / Contribution）
* **动态载入**：MEF 或 `AssemblyLoadContext`，扫描 `Plugins/` 目录
* **沙盒隔离**：第三方插件在独立子进程中运行

### 4. PuddingCode Pro Mode（编程专业形态）

原有的 Coding Agent 能力作为插件保留：

* 任务规划器（Plan → Execute → Verify）
* LSP 语义感知（Roslyn）
* 自我修复环（编译 → 捕获错误 → 递归修复）
* Git 快照与回滚
* 权限沙盒（PermissionGuard）与输出蒸馏（OutputDistiller）

---

## 三、视觉设计语言

### 配色系统 — 布丁美学

| 用途 | 色值 | 说明 |
| --- | --- | --- |
| **背景** | 极浅奶油色 | Creamy White |
| **交互** | 焦糖色 | 按钮、链接、活跃元素 |
| **成功** | 抹茶绿 `#A8E6CF` | 完成、确认 |
| **提醒** | 蜜桃粉 | 通知、待办 |
| **警告** | 焦糖棕 | 错误、危险操作 |

### 形态语言

* 大圆角（BorderRadius > 20px）+ 柔和弥散阴影
* 布丁呼吸缩放效果 + 流体粒子流动画
* Agent 角色化：Leader（焦糖布丁）、Scholar（蓝莓布丁）、Executor（草莓布丁）、Keeper（抹茶布丁）

---

## 四、安全与隐私策略

* **确认机制**：所有涉及文件增删改的操作，必须通过精灵弹出"确认卡片"，用户点击后方可执行。
* **撤销保障**：建立 `undo_log.json`，支持一键将操作恢复原位。
* **隐私屏障**：语音转写与文本推理 100% 在本地完成，敏感信息（如密码框）自动避让截图与采集。

---

## 五、核心场景（MVP）

1. **语义清理**：用户说"帮我清理下桌面上的垃圾"，布丁分析文件名（如 `tmp_`、`副本_`），弹出清理清单供确认。
2. **生活管家**：用户拖入体检报告，布丁自动更新 `Profile.md` 里的禁忌项（如"海鲜过敏"）。
3. **桌面伴侣**：闲置时，布丁会随机根据天气或时间做出不同的可爱动作。

---

## 六、路线图

> 详细里程碑见 [Tasks.md](Tasks.md)，详细设计见 [task18定位.md](Tasks/task18定位.md)。

| 阶段 | 目标 | 关键交付 |
| --- | --- | --- |
| **一：透明精灵壳** | 桌面精灵原型 | Avalonia 异形窗口 + 静态布丁 + 鼠标拖动 |
| **二：动作与状态机** | 精灵动态表情 | Lottie 动画 + 5 种状态切换 |
| **三：意图与气泡** | 交互闭环 | Chat Bubble + 种子插件（意图输入框） |
| **四：核心后端** | Agent 能力 | LocalActionProvider + MemoryHub + Leader 系统指令 |
| **五：管理窗口** | 深度配置 | Swarm 视图 + Editor + 成果墙 + 记忆抽屉 |
| **六：插件化** | 生态扩展 | IPuddingPlugin + 动态载入 + 沙盒隔离 |

---

## 七、核心技术栈

| 类别 | 选型 | 备注 |
| --- | --- | --- |
| **开发语言** | C#（.NET 10） | 跨平台 |
| **桌面框架** | Avalonia UI | 异形窗口、Lottie 动画、SkiaSharp 渲染 |
| **CLI 框架** | Spectre.Console | Pro Mode 终端交互 |
| **AI SDK** | Semantic Kernel | Tool Calling + Agent 编排 |
| **记忆索引** | SQLite + sqlite-vec | Markdown 为 Truth，SQLite 为索引 |
| **动画引擎** | SkiaSharp + Avalonia.Lottie | 布丁流体效果 + 动态表情 |
| **状态绑定** | ReactiveUI | 精灵状态与 Swarm 状态强绑定 |
| **命令执行** | CliWrap | 本地命令沙盒执行 |
| **本地推理** | LLamaSharp | llama.cpp C# 绑定，内置 Qwen / Phi 模型，0 延时 |
| **语音唤醒** | Porcupine | 低功耗唤醒词引擎 |
| **语义感知** | FlaUI | Windows UI Automation 封装 |
| **多模型路由** | 自定义 C# 实现 | 支持 Claude / DeepSeek 等切换 |

---

## 八、开源参考

| 领域 | 项目 | 用途 |
| --- | --- | --- |
| **桌面 UI** | [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) | 跨平台桌面框架，异形窗口 + 动画 |
| **终端 UI** | [Spectre.Console](https://github.com/spectreconsole/spectre.console) | Pro Mode CLI 交互 |
| **AI 编排** | [Semantic Kernel](https://github.com/microsoft/semantic-kernel) | Tool Calling + Agent 框架 |
| **语义分析** | [Roslyn](https://github.com/dotnet/roslyn) | C# 深度代码理解（Pro Mode） |
| **LSP** | [csharp-language-server-protocol](https://github.com/OmniSharp/csharp-language-server-protocol) | 语义查询（Pro Mode） |
| **语法解析** | [Tree-sitter](https://github.com/tree-sitter/tree-sitter) | 多语言函数边界识别 |
| **向量检索** | [Microsoft.Extensions.VectorData](https://www.nuget.org/packages/Microsoft.Extensions.VectorData) | 记忆检索 |
| **沙盒** | [Docker.DotNet](https://github.com/dotnet/Docker.DotNet) | 命令执行隔离 |
| **本地推理** | [LLamaSharp](https://github.com/SciSharp/LLamaSharp) | llama.cpp C# 绑定，本地 LLM 推理 |
| **语音唤醒** | [Porcupine](https://github.com/Picovoice/porcupine) | 低功耗唤醒词引擎 |
| **UI 自动化** | [FlaUI](https://github.com/FlaUI/FlaUI) | Windows UI Automation 封装 |
