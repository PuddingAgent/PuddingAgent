# Pudding Runtime Space UI 设计语言与页面方案

> 日期：2026-05-13  
> 范围：`PuddingPlatformAdmin` 前端整体视觉语言、登录/初始化、Chat 主舞台、Runtime Timeline、后台管理页面  
> 关系：本方案继承并升级 [`PuddingUiUxRedesign.md`](./PuddingUiUxRedesign.md) 的“本地私人 AI Agent”定位，以及 [`ChatUIRedesign.md`](./ChatUIRedesign.md) 的“混合分层 / Agent 时间线”方向。  
> 核心判断：Pudding 不应是“聊天卡片工厂”，而应是一个安静、可信、持续运行的本地 AI Runtime 空间。

---

## 1. 一句话总纲

**Pudding 的 UI 应像一个安静运行的 AI Runtime：用户在 Agent Console 注入意图，系统在 Runtime Timeline 中检索、思考、调用工具、组织信息，最终答案从运行过程里逐渐凝聚出来。**

不是：

- 聊天气泡一张张弹出
- DOM 元素被 append
- 工具、思维、消息各自弹卡片
- 满屏霓虹、粒子、游戏化特效

而是：

- 信息在流动、分层、编织
- 状态有呼吸、有节奏、有停顿
- 工具调用像 Runtime 信号在线路中传递
- 最终答案像神经元激活后的文字逐渐稳定

关键词：**Fluid / Calm / Rhythm / Neural / Ambient / Cinematic**。

---

## 2. 设计语言核心定义

### 2.1 命名

建议将新视觉语言命名为：

**Quiet Runtime Space / 安静运行的 AI Runtime 空间**

它不是传统聊天应用，也不是企业后台大屏，而是本地私人 Agent 正在持续感知、检索、推理、调用工具、组织答案的可视化空间。

### 2.2 六个关键词的 UI 解释

| 关键词 | UI 中的表达 | 必须避免 |
|---|---|---|
| **Fluid** | 信息像水流一样进入 Timeline，逐步汇聚为结构 | 卡片突然出现、布局跳变 |
| **Calm** | 低饱和、低噪声、柔和发光、留白稳定 | 强 bounce、高频闪烁、满屏动效 |
| **Rhythm** | Thinking → Tool → Synthesizing → Completed 有节奏起伏 | 所有区域同时动 |
| **Neural** | 用节点、细线、微光表达记忆/工具/上下文连接 | 过度具象的大脑、赛博装饰堆砌 |
| **Ambient** | 背景只是空气感，低密度微粒与雾化光 | 粒子成为主角、烟花爆炸 |
| **Cinematic** | 暗场、景深、缓慢镜头感、聚光式层次 | 科幻电影 HUD 堆满屏幕 |

### 2.3 视觉原则

1. **主内容永远优先**：文字、工具结果、可操作状态的可读性高于氛围。
2. **动效只解释状态，不展示技巧**：用户看动效是为了知道 Agent 正在做什么，而不是欣赏特效。
3. **粒子永远在背景层**：它是空气感，不是功能控件。
4. **后台越低频，越接近 Ant Design Pro 原生**：不要把所有管理页都做成科幻面板。
5. **Chat 是主舞台，Console 是工具箱**：登录后默认进入 `/chat` 的方向保持正确。

---

## 3. Design Tokens 建议

现有项目已有暖色基础：`#7c3aed`、`#f5f0e8`、`#fafaf7`、`#5c4a3a`、`#1f1a17`。本方案在此基础上增加 Runtime 语义色。

### 3.1 颜色 Token

| Token | 浅色建议 | 暗色建议 | 用途 |
|---|---:|---:|---|
| `runtime-bg` | `#F5F0E8` | `#070A12` | Runtime 主舞台背景 |
| `runtime-bg-deep` | `#EDE5D9` | `#0B1020` | 深层背景/登录背景 |
| `glass-surface` | `rgba(250,250,247,.72)` | `rgba(17,24,39,.68)` | 毛玻璃面板 |
| `glass-border` | `rgba(124,58,237,.18)` | `rgba(167,139,250,.22)` | 面板边框 |
| `neural-line` | `rgba(124,58,237,.18)` | `rgba(167,139,250,.24)` | 神经连线/Timeline |
| `memory-glow` | `#A78BFA` | `#A78BFA` | 记忆、潜意识、上下文召回 |
| `tool-signal` | `#22D3EE` | `#22D3EE` | 工具调用、检索、外部信号 |
| `success-signal` | `#22C55E` | `#4ADE80` | 完成、健康、可用 |
| `warning-signal` | `#F97316` | `#FB923C` | 配额、上下文压力、降级 |
| `error-signal` | `#EF4444` | `#F87171` | 错误、失败、中断 |
| `text-primary` | `#1A1A2E` | `#E6EAF2` | 正文 |
| `text-muted` | `#5C4A3A` | `#94A3B8` | 次级说明 |

