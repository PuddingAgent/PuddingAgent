---
title: 索引/检索 已知缺陷登记（2026-10-01）
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: active
description: 现象 code_index_list_projects 只列 4 条，不含 scope-6526fb344e33 与 scope-0ca100c528ef；但：
categories: [docs, features]
tags: [index, retrieval, known, defects, features]
related_docs: []
related_files: [Source/PuddingRuntimeTests/Tools/CodeIndexStatusRegistryGateTests.cs, Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs, Source/PuddingCodeIndex/Contracts/Retrieval/ScopeOverlap.cs, Source/PuddingAgent/bin/Debug/net10.0/PuddingAgent.exe, Source/PuddingCore/Configuration/PuddingBuildOutputSync.cs, Source/PuddingDesktop.WpfArchive/Bootstrap/DesktopBootstrapSignalService.cs, Source/PuddingHost/Services/CodeIndexStatusProbe.cs, Source/PuddingHost/Controllers/IndexAdminController.cs, Source/PuddingHost/Hosting/FullTextIndexSupplyAccessor.cs, Source/PuddingRuntime/Tools/BuiltIns/CodeIntelligence/CodeQueryTools.cs, Source/PuddingRuntimeTests/Tools/CodeSymbolSearchStalePathGateTests.cs]
slug: features-index-retrieval-known-defects-2026-10-01
draft: false
---

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

### D1 修复状态（2026-10-01，代码已交付）

`code_index_status` 现已 **fail-closed**：调用前用**注册表**（`ICodeProjectRegistry.ListProjectsAsync`，与 `code_index_list_projects` **同一 API、同一 workspace 入参**）核对 `project_id`；未登记 ⇒ 返回 `status="not_registered"` + `message` 指向 `code_index_list_projects`，且**根本不去读索引视图**（有断言：状态读取次数 = 0）。已登记项目的返回体**逐字冻结**作为回归线。

- **单点定义**：`CodeQueryTools.CodeQueryToolHelper.IsRegistered(...)`（风格对齐 `CodeIndexStatusProbe.IsStale`）；刻意用 **Ordinal** 比较、不 `Trim`、不忽略大小写 —— 放宽比较会把两个不同项目判成同一个，从而又把「已注销」当「已登记」放行，正是本缺陷要堵的洞。
- **口径选择**：用带 `Status <> Removed` 过滤的 `ListProjectsAsync`，**不用**无过滤的 `GetProjectAsync`（后者会把已注销行判成「已登记」）；测试里让 `GetProjectAsync` 直接 `throw`，把这条口径钉成**会失败的断言**。
- **验证**：新测试 `Source/PuddingRuntimeTests/Tools/CodeIndexStatusRegistryGateTests.cs`（4 条）在**改动前 4/4 红**（D1 在单测层复现）→ 改动后 4/4 绿；父级独立变异（`IsRegistered` 恒真）⇒ **2 红**（`not_registered` 与自动探测两条），逐位复原后产物 `PuddingRuntime.dll` SHA 精确回到 `5D275F47…`、复跑 4/4 绿。
- ⚠️ **范围**：`code_symbol_search` 的 D2（陈旧 `E:` 死路径仍被服务）**未修**，仍为独立切片。
- ⚠️ **生效条件**：工具是进程内实现，**需重启 Core 才在运行中的 Agent 上生效**；在此之前线上工具仍是旧行为。

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


---

## D5 · 根项目索引停在 `Registering` 且覆盖不全：`Source/PuddingHost/**` 符号检索不到（2026-10-01 新增）

### 现象：三个探针（`code_symbol_search`，无 project 过滤）

| 查询 | 返回 | 结论 |
|---|---|---|
| `CodeIndexMaintenanceHostedService` | **仅 1 条**，`file_path` = `E:\...\Source\PuddingHost\Hosting\...`，`project_id` = `b375fee0d6524ad393a26e72ba1e917d` | D: 的真实副本**未返回** |
| `FullTextIndexStatusProbe`（D: 中存在，S-A2 刚改过） | **0 条** | 目录级盲区 |
| `StorageAdminController` | **仅 1 条**，E: 路径，同一孤儿 `project_id` | 同上 |
| `BootstrapRebootTool`（**对照**，属 PuddingRuntime） | 2 条，**均为 D: 路径**，`project_id` = `8a48458b…` | 索引**部分覆盖**，不是全空 |

