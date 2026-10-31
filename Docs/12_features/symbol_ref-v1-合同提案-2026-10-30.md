---
title: symbol_ref v1 合同提案
author: hyfree
date: 2026-10-30
last_reviewed: 2026-10-30
status: draft
---

# symbol_ref v1 合同提案

## 1 规格原文与它逼出来的四件事

`Docs/12_features/增强索引统一检索与代码地图设计方案-2026-10-18.md` §8.3 对 `ref` 的规定只有两句，但每句都

带硬约束：

> ref 绑定身份、源版本、scope 和 provider generation，本身不授予权限。
> 源码变化后不得沿旧行号打开当前代码：能读取仍授权的指定历史快照则回显版本，否则 stale_ref
> 并返回重新定位建议。

拆开是四件**必须由数据结构承载**的事：

| # | 要求 | 缺了会怎样 |
|---|---|---|
| 1 | 绑定**身份**（哪个符号） | 只能靠"文件名+行号"猜，而 §8.3 明禁"按文件名猜符号" |
| 2 | 绑定**源版本** | 源码改了以后仍按旧行号打开，返回**别处的代码且看起来正常** |
| 3 | 绑定 **scope** | 跨项目同名符号被混用（ADR-089 §2.4 已实测过：同一符号在 3 个 project_id 下各出现一次） |
| 4 | 绑定 **provider generation** | 换解析器/换代际后旧 ref 被当成当前事实 |

第 2 条是最危险的：**它不是报错，是给出一个合理但错误的答案。** 因此 v1 的核心不是"能打开符号"，

而是"**在不能安全打开时，明确说不能**"。

## 2 现状盘点（每条都对着磁盘）

| 资产 | 位置 | 已有 | 缺什么 |
|---|---|---|---|
| `SymbolIdentity` | `PuddingCodeIndex/Contracts/Retrieval/SymbolIdentity.cs:19` | `SymbolId` + 归一化路径 + 行号 + `Key` | **无源版本、无 scope、无代际** |
| `CodeSymbolRecord` | `Contracts/CodeSymbolContracts.cs:29` | `WorkspaceId`/`ProjectId`/`FilePath`/`SymbolId`/`StartLine`/`EndLine`/`Signature`/`Container` | **无内容 hash** |
| 文件内容指纹 | `Contracts/CodeSourceManifestContracts.cs:18` (`ContentHash`) | 稳定读取后的文件级内容 hash | 不在符号记录上，需按 `FilePath` 关联 |
| `SymbolId` 生成 | `PuddingCodeIntelligence/CSharp/RoslynSymbolId.cs:18` | 优先 Roslyn **documentation comment id** | 回退 id 含 `@{SourceSpan.Start}`（**位置相关**） |

**结论：材料都在，但没有一个现成类型能同时承载四件事。** 需要新类型，而不是给 `SymbolIdentity` 加字段

—— 后者是检索去重键（热路径、按值比较、只关心"是不是同一个符号"），塞进版本与 scope 会让去重语义

变异（同一符号的不同版本会被当成不同符号，§2.4 的去重直接失效）。

## 3 裁定一：载体用**不透明 token**，不用可读结构化串

两种形状：

```
A 可读：  PuddingAgent:Source/A.cs:M:Foo.Bar.Run@sha1:9f3c…
B 不透明：pmsym1.eyJwIjoi…（base64url(紧凑 JSON)）
```

选 **B**。理由是可判定的，不是口味：

