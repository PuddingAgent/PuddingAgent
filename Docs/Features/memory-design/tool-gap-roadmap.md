# Pudding 工具缺口路线图

> 最后更新: 2026-07-12
> 上下文: 长效学习系统 5 管道全部建成，file_patch P0-P2 完成，现在填补工具缺口

---

## 总览

| 优先级 | 工具 | 状态 | 说明 |
|:--:|------|:--:|------|
| P0 | `subconscious_trigger` | 🔴 实施中 | 手动触发 4 条潜意识管道 |
| P0 | TS/JS 语义分析 | ⬜ | code_symbol_search 等仅 C# |
| P1 | git 工具集 | ⬜ | status/diff/log/blame 专用工具 |
| P1 | `diff_compare` | ⬜ | 两个文件/片段并排对比 |
| P2 | 浏览器工具 | ⬜ | JS 渲染页面抓取 |
| P2 | MCP 客户端 | ⬜ | 动态工具加载 |
| P3 | 图片/PDF 分析 | ⬜ | OCR/视觉 |
| P3 | `db_query` | ⬜ | 数据库只读查询 |
| P3 | `config_view` | ⬜ | 运行时配置查看 |

---

## P0: subconscious_trigger

### 设计
- 工具签名: `subconscious_trigger(action="auto_dream", workspace_id="default")`
- Actions: auto_dream / extract_patterns / improve_skills / consolidate / all
- 与定时器共享 ISubconsciousOrchestrator 入口
- 日志标记 [SubconsciousTrigger] 区分自动触发

### 改动
- SubconsciousTriggerTool.cs (新建)
- Program.cs (+注册)

### 文件
- `Source/PuddingRuntime/Tools/BuiltIns/SubconsciousTriggerTool.cs`

---

## P0: TS/JS 语义分析

### 问题
code_symbol_search / code_explore / code_callers / code_callees / code_impact / code_summary
全部依赖 Roslyn，仅支持 C#。PuddingWeb 等前端项目完全无法使用。

### 方向
- tree-sitter 解析 TypeScript/JavaScript
- 或 TypeScript Compiler API
- 与现有 code_index 框架集成

---

## 已完成的基础设施

```
✅ Pre-Compaction Flush      ← 压缩前抢救事实
✅ Background Extractor       ← 会话后搬运事实
✅ Auto-Dream                 ← 定期整理（每6h）
✅ 管道2：经验→SKILL          ← 黄金路径→技能（每12h）
✅ Skill Self-Improvement     ← 技能自进化（每4h）
✅ file_patch P0-P2           ← 歧义检测+行级匹配+"Did you mean?"
✅ HttpClient 超时对齐         ← 120s→300s
✅ TokenCostService           ← 成本追踪+3 REST端点
✅ Git 模型 agent_skill       ← push/pull/diff
✅ Local SKILL Hub            ← skill_hub 管理
```

## 工作流改进

```
✅ P0-1: 技能自动路由          ← 场景匹配 → 自动加载 SKILL
✅ P0-2: 子代理派发前自检       ← 查 SKILL + 经验 + 历史教训
✅ P1:   指令级联              ← AGENTS.md → PINNED
✅ P2-1: 两阶段审查协议        ← spec + quality
✅ P2-2: Continuous Execution  ← 不中断
```

## 竞品参考

- Reasonix: Cache-TTL-aware maintenance (#3968)
- Claude Code: /compact + Hooks 双重路径
- LangGraph: interrupt/checkpoint
- 发现: >20K stars 框架无一提供独立管道触发工具 — Pudding 首创