**判定**：当前仓库的符号索引 = **部分覆盖 + 陈旧 E: 残留**。`Source/PuddingHost/**`（3/3 探针）在 D: 侧无结果，查询**回落到旧 E: 行**；`Source/PuddingRuntime/**` 正常。
⇒ **`code_symbol_search` 现在既「漏」又「假」**：漏掉真实存在的文件，同时把不存在的盘符路径当结果给出。

### 根项目状态自相矛盾且已冻结

两轮实测（间隔 ≥2 小时）**取值完全一致**：

```
code_index_list_projects → 8a48458b…: status="Registering", updated_at_utc=2026-10-01T04:52:56.1743861Z
code_index_status(8a48458b…) → status="Pending", started_at_utc=null,
                               completed_at_utc=2026-10-01T04:52:56.1743861Z   ← Pending 却有 completed_at
```

### 源码层面已登记的治理缺口（关键）

| 位置 | 原文要点 |
|---|---|
| `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs:204` | `Registering` = **"a full run is still owed"**（仍欠一次完整运行） |
| 同文件 `:298-314`（U3-G1） | **"records a cancelled run without moving the row off `Registering`"** ⇒ 被取消/中断的运行**不会**把行移出 `Registering` |
| `Source/PuddingCodeIndex/Contracts/Retrieval/ScopeOverlap.cs:70` | **"索引侧的治理实现（Registering 超时收敛等）属 U3 收尾项，不在本刀"** ⇒ **没有任何机制会收敛卡住的 `Registering`** |

⇒ 根项目**仍欠一次完整运行**，且**无超时收敛**；在补完之前，根级目录（如 `Source/PuddingHost/**`）的符号就是检索不到。这不是"没索引过"，而是"索引过一半、剩下的没人管"。

### D4 修正：我上一版的判断不完整

- 上一版据 `-wal = 0 B` 推断「采样窗口内无活跃写者」。**该推断已被推翻**：同一文件 2 小时后实测
  `-wal = 3,506,152 B`、`main mtime = 2026-10-01T08:18:47Z`（采样时刻约 1.5 分钟前）、`-shm mtime` 同步刷新。
- ⇒ 修正为：**索引库存在活跃写者**；`SQLite Error 5: database is locked` 是**写者繁忙**所致，不是"死锁无人动"。
- **教训**：单次 WAL 采样不足以判定"无写者"；必须**跨时间窗多次采样**，并同时看 main/WAL/shm 三个文件的 mtime。
- 现行阻塞：**修复与清理两条路被同一把锁挡住** —— `code_index_register_project(index=true)` 与 `code_index_unregister_project(remove_index_data=true)` 均报 `SQLite Error 5`。

### 运维结论（2026-10-01 修正：**重启 ≠ 部署**）

**旧结论作废。** 我先前写「一次 Core 重启可同时解决三件事」，**本轮实测证伪**。

**证据链（本轮实测）**

| 证据 | 取值 |
|---|---|
| 运行中 Core | PID **32848**，启动 **2026-10-01T17:26:27+08:00**（较上轮 PID 13552 **确已重启**） |
| exe 路径 | `Source\PuddingAgent\bin\Debug\net10.0\PuddingAgent.exe` |
| 该目录 `PuddingHost.dll` | len=851456，mtime **2026-10-01T03:14:26Z**（本地 11:14，**早于全部四个切片**），sha256 `708B3EEF…` |
| 类型扫描（该 DLL） | `IndexAdminController`=**False** · `FullTextIndexStatusProbe`=**False** · `CodeIndexStatusProbe`=**False** · `FullTextIndexSupplyAccessor`=**False**；对照 `StorageAdminController`=**True**（⇒ 扫描有效，非仪器故障） |
| HTTP | `/api/admin/index/status` → **404**；对照 `/api/admin/storage/overview` → **401**（路由存在，仅缺鉴权） |
| 工具行为 | `code_index_status("scope-6526fb344e33")` → **仍为 `Completed`**（D1 修复应返回 `not_registered`） |
| `bin\Release\net10.0\PuddingHost.dll` | mtime 04:17:59Z；`IndexAdminController`=**True** 但 `CodeIndexStatusProbe`=**False** ⇒ 含 S-A、**缺 S-A2/D1** |