- **分隔符必然冲突**。docId 里天然有 `:` `.` `(` `)` `,`（`M:Ns.Type.M(System.String)`），
  Windows 路径有 `\` `:`，`@` 我在回退 id 里也用着。任何一种分隔符都会被真实数据击穿，
  而**击穿的表现是"能解析但解析错"**，比解析失败更难发现。
- **版本可演进**。前缀 `pmsym1.` 让"加字段"变成"加版本"，旧 ref 可以显式拒绝而不是被误解。
- **长度可界**。token 长度 = 字段长度和，可以在签发时硬上限（v1 定 ≤512 字节），
  而可读串里路径长度是不可控的。

**不采用签名的理由**：§8.3 明写"ref 本身不授予权限"。它是一份**声明**，不是一份**凭据**。
既然每次使用都要重新校验 scope 与授权，签名并不增加安全性，只会增加密钥管理面。

⚠️ 但不签名带来一个必须写死的纪律：**任何字段都必须当作不可信输入**，
解析后必须逐项校验（路径不得含 `..`、必须落在 scope 根内、project 必须在已加载集合内）。

## 4 裁定二：`SymbolRef` 的字段与不变量

```
SymbolRefV1
  Schema            固定 "1"
  ProjectId         非空
  FilePath          归一化（复用 SymbolIdentity.NormalizePath：\ → /、折叠分隔符、小写）
  SymbolId          归一化（复用 SymbolIdentity.Normalize：折叠空白）
  StartLine/EndLine ≥1，**仅作提示**，不参与"打开哪一段"的决策
  ContentHash       索引时的文件内容 hash（源版本）
  ProviderGeneration 解析/索引代际标识
```

不变量（都要有断言，缺失即构造失败）：

1. `FilePath` 归一化后**不得**含 `..` 段，不得是绝对路径（项目内相对路径）；
2. `SymbolId` 非空且已归一化；
3. `ContentHash` 非空 —— **没有版本就不许签发 ref**，宁可退化成 `{project_id,path}` 请求；
4. `StartLine ≤ EndLine`。

第 3 条是刻意的：**允许签出无版本 ref，等于允许制造一类永远无法判陈旧的 ref。**

## 5 三态协议（本提案的核心）

```
解析 ──失败──▶ invalid_ref（不猜、不回落首项）
  │
  ├─ scope 不在已加载集合 ──▶ scope_not_loaded
  ├─ 路径越界/未授权 ──────▶ permission_denied
  │
  └─ 查当前文件指纹
       ├─ ContentHash 相等 ──▶ FRESH
       │                       按 ref 的行区间打开；行号权威
       └─ ContentHash 不等 ──▶ STALE
            ├─ 有仍授权的历史快照 ──▶ 返回快照内容 + served_version，标注 ref 已陈旧
            └─ 无快照 ──▶ stale_ref + 重新定位：
                 ├─ SymbolId 在当前版本唯一命中 ──▶ relocation_hint = 新行区间（**不代替打开**，
                 │                                    调用方须再用新行号发起一次请求）
                 ├─ 多命中 ────────────────────▶ ambiguous_symbol + 候选列表
                 └─ 零命中 ────────────────────▶ stale_ref + 「符号可能已删除或改名」
