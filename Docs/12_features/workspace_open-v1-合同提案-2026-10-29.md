---
title: workspace_open v1 合同提案（含裁定）
author: hyfree
date: 2026-10-29
last_reviewed: 2026-10-29
status: draft
---

# workspace_open v1 合同提案

## 1 背景与现状

三个服务已抽出并各自有特征测试：`ICodeOutlineService`（outline）、`ICodeSummaryService`（summary）、
`ICodeProjectMapService`（map）。`IRetrievalPort` 只承担 `workspace_search` 的检索面。

设计文档 `Docs/12_features/增强索引统一检索与代码地图设计方案-2026-10-18.md` §8.3 给出 `workspace_open`
的规格：请求 `ref` 或 `{project_id,path}` 二选一，`view=excerpt|outline|summary|relations|map|status`。

Codex 的验收意见（2026-10-29）：当前形状不足以支撑 `workspace_open`，应先等三个服务的边界稳定。
现在三个服务已齐，可以开始，但要避免把工具层的形状带进合同。

## 2 本提案要解决的一个真问题：`excerpt` 的语义

两种解读：

- **A 文件切片**：`path` + `lines` → 返回该文件的行范围。
- **B 符号上下文**：`symbol_ref` → 返回该符号的声明体（± 上下文行）。

**v1 只做 A。** B 需要 `ref` 体系（身份 + 版本 + 授权 + `stale_ref`），属后续。

## 3 合同形状（v1，已按 §5 裁定修正）

```csharp
public enum WorkspaceOpenView { Excerpt = 0, Outline = 1 }

public sealed record WorkspaceOpenRequest(
    string WorkspaceId,
    string? ProjectId,          // 与 Path 至少给一个
    string? Path,               // 相对工作目录或绝对路径
    WorkspaceOpenView View,
    int? StartLine,             // Excerpt 专用；1-based
    int? LineCount,             // Excerpt 专用；实现负责夹取
    int? Depth,                 // Outline 专用（复用 project_map 的 1..3 语义）
    int MaxBytes);              // 返回内容总字节上限，超出即截断并声明

public abstract record WorkspaceOpenPayload;

public sealed record ExcerptPayload(
    IReadOnlyList<string> Lines,
    int StartLine,
    int TotalLineCount) : WorkspaceOpenPayload;

public sealed record OutlinePayload(
    IReadOnlyList<OutlineNode> Nodes,   // PuddingCodeIntelligence.Contracts.OutlineNode
    int TotalCount,
    bool Truncated) : WorkspaceOpenPayload;

public sealed record WorkspaceOpenResult(
    bool Success,
    string? Error,
    WorkspaceOpenView View,
    string? ResolvedPath,
    WorkspaceOpenPayload? Payload,
    bool Truncated,
    string? TruncatedReason);
```

## 4 关键取舍（每条都写理由）

1. **结果里带 `TotalLineCount` 而不是只给"截断布尔"**：只知道"被截断"没法让调用方决定下一步看哪里。
2. **`OutlinePayload` 是投影，不是 `CodeOutlineOutcome` 本身**：见 §5 Q1。
3. **`MaxBytes` 进请求而不是配置**：调用方的上下文预算各不相同。
4. **失败一律 `Success=false` + `Error`**，不用异常：与既有工具契约一致。
5. **不提供 `relations`/`map`/`status`**：v1 显式不支持（`Success=false` + 替代方案）。

## 5 三个问题的裁定

> **Codex 本次未能跑成的记录（2026-10-29 21:25）**：两次尝试失败 ——
> ① 它默认走 MCP `codex_apps/github.fetch_file` 拉仓库文件（失败），而不是读本地；
> ② 显式禁用 MCP 后能读本地了，但十分钟未出答案，已 cancel。
> 以下结论由作者自结，后续可用 Codex 复核。

**Q1 — 不把 `CodeOutlineOutcome` 整个嵌进结果。**
那会把 outline 的 `Origin` / 两种节点列表 / `HasSyntaxErrors` 一起绑进 open 的契约面，
服务改一个字段就要动 open。做法：在 open 契约里只放**投影** `OutlinePayload`（`Nodes`/`TotalCount`/`Truncated`），
由实现映射；另加一条契约测试断言「投影字段与服务结果对应字段逐个相等」——
**解耦形状，不解耦数值来源**。

**Q2 — 必须更严，且加在实现层。**
`code_outline` 是「在自己机器上看看」，`workspace_open` 属于检索面、scope 就是 workspace。
路径校验放**实现层**（解析出 workspace 根后验证 `Path` 在其子树内），失败返回 `Success=false` + 明确措辞；
契约只表达意图（`Path`），不承载策略——将来改策略不动契约。

**Q3 — `MaxBytes` = 返回内容的总字节。**
调用方的约束是上下文预算（一坨内容），不是单行宽度。按行截断是展示关心的事，可留在工具层，
但不能写成契约含义，否则调用方无法预估开销。

**最可能逼着改契约的一件事**：加 `summary` / `map` 时，结果会被迫长成「什么视图都塞」的联合体。
**最便宜的预防**：第一天就把负载做成判别容器（`WorkspaceOpenPayload` + 各视图子类），
以后加视图只是加子类，**结果外形不变**。§3 已按此定型。

**另一条教训**：节点类型直接用 `PuddingCodeIntelligence.Contracts.OutlineNode`，不重定义——
仓库里已经有过「两个同名 OutlineNode」的教训。

## 6 不在本提案范围

- `ref` 体系（身份/版本/授权）与 `stale_ref` 语义
- 工具外壳与 DI 接线（S5）
- `summary`/`map` 视图（服务已就绪，等这段合同稳定后逐个加）