### 3.2 字体与排版

| 场景 | 建议 |
|---|---|
| 主 UI 字体 | 保持系统字体栈，优先可读性 |
| Runtime 状态文字 | 12–13px，字距略松，低对比但清晰 |
| 正文回答 | 15–16px，行高 1.65–1.8，避免压迫 |
| 代码/工具输出 | 等宽字体，仅在展开详情时明显呈现 |
| 大标题 | 少用超大字号，使用空间层级与光感区分 |

### 3.3 圆角、阴影、玻璃

| Token | 建议值 | 用途 |
|---|---:|---|
| `radius-sm` | 8px | 小按钮、Tag |
| `radius-md` | 12px | 输入框、工具节点 |
| `radius-lg` | 18px | 面板、消息块 |
| `radius-xl` | 24px | 登录卡片、Agent Console |
| `shadow-glass` | `0 18px 60px rgba(0,0,0,.18)` | 漂浮面板 |
| `shadow-glow-purple` | `0 0 28px rgba(167,139,250,.18)` | Runtime 呼吸 |
| `shadow-glow-cyan` | `0 0 24px rgba(34,211,238,.16)` | Tool 执行 |

---

## 4. 全局动效系统

### 4.1 动效预算

Pudding 的动效必须被预算管理，否则“安静运行”会变成“到处都在动”。

| 区域 | 默认允许的循环动效数量 | 说明 |
|---|---:|---|
| `/chat` 主舞台 | 1 个主循环 + 1 个弱背景循环 | 当前执行节点呼吸为主 |
| 登录 / Bootstrap | 1 个背景慢循环 | 粒子雾或能量环二选一 |
| Runtime Management | 1 个健康状态弱 pulse | 只表达心跳/在线 |
| 普通后台管理页 | 0 | 使用静态、hover、短过渡 |

### 4.2 动效时长

| 类型 | 建议时长 |
|---|---:|
| Hover / Focus | 150–220ms |
| 面板展开 | 220–320ms |
| Timeline 节点进入 | 240–360ms |
| 内容块凝聚 | 300–480ms |
| 背景呼吸 | 4000–6000ms |
| 错误反馈 | 200–320ms |
| 页面切换 | 180–240ms |

### 4.3 全局节奏

Agent 一次输出应像音乐一样有节奏：

```text
Idle
  ↓
Composing
  ↓
Submitted
  ↓
Routing
  ↓
Recalling Memory
  ↓
Thinking
  ↓
Tool Executing
  ↓
Synthesizing
  ↓
Condensing Output
  ↓
Completed
```

异常分支：`Error`、`Cancelled`、`RateLimited`、`AwaitingApproval`。

### 4.4 状态与视觉映射

| 状态 | 用户可见文案 | 视觉 | 动效 |
|---|---|---|---|
| `Idle` | Runtime 就绪 | 背景低亮，Timeline 静止 | 极慢呼吸 |
| `Composing` | 正在输入意图 | 输入框底部微光 | focus glow |
| `Submitted` | 意图已接收 | 新节点淡入 | 节点浮现 |
| `Routing` | 正在选择 Agent / 模型 | 细线连接上下文胶囊与 Timeline | 线性扫描 |
| `RecallingMemory` | 正在检索本地记忆 | 紫色节点扩散 | soft diffuse |
| `Thinking` | 正在组织思路 | Context Core 呼吸 | neural pulse |
| `ToolExecuting` | 正在调用工具 | 青色状态线、工具节点点亮 | wave scan |
| `Synthesizing` | 正在整理信息 | 段落骨架浮现 | skeleton morph |
| `CondensingOutput` | 信息正在凝聚 | 内容分块渐显 | reveal + fade |
| `Completed` | 已完成 | 光效收敛，操作出现 | settle |
| `Error` | 执行受阻 | 红色边缘与文字说明 | 不使用强 shake |
| `Cancelled` | 已停止 | 节点转灰 | fade out |
| `AwaitingApproval` | 等待确认 | 面板固定，按钮突出 | 静态强调 |

### 4.5 Reduced Motion

必须支持系统 `prefers-reduced-motion`，并建议在设置中提供“降低动态效果”。降级策略：