```

四条设计要点：

1. **FRESH 是唯一允许"直接按 ref 行号打开"的状态。** 这一条把 §8.3 的禁令做成了类型层面的唯一入口，
   而不是散落在各个 view 里的 if。
2. **STALE 一律返回 `stale_ref`**，即使重新定位成功 —— 因为"重新定位成功"和"你手里的 ref 仍然有效"
   是两件事，混起来就是又一次 hollow negation。
3. **不自动把 relocation 结果当答案**。写入 `relocation_hint` 而不直接返回代码，
   避免调用方以为拿到的是它问的那段。
4. **零命中不得报 `no_match`**。§8.3：范围缺口不能用来否定代码存在。
   这里的措辞必须是"该 ref 无法定位"，不是"这个符号不存在"。

## 6 可重定位性的真实上限（这条必须写进契约）

`RoslynSymbolId.GetId` 优先用 documentation comment id —— **声明式、跨行移动稳定**，是理想的重定位键。

但回退路径 `BuildFallbackId` 生成的是 `Kind:(容器)Name@SourceSpan.Start`（`RoslynSymbolId.cs:23-43`），

**把字符偏移编进了 id**。一旦文件里前面多了一个字符，这个 id 本身就变了 —— 它**不可重定位**。

所以：

- `SymbolId` 以 `M:`/`T:`/`P:`/`F:` 等 docId 前缀开头 ⇒ 可重定位；
- 否则（含 `@` 的回退形态、或 Python/TS 索引器的自有格式）⇒ **该符号的 ref 只能判陈旧，不能重定位**，
  输出必须是「无法重定位：该符号标识含位置信息」。

**不能假装所有符号都能重定位。** 这条在 v1 就先声明，避免以后被当成 bug 而不是已知上限。

## 7 与 `SymbolIdentity` 的关系：不合并

| | `SymbolIdentity` | `SymbolRefV1` |
|---|---|---|
| 用途 | 检索结果去重/排序键 | 跨请求的符号引用 |
| 生命周期 | 单次查询内 | 跨请求、跨会话 |
| 值相等含义 | "同一个符号" | "同一符号 + 同一源版本 + 同一代际" |
| 是否含版本 | 否（刻意） | 是（必需） |

**合会坏掉 §2.4**：同一符号在索引更新前后本应去重成一条，加了版本就变成两条。

## 8 待裁定（我不自己拍板的三件）

| # | 问题 | 我倾向 | 为什么不定 |
|---|---|---|---|
| Q1 | v1 是否**必须**支持历史快照读取 | 不支持：一律 `stale_ref` + relocation | 快照读取涉及保留策略与存储成本，超出 v1 边界；但 §8.3 写了"能读取则回显"，需要用户确认可以先不做 |
| Q2 | `ProviderGeneration` 取什么值 | 索引器程序集版本 + 解析器名称 | 需要先确认索引侧是否已有稳定代际标识，避免再造一个 |
| Q3 | ref 是否带过期时间 | 不带 | ref 的有效性由版本与 scope 判定，加 TTL 会引入"本来有效却被时间判死"的新失败面 |

## 9 实施顺序（冻结后）

1. `SymbolRefV1` 类型 + 编解码 + 不变量断言 + **8 条契约测试**（含往返、非法字符、超长、
   `..` 逃逸、缺 ContentHash 拒绝）；
2. `SymbolRefFreshness` 判定器（FRESH/STALE/不可重定位三态）+ 变异取红；
3. `workspace_open` 接受 `ref` 参数（与现有 `{project_id,path}` 二选一，**同给报错不择一**）；
4. `relations` 视图接 `IRetrievalPort` 的关系意图（复用 §8.2 同名语义）；
5. 最后才做 `summary` / `status` 视图。

第 3 步之前不碰工具层 —— 与 `workspace_open` v1 同一条纪律：先冻结再实现。

## 10 本提案不解决的问题

- 跨 **worktree** 的身份（`FilePath` 目前是项目内相对路径，多 worktree 同名路径会撞）；
- 符号改名后能否追踪（需要 git 层的 rename 检测，索引侧无此信息）；
- `relations` 的 impact 多跳预算在 open 视图里是否需要独立的 `depth` 上界。

## 11 `workspace_search` 方向的裁定（2026-10-30 实测追加）

原待办「`workspace_search` 签发 ref」**撤回**，三条理由：

1. **语义错位。** `workspace_open` 的 ref 语义是「打开该符号的声明体」。检索命中里的行号是**证据位置**：`callers`/`callees`/`references` 命中的行指向调用点，不是声明体起点。而 ref 的校验只有**文件级** `content_hash`（实测 `stale_ref` 报文只比对文件指纹），不校验符号跨度 ⇒ 用调用点行号签发的 ref 会是 **Fresh 但打开错行**。静默错位比直接报错更危险。

2. **无法唯一确定项目。** `RetrievalHit` 只带 `Identity`（`symbolId`、规范化路径、行）与 `Evidence`（路径 / 行 / 可选 `endLine`），**没有 `project_id`**。当前工作区 4 个已登记项目相互嵌套（根 + `Source\PuddingCore` / `PuddingPlatform` / `PuddingRuntime`），同一文件同时落在多个项目根内 ⇒ 用路径前缀推项目就是猜，违反 §8.3「不猜，拒签」。

3. **架构边界被测试锁住。** `WorkspaceSearchToolCompositionTests` 断言 `workspace_search` **只依赖** `IRetrievalPort`（1 个构造函数、1 个参数）。把签发器塞进工具会破坏这条边界；正确位置在端口或其装饰器，而那需要先给 `RetrievalHit` 增加 `SymbolRef` 字段 —— 是契约变更，不是接线。

**真实缺口在入参方向。** `增强索引统一检索与代码地图设计方案-2026-10-18.md` 明确要求
`workspace_search(symbol_ref="…", intent="callers", depth=1)`，且「`implementation`/`references`/`callers`/`callees`/`impact` 必须给精确 `symbol_ref`，禁止模糊取首项」。
但 `RetrievalRequest` 当前**没有 ref 字段**，`Query` 又是必填非空 ⇒ 该条款在契约层无法表达。

**下一原子任务**：让 `RetrievalRequest` 能表达「已解析的符号身份」，`workspace_search` 接受 `symbol_ref` 入参承担关系类意图。输出方向（命中带 ref）待命中能带回 `project_id` 与真实声明跨度后再议。

**顺带修正**：`workspace_open` 的 `ref` 参数说明原写「token returned by workspace_search」，与实际不符（当前唯一签发者是 `code_symbol_search`），已改为如实描述。

## 2026-10-30 线上验收追加：容器锚点的「空洞否定」（已修）

对**类**符号（`T:PuddingCodeIndex.Services.Retrieval.SymbolRefIssuer`）签发的 ref 查 `references`，
> 注（2026-10-31）：`SymbolRefIssuer` 类型已于当日迁至 `PuddingRetrieval`（命名空间变更为 `PuddingRetrieval.SymbolRefIssuer`，`PuddingCodeIndex/Services/Retrieval/` 目录已删除）。上面这条**观察本身仍然成立**（它记录的是当时线上签发的 token 与当时的报因缺陷），只是该符号现在的命名空间标识已变 —— 复现时请用新命名空间。

返回 `GenuinelyAbsent` +「索引可用、过滤面干净，确实没有匹配项」；而同名**名字检索**同一意图返回 **11 条**。
原因是关系边挂在成员（`.ctor` / `FromManifest` / `Issue`…）上，容器自身零边。

- **不是锚点解析失败**：方法锚点 `M:…SymbolRefIssuer.TryMakeRelative(…)` 查 `callers` 返回 2 条
  （`SymbolRefIssuer.cs` 第 114 / 155 行），与名字面 `why` 字段里的证据行号完全一致
  ⇒ 身份解析、边展开、去重都是对的。
- **缺陷在报因**：`GenuinelyAbsent` 只允许 `AcceptAbsence`，Agent 会读成「没人用它」然后换查询打转 ——
  这正是 §8.9 要消灭的空洞否定。
- **修法**（`RetrievalPortV1.RetrieveRelationsAsync`）：锚点是容器且 0 条边时，仍报 `GenuinelyAbsent`
  （对符号本身为真），但下一步必须 `UseAlternativeFace`，正文写明「关系边挂在成员上 / 本端口不展开成员 /
  这不代表没人用它 / 请看具体成员的 ref」。
- **三态**：基线 24/24 绿 → 变异（去掉容器判定）24 中 1 红，且单独重跑该测试仍红 → 还原 SHA 位级一致
  `60614A19…` + `dotnet build -t:Rebuild` → 24/24 复绿。
- **未做（待裁定）**：是否让容器锚点**自动展开到成员**。展开会改变「谁调用了 X」的语义
  （把成员的调用者混进容器的调用者），故本轮只做诚实报因，不擅自改语义。
