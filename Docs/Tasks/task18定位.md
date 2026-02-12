# PuddingAssistant 产品设计文档

> **定位：** 从"程序员的生产力工具"向"人类的数字化代理"的范式转移。PuddingAssistant 是用户的"数字化布丁管家"，对标 OpenClaw，追求更懂用户、更具视觉吸引力。

---

## 一、设计原则

### 1.1 范式转变

| 维度 | PuddingCode（旧） | PuddingAssistant（新） |
| --- | --- | --- |
| **核心关注** | 文件树、Diff 对比、终端输出 | "我的任务谁在做？做得怎样了？" |
| **交互语言** | 文件中心 | 会话与拓扑中心 |
| **逻辑架构** | 线性流水线、固定角色 | 动态博弈集群、按需生成 |
| **记忆系统** | 代码文档（`.cs` / `.md`） | 个人生活图谱 |
| **目标用户** | 程序员 | 大众用户 |

### 1.2 PuddingCode 的重新定位

PuddingCode 不消失，它成为 PuddingAssistant 的 **"专业形态（Pro Mode）"** 或 **技能插件**。

* 当用户说"帮我写个脚本爬取超市价格"时，Swarm 中亮起一个带有"代码标记"的特殊 Worker。
* 类比：PuddingAssistant 是私人管家，而当需要修房子时，管家从工具箱里掏出一个专业的工程团队。

---

## 二、系统架构

### 2.1 本地环境连接器（Local Bridge）

Agent 必须拥有 **手（执行命令）**、**眼（读取文件/感知屏幕）** 和 **大脑（记忆与反思）**。

**开发目标：** 实现 `LocalActionProvider` 类，基于 `CliWrap` 和 `FileSystemConnector`。

| 能力 | 说明 |
| --- | --- |
| **读写文件** | 不只读代码，还能读取 PDF 菜单、Excel 账单 |
| **执行指令** | `ls`（查看文件）、`curl`（下载数据）、`python script.py` 等 |
| **环境感知** | Agent 能回答"我现在的桌面上有哪些文件？" |

### 2.2 记忆引擎（MemoryHub）

采用 OpenClaw 风格的 **人类可读持久化记忆**，管理 `~/Documents/Pudding/Memory/` 目录。

**分层结构：**

| 层级 | 存储内容 | 示例 |
| --- | --- | --- |
| **底层 — 用户约束** | `Profile.md`，核心偏好 | 张三、海鲜过敏、预算 500 |
| **中层 — 沉淀知识** | 任务中产生的事实 | 2026 年鸡蛋的价格波动 |
| **顶层 — 情感链接** | 长期个人化记忆 | 用户上周因蛋挞太甜心情不好 |

**核心逻辑：**

* **`Profile.md`**：存储核心约束。
* **`Session_Log/`**：按日期存储任务流。
* **Refinement（提炼）**：任务结束时，Leader 自动提取关键信息（如"用户不喜欢甜度太高的蛋挞"）存入长期记忆。

### 2.3 Swarm 编排逻辑

**核心理念：** 基于意图的动态生成（On-demand Spawning）+ 顺序依赖链（Sequential Dependency）。

* Agent 按需生成：处理"海鲜过敏菜谱"时，Leader 实时生成 `Nutritionist_Agent`（营养师）和 `Market_Scanner`（市场调研）。
* 任务结束后临时角色消失，只留下结论和记忆。

**任务流转示例（海鲜过敏菜谱）：**

```
Leader → Researcher（读 Profile.md + 上网查配方）
       → Auditor（比对过敏原，给出修改建议）
       → Reporter（生成最终一周菜谱 Markdown）
```

**Leader 系统指令核心逻辑：**

1. **环境自省**：确认可操作的本地工具。
2. **约束检查**：执行任何建议前必须检查 `Memory/Profile.md`。
3. **任务拆解**：优先并行，严格遵守逻辑先后顺序。

### 2.4 插件化架构

通过环形菜单（Radial Menu）作为入口，插件化实现功能扩展。

**插件接口标准：**

* `IPuddingPlugin` 接口：`PluginIcon`（矢量图或 Lottie）、`OnAction()`（触发逻辑）、`Contribution`（处理能力声明）。
* **动态载入**：MEF 或 `AssemblyLoadContext`，扫描 `Plugins/` 文件夹下的 `.dll`。
* **沙盒隔离**：第三方插件在独立子进程中运行，防止崩溃影响主体。

