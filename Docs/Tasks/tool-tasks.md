# PuddingAgent Tool 任务卡

参考 Claude Code 42 Tools 架构，为 PuddingAgent 建立以下 Tool 任务卡。

---

## 已有 Tool（保留/增强）

| Tool | 状态 | 优先级 |
|------|------|--------|
| `search_memory` | ✅ 已有 | — |
| `bash` (BashSkill) | ✅ 已有 | — |
| `read_file` (ReadFileSkill) | ✅ 已有 | — |
| `write_file` (WriteFileSkill) | ✅ 已有 | — |
| `terminal_execute` (TerminalSkill) | ✅ 已有 | — |

---

## 新增 Tool 任务卡

### T1: web_search — 网络搜索
- **描述**: 给 Agent 联网搜索能力，调用外部搜索引擎 API
- **类别**: 外部集成
- **优先级**: P0
- **依赖**: Mimo API 支持 web_search（或自建 DuckDuckGo/Bing API）
- **验收**: Agent 说"搜索 X"→ 返回搜索结果摘要

### T2: web_fetch — 网页抓取
- **描述**: 读取 URL 内容并提取纯文本/Markdown
- **类别**: 外部集成
- **优先级**: P1
- **依赖**: HttpClient + HTML 解析
- **验收**: Agent 说"读取 https://example.com"→ 返回页面文本

### T3: task_create — 创建任务
- **描述**: Agent 主动创建待办任务（调用 Todo API）
- **类别**: 工作流与计划
- **优先级**: P1
- **依赖**: Todo API 连通
- **验收**: Agent 说"帮我记住要做 X"→ 创建任务卡片

### T4: save_memory — 主动记忆
- **描述**: Agent 主动将信息写入 MemoryFacts + MemoryLibrary
- **类别**: 记忆系统
- **优先级**: P0
- **验收**: Agent 说"记住 X"→ MemoryFacts 新增记录

### T5: think — 深度思考
- **描述**: 强制 LLM 进行慢思考（不调用其他工具），返回推理过程
- **类别**: 工作流与计划
- **优先级**: P2
- **验收**: Agent 调用 think 工具 → 返回推理链文本

### T6: skill_list — 技能列表
- **描述**: 列出当前可用的所有 Skills/Tools
- **类别**: 元认知
- **优先级**: P2
- **验收**: Agent 调用 → 返回工具清单

### T7: session_summary — 会话摘要
- **描述**: 生成当前会话的结构化摘要并存入 MemoryLibrary
- **类别**: 记忆系统
- **优先级**: P1
- **验收**: Agent 在会话中调用 → 生成摘要存入 Library

### T8: file_glob — 文件名搜索
- **描述**: 按通配符模式搜索文件名（如 "**/*.cs"）
- **类别**: 文件操作
- **优先级**: P1
- **验收**: Agent 调用 glob("*.cs")→ 返回匹配文件列表

### T9: file_grep — 文本搜索
- **描述**: 在项目中按正则/关键词搜索文本
- **类别**: 文件操作
- **优先级**: P1
- **验收**: Agent 调用 grep("class Foo")→ 返回匹配行

### T10: todo_list — 查询任务列表
- **描述**: 查询 Todo 系统中的任务
- **类别**: 工作流与计划
- **优先级**: P2
- **依赖**: Todo API
- **验收**: Agent 调用 → 返回任务列表

---

## 实施计划

**Phase 1 (本周)**: T1 web_search + T4 save_memory（P0，最高价值）
**Phase 2 (下周)**: T2 web_fetch + T8 file_glob + T9 file_grep
**Phase 3 (远期)**: T3 task_create + T5 think + T7 session_summary + T10 todo_list

---

## Tool 架构规范

每个 Tool 必须：
1. 实现 `ITool` 接口（name + description + parameters + ExecuteAsync）
2. 同时实现 `IAgentSkill`（兼容现有 SkillRuntime）
3. 在 `Program.cs` 中注册为 Singleton + IAgentSkill
4. 在 Agent Template 的 AllowedToolNames 中声明
5. 通过 `IStreamingEventBus` 发射 tool_call/tool_result 事件
