# 索引/检索 已知缺陷登记（2026-10-01）

> 本文件记录**检索面上**已复现、尚未修复的缺陷：证据、复现步骤、影响面、修复方向、未验证项。
> 纪律：以下每条均为**本机实跑**所得，附原始返回；未能证实的部分标注为「未验证」。

---

## D1 · 已从注册表移除的项目，查询面仍照常作答（不 fail-closed）

**现象**
`code_index_list_projects` 只列 4 条，**不含** `scope-6526fb344e33` 与 `scope-0ca100c528ef`；但：

| 调用 | 返回 |
|---|---|
`code_index_status("scope-6526fb344e33")` | `status = "Completed"`, `completed_at_utc = 2026-10-01T03:30:50Z` —— **不是 not found** |
`code_index_status("scope-0ca100c528ef")` | `status = "Completed"`, `completed_at_utc = 2026-10-01T03:30:50Z` —— 同上 |

**判定**：注册表（`list_projects` 的真源）与**查询视图**（`code_index_status`）口径不一致。"
已注销" ≠ "已清除"。

## D2 · 陈旧 `E:\` 根路径仍被服务给调用方

**现象**（同一次调用，`code_symbol_search(query="ICodeIndexMaintenance", project_id="scope-6526fb344e33")`，`count = 10`）

```
symbol_id : T:PuddingCodeIndex.Contracts.ICodeIndexMaintenance
file_path : E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs
project_id: scope-6526fb344e33
```

**关键事实**：宿主**已无 E: 盘**；该路径**必然不存在**。而真实文件就在本机
`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs`。

**影响面**：任何消费 `code_symbol_search` 的一方（Agent、代码审阅流程、以及即将接入的
Admin 面板 B 卡）**都会拿到不存在的路径**。这正是 2026-10-01 那条环境纪律
「不要引用文档里的路径，一律现场 `Test-Path` 实测」在**检索工具**上的重演 —— 这次脏数据
来自工具本身。

## D3 · 同一项目的状态在两个视图里互相矛盾

| 视图 | 对根项目 `8a48458b30150fdbed4baaced35d24cf`（`D:\CodeProject\PuddingAgent\PuddingAgent`）的表述 |
|---|---|
`code_index_list_projects` | `status = "Registering"`（`updated_at_utc = 04:52:56`） |
`code_index_status` | `status = "Pending"`（`completed_at_utc = 04:52:56`） |

同一个 id、同一个时间戳，**两个状态词**；且 `Pending` 与"有 completed_at"自相矛盾。
**未验证**：二者是否本就是不同字段（注册态 vs 索引态）—— 若是，UI 必须分别呈现，不能合并成一个「状态」。

## D4 · 修复被 SQLite 写锁阻塞（当前无法清理）

**尝试**：`code_index_unregister_project(project_id="scope-6526fb344e33", remove_index_data=true)`
**两次**均失败：

```
SQLite Error 5: 'database is locked'.
```

**第二次被运行时 fail-closed 拦下**（`execution_stalled`：同一 canonical 调用两次结果不变，禁止盲重试）。
⇒ **判定为阻塞，不是失败**：需要写锁空闲窗口，或由宿主侧提供一条受控的清理路径。

**写者取证**（本机实测，`D:\Data\databases\code-index\code_index.db`）

| 指标 | 值 |
|---|---|
main 大小 | 2,836,717,568 B |
main mtime | **2026-10-01T06:02:43Z**（≈ 本地 14:02:43，即本次排查前 1 分钟） |
`-wal` | **0 B** |
`-shm` | 32,768 B |
`-wal` 4 秒两次采样 | **0 → 0，未变化** |

⇒ 结论：**写入是间歇的**（mtime 证明近期有写，但采样窗口内无活跃写者）。
不能用「WAL 未变」推定「没有写者」，也不能用「锁失败」推定「有人长期占锁」。

---

## 影响与前置条件（重要）

1. **S-A2（符号索引块并入 `GET /api/admin/index/status`）必须先解决 D1/D2**，否则面板 B 卡会把
   **陈旧、指向不存在盘符的数据**渲染成"已索引"。⇒ **B 卡的验收线里必须包含 fail-closed 与
   路径有效性两条**，不能只看"有数据"。
2. 任何"索引好不好用"的结论，在 D1~D3 修好前都不可信 —— 因为**查询面本身在撒谎**。

## 建议修复方向（未实施，待裁定）

| 方向 | 说明 |
|---|---|
F1 | 查询面 **fail-closed**：`code_index_status` / `code_symbol_search` 对未在注册表登记的项目返回 not found，而不是"上次的 Completed" |
F2 | 清理路径：提供**可在锁空闲窗口执行**的定向清除（或宿主侧受控入口），并让 `remove_index_data` 对"注册表已无、索引仍有"的孤儿数据同样生效 |
F3 | 路径有效性：检索结果返回前校验根路径是否可达；不可达则显式标注（例如 `stale: true`），而不是静默给死路径 |
F4 | 状态口径统一：明确 `Registering` 与 `Pending` 的语义边界，并在面板上分别表达 |

## 复现清单（30 秒内可重跑）

```
code_index_list_projects()                 # 4 条，无 scope-6526fb344e33
code_index_status("scope-6526fb344e33")    # Completed（应为 not found）
code_symbol_search("ICodeIndexMaintenance", project_id="scope-6526fb344e33")
                                           # 10 条，file_path 全为 E:\github\...
Test-Path "E:\github\AgentNetworkPlan\PuddingAgent"   # False
```