- 粒子静态化或隐藏
- 背景呼吸停止
- Timeline 节点改为 opacity 过渡
- 所有旋转、扫描线、视差关闭
- 流式输出仍保留块级更新，但不做位移

---

## 5. 核心组件设计

### 5.1 Agent Console：输入框不是 textarea

输入区域应被定义为 **Agent Console**，它不是普通输入框，而是用户向 AI Runtime 注入意图的控制台。

#### 草图

```text
┌──────────────────────────────────────────────────────────────┐
│ Context: 研发空间 · Code Agent        Memory: ready · 72% ctx │
├──────────────────────────────────────────────────────────────┤
│  /  描述你的目标、拖入文件、或输入命令…                       │
│                                                              │
│  底部流光：输入时缓慢流动；发送瞬间向 Timeline 扩散           │
├──────────────────────────────────────────────────────────────┤
│  + 附件   / 命令   记忆: 自动   Token Ring     停止 / 发送    │
└──────────────────────────────────────────────────────────────┘
```

#### 行为

- 默认半透明磨砂玻璃，固定底部。
- 输入 focus 时出现淡紫/淡蓝边缘光。
- 输入内容增长时，`Context Ring` 或上下文压力条缓慢变化。
- Enter 发送瞬间：一道柔和能量波向上扩散，少量粒子被“送入”Runtime Timeline。
- 普通模式只显示自然语言状态；Token、余额、ASP/LSP、索引状态进入专家模式。

### 5.2 Runtime Timeline：Thought / Tool / Message 融合

不要三个独立卡片，而是一个连续的信息流容器。

#### 草图

```text
┌ Agent Runtime ───────────────────────────────────────────────┐
│  ◌ 正在理解你的问题                                           │
│  │                                                           │
│  ● 正在检索本地记忆                         Memory · 0.8s    │
│  │                                                           │
│  ● 调用 Web Search，获取 3 个结果            Tool · 1.2s      │
│  │                                                           │
│  ● 正在组织回答                              Thinking        │
│  │                                                           │
│  ◆ 最终回答开始凝聚…                                         │
└──────────────────────────────────────────────────────────────┘
```

#### 结构

| 层级 | 默认显示 | 展开后显示 |
|---|---|---|
| Thinking | 思考摘要：正在分析/规划/组织 | 可选详细摘要，不展示原始内部思维链 |
| Tool Call | 工具名、目的、状态、耗时 | 参数摘要、返回摘要、可复制日志 |
| Tool Result | 结果数量、关键结论 | 详细结果，默认折叠 |
| Final Answer | 完整答案 | 操作：复制、追问、保存记忆、重新执行 |

### 5.3 输出不是打字机，而是信息凝聚

前端即使收到 SSE delta，也不应机械逐字打印。建议 UI 层进行轻量缓冲：

- 每 120–240ms 聚合一次文本块。
- 按段落、列表项、代码块边界更新。
- 字符轻微 fade-in，行与行之间有呼吸节奏。
- 新段落出现时有极弱光晕扩散。
- 完成后执行一次 settle，操作按钮淡入。

输出阶段：

1. **Latent / 潜伏层**：淡淡呼吸光与状态文字。
2. **Structuring / 组织层**：骨架、大纲、工具摘要、引用来源出现。
3. **Crystallized / 凝聚层**：最终答案稳定，可复制、追问、归档。

### 5.4 Tool Pulse：工具调用动画

工具调用不弹卡片，而是在 Timeline 中点亮一个节点。

```text
───────╮
       ● Web Search       running
───────╯  cyan signal line flowing

完成后：
───────╮
       ● Web Search       done · 3 results · 1.2s
───────╯  soft fade to stable
```

规则：

- 当前工具节点轻微亮起。
- 状态线只在当前节点流动。
- 完成后柔和熄灭，保留摘要。
- 失败时显示明确原因与可重试操作。

### 5.5 Thought Summary：思维链展示

默认不展示原始内部思维链，应展示“思考摘要 / 执行摘要”。

默认态：

- 半透明
- 低对比
- 模糊边缘
- 一句话摘要

Hover / 展开：

- 清晰
- 水波式向下展开
- 显示更多摘要细节、计划、约束与中间结论

示例：

```text
Thinking Summary
已确认目标：优化前端视觉语言与交互节奏
已检索：现有 UI 文档、Chat 组件、登录页、后台路由
下一步：整理页面级方案并输出文档
```

首个答案 Token 出现前，主消息中的运行监视区遵循以下边界：