⇒ **重启只是重新加载同一份旧二进制**。四个切片的源码**从未编译进 Core 的启动目录**。
⇒ 真正的激活动作 = **编译 + 部署 + 重启**（`bootstrap_reboot` / `deployment_mode=desktop-build`），**不是**单纯重启。

### ✅ 可部署产物已备好（本轮新增，非破坏性）

用**输出重定向**构建，**未触碰运行中的 Core 目录**：

```
dotnet build "Source\PuddingAgent\PuddingAgent.csproj" -c Debug -o "temp\host-preview"
→ exit 0，143 warning / 0 error，用时 8.36s
```

| 项 | 值 |
|---|---|
| 产物规模 | **957 文件 / 791,902,371 B**，含 `PuddingAgent.exe` |
| `PuddingHost.dll` | len=899584，sha256 `01447A69…0D0FC`；**四个新类型全 True**（含 `CodeIndexStatusProbe`） |
| `PuddingRuntime.dll` | len=4529152，sha256 `3D922583…3A015`；含 **`not_registered`** ⇒ D1 修复已编译在内 |
| `PuddingAgent.dll` | sha256 `9053C33E9E2D5F931E38C9B14940DB027EEC2FC78FFB68F38A9CE3C7A553A67D`（供 `bootstrap_reboot` 的 `artifact_assembly_sha256`） |
| **安全证明** | 运行目录 `PuddingHost.dll` 仍为 `708B3EEF…`、`PuddingRuntime.dll` 仍为 `FA3654B0…` —— **逐位未变** |

### ✅ 部署风险已裁定：**覆盖式（增量 + 逐文件回滚），不是镜像式**（2026-10-01 19:50 源码取证）

原先登记的「wwwroot 体量不等 ⇒ 可能被镜像删除 ≈165 MB」**已证伪**。权威实现是
`Source/PuddingCore/Configuration/PuddingBuildOutputSync.cs` 的
`DeployDirectoryTransactional(sourceDirectory, targetDirectory)`（L101-L223）；Desktop 侧调用点
`Source/PuddingDesktop.WpfArchive/Bootstrap/DesktopBootstrapSignalService.cs:800`。

| 源码行 | 行为 |
|---|---|
| L144-L152 | 枚举源目录**全部文件**，命中目标后 `FilesIdentical` ⇒ **字节一致即 skip**（不重写） |
| L154-L176 | 变化的文件先复制到 `staging/` 并**逐文件复校字节**；暂存失败则**完全不碰** live 目录 |
| L191-L206 | 提交阶段：原文件存在则先备份到 `backup/`，再 `File.Move(..., overwrite: true)` |
| L207-L224 | 提交失败 ⇒ **逆序回滚**已改文件；`File.Delete` 仅用于「本无原文件」的新增文件 |

**关键结论**：函数内**没有任何“删除目标多余文件”的分支** —— `File.Delete` 只出现在**回滚新增文件**这一条路径上。
⇒ 目标目录中不在产物里的文件（即 live 多出的 455 文件 / ≈165 MB）**会被原样保留**；部署是**纯增量覆盖**。