**四类插件象限：**

| 类别 | 功能 |
| --- | --- |
| **A. 意图执行（Execution）** | 快捷指令库、本地搜索、脚本唤醒 |
| **B. 环境感知（Contextual）** | 屏幕拾取、文本精修、剪贴板管家 |
| **C. 数字管家（Assistant）** | 记忆编辑器、任务看板、行程/提醒 |
| **D. 系统管理（System）** | Swarm 视图切换、Agent 商店、设置与调试 |

---

## 三、交互设计

### 3.1 双态架构

#### 桌面精灵态（Desktop Spirit）— 常驻与伴随

布丁精灵是 **Swarm 集群的人格化出口**，常驻桌面的轻量交互入口。

* **非窗口化存在**：置顶、半透明、异形渲染的动画实体，不带标题栏和关闭按钮。
* **交互行为**：

| 动作 | 交互行为 | 触发后台逻辑 |
| --- | --- | --- |
| **拖入文件** | 精灵张嘴"吞下"文件 | 启动 `FileScanner` + `SummaryWorker` |
| **双击** | 弹出半透明快捷输入框 | 激活意图识别 |
| **右键长按** | 变形为环形菜单（Radial Menu） | 打开 Swarm 配置、管理后台或记忆库 |
| **闲置** | 桌面上走动，吐出气泡 | 提醒待办或报告异步任务进度 |

* **情绪反馈**：思考时变色（抹茶绿→焦糖色），成功时跳动，失败时垂头丧气。
* **唤醒机制**：点击精灵弹出管理窗口。

#### 窗口态（Command Center）— 深度配置

用户需要深度操作时调出，包含：

* **Swarm 视图（指挥部）**：精灵的"大脑解剖图"，可查看 Worker 分裂状态，开发者可拖拽连线干预逻辑流向。
* **Editor（记忆与脚本编辑）**：调整 `MEMORY.md`、修改 System Prompt、编写自动化脚本，风格为"实验室记录本"。

#### 窗口与桌面的联动

* **任务溢出**：Swarm 视图启动任务时，微小"光球"从窗口飞出汇聚到精灵身上。
* **通知气泡**：精灵在桌面吐出对话气泡，点击跳转到窗口查看详细报告。
* **Editor 联动**：编辑 `Profile.md` 时，精灵做出"记笔记"的动作。

### 3.2 界面布局 — 三个核心板块

1. **Swarm 空间（Swarm Space）**：主屏幕，动态展示布丁协作。
2. **成果墙（Output Gallery）**：右侧可展开，显示 Markdown 报表、图表或代码段。
3. **记忆抽屉（Memory Drawer）**：左侧可拉出，以卡片展示用户档案（过敏原、工作习惯、家人生日等）。

### 3.3 进度感知 — 弹幕系统

用弹幕（Bubbles）代替控制台日志，隐藏技术复杂度：

* Worker-1："我去查查鸡蛋多少钱..."
* Worker-2："张三好像不吃香菜，我记下了。"

---

## 四、视觉设计

### 4.1 配色系统 — 布丁美学

| 元素 | PuddingCode（弃用） | PuddingAssistant（启用） |
| --- | --- | --- |
| **配色方案** | 深灰色、代码蓝、警告红 | 奶油黄、抹茶绿、焦糖棕、蜜桃粉 |
| **节点形状** | 矩形框、电路图连线 | 圆润的布丁球、平滑曲线、弹性动画 |
| **交互反馈** | 编译成功的文字提示 | Agent 互相点头、发射能量气泡、弹幕 |
| **核心视图** | 编辑器 + 终端 | Swarm 地图 + 意图输入框 + 成果展示墙 |

**配色细节：**

* **背景**：极浅的奶油色（Creamy White）
* **强调色**：焦糖色（交互）、抹茶绿（成功）、蜜桃粉（提醒）

### 4.2 形态语言

* **圆角与阴影**：所有窗口、按钮、对话框采用大圆角（BorderRadius > 20px）+ 柔和弥散阴影。
* **微动效**：节点思考时"布丁呼吸"缩放效果；数据传输时流体粒子流。

### 4.3 精灵视觉设计

