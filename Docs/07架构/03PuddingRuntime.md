# Agent Runtime（进程内模块）

> **2026-05-02 简化**：Runtime 不再是独立进程，而是 Pudding Agent 内的一个模块。1 进程 = 1 Agent = 1 Runtime 实例。
> **2026-05-03 补充**：参考 Claude Code QueryEngine 的 12 步状态机主循环设计。

## 定位

Runtime 模块负责 Agent 的实际执行：LLM 对话、工具调用、记忆管理、Skill 装配。

## 核心设计原则：Harness 故意做傻

借鉴 Claude Code 的核心哲学：**LLM = Brain, Harness = Body**。Runtime 是"故意做傻"的执行器——所有智能决策在 LLM 端，Runtime 只负责：

- 感知（接收用户输入、工具结果）
- 执行（调用工具、管理状态）
- 记忆（存储/召回上下文）
- 约束（权限检查、Token 预算）

Runtime 自身不包含业务逻辑判断，"这个工具该不该调"由 LLM 决策，Runtime 只负责"能不能调"的安全约束。

## 主循环：while(true) + 工具调用

参考 Claude Code QueryEngine 的 12 步 pipeline（简化版）：

```
User Input
  → ① 输入预处理（斜杠命令、附件解析）
  → ② 上下文装配（系统提示词 + 记忆 + 工具定义）
  → ③ API 调用（发送给 LLM）
  → ④ 流式响应接收
  → ⑤ 工具调用提取（从 LLM 响应中解析 function_call）
  → ⑥ 权限检查（多层防御，参考 EP07）
  → ⑦ 工具执行（进程内直接执行）
  → ⑧ 结果注入（工具结果追加到对话上下文）
  → ⑨ 继续/结束判断
  → 循环回到 ②
```

关键点：
- 这是一个 `while(true)` 循环，不是请求-响应
- 每个工具调用都是一次完整的 API roundtrip
- "思考→行动→观察→思考"的循环由 Runtime 驱动
- 停止信号（用户取消/Token 超限/错误）由 Controller 模块下发

## 核心能力

- LLM 对话循环（多轮会话、工具调用）
- 记忆引擎（会话记忆、长期记忆召回与写回）
- Skill/MCP 装配与执行
- 工具进程内直接执行

## 数据存储

所有数据存储在进程本地的 SQLite 数据库中：
- 会话历史
- 记忆条目
- 配置与偏好
- 审计日志

不依赖外部数据库服务。