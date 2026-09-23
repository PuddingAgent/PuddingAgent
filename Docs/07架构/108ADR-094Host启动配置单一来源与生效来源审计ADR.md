# ADR-094：Host 启动配置的单一来源与生效来源审计

日期：2026-09-23。状态：**Proposed**（C-1 已实施）。决策责任：Host 组合与启动顺序、日志早期启动、URL/CORS/环境名等启动期边界；外部控制器负责部署与生命周期验收。

依据：[架构评审处置裁决](../Reports/ArchReview-Triage-2026-09-23.md)、[GPT-6 Astra 评审](../Reports/ArchReview-GPT6Astra-2026-09-23.md)。

## 背景（逐行事实）

`Source/PuddingHost/Hosting/PuddingApplicationHost.cs`：

| 关注点 | 现状 | 行 |
|---|---|---|
bootstrap 配置 | 单独构建：`appsettings.json` + `appsettings.{env}.json` + 环境变量；`ASPNETCORE_ENVIRONMENT` **在此直接读** | `:40-48` |
builder 配置 | 另建 `WebApplication.CreateBuilder(args)`，再加 `system.json` + 环境变量（+ 命令行） | `:54-66` |
URL 绑定 | `options.Urls` 优先；否则**只认环境变量** `ASPNETCORE_URLS`；都没设才 `UseUrls("http://0.0.0.0:8080")` | `:68-79` |
CORS | `builder.Configuration["Cors:AllowedOrigins"]` + 代码内联默认值 | `:118-127` |

两点必须点明：

1. `builder.Configuration` **已装载** appsettings / system.json / 环境变量 / 命令行，但 URL 段**绕过它直读 `Environment`**；
   而 `UseUrls` 一旦调用即**显式指定**监听地址 ⇒ 若配置里定义了 urls 而环境变量未设，会被静默压成 `8080`。
2. 同一启动过程存在**按用途分裂的解析口径**（日志用第一套、URL 用环境变量直读、CORS 用第二套），
   且**当前未启用入站请求日志**（全日志 grep `Request starting|Request finished` 0 命中）
   ⇒ 事后也无法回溯「到底哪个值生效」。

## 决策

1. **一次性生效配置（effective configuration）**：单一构建、带**来源与优先级记录**，
   供日志早期启动、URL、CORS、服务注册统一消费；**禁止任何组件再直读 `Environment`**。
2. **字段分层显式化**：`bootstrap-only`（日志早期、DataRoot、环境名）与 `runtime`（URL/CORS/业务）
   分开声明；runtime 字段**不得**在 bootstrap 阶段被静默定值。
3. **生效来源审计**：启动期输出一次**脱敏**审计（键 → 值指纹 + 来源 + 触发层：CLI / env / system.json / appsettings / 默认），
   同时写入启动日志，作为事后取证的唯一依据。
4. **URL 优先级显式分级**：`options.Urls` > 配置链（含 env / CLI）> 默认 `8080`；
   当**配置链与 options 同时给出**时，必须打印生效来源，**不允许静默覆盖**。
5. **CORS 默认值显式化**：内联默认源改为**具名默认来源**并计入审计（当前配置缺失时静默生效，事后不可查）。
6. **hot reload 不得改变启动期不可变字段**（URL、环境名）；触发即 `warn` 并忽略，且计入审计。

## 对既有设计的修订

- 修订 **URL 绑定段**：去掉对 `Environment.GetEnvironmentVariable("ASPNETCORE_URLS")` 的直读，改由生效配置消费。
  ⚠️ 这是**行为变更点**：在"配置里已有 urls"的环境会改变监听地址 ⇒ 必须在 **C-1 观测落地后二次批准**。
- 修订 **CORS 段**：保留 `Cors:AllowedOrigins` 键名与语义，仅把默认值来源显式化（不改默认值本身）。

## 备选方案与取舍

| 方案 | 评价 |
|---|---|
| **A** 保持现状，只把"配置里的 urls 不生效"写进文档 | 零风险；但"哪个值生效"仍不可查，跨环境漂移继续；不采用 |
| **B** 统一到 `builder.Configuration["urls"]` | 语义统一、改动小；但会**静默改变**配置了 urls 的环境的监听地址，无观测支撑；需灰度 |
| **C** 显式分级 + 启动来源审计（**推荐**），分两步落地 | 兼顾安全与可判定：C-1 零行为变更先可观测；C-2 再统一口径（需二次批准） |

## 发布与验收

**C-1（零行为变更，先做）**
- 启动打印脱敏来源审计（含 URL/CORS/环境名/DataRoot/日志级别）；
- 同一输入组合下，bootstrap、builder、URL、CORS 的 effective 值**一致**（不一致即失败）；
- hot reload 不改变启动期不可变字段。

**C-2（行为变更，二次批准）**
- 配置链中的 urls 生效；冲突路径显式打印生效来源；
- `Environment` 直读点**清零**（含 CORS 与环境名）。

覆盖矩阵：`env × CLI × system.json × appsettings` 四源组合 + 缺省路径 + 冲突路径 + DataRoot 覆盖 + 日志早期可用性。

## 2026-09-23 C-1 实施记录

**已落地（零行为变更）**：

- 新增 `Source/PuddingHost/Hosting/StartupConfigurationAudit.cs`：纯函数式审计（`ResolveUrlBinding` / `Build` / `RenderValue` / `Emit`）。
- `PuddingApplicationHost.CreateBuilder`：URL 绑定改为调用 `ResolveUrlBinding`，解析结果与旧实现**逐字等价**；
  在 CORS 之后输出一次生效来源审计（`Console.WriteLine`，与既有 `[Startup]` 输出同风格）。
  审计覆盖：`urls.effective` / `urls.options` / `urls.env` / `urls.configuration` / `Cors:AllowedOrigins` /
  `ASPNETCORE_ENVIRONMENT` / `DataRoot`（指纹脱敏）/ `Serilog:MinimumLevel`；并输出覆盖告警。
- 新增 `Tests/PuddingHost.Tests/Hosting/StartupConfigurationAuditTests.cs`（xunit，7 用例）。

**验证（实跑）**：

| 项 | 结果 |
|---|---|
定向 7 用例 | `失败 0 / 通过 7 / 总计 7` |
**行为不变矩阵**（12 组合对拍旧谓词） | 绿；**变异取红**：把 C-2 的“让配置链 urls 生效”提前注入 ⇒ `失败 3 / 通过 4`，报错为 `Expected ["http://0.0.0.0:8080"] / Actual ["http://config:5678"]` |
全量 `PuddingHost.Tests` | `失败 0 / 通过 119 / 总计 119`（启动路径改动，必须全量回归） |

**派生事实**：`Source/PuddingHost/*.json` 中**不存在** `urls` 键
⇒ C-2 的“静默改变监听地址”风险当前较低，但仍需在 C-1 观测到实际后台数据后再批。

**尚未做**：C-2（统一解析口径、清零 `Environment` 直读点）；尚未重启部署（C-1 产出为启动日志，需重启才可见）。

---

## 本次状态

**Proposed（整体未批准；其中 C-1 已于 2026-09-23 实施并验证，见上一节）。未重启、未部署。**

实施前必须由实施者回填（不得凭本 ADR 转述）：
1. `appsettings.json` / `system.json` 中**是否已有 urls 类键**（决定 C-2 是否构成静默行为变更）；
2. `options.Urls` 的实际来源（Desktop 与 Console 两个宿主分别确认）；
3. 是否存在其它 `Environment.GetEnvironmentVariable` 直读点（本 ADR 只核了 URL 段）。

外部控制器负责部署与生命周期验收。
