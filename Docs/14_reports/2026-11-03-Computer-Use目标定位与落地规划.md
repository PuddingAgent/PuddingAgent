---
title: 2026-11-03 Computer Use 目标定位与落地规划（Codex 两轮）
author: 鲸鲸
date: 2026-11-03
last_reviewed: 2026-11-03
status: draft
description: "由用户提问「computer use 的目标是什么」触发的两轮 Codex 规划：目标陈述、验收判据、靶子顺序、组件边界、UIA 重新评估、最小 benchmark 格式与两周实施序列。"
categories: [docs, report]
tags: [computer-use, strategy, planning, uia, benchmark]
related_docs: [Docs/14_reports/2026-11-03-Codex规划-点击验证与覆盖层处理.md, Docs/14_reports/2026-11-03-白底网页元素检测失效诊断.md]
related_files: []
slug: report-2026-11-03-computer-use-goal-and-plan
draft: false
---

# Computer Use 目标定位与落地规划（Codex 两轮）

## 缘起

用户问了一句：**"computer use 的目标是什么？"**

这个问题暴露了一个事实：我花了一整天在**最难、且最不需要**的靶子（ChatGPT，有官方 API、
Electron 白底、覆盖层、合成层不参与命中测试）上打磨元素检测精度；
而核心能力（点击 + 打字 + 回车发送）在当天凌晨就已经跑通。

于是请 Codex 做两轮规划。两轮都带硬预算（最多读 1 个文件、最多 3 次工具调用、
禁止仓库探索），日志量 50 KB 左右 / 单轮 15–25 分钟。

---

## 第 1 轮：目标应该是什么

### 目标陈述（推荐方案）

> **给定一个授权任务，在没有足够 API 的桌面应用中，用可观测的 UI 状态完成它，
> 并验证预期结果，或在三次尝试内返回 `uncertain/blocked`。**

三个候选框架的取舍：

| 框架 | 问题 |
|---|---|
| 「检测 95% 的可点击元素」 | 优化的是代理指标；检出一堆矩形 ≠ 任务完成 |
| 「在应用 X/Y/Z 里可靠工作」 | 易测，但鼓励按应用打补丁 |
| **推荐框架** | 以"完成并验证的任务"为单位，允许自适应，**诚实失败是有效结果** |

### 分项「够用」标准（关键区分：精确 vs 自适应）

| 环节 | 标准 | 判据 |
|---|---|---|
| **目标发现** | 自适应 | 正确目标进入 top-5 的次数 ≥95%；**不需要**完整枚举与高精度 |
| **坐标精度** | 自适应 | 测"有效命中"而非像素精度；≥32×32 控件首点成功 ≥99%；状态未变可重定位两次 |
| **动作执行** | **精确** | 文本/快捷键/选区/顺序必须与意图一致，≥99.5%；**绝不盲目重试**非幂等动作 |
| **结果验证** | **精确** | **零假成功报告**；未知可接受；必须有应用状态断言/UIA 值/对话框迁移/任务专属视觉证据 —— **"像素变了"不算** |

### 验证靶子顺序

1. **确定性控件画廊**（按钮/输入框/菜单/对话框/DPI 变体/重叠 + 机器可读 oracle）
2. **记事本**（焦点、输入、快捷键、菜单、保存对话框）
3. **计算器**（小目标 + 状态迁移立即可观测）
4. **画图**（工具选择、画布坐标、拖拽、模态对话框）
5. **一个用户指定的真实无 API 老应用**

**ChatGPT 只作非门禁的压力测试**。理由：它有 API 和浏览器级自动化，
GUI 控制在这里优先级最低；而它的渲染架构使失败不具备代表性。

### 明确的非目标（防止变成无底洞）

- 通用纯像素检测器（覆盖所有 Windows 应用）
- 对有 API / DOM 级工具的应用做 GUI 自动化
- DRM 界面、游戏、远程桌面、安全桌面、CAPTCHA、刻意反自动化的场景
- 仅从截图差异推断光标/焦点
- 无人值守的破坏性、财务、凭据、权限变更操作
- 任意遮挡、动画、自定义覆盖层、不可访问的超小控件下的完美操作

