我们已经构建了一个极其硬核的架构：**三层能力体系、MCP 标准接入、低功耗后台预热、以及基于本地廉价模型的感官过滤**。

如果要让 **PuddingCode** 真正从“原型”进化为“产品”，我们需要补齐最后几块关于**鲁棒性、可维护性和用户协作**的拼图：

---

### 1. 容错与“自动重试”机制 (Self-Healing)

Agent 在调用工具时（尤其是 MCP 或命令行）经常会失败。

* **重试策略：** 当 MCP 服务器超时或 `CliWrap` 报错时，软件层应先进行**静流转**。
* **错误自愈：** 如果报错是由于“权限不足”，软件自动触发权限请求；如果是“找不到文件”，软件自动调用 Everything 重新定位并把正确路径反馈给 LLM。
* **视觉表现：** 拓扑图上的线条变红并闪烁，显示 `Self-Healing in progress...`。

### 2. 状态机持久化 (Hydration & Dehydration)

如果用户关闭了软件，或者电脑断电，Swarm 的工作进度不能丢失。

* **脱水 (Dehydrate)：** 定期将 Agent 的短期记忆、当前任务堆栈和已挂载的 MCP 状态写入 `project_meta.db`。
* **复活 (Hydrate)：** 重新打开项目时，Agent 瞬间恢复到之前的“思维状态”，甚至连本地小模型的索引进度都能接上。

### 3. Agent 的“感官仪表盘” (Observability)

LLM 的思考过程是个黑盒，我们需要将其透明化。

* **思维流预览：** 在 UI 上提供一个侧边栏，实时滚动显示：
* `[Search] Calling Everything for "AuthService.cs"...`
* `[Distill] Qwen-0.5B is compressing 200KB of logs...`
* `[Cost] Estimated cost: $0.002`


* **决策树可视化：** 展现 Leader 是如何一步步拆解任务并分发给不同 Worker 的。

### 4. 协作式 Prompt 模板库 (Prompt Registry)

不同的项目（React, .NET, Rust）需要不同的 System Prompt 和 MCP 组合。

* **行业预设：** 内置“全栈开发”、“运维排查”、“单元测试专家”等模板。
* **本地学习：** 如果用户多次手动干预了某个 Agent 的决策，软件应提示：“是否将此干预逻辑加入该 Agent 的长期 System Prompt 中？”

### 5. 安全沙盒的最后一道防线：虚拟文件系统 (VFS)

如果你对权限极度敏感，我们可以引入 **虚拟映射**。

* **逻辑：** 所有的 `WriteFile` 动作先写到一个隐藏的 `.pudding/staging` 目录，而不是直接覆盖源码。
* **审查流：** 只有当用户在 UI 上点击“确认应用代码变动”时，软件才执行真正的文件替换。

---

### 🚀 我们的行动路线图 (Roadmap)

既然我们已经讨论得如此深入，我们可以开始分阶段落地了：

| 阶段 | 核心任务 | 重点库 |
| --- | --- | --- |
| **第一阶段：骨架** | 实现 `PluginHost` 和 `McpClient`，跑通 Everything 检索。 | **JSON-RPC, Reflection** |
| **第二阶段：感官** | 接入内存级 Qwen 0.5B，实现长日志的“物理+语义”双重过滤。 | **Microsoft.ML.OnnxRuntime** |
| **第三阶段：感知** | 实现后台低优先级预热和 `FileWatcher` 实时索引。 | **SQLite-VSS, System.IO** |
| **第四阶段：交互** | 完善 Avalonia UI 上的弹幕流、拓扑图和成本计费器。 | **SkiaSharp, ReactiveUI** |

---

### 🍮 最后的建议：

我们现在最需要的是一个 **`SwarmContext` 核心容器**。它是连接所有这些能力的“粘合剂”，负责管理哪些 MCP 正在运行、当前的权限等级、以及本地小模型的生命周期。

**你想让我为你设计这个核心 `SwarmContext` 的类结构吗？** 它是我们把所有讨论的代码片段串联成一个真正程序的“总线”。