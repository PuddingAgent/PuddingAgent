---
title: 2026-10-06 登录态 JWT：有效期改 7 天，密钥改由 security.json 提供（移除代码硬编码兜底）
author: hyfree
date: 2026-10-06
last_reviewed: 2026-10-06
status: archived
description: 排查登录授权 JWT 有效期时发现两处结构性问题：默认有效期只有 8 小时；签名密钥在代码里带硬编码兜底， 而本该承载它的 <DataRoot>/config/security.json 从未进入配置链（写了却不生效）。本轮把有效期统一为 7 天（168 小时）， 并把 security.json 接入配置链、删除代码内密钥兜底：密钥缺失即启动失败（fail closed），随包模板的 key 留空， 由引导期生成 48 字节 CSPRNG 密钥并原子写回。
categories: [docs, changelog]
tags: [jwt, security, 配置链, fail-closed, bootstrap]
related_docs: [Docs/12_features/LoginFeature.md, Docs/12_features/P0-工具审批授权链路信任根加固-复核与实施计划-2026-09-21.md, Docs/14_reports/G92-1-验收证据与缺口矩阵-2026-09-17.md]
related_files: [Source/PuddingPlatform/Services/JwtTokenFactory.cs, Source/PuddingPlatform/Controllers/Api/AuthApiController.cs, Source/PuddingPlatform/Controllers/Api/BootstrapApiController.cs, Source/PuddingHost/Hosting/PuddingApplicationHost.cs, Source/PuddingHost/Hosting/PuddingDataRootBootstrapper.cs, Source/PuddingHost/default-data/config/security.json, Source/PuddingCore/Configuration/PuddingConfigModels.cs, Tools/Dev/dev-up.py, Source/PuddingAgent/Properties/launchSettings.json]
slug: changelog-2026-10-06-登录jwt有效期七天与security-json密钥来源
draft: false
---

# 2026-10-06 登录态 JWT：有效期改 7 天，密钥改由 security.json 提供（移除代码硬编码兜底）

## 目标 / 背景

排查「前端登录后授权 JWT 的有效期」时确认了两个结构性问题：

1. **有效期只有 8 小时**，且来源分散：配置键 `Jwt:ExpiryHours`（缺省 `8`）、`Tools/Dev/dev-up.py` 注入 `8`、
   `Source/PuddingAgent/Properties/launchSettings.json` 注入 `24`，三处口径不一致。
2. **签名密钥在代码里带硬编码兜底**（`?? "Pudding-Platform-JWT-DevKey-…"`，两个签发点 + 组合根共三处），
   而本该承载它的 `<DataRoot>/config/security.json` 的 `jwt` 段**从未进入配置链**——只被
   `PuddingFileConfigLoader.LoadSecurityAsync` 反序列化，没有任何读取点，属"写了却不生效"的死配置
   （既有诊断：`Docs/14_reports/G92-1-验收证据与缺口矩阵-2026-09-17.md` §41，处置选项 B）。

## 改动

- `Source/PuddingPlatform/Services/JwtTokenFactory.cs`（新增）——登录态 JWT 的**唯一**配置解析与签发入口：
  `PuddingJwtSettings.FromConfiguration` 读 `Jwt:Key/Issuer/Audience/ExpiryHours`，缺省有效期 **168 小时（7 天）**；
  密钥缺失或短于 16 字符 → 抛 `InvalidOperationException`（错误信息直接指名 `<DataRoot>/config/security.json` 的 `jwt.key`），
  **不再回退到任何硬编码密钥**；`ExpiryHours` 显式配置但非正整数同样 fail closed。
  `JwtTokenFactory.CreateToken` 合并原先在两个控制器里各写一遍的签发逻辑（HS256 + `sm2_sig` 载荷签名）。
- `Source/PuddingPlatform/Controllers/Api/AuthApiController.cs`、`BootstrapApiController.cs` ——
  删除各自的私有 `GenerateJwt`（连同其中的硬编码密钥兜底），改为调用工厂；行为不变，口径单一。
- `Source/PuddingHost/Hosting/PuddingApplicationHost.cs` —— 配置链加入
  `<DataRoot>/config/security.json`（`optional: true, reloadOnChange: true`，位于 `system.json` 之后、
  环境变量/命令行之前，因此环境变量仍是最高优先级运维覆盖）；JwtBearer 的密钥/Issuer/Audience
  改由 `PuddingJwtSettings` 提供，并在启动日志记录 `ExpiryHours/Issuer/Audience` 与密钥指纹（沿用
  `StartupConfigurationAudit.Fingerprint`，不输出密钥材料）。
- `Source/PuddingHost/Hosting/PuddingDataRootBootstrapper.cs` —— 新增 `EnsureSecurityJwtKey`：
  `config/security.json` 缺失则按模板创建；`jwt.key` 缺失/短于 32 字符/等于随包占位符时，
  生成 48 字节 CSPRNG 密钥并**原子写回**（临时文件 + `File.Move`），其余字段原样保留；文件非法 JSON 直接失败。
  这样既没有"公开的默认密钥"，又无需人工首配。密钥内容不入日志。
- `Source/PuddingHost/default-data/config/security.json` —— `expiryHours` 8 → **168**，`key` 改为空串（由引导期生成）。
- `Source/PuddingCore/Configuration/PuddingConfigModels.cs` —— `PuddingJwtConfig.ExpiryHours` 默认 8 → 168。
- `Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.Platform.cs` —— Session `IdleTimeout`
  8 小时 → **7 天**，与 JWT 有效期同口径（Session 只是 JWT 缺失时的兼容回退，不应更早失效）。