- 主代理的当前工具/执行阶段与最近可展示的推理摘要可以同时存在，阶段切换不得让推理摘要瞬间消失。
- 尚未收到推理摘要或工具事件时，展示“主代理正在运行、等待模型首个可见事件、已等待时长”，不得编造内部思考步骤。
- 主代理调用子代理时，只显示“正在调用/等待 N 个子代理返回”的父级委派摘要；子代理任务、工具、轮次和输出详情只在右侧子代理托盘坞展开。
- Agent-first 路由即使尚未选中侧栏会话，也必须以已解析的主会话绑定托盘坞，保证运行图标可见。
- 子代理预算耗尽是可恢复的运行终态：托盘坞显示“预算已用尽/运行结束”，不得继续计时或因历史事件重放恢复为运行中。
- 子代理检查器的“模型推理”原样展示 Runtime 实际收到的模型推理返回值；该字段不脱敏，过长内容在 4096 字符处明确标注截断，不得用字符数或安全声明替代实际内容。
- 消息投递的重复命中属于不可见的传输状态；相同 envelope `message_id` 只显示一次，幂等占位回执不得成为 Agent 气泡。
- 心跳输入投影为 `context.text` 的“系统心跳”卡片，不显示完整 `pudding-message` JSON；心跳是自主执行轮次，必须恢复最近非心跳上下文并完成一个已授权、安全、可回滚的推进步骤，不能以询问用户下一步结束。

---

## 6. 页面级设计方案

### 6.1 `/user/login` 登录页

#### 定位

登录页是“进入本地 AI Runtime”的入口，不是普通后台登录页。

#### 草图

```text
┌──────────────────────────────────────────────────────────────┐
│  dark runtime field                                           │
│      ·    ·       faint neural lines                          │
│          ╲        ╱                                           │
│           ╲      ╱                                            │
│            ┌────────────────────────────┐                     │
│            │ Pudding Runtime            │                     │
│            │ 本地私人 AI Agent 正在等待唤醒 │                 │
│            │                            │                     │
│            │  User ID                   │                     │
│            │  Password                  │                     │
│            │                            │                     │
│            │  [ Enter Runtime  → ]      │                     │
│            └────────────────────────────┘                     │
│                slow particles, not decoration overload         │
└──────────────────────────────────────────────────────────────┘
```

#### 风格

- 深色半透明背景，径向渐变：`#070A12 → #111827`。
- 缓慢流动的粒子雾，桌面 24–48 个粒子，移动端可降为 0–12。
- 微弱神经网络连线，最多 1–3 条主要线，不铺满。
- 中央漂浮式登录框，玻璃模糊，边框淡紫。

#### 动画

- 输入框 focus：淡蓝/淡紫光晕。
- 鼠标附近粒子轻微逃逸，范围小、速度慢。
- 登录按钮 hover：内部能量流动，但不放大。
- 点击登录：按钮内核收缩 120ms，粒子向按钮中心轻微汇聚，进入“启动 Agent”状态。
- Loading 不用 spinner，使用缓慢旋转能量环 + 中心粒子聚合，文案为“AI Core 正在启动”。

#### 可用性要求

- 表单文字对比度必须 ≥ 4.5:1。
- 错误状态不能只靠红色，要有明确文字。
- 登录页动效不得遮挡输入框。

---

### 6.2 `/bootstrap` 初始化页

#### 定位

首次启动不是“注册”，而是“初始化本地 Runtime”。

#### 草图

```text
┌──────────────────────────────────────────────────────────────┐
│                   Initialize Pudding Runtime                  │
│                                                              │
│             ●────────────●────────────●                       │
│          Identity      Admin Seal    Enter Runtime             │
│                                                              │
│          ┌────────────────────────────────────┐               │
│          │ 创建本地管理员身份                  │               │
│          │ User ID / Email / Password          │               │
│          │ ✓ 8 位以上  ✓ 大写  ✓ 小写  ✓ 数字   │               │
│          │ [ Seal & Launch Runtime ]           │               │
│          └────────────────────────────────────┘               │
└──────────────────────────────────────────────────────────────┘
```

#### 方案

- 与登录页共享暗色玻璃基调。
- 使用三枚安静发光节点替代强烈 Steps。
- 密码强度使用实时 checklist。
- 创建成功后的跳转使用短暂“Runtime 已完成封存”状态，随后进入 `/chat`。

---

### 6.3 `/chat` 主舞台

#### 定位

Chat 是 Pudding 的默认主界面，是 Quiet Runtime Space 的完整体现。

