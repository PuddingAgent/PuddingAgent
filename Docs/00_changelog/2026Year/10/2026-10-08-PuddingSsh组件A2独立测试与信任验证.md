---
title: 2026-10-08 PuddingSsh 组件 A2：独立测试、known_hosts/pin 验证与 POSIX 封装
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "按设计方案 A2/S2 补齐 PuddingSsh：独立测试工程（87 用例，真实 OpenSSH fixture 驱动 known_hosts/pin 验证）、POSIX 执行封装、请求校验与完整错误分类器；边界断言变异取红；未接宿主。"
categories: [docs, changelog]
tags: [ssh, tools, componentization, known-hosts, testing]
related_docs: [Docs/12_features/SSH工具组件化设计与实施方案-2026-10-08.md, Docs/10_conventions/组件化交付规程.md, Docs/14_reports/2026-10-08-PuddingSsh-A2-独立测试与信任验证报告.md, Docs/14_reports/2026-10-08-PuddingSsh-A1-库能力实测报告.md]
related_files: [Source/PuddingSsh/Validation/SshKnownHostsParser.cs, Source/PuddingSsh/Validation/SshTrustSnapshot.cs, Source/PuddingSsh/Validation/SshPosixCommand.cs, Source/PuddingSsh/Validation/SshRequestValidator.cs, Source/PuddingSsh/Diagnostics/SshFailureClassifier.cs, Source/PuddingSshTests/PuddingSshTests.csproj, Source/PuddingSshTests/ComponentBoundaryTests.cs, Source/PuddingSshTests/Fixtures/fingerprints.txt, TestScripts/ssh-probe/make-known-hosts-fixtures.sh, Source/PuddingSsh/code_map.md, code_map.md]
slug: changelog-pudding-ssh-a2-tests-and-trust
draft: false
---

# 2026-10-08 PuddingSsh 组件 A2：独立测试、known_hosts/pin 验证与 POSIX 封装

## 目标 / 背景

执行[SSH 工具组件化设计与实施方案](../../../12_features/SSH工具组件化设计与实施方案-2026-10-08.md) 的 **A2 / S2**：
「独立测试工程、指纹验证、POSIX 封装、错误分类器」，判据为「纯测试只引用组件；无 Core、Desktop、数据库依赖」。
按[组件化交付规程](../../../10_conventions/组件化交付规程.md)，**S5 之前不改宿主**：本次未登记 `PuddingAgentNetwork.slnx`、
未加消费方引用、未进 DI、未改 Runtime/Host/Desktop。

## 改动

### 组件新增 `Source/PuddingSsh/Validation/`

- `SshHostTokens.cs` — 新增。known_hosts 主机 token：22 端口裸主机名，非默认端口与 IPv6 用 `[host]:port`；不做 DNS/地址互换。
- `SshKnownHostsParser.cs` — 新增。解析明文精确名称、逗号分隔名称、`|1|salt|hash` 哈希主机与 `@revoked`；
  通配/否定模式、`@cert-authority`、未知标记、非 v1 哈希、语法或公钥非法的记录一律**整行丢弃并 fail-closed**；
  提供 OpenSSH 同款 `ComputeHashedHostToken`（HMAC-SHA1）。
- `SshTrustSnapshot.cs` — 新增。冻结信任快照（`known_hosts` / `pin` 两种来源互斥、无首次信任）与单目标验证器：
  未知主机 `ssh.host_key_untrusted`、已登记换密钥 `ssh.host_key_mismatch`、命中撤销 `ssh.host_key_revoked`；
  pin 指纹由**出示的 blob** 现算；`ComputeSha256Fingerprint` 与 OpenSSH 同格式。
- `SshPosixCommand.cs` — 新增。`cd -- '<POSIX 单引号编码的 cwd>' && <原始 command>`；cwd 必须干净绝对路径，
  命令必填、拒绝 NUL、UTF-8 ≤ 16 KiB。
- `SshRequestValidator.cs` — 新增。目标/请求形状校验与有效限额合成（`min(请求, 主机上限, 宿主 deadline)`）。

### 组件升级

- `Diagnostics/SshFailureClassifier.cs` — 升级为 `Classify(Exception, SshFailureFacts)` → `SshFailure(错误码, phase, retrySafe)`；
  刻意区分「提交前 `connect_failed`」与「提交后 `connection_lost`」，凭据阶段的 IO/格式错误映射为 `credential_unavailable`；
  重试安全性闭合（只有未提交 + `dns_failed/connect_failed/busy`）。保留 A1 的兼容入口。
- `Diagnostics/SshDiagnostics.cs` — 新增 `ssh.request_invalid`（请求形状非法）。设计 §10 表未列该码，
  因为原本假定请求参数由工具层拦下；A2 引入封装与校验后组件必须能自己 fail-closed，故显式分离它与 `ssh.config_invalid`。
- `Transport/SshNetTransportSession.cs` — 改用带事实的分类器，结果里的 `retry_safe` 来自分类器而非就地推断。
- `PuddingSsh.csproj` — 新增 `InternalsVisibleTo("PuddingSshTests")`（仅对本组件测试工程；规程反例指的是向上层调用方开放）。

