## 一、 系统架构设计 (Architecture)

PuddingAssistant 采用**“大脑-中枢-工具”**的三层解耦架构，核心由 C# (.NET 9) 驱动。

### 1. 核心分层

* **交互层 (The Shell/Face):** 基于 `Spectre.Console` 实现。处理 TUI 渲染、实时思维流展示和用户输入。
* **中枢层 (The Orchestrator/Brain):** 基于 `Semantic Kernel` 或自定义 Agent 框架。负责任务拆解（Planning）、工具调用决策（Tool Calling）和长短期记忆管理。
* **执行层 (The Hands/Senses):**
* **LSP Client:** 对接各语言的 Language Server（Roslyn, Pyright 等）。
* **File/Git Manager:** 处理原子化的文件修改和 Git 快照。
* **Sandbox Shell:** 在受限环境中执行编译和测试命令。



---

## 二、 核心功能模块详细设计

### 1. 任务规划器 (Pudding Planner)

* **逻辑：** 借鉴 OpenCode，将任务拆分为 `Analysis` -> `Planning` -> `Execution` -> `Verification` 四个阶段。
* **特性：** 采用 **Recursive Thinking**。如果子任务执行失败，Planner 会自动重新生成计划。

### 2. LSP 语义感知器 (Sense Tooling)

* **功能：** 不再让 AI 盲目读全文件。提供 `get_symbol_definitions`、`find_references` 等接口。
* **优势：** 极大地节省 Token，并提高 AI 在大型项目中的重构准确度。

### 3. 自我修复环 (Self-Healing Loop)

* **流程：** 1. Agent 修改代码。
2. 自动触发 `dotnet build` 或 `pytest`。
3. 捕获控制台输出（stdout/stderr）。
4. 将错误日志反馈给 Opus 4.6，进入下一次修复。

### 4. 视觉反馈系统 (Rich TUI)

* **实时 Diff:** 借鉴 Crush，在终端用侧边栏或浮窗显示代码改动的对比。
* **思维流 (Thought Stream):** 用淡紫色或橙色的小号字体实时滚动显示 AI 的思考过程，增加透明度。

---

## 三、 路线图 (Roadmap 2026)

我们将开发分为四个阶段，从“能对话的脚本”进化为“全自动工程师”。

### **阶段 1：奠基 (Foundation) - 第 1-2 周**

* **核心目标：** 打通 C# CLI 与 Claude Opus 4.6 的连接。
* **关键任务：**
* 使用 `Spectre.Console` 搭建 CLI 基础框架。
* 实现基础的 **Tool Calling**（读写文件、运行简单的 Shell 命令）。
* 建立简单的 `CLAUDE.md` 项目指令系统，让 Agent 了解当前项目规范。



### **阶段 2：感知 (Sensing) - 第 3-5 周**

* **核心目标：** 让 Agent 拥有“眼睛”，能理解代码架构。
* **关键任务：**
* 集成 **LSP Proxy**。实现 C# 和 Python 的语义查询工具。
* 引入 **Local Vector Index**。对本地代码库进行向量化，实现相关代码片段的精准检索。
* 实现 **Git Snapshot** 功能，确保每次改动前都有“后悔药”。



### **阶段 3：自编程 (Self-Programming) - 第 6-9 周**

* **核心目标：** 实现完整的“计划-执行-验证”闭环。
* **关键任务：**
* 开发 **Verification Engine**。自动识别项目类型并运行对应的测试框架。
* 实现 **Recursive Bug-fixing**。Agent 能够根据错误日志连续进行 5 次以上的自主尝试。
* 加入 **Token 剪枝算法**。自动压缩长对话上下文。



### **阶段 4：蜂群与美化 (Swarm & Polishing) - 第 10 周+**

* **核心目标：** 极致的协作与 UX 体验。
* **关键任务：**
* 实现 **Swarm Mode**。主 Agent 调度多个子 Agent 并行处理前后端任务。
* 极致 TUI 美化：加入平滑的加载动画、任务卡片和“布丁感”配色方案。
* 发布 **PuddingAssistant Hub**。支持用户分享自己调教好的“布丁技能（Skills）”。



---

## 四、 核心技术栈清单

| 类别 | 推荐选型 | 备注 |
| --- | --- | --- |
| **开发语言** | C# (.NET 9) | 利用原生 AOT 提高启动速度 |
| **终端框架** | `Spectre.Console` | 实现类似 Crush 的美学 |
| **AI SDK** | `Semantic Kernel` | 强大的 Tool Calling 管理能力 |
| **本地索引** | `Microsoft.Extensions.VectorData` | 处理代码 RAG |
| **多模型路由** | `LiteLLM` (或自定义 C# 实现) | 支持 Claude, DeepSeek 等切换 |

---

## 五 、开源

1. 视觉与交互层：UI/UX (复刻 Crush 的美感)
https://github.com/spectreconsole/spectre.console
用途： 实现进度条、表格、富文本面板、动态渲染。它内置的 Status 组件可以完美模拟 Agent 思考时的“气泡感”。

Terminal.Gui:
https://github.com/gui-cs/Terminal.Gui
用途： 如果你想在终端里做一个完整的仪表盘（比如侧边栏显示文件树），这个库比 Spectre 更适合复杂的交互式 TUI。



2.智能体大脑：Agent Framework (实现 OpenCode 的逻辑)
https://github.com/microsoft/semantic-kernel
负责处理 Claude Opus 4.6 和其他模型的 API 调用、工具选择和任务编排。

3. 感知与工具：LSP & Code Analysis (AI 的五感)

https://github.com/OmniSharp/csharp-language-server-protocol

让 Agent “看懂”代码，而不只是读字符串。
用途： 既然你要做 LSP Proxy，这个库帮你处理了所有底层的 JSON-RPC 通讯。你只需要写 Handler，就能让你的 CLI 具备“跳转定义”的能力。

https://github.com/tree-sitter/tree-sitter
用途： 高性能语法解析。如果你想让 Agent 识别 Python、TS 或 SQL 的函数边界，Tree-sitter 是目前最通用的方案。

https://github.com/dotnet/roslyn
用途： 针对 C# 的深度手术刀。如果你想让 PuddingAssistant 完美重构 C# 代码，Roslyn 是必选项。


4. 记忆与检索：Vector & RAG ，管理项目上下文，减少 Token 浪费。


Microsoft.Extensions.VectorData:

用途： 2025 年新出的标准抽象库。它可以让你无缝切换本地的向量数据库（如 SQLite-VSS, Qdrant, 或 FAISS）。


https://github.com/ssone95/ChromaDB.Client
用途： 如果你想在本地跑一个轻量级的向量库来存储代码索引，Chroma 是非常友好的选择。


5. 执行与安全：Sandboxing (防止 Agent 删库)

https://github.com/dotnet/Docker.DotNet
用途： 在 CLI 中动态启动 Docker 容器。你可以让 Agent 的 ExecuteCommand 永远运行在容器内，与宿主机隔离。

https://github.com/bytecodealliance/wasmtime-dotnet

用途： 如果你想追求极致的轻量，可以在 WebAssembly 沙盒里运行 Agent 生成的代码脚本。