#### 主布局草图

```text
┌────────────────────────────────────────────────────────────────────┐
│  Pudding Runtime                         Context: Workspace · Agent │
├───────────────┬──────────────────────────────────────┬─────────────┤
│ Session Rail  │ Runtime Timeline                      │ Inspector   │
│               │                                      │ optional    │
│ + New         │  User intention                       │             │
│ Search        │  ┌────────────────────────────────┐  │ Memory      │
│ Sessions      │  │ 帮我分析这个任务并给方案         │  │ Token       │
│               │  └────────────────────────────────┘  │ Tool        │
│               │                                      │             │
│               │  Agent Runtime                       │             │
│               │  ◌ 正在理解问题                       │             │
│               │  ● 正在检索记忆                       │             │
│               │  ● 调用工具                           │             │
│               │  ◆ 答案正在凝聚...                    │             │
├───────────────┴──────────────────────────────────────┴─────────────┤
│ Agent Console                                                       │
│ / 输入目标、命令或拖入文件…                             Stop / Send │
└────────────────────────────────────────────────────────────────────┘
```

#### 设计重点

1. **Runtime Timeline 替代消息卡片堆叠**
   - 用户消息、思考摘要、工具调用、最终回答属于同一个工作流。
   - 工具和思考不再以独立卡片弹出，而是连续 Timeline 节点。

2. **Agent Console 替代普通输入框**
   - 输入时底部流光。
   - 上下文 Ring 显示 Token 压力。
   - 发送时能量波向上扩散。

3. **最终回答优先级最高**
   - Runtime 过程默认摘要化。
   - 最终答案块更稳定、更清晰、更可读。
   - 长工具输出默认折叠。

4. **开发者信息进入 Runtime Inspector**
   - 原始事件、上下文组装、潜意识 LLM、Token 明细默认不展示给普通用户。
   - 专家模式可打开右侧 Inspector。

#### 空状态草图

```text
┌──────────────────────────────────────────────────────────────┐
│                     Pudding Runtime Ready                     │
│              一个本地 AI Agent 正在安静等待意图                │
│                                                              │
│        ○ 分析代码任务      ○ 整理会议记录      ○ 检索记忆       │
│                                                              │
│        faint particles + one breathing context core           │
└──────────────────────────────────────────────────────────────┘
```

#### Chat 的动效顺序

```text
用户 Enter
  ↓
输入框底部能量波向上扩散
  ↓
Timeline 新 Runtime 容器缓慢浮现
  ↓
Thinking Core 呼吸
  ↓
Tool 节点按执行顺序点亮
  ↓
答案骨架浮现
  ↓
文本块凝聚
  ↓
光效收敛，操作按钮出现
```

---

### 6.4 `/workspace` 工作空间列表

#### 定位

Workspace 不是后台表格，而是 Agent 工作发生的“场域”。

#### 草图

```text
┌──────────────────────────────────────────────────────────────┐
│ Workspaces / Agent Fields                         [Table View]│
├──────────────────────────────────────────────────────────────┤
│ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐            │
│ │ Default      │ │ Coding Lab   │ │ Research     │            │
│ │ 3 Agents     │ │ 5 Agents     │ │ 2 Agents     │            │
│ │ Memory ready │ │ Tools 12     │ │ Last 2h      │            │
│ │ [Enter Chat] │ │ [Configure]  │ │ [Enter Chat] │            │
│ └──────────────┘ └──────────────┘ └──────────────┘            │
└──────────────────────────────────────────────────────────────┘
```

#### 方案

- 默认卡片视图，表格作为高级模式。
- “进入 Chat”比“删除/编辑”更突出。
- 展示 Agent 数、最近活跃、记忆/工具状态。
- 视觉品牌化 40–50%，不要粒子。

---

### 6.5 `/workspace/:id` 场景驾驶舱

#### 定位

场景配置驾驶舱，不是第二个主 Chat。

#### 草图

```text
┌──────────────────────────────────────────────────────────────┐
│ Coding Lab                         Runtime healthy · 3 Agents │
│ Default Agent: Code Assistant       [Enter Chat] [Settings]   │
├──────────┬──────────┬──────────┬──────────┬──────────────────┤
│ Agents   │ Tools    │ Memory   │ Channels │ Policies         │
├──────────┴──────────┴──────────┴──────────┴──────────────────┤
│ Configuration panels                                           │
│ Test Console: 用于验证 Agent，不作为主对话入口                 │
└──────────────────────────────────────────────────────────────┘
```

#### 方案