### 最便宜的可用路径

> **Stop treating generalized visual segmentation as the primary control plane.**

建路由栈：

```
API/tool → UI Automation/accessibility → 键盘导航 → OCR/视觉 grounding → 用户澄清
```

用 UIA 的属性、焦点状态、控件模式、包围盒；只有在语义数据缺失时才让视觉提出候选。
加**有界重试**（每次重试都基于新观察）和**任务专属成功断言**。
歧义或高风险目标应触发**澄清**，而不是继续做检测器工程。

通用分割、覆盖层命中的重建、Electron 专项加固 —— **无限期推迟**，除非出现真实的
无 API 工作流需要它们。

### 下一个里程碑的「完成定义」

跨 15 个确定性任务（控件画廊 + 记事本 + 计算器 + 画图），每个跑 5 次干净试验：

- ≥90% 端到端验证通过
- 零假成功报告
- 无错误的重大动作
- 每步最多 3 次尝试
- 失败返回有用的 `uncertain/blocked` 原因
- 在 100% / 150% / 200% DPI 下均可用

---

## 第 2 轮：怎么落地

### 组件边界与依赖方向

```text
                Pudding.Contracts.Computer
                  ^        ^        ^
                  |        |        |
        Computer.Policy  |  Computer.Verification
                  ^        |        ^
                  +--------+--------+
                           |
                 Computer.Orchestration
                  ^                    ^
                  |                    |
     Windows.UIAutomation      PuddingComputer.Windows
                                capture/input/OpenCV

Computer.Benchmarks --> Orchestration + Contracts
Host (S5 only) -------> Orchestration
```

**职责**：

- `Computer.Policy`：风险分级、路由资格、尝试上限、何时强制澄清
- `Computer.Verification`：类型化 oracle、证据评估、**唯一能转移到 `Succeeded` 的地方**
- `Computer.Orchestration`：路由 + observe → select → act → verify 状态机
- 平台项目只做观察或执行原语，**永不声明成功、永不决定重试**

**禁止的依赖**：contracts → 实现；UIA → orchestration/input/OCR；verification → 平台实现；
policy → Windows/原生库；任何 S1–S4 组件 → Host/Runtime/Agent。
benchmark 可消费生产组件，生产组件**不得**消费 benchmark。

### UIA 需要重新纳入

之前把 UIA 判成"死路"，**理由只对 Chromium 成立**（只暴露 7 个 framework Panes，
无 Edit/Document 节点）。而新的验证靶子是记事本/计算器/画图（原生 Win32），
UIA 应当作为**首要发现渠道**；Chromium 保留一个明确的 per-app 能力降级。

首个组件 `PuddingComputer.Windows.UIAutomation`，初始只读：

- 附着到已确认的顶层窗口
- 有界深度/时间地枚举 UIA 子树
- 归一化为契约记录：控件类型、名称、automationId、包围盒、enabled/off-screen/焦点态、
  支持的 pattern、可读值
- 保留父子关系，生成稳定的快照内目标 ID
- 报告 `Supported` / `Sparse` / `Unavailable` / `TimedOut`，**绝不静默退回坐标**

**不负责**：点击、输入、排序、重试、OCR、验证结果。

**边界断言**：其项目图只能引用 computer contracts + UIA/原生包。构建期的架构测试必须在
它引用 Host/Runtime/Orchestration/Policy/Verification/OpenCV/截图/输入模拟时失败。
变异测试：移除某个 UIA 属性映射必须让原生控件画廊测试变红。

### 最小 benchmark 格式

单个 JSON 数组，每个任务：

```json
{
  "id": "calculator-add",
  "app": "calculator",
  "setup": "launch-clean",
  "goal": "Compute 17 + 25",
  "risk": "reversible",
  "maxAttempts": 3,
  "oracle": {
    "kind": "uia-property",
    "selector": { "automationId": "CalculatorResults" },
    "property": "name",
    "operator": "endsWith",
    "expected": "42"
  }
}
```