| 体量差异（已量化，非风险） | 值 |
|---|---|
| preview `wwwroot` | **317** 文件 / 40,453,107 B |
| live `wwwroot` | **772** 文件 / 205,676,954 B |
| 仅 live 存在 | **560** 个（`\admin` 171 · `\` 根 160 · `\lib\*` 168 · 其余为静态页面目录） |
| 仅 preview 存在 | **105** 个（多为 `\admin\*.gz` 预压缩产物） |
| admin SPA 一致性 | 两处 `wwwroot\admin\index.html` sha256 **相同** `C3B94DC5…` ⇒ 前端部署与构建源一致 |
| 旁注（**未定论**） | 体量差异**未完全归因**：preview 是单项目 `-o` 构建产物，live 是 Desktop 全量构建 + 历次部署的累积（含其他项目的静态 Web 资产）。该差异**对部署安全性无影响**（覆盖式），故不再深挖 |

**残留风险（低）**：失败路径会在目标目录**父级**留下 `.pudding-bootstrap-<guid>` 事务目录；`TryDeleteTransactionDirectory` 负责清理，异常中断可能残留 —— 部署后按 `dir /b /ad .pudding-bootstrap-*` 复查一次。

**在重启完成之前**：符号检索的可信度有限，**不应据 `code_symbol_search` 结果下"某代码不存在"的结论**（应改用 `file_read` / `search_grep` 现场核对）。

## D7 · 【已证伪】「配置的 scope 失效 ⇒ 既有索引被清空」（2026-10-01 20:2x 隔离实验）

**背景**：上一轮曾把 2026-10-01 的「全文索引根 0 条目」归因为 fail-open 缺陷 —— 猜测
「配置的 scope 路径不存在时，预建/供给路径会枚举 0 文件并把该 scope 的索引重建为空」。
该猜测当时明确标注为**未证实**。本实验在**隔离 lab** 中验证，**结论：假设不成立**。

**实验设计**（全程只碰 `temp/ft-lab/**`，生产索引根 `D:\Data\fulltext-index` **未被访问**）

| 步 | 操作 | 结果 |
|---|---|---|
| 1 | 3 文件语料建索引：`build --scope temp\ft-lab\corpus --index-root temp\ft-lab\index --wait` | `state=Succeeded` · `IndexedFileCount=3` · `TotalBytes=170` · `ElapsedMs=580` · **exit 0** |
| — | 盘上产物 | scope 目录 `b4bc6536…dcec6`：**6 文件 / 2,498 B**（Lucene `_0.cfe/_0.cfs/_0.si/segments_*/\.last_indexed`） |
| 2 | `status` 复核 | `exists=true` · `hasIndex=true` · `indexEntryCount=6` · `indexBytes=2498` |
| 3 | **把语料目录改名**（模拟「配置的 scope 已不存在」，即 E: 盘消失那一幕）后重建 | `outcome=**Rejected**` · `reason=scope 目录不存在：…` · `scope[0].scopeKey=**none**` · `jobId=**none**` · **exit 2** |
| — | 盘上产物（重建被拒后） | 仍为 **6 文件 / 2,498 B** —— **逐字节未变** |
| 4 | `status` 复核 | `exists=**false**`（scope 确实不存在）但 `hasIndex=**true**` · `indexEntryCount=**6**` · `indexBytes=**2498**` |

**判定**：供给/构建路径在 scope 缺失时**在任何 job 创建之前就 fail-closed 拒绝**
（`scopeKey=none`、无 jobId、exit 2），**完全不动既有索引**。
⇒ 「scope 失效 ⇒ 索引被清空」**在供给路径上被证伪**。

**由此收窄的剩余空间**（诚实标注）：
- CLI 只有 `plan / status / build / cancel` 四个命令（`SupplyCommandLine.cs:195-204`）⇒ **维护循环（incremental）路径无法从 CLI 触发**，本轮未覆盖。
- 但本分支查证过 **`FullTextIndex:Maintenance` 绑定器不存在于本地线**（S5b 维护接线只在 tag `backup/origin-master-2d264fe` 那条线上）⇒ 本分支**没有能清空索引的维护路径**。
- 因此对「索引根 0 条目」目前**证据最强的解释是**：`FullTextIndex.Scopes` 当时指向已不存在的 `E:`，
  该 scope 从未成功建过索引；而 `D:` 这个 scope key 的索引**是当时才第一次建立**（重建后 12 文件 / 106,029,333 B）。
- ⚠️ 需要更正的一处旧表述：此前「9-30 还有 225 条、现在 0 条」中的 **225 这个数字来源不可考**
  （疑为另一分支 / 另一索引根的旧观测），**不应作为「索引被清空」的证据引用**。

**行动结论**：从待办中**移除**「复现 fail-open 清空缺陷」这一项（无机制支持，继续追查是空耗）；
索引「看不见 / 搜不到」的真实成因回到已登记的 **D5（根项目停在 `Registering`，全量运行从未完成
⇒ `Source/PuddingHost/**` 未被覆盖）** 与 **D1（未登记项目 fail-closed，修复待部署）**。

**复现命令**：`powershell -File temp\ft-lab-run.ps1`（脚本与逐行日志 `temp\ft-lab-log2.txt` 均在已 gitignore 的 `temp/`）。

## D5 更新 · 2026-10-01 20:53 —— 状态被"修好"了，但**索引内容没跟上**（新的反证）

**动作**：`code_index_register_project(project_id=8a48458b…, path=D:\CodeProject\PuddingAgent\PuddingAgent, index=true)`
（本轮不再报 `SQLite Error 5: database is locked`，返回 `index_status=Pending` / `index_message=Indexing enqueued for background processing.`）

**状态确实翻转了**（对比同日前值）：

| 视图 | 20:52 之前 | 20:52 之后 |
|---|---|---|
`code_index_list_projects` 根项目 | `status=Registering` | **`status=Active`** |
`code_index_status` 根项目 | `status=Pending` · `completed_at=11:46:53Z` · `started_at=null` | **`status=Completed`** · `completed_at=**12:52:51.6Z**` · `started_at=null` |
⇒ D3（两个视图互相矛盾）**已被消除**：现在两边一致。

**但覆盖范围没有改善（关键反证）**：

| 探针 | 结果 |
|---|---|
`code_symbol_search("CodeIndexStatusProbe")`（**只存在于 D: 的 `Source/PuddingHost/Services/`，是本会话新增文件**） | **0 条** ⇒ D: 侧 `Source/PuddingHost/**` **仍然不可见** |
`code_symbol_search("GenerateImageTool")`（对照：D: 上也确实存在） | 3 条，**全部** `file_path=E:\github\AgentNetworkPlan\…Source\PuddingHost\Tools\GenerateImageTool.cs`、`project_id=**b375fee0d6524ad393a26e72ba1e917d**` |

**判定（诚实）**：`Completed` / `Active` 是**状态面的绿灯，不是覆盖面的绿灯**。
- 时间反证：`completed_at=12:52:51`，而注册调用发起于 12:52:4x ⇒ 该次「全量运行」在**秒级**内被判定完成，
  与「重新索引 4,500+ 文件」应有的耗时量级**严重不符** ⇒ 高度怀疑**运行没有真正执行索引工作**（只推进了状态机）。
- `GenerateImageTool` 之所以还能搜到，靠的是一条**孤儿 project（`b375fee0…`，路径指向已不存在的 E: 盘）**里的陈旧行 ——
  它既**不是** D: 的真值，又**掩盖了** D: 确实没有覆盖这一事实。

**由此暴露我自己 S-A2 设计的一个缺口**（新发现，非外部缺陷）：
`CodeIndexStatusProbe.IsStale` 的判据是 `未登记 || 根路径不存在`，**覆盖不到**
「已登记 + 根路径存在 + **运行时报告 Completed，但索引里没有该项目的任何符号**」这一失败模式。
⇒ 待办新增：**给 `codeIndex` 块加"覆盖可信度"信号**（至少要有可判定的证据，例如该 scope 的已索引文件数 / 最近一次运行耗时 /
探针式抽查）——否则面板会在这种情形下显示「正常」，与本项目「不许把未知或坏当成正常」的纪律冲突。

**当前可用兜底（已在本会话反复使用）**：符号检索不可信期间，定位代码一律用
`file_search` / `search_grep` / `file_read` 在 D: 上现场核对，**不引用 `code_symbol_search` 返回的 `E:` 路径**。

**未完成**：为什么「全量运行」会秒级完成（是否被某一 guard 短路、是否 Core 侧日志有原因）——
本轮未查 Core 日志，列为下一步；修复可能的入口是 `PuddingCodeIndexer.Cli`（尚未确认其命令面）。

## D8 · 已处理：`code_symbol_search` 返回「已注销项目」的陈旧行（含指向已不存在 `E:` 盘的路径）

**现象**：即使项目已从注册表移除（`code_index_list_projects` 里看不到），其符号行**仍留在符号存储里并被检索命中**，
返回的 `file_path` 全部是 `E:\github\AgentNetworkPlan\…`（本机已无 E: 盘）。

**证据（本会话实测）**：

| 时点 | 探针 | 结果 |
|---|---|---|
清理前 | `code_symbol_search("GenerateImageTool")` | 3 条，**全部** `E:\github\…`，`project_id=b375fee0d6524ad393a26e72ba1e917d` |
清理前 | `code_symbol_search("BootstrapRebootTool")` | 10 条，**全部** `E:\github\…`，`project_id=scope-6526fb344e33` |
清理前 | `code_symbol_search("CodeIndexStatusProbe")` | 0 条（该文件只存在于 D:） |

**处置（已执行，四例全部成功）**：`code_index_unregister_project(project_id, remove_index_data=true)`

```
b375fee0d6524ad393a26e72ba1e917d   → removed
scope-6526fb344e33                  → removed
scope-0ca100c528ef                  → removed
scope-ee887ff5297f                  → removed
```
⇒ 本轮**未再出现** `SQLite Error 5: database is locked`（与当日 16:18 那次失败形成对照，说明该锁是间歇性的）。

**清理后验证（同探针复测 + 对照）**：

| 探针 | 清理后 | 判读 |
|---|---|---|
`code_symbol_search("GenerateImageTool")` | **0 条** | 陈旧 `E:` 行已消失 |
`code_symbol_search("BootstrapRebootTool")` | **0 条** | 同上（该符号现在**诚实为空**，因为 `Source/PuddingRuntime` 的 D: 覆盖也不完整） |
**对照** `code_symbol_search("IndexPrebuildService")` | **5 条，全部 `D:\CodeProject\…`**，`project_id=8a48458b…` | **仪器有效**：搜索本身工作正常，返回的是真 D: 路径 |
`code_index_status(b375fee0…)` | `status=Unknown` / `message="Project is not registered."` | 数据删除后该 id 不再被"认领" |

**另一个附带发现（D5 覆盖面的进一步收窄）**：

| 符号所在位置 | 是否可搜到 |
|---|---|
`Tests/PuddingHost.Tests/**`（如 `IndexPrebuildServiceTests`） | ✅ **可以**（D: 路径，root 项目 `8a48458b…`） |
`Source/PuddingHost/**`（如 `StorageAdminController`、`CodeIndexStatusProbe`） | ❌ 搜不到 |
`Source/PuddingCore/**`（如 `PuddingBuildOutputSync`） | ❌ 搜不到 |

⇒ D5 的真实形状不是"完全没索引"，而是**覆盖被限制在一部分子树**（本样本中 `Tests/**` 有、`Source/**` 无），
结合「全量运行秒级判定完成」，高度指向**运行范围/枚举被某种条件截断**，而非"没有运行"。

**给后续修复的方向（未实现）**：
1. `code_symbol_search`（以及其它读符号的查询）**必须按"已登记且根路径存在"过滤**——`D1` 只补了 `code_index_status` 这一个入口，**搜索入口没有补**；
2. `unregister(remove_index_data:true)` 应当是**数据清理的权威语义**，建议在注销路径上强制清理（而非依赖手动传参）；
3. 需要一条"**覆盖可信度**"信号（登记范围 vs 实际已索引范围），否则 `Active`/`Completed` 会持续欺骗调用方与面板。

**兜底（仍生效）**：符号检索结果必须**先用 `Test-Path` 验证根路径**再采信；定位代码优先 `file_search` / `search_grep` / `file_read`。

---

## 附 · `PuddingCodeIndexer.Cli` **不能**用来修 Agent 侧覆盖（已实测，防止走错路）

```
> dotnet run --project Source\PuddingCodeIndexer.Cli -c Debug -- status
No indexed projects found.
Database: C:\Users\hyfree\AppData\Local\PuddingCodeIndexer\code-index.db
EXIT=0
```

⇒ 该 CLI 默认用的是 **`%LOCALAPPDATA%\PuddingCodeIndexer\code-index.db`**（**空的**），
而 Agent 工具读的是 **`D:\Data\databases\code-index\code_index.db`**（2.8 GB）。**两者是不同的库**。
所以：**不要**在 CLI 上跑 `index <path>` 来指望修复 Agent 可见的覆盖 —— 那只会写进另一个库（已在本轮查证，避免了空耗）。
CLI 的命令面为：`index / search / status / watch / definition / references / hover`（`Program.cs:37-50`）。

---

## 更正 · D2 / D5 在当前 live 状态下**均不成立**（2026-10-01 21:59 复测，父级亲取）

**测量（同一批探针，两次采样，间隔约 35 分钟）**

| 探针符号 | 21:24 轮 | 21:59 轮（本轮） | 命中文件 |
|---|---|---|---|
`CodeIndexStatusProbe` | 0 条 | **1 条** | `Source/PuddingHost/Services/CodeIndexStatusProbe.cs` |
`IndexAdminController` | — | **1 条** | `Source/PuddingHost/Controllers/IndexAdminController.cs` |
`FullTextIndexSupplyAccessor` | — | **2 条** | `Source/PuddingHost/Hosting/FullTextIndexSupplyAccessor.cs`（类 + 接口） |
`PuddingBuildOutputSync` | 0 条 | **2 条** | `Source/PuddingCore/Configuration/…` + `Source/PuddingCoreTests/…` |
`PuddingToolServiceCollectionExtensions` | — | **1 条** | `Source/PuddingRuntime/Tools/Platform/…` |
`VolcengineArkImageGenerationProvider` | — | **2 条** | `Source/PuddingRuntime/Services/…` + 其测试 |
`ImageGenerationService` | — | **7 条** | `PuddingPlatform` / `PuddingCore.Abstractions` / 三个测试工程 |
`ICodeIndexMaintenance` | — | **1 条** | `Source/PuddingCodeIndex/Contracts/…` |
`CodeQueryToolHelper` | — | **2 条** | `Source/PuddingRuntime/Tools/BuiltIns/CodeIntelligence/CodeQueryTools.cs` |
**`NotRegisteredStatus`**（D1 修复 2026-10-01 **新加**的字段，该文件 L45） | — | **1 条** | 同上 ⇒ **索引内含最新提交的源码** |

- 上述命中**全部**为 `D:\CodeProject\PuddingAgent\PuddingAgent\…`，`project_id=8a48458b30150fdbed4baaced35d24cf`（root）。
- 覆盖子树：`Source/PuddingHost` · `Source/PuddingCore` · `Source/PuddingRuntime` · `Source/PuddingPlatform` · `Source/PuddingCodeIndex` · `Source/*Tests` · `Tests/PuddingAgent.IntegrationTests`。

⇒ **判定：覆盖面完整、路径正确、内容新鲜。** 因此：
- **D2（陈旧 `E:` 死路径仍被服务）**：已随上轮四个陈旧项目注销而消失，**当前不成立**（上轮已复核 0 命中）。
- **D5（覆盖被截断）**：**不成立**，撤回该结论。

### 我上一轮为什么判错（两个独立成因，都要记住）

1. **时点**：上一轮采样正好落在**重建进行中**——root 项目 `updated_at_utc=13:58:07Z` 与本轮采样几乎同一分钟，且 `code_index_list_projects` 当时显示 `Registering`（`CodeIndexScheduler` 语义为「仍欠一次完整运行」）。**重建期间查询会静默返回空**，肉眼看起来就像"覆盖被截断"。
2. **探针名错**：我拿**文件名**当符号名去搜（`CodeQueryTools`），而 `code_outline` 证实该文件里**没有这个类型**（实际是 `CodeQueryToolHelper` / `CodeIndexStatusTool` / `CodeSymbolSearchTool` / `CodeExploreTool` …）。⇒ 那次 0 命中是**我的探针错了**，不是索引缺口。

**新增纪律（索引覆盖测量的三条硬要求）**
1. **必须多次采样看趋势**，单次不得定论（重建是进行时，不是状态位）；
2. 探针符号名必须**取自文件内容**（`code_outline` / `search_grep`），**不得取自文件名**；
3. 每次「0 命中」必须配一个**已知存在**的对照符号；无对照则无法区分「真不存在」与「索引尚未追上」。

### 收窄后**仍然成立**的两条

- **D1 的范围**：`code_index_status` 已 fail-closed，但**搜索入口（`code_symbol_search` 等）仍未按「已登记且根路径存在」过滤**。优先级**下调为防御性**——当前注册表 4 条全部是有效 `D:` 路径，错误命中已无来源。
  - **更新（2026-10-01，提交 `3fa0800`）**：`code_symbol_search` 已补上注册表门禁与路径有效性校验——显式项目未登记 ⇒ `not_registered` fail-closed（与 `code_index_status` 同一句文案，单点定义 `CodeQueryToolHelper.BuildNotRegisteredMessage`）；每条命中校验「文件存在 + 落在其登记项目根目录内」，失效命中计入 `stale_skipped` 并从 `results` 剔除（`include_stale=true` 可带 `stale_reason` 排查）；输出新增 `searched_scope` / `registered_project_count` / `complete`。测试：`Source/PuddingRuntimeTests/Tools/CodeSymbolSearchStalePathGateTests.cs`（6 条）。仍属**防御性**（当前 live 注册表无误配来源），且**需重启 Core 才在运行中 Agent 生效**。
- **没有「覆盖 / 新鲜度」信号**：调用方无法区分「索引正在重建（空结果是暂时的）」与「代码真的不存在」。**这正是本会话我两次误判的直接原因**，也是面板 `codeIndex` 块必须补的那条信号（`stale` 只覆盖「未登记 / 根路径不存在」，覆盖不到「重建中」）。

---

## D9 · 部署产物**不得**来自脏工作树（2026-10-01 21:59 实测）

**实测**
- `temp/host-preview`（构建于 `2026-10-01T09:40:05Z`）已**过时**：`Source/**` + `Tests/**` 中 **129 个** `.cs`（已排除 `obj/`、`bin/`）比该 `PuddingHost.dll` 新。
  - 该产物：`PuddingHost.dll` sha256 `01447A694927F80F0CBBA1CEAD4F52873F5D349943A18DD656FE631AF1B0D0FC`；`PuddingAgent.dll` sha256 `9053C33E9E2D5F931E38C9B14940DB027EEC2FC78FFB68F38A9CE3C7A553A67D`。
- 更关键：构建它时**工作树并不干净**——本轮 `git status` 实测有 **8 个 `M` + 3 个 `??`**，全部落在 LLM 流式/计时区域（`PuddingCore/Core/{AnthropicMessages,OpenAi,Responses}LlmGateway.cs`、`Models/StreamDelta.cs`、`PuddingRuntime/Services/AgentExecution/AgentExecutionService.Streaming.cs`、`Services/DirectLlmClient.cs`、`ProviderStreamTiming.cs`（新）、`AgentTurnTimingCollector.cs`（新）及其测试），**属他人未提交的在飞改动**，非本 Agent 所有。

**判定**
- 从脏树构建的产物**不对应任何 commit**；一旦部署＝把**他人半成品**一并带上线，且不可复现、无法用 SHA 追溯。

**部署前置条件（硬性，任何一次都要满足）**
1. 工作树干净 —— 或**显式列出**将随产物带入的未提交改动并确认其已完成；
2. 产物必须**在目标 commit 上重新构建**（不得复用旧 `temp/host-preview`）；
3. 记录 `artifact_assembly_sha256`，部署后按 SHA 复核运行目录 DLL。

**注**：本机 `git status` 显示 `## master` 无 upstream 提示、`HEAD == origin/master == 1959fa5`（本轮实测 `ls-remote` 一致）。