- 顶部 Scene Header：名称、状态、默认 Agent、Runtime 健康。
- Tabs 分组：Agents / Tools / Memory / Channels / Policies。
- 内嵌 Chat 明确标注为 Test Console。
- 真实使用引导到 `/chat?workspace=xxx`。

---

### 6.6 `/llm-resource-pool` 模型资源池

#### 定位

Runtime 的“能源与算力面板”。

#### 草图

```text
┌──────────────────────────────────────────────────────────────┐
│ LLM Resource Pool                                             │
├──────────────┬──────────────┬──────────────┬─────────────────┤
│ Providers OK │ Quota Ring   │ Cost Trend   │ Context Windows │
├──────────────┴──────────────┴──────────────┴─────────────────┤
│ Provider / Model table                                         │
│ capability tags · health · limits · cost                       │
└──────────────────────────────────────────────────────────────┘
```

#### 方案

- 表格保留。
- 顶部增加 Provider Health、Token Quota Ring、模型能力矩阵。
- 使用柔和仪表，不做炫目大屏。

---

### 6.7 `/global-agent-template` Agent 蓝图库

#### 定位

Agent Library / Agent 蓝图库。

#### 方案

- 支持卡片 + 表格双模式。
- 卡片展示：角色、模型、记忆模式、工具能力、风险级别。
- 详情 Drawer 分区：Persona / Reasoning / Tools / Memory / Runtime Limits。
- Prompt 大文本默认折叠高级区域，不统治页面。

---

### 6.8 `/capability-management` 与 `/skill-management`

#### 定位

Capability Graph / Skill Registry，Runtime 可调用能力节点。

#### 草图

```text
┌──────────────────────────────────────────────────────────────┐
│ Capability Registry                                           │
├──────────────────────────────────────────────────────────────┤
│ Query/Read ── Tool ── Memory ── Execute ── Security            │
│    blue       cyan     purple     orange      red              │
├──────────────────────────────────────────────────────────────┤
│ table/details/drawer                                           │
└──────────────────────────────────────────────────────────────┘
```

#### 分类颜色

| 类型 | 色彩 |
|---|---|
| Query / Read | 蓝 |
| Tool / Search | 青 |
| Memory | 紫 |
| Execute / Write | 橙 |
| Security / Secret | 红 |
| Local / Runtime | 绿 |

功能图标必须使用 SVG，不使用 emoji 作为功能图标。

---

### 6.9 `/keyvault` 本地密钥保险箱

#### 定位

Local Secret Vault / 本地密钥保险箱。信任页面必须克制。

#### 方案

- 深色或半暗色安全面板。
- 密钥永远默认遮蔽。
- “显示”“复制”“轮换”“删除”操作分级。
- 危险操作二次确认。
- 不启用粒子与神经线，避免安全页面显得轻浮。

---

### 6.10 `/user-management`、`/role-management`、`/team-management`

#### 定位

低频后台工具箱。

#### 方案

- 保持 Ant Design Pro 标准效率。
- 使用统一页头、状态 Tag、Drawer 表单。
- 不加入 Runtime 背景、粒子、神经线。
- Header 可有极弱品牌渐变，但不影响表格效率。

---

### 6.11 `/session` 会话归档

#### 定位

Session Archive / Runtime 运行记录。

#### 草图

```text
┌──────────────────────────────────────────────────────────────┐
│ Session Archive                                [Table/Timeline]│
├──────────────────────────────────────────────────────────────┤
│ 09:10  会话开始                                                │
│ 09:12  Agent 切换为 Code Assistant                             │
│ 09:13  调用 grep_search，返回 12 条                             │
│ 09:15  生成回答完成                                             │
└──────────────────────────────────────────────────────────────┘
```

#### 方案

- 表格视图保留。
- 增加 Timeline 视图：会话开始、Agent 切换、工具调用、失败/取消、完成。
- 支持 Workspace / Agent / Status 过滤。

---

### 6.12 `/runtime-management` Runtime 节点管理

#### 定位

后台中最适合承接“安静运行的 AI 系统”的页面。

#### 草图

```text
┌──────────────────────────────────────────────────────────────┐
│ Runtime Nodes                                                 │
├──────────────────────────────────────────────────────────────┤
│       ● local-runtime       online · heartbeat 10s             │
│      ╱ ╲                                                     │
│   ● worker-a              degraded                            │
│   ○ worker-b              frozen                              │
├──────────────────────────────────────────────────────────────┤
│ Node details table                                             │
└──────────────────────────────────────────────────────────────┘
```

#### 方案