* **形态**：半透明、Q弹流体（Squishy Fluid），像刚脱模的布丁，具有呼吸感起伏和惯性拉伸（Squash & Stretch）。
* **表情**：极简点阵，两个圆点眼睛 + 点阵符号（`^o^` / `-_-` / `>_<`）。
* **透明度**：平时 20% 穿透，鼠标靠近时 90% 不透明 + 弹跳。
* **五种基本状态**：Idle、Thinking、Success、Error、Sleeping。

### 4.4 Agent 角色化

| 角色 | 视觉形象 | 负责领域 |
| --- | --- | --- |
| **Leader（总管）** | 戴小礼帽的焦糖布丁 | 接收指令、拆解任务、最终汇总 |
| **Scholar（学者）** | 戴圆眼镜的蓝莓布丁 | 查资料、读文件、知识检索 |
| **Executor（能手）** | 系围裙的草莓布丁 | 运行脚本、操作本地文件、改写内容 |
| **Keeper（记忆员）** | 抱着笔记本的抹茶布丁 | 维护 `Profile.md`，记录用户喜好 |

---

## 五、技术实现（Avalonia）

### 5.1 核心技术方案

| 视觉元素 | 技术路径 |
| --- | --- |
| **异形窗口** | `TransparencyLevelHint="Transparent"` + 取消 `HasSystemDecorations` |
| **置顶精灵** | `Topmost="True"` + MouseHitTest 处理鼠标穿透 |
| **动画系统** | Lottie（Avalonia.Lottie）动态表情 |
| **流体渲染** | SkiaSharp + 贝塞尔曲线动态计算（非预渲染 GIF） |
| **Swarm Canvas** | 自定义 `Control`，力导向布局（Force-directed graph） |
| **毛玻璃效果** | GlassMorphism，背景模糊增加层次感 |

### 5.2 渲染引擎

* **SkiaSharp + Avalonia Composition API**：高性能绘图，流体效果通过贝塞尔曲线实时计算。
* **跨平台异形窗口**：
  * Windows：`DwmExtendFrameIntoClientArea`
  * macOS：`NSWindow.BackgroundColor = NSColor.Clear`

### 5.3 逻辑调度层（Swarm 桥接）

* **ReactiveUI**：精灵状态与 Swarm Leader 状态强绑定。
* **SignalBus（消息总线）**：Worker 发现关键结论时推送 `ExpressionEvent`，驱动精灵变色并弹出气泡。

### 5.4 自定义控件

* 放弃标准 `Button` / `ListBox`，为布丁节点编写专用 `UserControl`。
* 环形菜单动画：右键点击时四周弹出 4-8 个气泡图标 + 震动反馈。
* 插件进度环 + 精灵眼睛跟随鼠标方向。

---

## 六、开发里程碑（MVP 路径）

### 阶段一：透明精灵壳

* Avalonia 实现完全透明、无边框、置顶窗口。
* 渲染静态布丁图标，实现鼠标拖动。

### 阶段二：动作与状态机

* 引入 Lottie 动画，根据 Swarm 状态（Idle / Thinking / Success / Error / Sleeping）切换表情。

### 阶段三：意图与气泡

* 实现异形气泡框（Chat Bubble），展示 Worker 汇总结果。
* 实现第一个"种子插件"：意图识别输入框（右键 → 输入 → 通过 Swarm 发送到 LLM）。

### 阶段四：核心后端

* 实现 `LocalActionProvider`（文件读写 + 命令执行 + 环境感知）。
* 实现 `MemoryHub`（Profile.md + Session_Log + Refinement）。
* 重写 Leader 系统指令（全能布丁管家）。

### 阶段五：管理窗口对接

* 开发主管理界面（Swarm 视图 + Editor + 成果墙 + 记忆抽屉）。
* 通过 `SwarmManager` 共享状态，实现精灵与 Swarm 视图同步。

### 阶段六：插件化

* 定义 `IPuddingPlugin` 接口协议。
* 实现 MEF / `AssemblyLoadContext` 动态载入。
* 实现沙盒隔离机制。

---

## 附录：场景演练 — 海鲜过敏菜谱任务

1. **触发**：用户对桌面精灵输入"我想吃布丁，但我海鲜过敏。"
2. **感知**：精灵变蓝（思考态），产生旋转光环。
3. **后台编排**：Leader 从 Memory 抓取 Profile.md → 派发 Researcher 查配方 → Auditor 比对过敏原 → Reporter 生成菜谱。
4. **反馈**：精灵跳一下，递出 Markdown 卡片："放心吃吧，这是我为你重构的椰奶布丁配方！"