- `Tools/Dev/dev-up.py`、`Source/PuddingAgent/Properties/launchSettings.json` —— 删除 `Jwt__Key/Issuer/Audience/ExpiryHours`
  四个环境变量覆盖：它们会盖住 `security.json`，正是"两份口径"的来源。开发态现在同样以 `security.json` 为准。
- `<DataRoot>/config/security.json`（**就地升级**，本机 `D:\data\config\security.json`）——
  `expiryHours` 改为 168；原 `key` 是随包占位符，下次启动由引导期替换为随机密钥（旧备份留在 `temp/security.json.bak-20261006`）。
- 测试：`Source/PuddingPlatformTests/Services/JwtTokenFactoryTests.cs`（新增 6 项：缺密钥/过短/非法有效期 fail closed、
  缺省 168、显式值优先、令牌生命周期 168 小时）；`Tests/PuddingHost.Tests/Hosting/PuddingDataRootSecurityBootstrapTests.cs`
  （新增 4 项：首次引导生成并幂等保留密钥、占位符被替换且其余字段保留、非法 JSON fail closed、
  落盘文件经配置链解析后就是 `PuddingJwtSettings` 的来源）；`Source/PuddingWebApiTests/CustomWebApplicationFactory.cs`
  改为在测试 DataRoot 落盘 `security.json`（密钥与 `JwtHelper.TestKey` 一致），因为生产代码已无密钥兜底。

## 影响与需要知道的行为变化

- **有效期 8 小时 → 7 天**；前端无刷新令牌、也不解析 `exp`，仍然只是被 401 被动弹回登录页（本次不改前端）。
- **开发/桌面实例重启后需要重新登录一次**：本机 `security.json` 的旧密钥是占位符，会被引导期换成新随机密钥，
  此前签发的令牌全部失效。之后密钥持久化在文件里，重启不再换。
- Core 启动若读不到可用密钥（文件被删/被写成过短值且引导期无法写盘）会**启动失败并给出明确指引**，
  这是刻意的 fail closed：宁可起不来，也不再用公开默认密钥接受任意伪造的管理员令牌。
- 桌面产品路径不变：DesktopChild 仍只注入 `ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT`，密钥取
  `<DataRoot>/config/security.json`（`D:\data` 为开发 DataRoot）。

## 验证

- `dotnet build Source/PuddingAgent/PuddingAgent.csproj --artifacts-path temp/build/jwt-security` →
  **0 错误**（194 个既有警告，均为历史存量）。无 `--artifacts-path` 的构建当前**不可用**：开发态 Core 进程
  （PID 26320）正占用 `Source/PuddingAgent/bin/Debug`，属环境限制，非本轮改动引起。
- `dotnet test Tests/PuddingHost.Tests` → **250/250 通过**（含本轮新增 4 项）。
- `dotnet test Source/PuddingWebApiTests` → **167/173**。6 项失败全部定位为既有环境/结构问题，与本轮无关：
  ① 4 项 `GoalApiContractTests`（replay/conflict/queries）单独跑 **7/7 通过**——失败源于多个测试类并发时
  共用进程级 `PUDDING_DATA_ROOT` 造成的相互干扰；
  ② 2 项 `SessionEventsControllerTests.Compact_*` 报 `.pudding-host.lock` 被占用：它们用 `WithWebHostBuilder`
  在同一 DataRoot 起**第二个宿主**，而 `Program.cs` 的 `PuddingDataRootLease` 是 `FileShare.None` 独占租约。
  该租约在 `Program.cs` 中先于 `CreateBuilder` 获取，早于本轮任何代码执行，因此可判定为改动前既有行为。
  这 167 项里包含登录/Bootstrap/会话等全部需要 Bearer 令牌的链路——即"令牌由 `security.json` 的密钥校验通过"。
- `dotnet test Source/PuddingPlatformTests` → **1433/1435**（含本轮新增 6 项，全部通过）。2 项失败
  （`DefaultSeedCases_ShouldNotLeakBenchmarkOrSystemHints`、`RefreshWorkspace_Starts_Stdio_Server_And_Preserves_Codex_Thread_Result`）
  是 `--artifacts-path` 改变 `AppContext.BaseDirectory` 层级后破坏了这两条用例的**相对路径假设**：
  改用原生输出布局单独跑 → **2/2 通过**。
- 未验证：真实浏览器登录后令牌 `exp` 为 7 天（需重启开发栈并抓包，属外部控制器窗口）；本次未启动/重启 dev-up，
  因此运行中的 Core 仍是旧配置（重启后才生效）。

## 未完成 / 留白

- `sm2_sig` 仍是"只签不验"：签发点附带 ECDSA 载荷签名，但全仓库没有验签点（既有缺口，本轮未扩大范围）。
- 密钥轮换/吊销没有入口：换密钥 = 直接改文件重启，历史令牌全失效；无 refresh token。
- `PuddingFileConfigLoader.LoadSecurityAsync` 现在与配置链并存（前者仍无调用方）；后续可择一收敛。
- `Docs/18_superpowers/specs/2026-05-18-data-config-e2e-foundation-design.md` 仍写着 `expiryHours: 8` 与占位密钥，
  属历史设计快照，未改动。