- 顶部增加 Node Map 或健康摘要。
- 在线：柔和绿点。
- 降级：琥珀呼吸。
- 离线：低饱和红。
- 冻结：锁定态，停止呼吸。
- 表格保留下方。
- 冻结等危险操作必须解释后果。

---

### 6.13 `/admin/sub-page`

若仍是 Ant Design Pro 示例页，建议隐藏或归档；若保留，应作为“系统信息 / 关于”页，而不是重要入口。

---

## 7. 登录与 Chat 关键动效草图

### 7.1 登录启动序列

```text
Input Focus
  → field glow 180ms
  → particles slightly drift away

Button Hover
  → subtle inner energy flow

Click Login
  → button core contracts
  → particles gather toward core
  → energy ring appears
  → “AI Core 正在启动”
  → route transition to /chat
```

### 7.2 Agent 输出序列

```text
User sends intention
  → Agent Console emits soft wave
  → Runtime container appears
  → Context Core breathes
  → Tool orbit dots rotate slowly
  → Timeline nodes light one by one
  → answer skeleton appears
  → semantic blocks fade in
  → particles near text softly dissipate
  → final answer settles
```

### 7.3 Tool 节点序列

```text
Tool queued
  → node appears low opacity
Tool running
  → cyan signal line flows through node
Tool done
  → node stabilizes green/cyan
  → summary text appears
Tool failed
  → node edge turns red
  → retry action appears
```

---

## 8. 信息层级与文案规范

### 8.1 普通用户看“执行过程”，开发者看“Runtime Inspector”

不要默认暴露：

- 原始 SSE payload
- 内部 JSON event
- 完整 prompt/context dump
- Token 明细堆叠
- ASP/LSP 等专业缩写

默认展示：

- 正在理解你的问题
- 正在检索本地记忆
- 正在调用文件搜索工具
- 工具返回 12 条结果
- 正在整理最终回答

### 8.2 状态色语义

| 颜色 | 含义 | 文案必须配合 |
|---|---|---|
| 蓝/青 | 正在执行、工具调用 | “正在调用工具” |
| 紫 | 记忆、上下文、思考 | “正在检索记忆” |
| 绿 | 完成、健康、可用 | “已完成” |
| 橙 | 需注意、降级、配额压力 | “上下文接近上限” |
| 红 | 失败、危险、中断 | “工具调用失败，可重试” |

### 8.3 错误文案

错误必须回答三件事：

1. 哪里失败了？
2. 对结果有什么影响？
3. 用户现在能做什么？

示例：

```text
文件搜索工具执行失败。
当前回答可能缺少最新代码上下文。
你可以重试工具调用，或继续让 Agent 基于已有上下文回答。
```

---

## 9. 无障碍与长期使用要求

- 所有图标按钮必须有 `aria-label` 或 Tooltip。
- 功能图标使用 SVG，不用 emoji 作为功能图标。
- 颜色状态必须配文字，不能只靠颜色点。
- Timeline 动态更新区域建议 `aria-live="polite"`。
- 错误消息使用 `role="alert"`。
- 输入框最小点击高度 ≥ 40px，按钮 ≥ 36px。
- `Tab` 可完整访问：会话列表、Timeline 操作、输入控制台、停止生成、命令面板。
- 移动端禁用复杂背景或降级为静态渐变。
- 长时间使用时，动效不能干扰阅读，不应造成晕动。

---

## 10. 落地优先级

### P0：防止方向跑偏

1. **补齐统一 UI 规范**
   - 将本方案作为 Runtime Space 设计基线。
   - 明确 token、动效预算、页面分级。

2. **Chat 输入区升级为 Agent Console**
   - 强化底部 dock、命令、发送/停止、上下文压力。
   - 默认状态栏降噪，技术指标进入专家模式。

3. **统一 Runtime Timeline**
   - Thought / Tool / Message 统一为连续信息流。
   - 工具和思考默认摘要化。

4. **登录 / Bootstrap 暗色玻璃化**
   - 最容易建立第一印象。
   - 只使用低密度粒子和弱神经线。

5. **加入 Reduced Motion 策略**
   - 动效系统上线前的必要保护。

### P1：强化 Runtime 感

1. **输出从打字机改为块级凝聚**
   - UI 层聚合 delta。
   - Markdown 按块渲染。

2. **Runtime 状态机视觉统一**
   - thinking / memory / tool / streaming / success / error 全局语义化。

3. **DevPanel 升级为 Runtime Inspector**
   - 默认折叠，高级用户打开。