**必填**：`id` / `app` / `setup` / `goal` / `risk` / `maxAttempts` + 一个 `oracle`

**risk 枚举**：`readOnly` · `reversible` · `nonIdempotent` · `destructiveOrSensitive`

**oracle 联合**：`uia-property` · `element-state` · `window-or-dialog` · `file-content` ·
`visual-region`。每次评估返回 `pass` / `fail` / `unknown` + 捕获的证据。
**只有 `pass` 允许成功**；`unknown` 对非幂等动作不消耗盲重试。

### 现有代码处置

| 组件 | 处置 | 理由 |
|---|---|---|
| `Pudding.Contracts/Computer` | **adapt** | 增加 accessibility 目标、证据、风险、尝试、三态结果；保留 `ImagePoint`/`ScreenPoint` 严格分离 |
| `ComputerCoordinateMapper` | **keep as-is** | 仍是图像→屏幕的权威转换边界 |
| `ComputerSnapshotAnnotator` | **adapt** | 标注融合后的语义 + 视觉候选，保留全部坐标系 |
| GDI 截图 | **keep as-is** | 必需的证据源与回退观察通道 |
| OpenCV 检测器 | **降级为 fallback** | 仅在无障碍发现稀疏/不可用时调用 |
| `WindowsInteractiveWindowFinder` | **adapt** | 返回可同时被 UIA 与截图使用的确认窗口身份 |
| `WindowsInputSimulator` | **adapt** | 暴露显式原语结果；风险与验证守卫由 orchestration 提供 |
| `WindowsDpiEnforcer` | **keep as-is** | 坐标正确性仍需 Per-Monitor V2 |
| 现有 90 个测试 | **保留为回归门禁** | 新行为需新增变异敏感测试，**不得削弱**现有断言 |

### 两周实施序列（每项含 pass/fail）

| # | 交付 | 通过条件 |
|---|---|---|
| 1 | Benchmark schema + 12 个任务 + 类型化 oracle + risk 枚举 | schema 测试通过，且刻意变异的 oracle/risk 会让测试失败 |
| 2 | 扩展 contracts（无原生依赖） | contracts 独立构建，架构测试拒绝禁止的引用 |
| 3 | 只读 UIA 适配器 + 确定性控件画廊 | 重复快照下能恢复预期的原生控件与属性 |
| 4 | 记事本/计算器/画图的 UIA 探针 | 目标控件进入 top-5 的比例 ≥95%；不支持的情况被显式分类 |
| 5 | 统一快照融合 + 语义排序 | UIA/焦点/截图/视觉候选共享同一快照身份，**无坐标类型泄漏** |
| 6 | policy + verification 引擎 | 任何 `unknown` 或失败的 oracle 都不能产出成功；非幂等盲重试的变异必须失败 |
| 7 | 独立 orchestration runner | 最多 3 次尝试、每次动作后重新观察、风险感知的回退路由 |
| 8 | 原生应用 benchmark 端到端 | 零假成功报告、每个结论都有证据留存、S1–S4 边界测试全绿、**宿主未被触碰** |

---

## 这两轮对我今天工作的判定

| 我今天做的 | 判定 |
|---|---|
| DPI / 坐标 / 窗口身份的修复 | **keep** —— 换成任何 GUI 自动化都仍是根基 |
| OpenCV 检测器做到 202 个目标 | **降级为 fallback** —— 不该当主控制面 |
| 死磕 ChatGPT 白底界面 | **降级为非门禁压力测试** —— 它不该用来门禁 |
| 90 个单元测试 + 变异验证 | **keep 为回归门禁** —— 纪律没错，只是方向偏了 |
| 花在"证明点击生效"上的力气 | 方法对了（Codex 认可方向），但**对象错了** |

**核心教训**：不是"做得不认真"，而是**没有先问"为谁做、做到什么程度算够"**。

## 未验证

以上两轮都是**规划**，没有任何一项经过实施或实验检验。
Codex 自己在第 1 轮给出的最便宜反证方式仍然有效：
若可逆探针出现假阳性，则目标陈述中的"验证"部分被证伪。
