# PuddingLlmUsage.Storage — code map
> 源指纹: 未生成（本文件首次整理，仓库内无指纹自检器 —— 不编造） · 条目数: 2 · 最近整理: 2026-10-30

> 余额采样的 **SQLite 适配器**。只引用 `PuddingLlmUsage`（零依赖叶子）+ `Microsoft.Data.Sqlite`；
> **不得**引用 `PuddingRuntime` / `PuddingPlatform` / `PuddingHost` ——
> 存储是「往哪写」，不是「谁来采样」；否则将来会有人在这里读配置、发 HTTP。

## 定位

独立库文件 `<DataRoot>/databases/llm_usage.db`，**不塞进约 12 GB 的平台库**：
采样是高频后台写，与业务读写争同一把 SQLite 写锁会让两侧都不稳。

## 表

| 表 | 内容 |
|---|---|
| `LlmBalanceSampleAttempt` | 一次采样尝试：`ProviderId / StartedAtUtc / CompletedAtUtc / Status{Running,Succeeded,Failed,Interrupted} / Error`。**先落 Started 再联网**，进程被杀会留下 `Running`，下次启动扫描改写为 `Interrupted` |
| `LlmBalanceCurrencySample` | 币种级观测：`AttemptId(FK) / Currency / TotalBalance / GrantedBalance / ToppedUpBalance / ObservedAtUtc` |

## 三个不能改的取舍

1. **金额存 TEXT + invariant 字符串，不用 REAL。** `248.55` 按二进制浮点存进去再读出来可能变成
   `248.55000000000001`；而消耗判定就是 `to - from` 与 0 比较，末位误差足以把「持平」判成「下降」。
2. **空观测列表记 `Failed` 而不是 `Succeeded`。** 「什么都没采到」不是一次成功的空采样。
3. **失败/中断尝试在读取时还原成 `Failure` 观测**（币种 `unknown`），使缺口进入分析序列 ——
   否则曲线上那段时间看起来像「没变化」。

时间一律存 ISO-8601 往返格式（含偏移），保证字符串序即时间序。

## 文件

| 文件 | 内容 |
|---|---|
| `PuddingLlmUsage.Storage.csproj` | 引用叶子 + `Microsoft.Data.Sqlite 10.0.9` + `SQLitePCLRaw.bundle_e_sqlite3 2.1.13` |
| `SqliteLlmUsageStore.cs` | `ILlmUsageStore` 实现。每次操作新建连接（SQLite 连接很轻；WAL 下长连接会累积读快照）；`CompleteAttemptAsync` 在单事务内更新尝试并写入全部币种观测 |

## 测试（`Source/PuddingLlmUsageStorageTests/`，12 用例）

| 覆盖 | 关键断言 |
|---|---|
| 精度往返 | `248.55` 读回仍是 `248.55`，缺失的金额读回仍是 `null` |
| 失败可见 | 失败尝试能被读回且 `Succeeded=false`；空观测列表记为失败 |
| 缺口可见 | 遗留 `Running` 被改写成 `Interrupted` 并可读回；第二次扫描是 no-op |
| 隔离 | 时间窗口过滤；跨 provider 不串 |
| **端到端** | 写入 `248.55 → 248.05`，读回后 `LlmUsageAnalyzer` 直接判出 `Consumption 0.5` —— 存储与分析器真的能对接，不是各自绿 |

未进 `PuddingAgentNetwork.slnx`（S5 时随宿主装配一起登记）。