4. **Runtime Management 节点健康视图**
   - Node Map + 表格详情。

### P2：后台工具箱整理

1. Workspace 卡片化入口。
2. Session Archive 增加 Timeline 视图。
3. Agent / Capability / Skill Registry 统一卡片 + 表格双模式。
4. KeyVault 视觉降噪、安全优先。

### P3：Ambient 系统锦上添花

1. 粒子雾 / 神经连线仅用于登录、Bootstrap、Chat 空状态。
2. Runtime 背景随状态轻微变化。
3. 主舞台/登录/初始化可有短页面转场，后台页保持快速直接。

---

## 11. 验收标准

### 11.1 快速进入对话

- 登录成功后进入 Chat 主界面。
- 首次进入后，用户 5 秒内能找到输入框并知道如何发送。
- 未选择 Agent 时，有明确提示和直接入口。

### 11.2 视觉克制

- 默认屏幕上同时循环动画不超过 1 个主区域。
- 所有动画支持 reduced motion。
- 粒子不覆盖主要文本，不影响阅读。

### 11.3 执行过程可理解

- 每次 Agent 回复期间，用户能看到当前阶段：思考、检索、工具调用、生成回答、完成或失败。
- 工具调用展示工具名、目的、状态和简短结果。
- 原始事件、payload、Token 明细不默认暴露给普通用户。

### 11.4 最终回答突出

- 最终回答视觉权重高于执行过程。
- 长工具输出默认折叠。
- 思考摘要不能挤压最终答案阅读空间。

### 11.5 后台低频化

- Chat 页到控制台入口清晰，但不干扰主对话。
- 后台页面保留效率，避免过强动态背景。
- 管理页有明确分组和返回 Chat 的入口。

### 11.6 可靠感

- 出错时说明失败原因、影响范围和可执行建议。
- 停止生成后状态明确，不继续显示“正在思考”。
- 会话切换、刷新、加载历史时不让用户误以为内容丢失。

---

## 12. 页面品牌化强度分级

| 页面类型 | 品牌化强度 | Runtime 特效 |
|---|---:|---|
| `/chat` | 100% | 完整 Runtime Space，但克制 |
| `/user/login`、`/bootstrap` | 80% | 暗色玻璃、弱粒子、能量环 |
| `/runtime-management`、`/session` | 50–60% | 节点健康、运行轨迹 |
| `/workspace`、`/workspace/:id` | 40–50% | 场景卡片、轻玻璃 |
| LLM / Agent / Skill / Capability | 25–40% | 卡片与 Drawer 语义化 |
| KeyVault | 20–30% | 安全克制，不用粒子 |
| 用户 / 角色 / 团队管理 | 10–20% | 基本后台效率优先 |

---

## 13. 反模式清单

禁止：

- 所有消息用卡片突然弹出。
- Thinking、Tool、Answer 三套视觉系统互相割裂。
- 大量 bounce、scale、shake。
- 粒子像烟花或游戏技能。
- 后台表格页套满动态背景。
- 默认展示原始思维链或原始 JSON 事件。
- 技术指标默认占满输入区状态栏。
- 动效造成布局跳动或文本阅读困难。

推荐：

- 连续 Runtime Timeline。
- 摘要优先，详情可展开。
- 当前节点动，其他节点静。
- 内容块凝聚，而非逐字打印。
- Chat 主舞台强品牌，后台工具箱弱品牌。
- reduced motion 从第一阶段就纳入。

---

## 14. 与现有文档的关系

- [`PuddingUiUxRedesign.md`](./PuddingUiUxRedesign.md)：保留“静谧书斋式 AI Agent / Chat 是主舞台”的产品定位，本方案将其升级为 Runtime Space 视觉系统。
- [`ChatUIRedesign.md`](./ChatUIRedesign.md)：保留“混合分层 / 紧凑列表 + 展开式 Agent 面板”的结构方向，本方案进一步要求 Thought / Tool / Message 融合为连续 Runtime Timeline。
- [`LoginFeature.md`](./LoginFeature.md)：认证、安全流程保持不变，本方案只改变登录/Bootstrap 的体验呈现。

---

## 15. 最终目标画面

用户看到的不是一个“正在打字的聊天机器人”，而是：

```text
一个安静运行的本地 AI 系统。

它接收你的意图，
在上下文、记忆和工具之间流动，
把离散信息组织成结构，
最后把答案温和、稳定地呈现在你面前。
```

这就是 Pudding 的视觉体验边界：**高级，但不炫技；科幻，但不嘈杂；智能，但始终可读、可控、可信。**
