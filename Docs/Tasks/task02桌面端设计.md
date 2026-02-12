# Task 02: 桌面端设计方案

> **目标：** 基于共享的 `PuddingAssistant.Core` 内核，构建跨平台桌面客户端。桌面端定位为 **"AI 协作指挥中心"**，提供 CLI 无法实现的可视化决策、多维度对比与实时监控能力。采用 **Avalonia UI**（C# / .NET 10）。

---

## 目录

1. [CLI vs 桌面端：定位与分工](#1-cli-vs-桌面端定位与分工)
2. [共享内核架构](#2-共享内核架构)
3. [桌面端界面布局](#3-桌面端界面布局)
4. [桌面端特有功能](#4-桌面端特有功能)
5. [核心抽象：UI 交互协议](#5-核心抽象ui-交互协议)
6. [技术栈选型](#6-技术栈选型)
7. [实现路线图](#7-实现路线图)

---

## 1. CLI vs 桌面端：定位与分工

两端不是并列关系，而是**互补关系**：CLI 是手术刀，桌面端是显微镜。

| 维度 | CLI（终端） | Desktop（桌面） |
| --- | --- | --- |
| **核心价值** | 低摩擦、高生产力 | 可视化决策、全局掌控 |
| **典型场景** | 快速修 Bug、脚本自动化、CI/CD 集成 | 20+ 文件重构、架构分析、多 Agent 协作监控 |
| **信息密度** | 单列流式，适合线性任务 | 多面板并行，适合复杂任务 |
| **用户群体** | 开发者（终端重度用户） | 开发者 + 非技术用户（未来 Crush 生活助手） |
| **开发优先级** | **Phase 1（MVP）** | Phase 2（扩展） |

### 为什么 CLI 优先？

- **低摩擦力：** 终端输入 `pudding fix` 比打开桌面窗口操作更快
- **管道与自动化：** CLI 可集成到 Git Hook、CI/CD、Shell 脚本中，桌面端无法替代
- **快速验证：** 先用 CLI 跑通 Agent 闭环，再包装桌面 UI

### 为什么需要桌面端？

- **可视化决策：** 展示思维导图、代码依赖拓扑图，增强对 AI 的信任感
- **多维度对比：** 类似 IDE 的左右分屏 Diff，一眼看清多文件变更
- **信息上限：** 当任务涉及大量文件时，CLI 单行显示会成为瓶颈

---

## 2. 共享内核架构

采用 **"内核 + 适配器"** 分层模式，代码复用率 > 90%。

```text
┌──────────────────────────────────────────────────────────────┐
│                     PuddingAssistant.Core (类库)                   │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────────┐  │
│  │ AgentEngine  │  │ MemoryStore  │  │ CapabilityManager │  │
│  │ (大脑/编排)   │  │ (记忆中枢)    │  │ (手/眼/能力系统)   │  │
│  └──────────────┘  └──────────────┘  └───────────────────┘  │
│  ┌──────────────────────────────────────────────────────┐    │
│  │         IPuddingUIProvider (UI 交互协议)               │    │
│  └──────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────┘
          │                                  │
          ▼                                  ▼
┌──────────────────┐              ┌──────────────────┐
│ PuddingAssistant.CLI  │              │ PuddingAssistant.Desktop│
│ (Spectre.Console) │              │ (Avalonia UI)      │
│ 适配器 1          │              │ 适配器 2            │
└──────────────────┘              └──────────────────┘
```

### Core 内核职责

| 模块 | 职责 |
| --- | --- |
| **AgentEngine** | Claude Opus 4.6 API 调度、Tool Calling 逻辑、任务规划与编排 |
| **MemoryStore** | SQLite 存储会话与长期记忆、向量索引（RAG）逻辑 |
| **CapabilityManager** | LSP 封装、文件系统操作、Shell 执行、Git 事务处理 |
| **IPuddingUIProvider** | UI 交互协议：确认操作、展示思考、渲染 Diff（由各端实现） |

### 关键原则

- **UI 仅仅是"皮肤"：** CLI 是 Core 的 `Console.WriteLine` 适配器；Desktop 是 Core 的 `Data Binding` 适配器
- **Core 零 UI 依赖：** 所有 Agent 逻辑、API 调用、Token 计数、记忆压缩全部在 Core 中完成

---

## 3. 桌面端界面布局

采用三段式"控制塔"布局：

```text
┌─────────────────┬────────────────────────────┬─────────────────────┐
│  📡 项目雷达     │    💬 对话与协作区           │  👁️ 观察者面板       │
│  (Project Ctx)  │    (Collaboration Stream)  │  (Observer Panel)   │
│                 │                            │                     │
│  ┌───────────┐  │  ┌────────────────────┐    │  ┌───────────────┐  │
│  │ LSP       │  │  │ 用户: 帮我重构      │    │  │ 实时 Diff     │  │
│  │ 拓扑图     │  │  │ AuthService       │    │  │ 终端          │  │
│  │           │  │  └────────────────────┘    │  │               │  │
│  │  📂 src/  │  │  ┌────────────────────┐    │  ├───────────────┤  │
│  │  ├ Auth*  │  │  │ 🍮 Pudding:        │    │  │ 资源监视器     │  │
│  │  ├ User   │  │  │ 正在分析依赖...     │    │  │ Token: $0.12  │  │
│  │  └ Data   │  │  └────────────────────┘    │  │ Model: claude │  │
│  └───────────┘  │                            │  ├───────────────┤  │
│                 │  ┌────────────────────┐    │  │ 沙盒监控       │  │
│  ┌───────────┐  │  │ 🧠 思维画布        │    │  │ > dotnet build│  │
│  │ 上下文槽   │  │  │  (点击展开流程图)   │    │  │   Build OK    │  │
│  │ [拖入文件] │  │  └────────────────────┘    │  └───────────────┘  │
│  └───────────┘  │                            │                     │
└─────────────────┴────────────────────────────┴─────────────────────┘
```

### 3.1 左侧：项目雷达 (Project Context)

| 组件 | 描述 |
| --- | --- |
| **LSP 拓扑图** | 动态依赖图（非普通文件树），被 Agent 修改的文件闪烁抹茶色呼吸灯 |
| **上下文槽 (Context Slots)** | 拖拽文件到此区域，直接赋予 Agent 临时记忆上下文 |

### 3.2 中间：对话与协作区 (Collaboration Stream)

| 组件 | 描述 |
| --- | --- |
| **气泡对话流** | 支持 Markdown 渲染、代码高亮、图像输入（截图改 Bug） |
| **思维画布 (Thinking Canvas)** | Agent 规划复杂任务时，点击"查看计划"弹出流程图，展示任务拆解逻辑 |

### 3.3 右侧：观察者面板 (Observer Panel)

| 组件 | 描述 |
| --- | --- |
| **实时 Diff 终端** | 预览代码改动，支持"一键同步到编辑器" |
| **资源与成本监视器** | 实时显示当前会话消耗的 Token 价值（美元） |
| **沙盒监控** | 显示 Agent 在 Docker 容器中执行的实时 stdout/stderr 输出 |

---

## 4. 桌面端特有功能

### 4.1 悬浮"布丁"按钮 (Overlay Mode)

- 在 IDE（VS Code / Visual Studio）中编码时，PuddingAssistant 以小圆球悬浮
- 按下快捷键，"吸取"当前屏幕代码进入 Agent 会话
- 适合碎片化场景：不离开 IDE 即可与 Agent 交互

### 4.2 多代理可视化 (Swarm Visualization)

- 启动 `pudding swarm` 时，界面展示多个"小布丁"头像
- 分别标注角色："前端专家"、"后端专家"、"QA"
- 可视化展示 Agent 之间的通信流与任务分配

### 4.3 时光机回滚 (Time Machine UI)

- 基于 Git 快照的可视化时间轴
- 滑动进度条查看代码被 Agent 一步步构建的过程
- 支持任意时间点回滚

---

## 5. 核心抽象：UI 交互协议

`IPuddingUIProvider` 是实现双端共享的关键接口，定义 Agent 向 UI 报告状态的统一协议。

```csharp
/// <summary>
/// UI 交互协议：Agent 通过此接口与任意前端通信。
/// CLI 和 Desktop 分别提供各自的实现。
/// </summary>
public interface IPuddingUIProvider
{
    /// <summary>请求用户确认危险操作（如删除文件、执行 Shell）</summary>
    Task<bool> RequestConfirmationAsync(string title, string message, CancellationToken ct = default);

    /// <summary>展示 Agent 的实时思考过程</summary>
    Task UpdateThinkingAsync(string thought, CancellationToken ct = default);

    /// <summary>渲染代码差异，等待用户审批</summary>
    Task<DiffDecision> ShowDiffAsync(string filePath, string oldContent, string newContent, CancellationToken ct = default);

    /// <summary>更新任务进度步骤</summary>
    Task UpdateProgressAsync(IReadOnlyList<TaskStep> steps, CancellationToken ct = default);

    /// <summary>显示通知消息（信息/警告/错误）</summary>
    Task NotifyAsync(string message, NotifyLevel level, CancellationToken ct = default);
}

public enum DiffDecision { Approve, Reject, Edit, Explain }
public enum NotifyLevel { Info, Warning, Error }
```

### 各端实现方式

| 方法 | CLI 实现 (Spectre.Console) | Desktop 实现 (Avalonia) |
| --- | --- | --- |
| `RequestConfirmationAsync` | `ConfirmationPrompt` 控制台交互 | 弹出模态对话框 |
| `UpdateThinkingAsync` | 灰色 `MarkupLine` 流式输出 | 更新 ViewModel 绑定的思维流面板 |
| `ShowDiffAsync` | 终端内 Diff 渲染 + `[Y/N/E/?]` 按键 | 左右分屏 Diff 视图 + 按钮组 |
| `UpdateProgressAsync` | `Table` 渲染步骤状态 | 进度条 + 步骤列表控件 |
| `NotifyAsync` | 彩色 `MarkupLine` | Toast 通知 / 状态栏消息 |

---

## 6. 技术栈选型

| 模块 | 技术选型 | 说明 |
| --- | --- | --- |
| **桌面 GUI 框架** | [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) | 跨平台（Win/Mac/Linux）、高性能、类 WPF 开发体验 |
| **共享内核** | `PuddingAssistant.Core`（NuGet / Project Reference） | .NET 10 类库，零 UI 依赖 |
| **本地数据库** | SQLite | 存储会话历史与长期记忆 |
| **向量索引** | `Microsoft.Extensions.VectorData` | 代码 RAG 检索 |
| **通知/动画** | Avalonia 自定义控件 | 软弹动画、呼吸灯、Toast 通知 |
| **IDE 联动** | Local WebSocket Server | 桌面端与 VS Code / Visual Studio 双向通信 |

---

## 7. 实现路线图

| 阶段 | 目标 | 交付物 |
| --- | --- | --- |
| **Phase 1: Core + CLI** | 跑通 Agent 闭环，验证 Tool Calling 准确度 | `PuddingAssistant.Core` 类库 + CLI MVP |
| **Phase 2: 桌面观察者** | 解决 CLI 在复杂重构时的信息超载 | 轻量级桌面看板：任务详情 + 费用统计 + Diff 预览 |
| **Phase 3: 完整桌面端** | 三段式控制塔布局，思维画布，气泡对话流 | 完整 Avalonia 桌面客户端 |
| **Phase 4: 深度集成** | 桌面端与 IDE 双向联动，悬浮按钮，Swarm 可视化 | IDE 插件 + WebSocket 桥接 + 多 Agent 面板 |

### 阶段间关键里程碑

```text
Phase 1                Phase 2                Phase 3                Phase 4
  │                      │                      │                      │
  ▼                      ▼                      ▼                      ▼
定义 IPuddingUIProvider → CLI 实现该接口 → Avalonia 实现该接口 → IDE 插件实现该接口
定义 Core 类库边界       → CLI 验证闭环   → 桌面看板上线        → 悬浮按钮 + Swarm
```