### 测试与夹具

- `Source/PuddingSshTests/` — 新增独立测试工程（`MSTest.Sdk/4.0.1`，**只引用 `PuddingSsh`**），87 个用例：
  `SshTrustSnapshotRealFixtureTests`(10)、`SshKnownHostsParserTests`(11)、`SshPinTrustTests`(4)、`SshHostTokensTests`(9)、
  `SshPosixCommandTests`(19)、`SshRequestValidatorTests`(8)、`SshFailureClassifierTests`(15)、
  `SshBoundedOutputCollectorTests`(6)、`ComponentBoundaryTests`(5)。
- `Source/PuddingSshTests/Fixtures/` — 新增**真实 OpenSSH**产物：`ssh-keyscan` 明文记录、`ssh-keygen -H` 哈希记录、
  `@revoked` 三种变体、不支持形态，以及 `fingerprints.txt`（`ssh-keygen -lf -E sha256` 指纹 + 每个 token 的
  `ssh-keygen -F` 交叉核对结论）。只含公钥，服务端主机密钥每次生成。
- `TestScripts/ssh-probe/make-known-hosts-fixtures.sh` — 新增 fixture 生成器；`[::1]:22` 一行属 derived
  （该环境下 IPv6 默认端口扫描未取回记录），已在文件内显式标注。

### 文档与索引

- `Docs/14_reports/2026-10-08-PuddingSsh-A2-独立测试与信任验证报告.md` — 新增（完整证据与语义裁定）。
- `Source/PuddingSsh/code_map.md`、`code_map.md`（§2 组件行状态 + §5 测试工程行）— 更新。
- `.gitattributes` — 新增 `Source/PuddingSshTests/Fixtures/** text eol=lf`：fixture 是真实 OpenSSH 产物
  （生成时 LF），固定为 LF 以免检出后与 `ssh-keygen` 的输出形态不一致。

## 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 独立测试 | `dotnet test Source/PuddingSshTests/PuddingSshTests.csproj --artifacts-path temp/build/ssh --results-directory temp/test-out/ssh` | **87/87 通过**，构建 0 警告 0 错误 |
| 哈希记录语义 | 同上（真 fixture） | `ssh-keygen -H` 产出的哈希主机记录全部匹配 ⇒ 我们的 HMAC-SHA1 host token 与 OpenSSH 一致 |
| 指纹原语 | 同上 | 我们算的 `SHA256` 与 `ssh-keygen -lf -E sha256` **逐字相同**（ed25519 与 rsa 各一条） |
| 边界变异取红 | 临时给测试工程加 `ProjectReference → PuddingContextPolicy` | 边界套 **3/5 变红**；复原后 csproj 哈希逐位相同、全绿 |
| 边界变异取红 | 临时给组件加同样的 `ProjectReference` | 边界套 **2/5 变红**；复原后哈希逐位相同 |
| A1 真实协议回归 | `powershell -File TestScripts/ssh-probe/run-ssh-probe.ps1` | 7 场景 exit 0、`failures = 0`（A2 改动后重跑，未回归） |
| 脚本编码门禁 | `TestScripts/check-script-encodings.ps1 -Recurse -AllowUnrunnable` | 未引入新的 BROKEN（本组 `.sh` 由 `.gitattributes` 的 `*.sh text eol=lf` 保障 LF） |

值得记录的发现：只查 `Assembly.GetReferencedAssemblies()` **抓不到未被使用的 `ProjectReference`**
（编译器不为其产生元数据引用），第一次变异只红 2 条；因此 `TestProject_MustOnlyReferenceTheComponent`
增加了对 `*.deps.json` 声明闭包的断言，变异后稳定红 3 条。

## 未完成 / 后续

- **A3**：`ISshClient` 协调器（并发准入 2/主机、Core 总并行 8、等待队列 32、deadline 合成、`ssh.busy`）与假传输故障矩阵。
- **A4**：真实 OpenSSH 全矩阵（两类 `.ssh`、撤销/换 key 的端到端拒绝、错误账号、断线、服务端忽略 TERM）与组件侧变异取红。
- **A5（S5）**：登记解决方案、Runtime 适配（目录/ACL/revision/凭据 + identity 文件名与 path 逃逸校验）、三个 Agent 工具与 DI 装配。
- 本次**未**验证：并发/排队、`.ssh` 目录与权限、服务端侧撤销复现、产品态验收；测试不需要 sshd（服务端不参与），
  端到端协议证据仍由 A1 探针提供。
- 环境副作用的清理：`apt install openssh-server` 曾触发 WSL 的系统 `ssh.socket`/`ssh.service`（端口 22 出现系统监听），
  已 `systemctl disable --now` 关闭；夹具始终使用自己的配置与密钥，不读系统 sshd 配置。
- 未提交任何 `temp/` 产物；未读写 `D:\data`；未使用生产主机。
