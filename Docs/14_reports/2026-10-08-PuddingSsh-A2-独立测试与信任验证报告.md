---
title: 2026-10-08 PuddingSsh A2 独立测试与信任验证报告
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: active
description: "PuddingSsh 组件 A2/S2 的证据：真实 OpenSSH fixture 驱动的 known_hosts/pin 验证、POSIX 封装、错误分类与 87 个独立用例，以及边界断言的变异取红结果。"
categories: [docs, reports]
tags: [ssh, componentization, known-hosts, testing, evidence]
related_docs: [Docs/12_features/SSH工具组件化设计与实施方案-2026-10-08.md, Docs/10_conventions/组件化交付规程.md, Docs/14_reports/2026-10-08-PuddingSsh-A1-库能力实测报告.md, Docs/00_changelog/2026Year/10/2026-10-08-PuddingSsh组件A2独立测试与信任验证.md]
related_files: [Source/PuddingSsh/Validation/SshKnownHostsParser.cs, Source/PuddingSsh/Validation/SshTrustSnapshot.cs, Source/PuddingSsh/Validation/SshPosixCommand.cs, Source/PuddingSsh/Diagnostics/SshFailureClassifier.cs, Source/PuddingSshTests/ComponentBoundaryTests.cs, Source/PuddingSshTests/Fixtures/fingerprints.txt, TestScripts/ssh-probe/make-known-hosts-fixtures.sh, Source/PuddingSsh/code_map.md]
slug: reports-pudding-ssh-a2-independent-tests-and-trust-2026-10-08
draft: false
---

# 2026-10-08 PuddingSsh A2 独立测试与信任验证报告

本报告是 [SSH 工具组件化设计与实施方案](../12_features/SSH工具组件化设计与实施方案-2026-10-08.md) **A2 / S2** 的执行证据：
独立测试工程、指纹验证（known_hosts / pin）、POSIX 封装与错误分类器。设计 §12.2 要求「以**真实 OpenSSH** 生成的
hashed 记录及撤销 fixture 验证，不靠自己生成的样例自证」，本报告给出该要求的兑现方式与结果。

## 1. 交付物

| 位置 | 内容 |
|---|---|
| `Source/PuddingSsh/Validation/` | `SshHostTokens`、`SshKnownHostsParser`、`SshTrustSnapshot`（含 known_hosts / pin 两个验证器）、`SshPosixCommand`、`SshRequestValidator` |
| `Source/PuddingSsh/Diagnostics/` | `SshFailureClassifier` 升级为「异常类别 + `SshFailureFacts(phase, submitted)` → `SshFailure(错误码, phase, retrySafe)`」，并保留 A1 的兼容入口；`SshErrorCodes` 新增 `ssh.request_invalid` |
| `Source/PuddingSshTests/` | 独立测试工程（**只引用 `PuddingSsh`**，`MSTest.Sdk/4.0.1`），87 个用例 |
| `Source/PuddingSshTests/Fixtures/` | 真实 OpenSSH fixture（明文 / 哈希 / 撤销 / 不支持形态 / 指纹与交叉核对结论） |
| `TestScripts/ssh-probe/make-known-hosts-fixtures.sh` | fixture 生成器（`ssh-keyscan` + `ssh-keygen -H` + `ssh-keygen -F` 交叉核对 + `ssh-keygen -lf`） |

## 2. 真实 OpenSSH fixture 的来源与自证边界

服务端：WSL2 `Ubuntu 26.04.1 LTS` + `OpenSSH_10.2p1 Ubuntu-2ubuntu3.7`（夹具主机密钥每次生成，仅公钥入库）。

| fixture | 来源 | 用途 |
|---|---|---|
| `known_hosts.plain` | `ssh-keyscan -p 22122 127.0.0.1`、`ssh-keyscan 127.0.0.1`、`ssh-keyscan -p 22122 ::1` | 明文精确名称；**真实** `[host]:port` 与 IPv6 方括号写法 |
| `known_hosts.hashed` | 上面这份的 `ssh-keygen -H` 输出 | 证明我们的 HMAC-SHA1 host token 与 OpenSSH **一致** |
| `known_hosts.revoked_same_key` | 明文 + `@revoked` 指向**出示的那把**密钥 | 撤销必须拒绝 |
| `known_hosts.revoked_other_key` | 明文 + `@revoked` 指向另一把密钥 | 撤销按密钥生效（轮换不被误杀） |
| `known_hosts.revoked_hashed` | 由 `ssh-keygen -F 127.0.0.1 -f known_hosts.hashed` 挑出的哈希行 + `@revoked` | 哈希记录的撤销 |
| `known_hosts.unsupported` | 通配、否定、`@cert-authority`、`\|2\|`、字段不足、非法 base64 各一行 | fail-closed |
| `fingerprints.txt` | `ssh-keygen -lf <key>.pub -E sha256` 与每个 token 的 `ssh-keygen -F` 结论 | 期望值来自 OpenSSH；测试断言「我们算的 SHA256 == OpenSSH 算的」 |

`fingerprints.txt` 中 4 个 token 的 `ssh-keygen -F` 结论均为 `present`（明文与哈希各一遍），
即 **OpenSSH 自己**确认这些记录按我们使用的 token 形式可被匹配。

**诚实标注**：生成时 `[::1]:22` 这条记录是 derived——`ssh-keyscan -6 ::1` 在该环境下未取回记录，
因此该行的主机字段由脚本用**真实主机公钥**写出并明确记录在 `derived_lines` 里；其余三条（含 `[127.0.0.1]:22122`、
`[::1]:22122`）都是 `ssh-keyscan` 的真实输出。

## 3. 测试结果

```powershell
dotnet test Source/PuddingSshTests/PuddingSshTests.csproj --artifacts-path temp/build/ssh --results-directory temp/test-out/ssh --nologo
```

`已通过! - 失败: 0，通过: 87，已跳过: 0，总计: 87`（构建 0 警告 0 错误）。分组覆盖：

| 组 | 用例数 | 关键断言（举例） |
|---|---|---|
| `SshTrustSnapshotRealFixtureTests` | 10 | 4 个 token 在明文与哈希 fixture 下都被信任；我们算的指纹与 `ssh-keygen -lf` 逐字相同；撤销同密钥拒绝 / 撤销他密钥仍信任；未知主机 `untrusted`、已登记换密钥 `mismatch`；不支持 fixture `known_hosts_unsupported` |
| `SshKnownHostsParserTests` | 11 | 明文/逗号/`\|1\|` 解析；`@revoked` 标记；通配/否定/CA/未知标记/非 v1 哈希/字段不足/非法 base64 一律 fail-closed 且**不入有效记录**；空文件可用但零信任；CRLF 容错 |
| `SshPinTrustTests` | 4 | pin 指纹由出示 blob 现算；接受 `ssh-keygen -lf` 原样文本；错误指纹/错误算法 ⇒ `mismatch`；空配置 ⇒ 参数错误或 `config_invalid` |
| `SshHostTokensTests` | 9 | 22 端口裸主机名、非 22 与 IPv6 的 `[host]:port`、已带方括号的输入、非法端口 |
| `SshPosixCommandTests` | 19 | `cd -- '…' && cmd` 精确串；`'`/`$`/反引号/双引号编码与**反向解码回到原值**；相对路径/`~`/空/NUL/换行/Tab 拒绝；默认 cwd 非法 ⇒ `config_invalid`（与请求非法区分）；命令 16 KiB **UTF-8 字节**边界（16383 通过 / 16386 拒绝） |
| `SshRequestValidatorTests` | 8 | 目标形状（host_id/revision/host/port/username）；限额只缩小（请求、主机上限、宿主 deadline 三者取最小） |
| `SshFailureClassifierTests` | 15 | 各异常映射；连接失败在**提交前/后**分别为 `connect_failed`/`connection_lost`；凭据阶段 IO/格式错误 ⇒ `credential_unavailable`；重试安全性矩阵闭合（只有未提交 + `dns_failed/connect_failed/busy` 为真） |
| `SshBoundedOutputCollectorTests` | 6 | 捕获受限但计数完整；双流共享预算；截断不产生 U+FFFD；**逐字节读取**（每字符跨 3 次读取）仍完整解码；固定缓冲 = 2×预算 |
| `ComponentBoundaryTests` | 5 | 检测器自检（含 `Renci.SshNet` 不得误报）；进程未加载上层/重依赖；声明闭包含组件与 `SSH.NET` 且无越界；组件元数据引用 = BCL + `SSH.NET`；测试工程闭包与元数据引用里 `Pudding*` 只有自己与组件 |

## 4. 变异取红（边界断言可证伪）

| 变异 | 结果 |
|---|---|
| 给**测试工程**加一条指向 `PuddingContextPolicy` 的 `ProjectReference` | 边界套 **3/5 变红**：`TestProject_MustOnlyReferenceTheComponent`、`Test_Dependency_Closure_Must_ContainComponent_AndNoForbiddenAssemblies`、`Test_Process_Must_Not_Load_Forbidden_Assemblies` |
| 给**组件**加同样的 `ProjectReference` | 边界套 **2/5 变红**：闭包与进程两条 |
| 两次复原 | csproj `Get-FileHash` **逐位相同**；全量 87/87 复绿 |

值得记录的发现：只查 `Assembly.GetReferencedAssemblies()` **抓不到未被使用的 `ProjectReference`**（编译器不产生元数据引用），
因此 `TestProject_MustOnlyReferenceTheComponent` 增加了对 `*.deps.json` 声明闭包的断言——这正是变异 1 第一次只红 2 条的原因。

## 5. 语义裁定（实现即合同，写在此处备查）

1. **看不懂的信任记录 = 整份快照不可用**。忽略 `!` 否定会*扩大*信任，忽略通配/CA 会误判「主机是否已知」，
   所以 known_hosts 里出现这类记录时返回 `ssh.known_hosts_unsupported`，而不是「跳过并继续信任」。
2. **撤销按密钥生效**（OpenSSH 语义）：出示的密钥命中 `@revoked` ⇒ `ssh.host_key_revoked`；
   被撤销的是另一把密钥时，已登记密钥仍被信任。否则「旧 key 标 revoked + 新 key 登记」的正常轮换会被整台主机误杀。
3. **pin 不信任调用方给的指纹字符串**：由出示的 blob 现算 SHA256（与 OpenSSH 同格式）再比较。
4. **新增错误码 `ssh.request_invalid`**：设计 §10 表未列请求参数拒绝（原本假定由工具层拦下），
   A2 引入 POSIX 封装与请求校验后组件必须能自己 fail-closed；它与 `ssh.config_invalid`（冻结配置非法）语义分离。
5. **`@cert-authority`/未知标记整行丢弃**：这些行的字段布局未必与普通记录一致，解析后放行等于信任一条语义不明记录。

## 6. 本次**未**覆盖（诚实边界）

- 并发准入、等待队列、`ssh.busy`、deadline 合成 —— A3。
- 真实 OpenSSH 侧的「撤销/换 key 必须被拒」端到端复现、两类 `.ssh` 目录、ACL/权限、错误账号、断线 —— A4/A5。
  （本次只在**纯组件**层用真实 OpenSSH 产物验证匹配语义；服务端不参与测试，测试不需要 sshd。）
- 组件在宿主中的装配、工具面与审批绑定 —— A5；产品态验收（新构建是否已被运行实例加载）—— A6。
- 测试工程未覆盖 `ISshClient` 协调器（尚不存在）；`ComponentBoundaryTests` 对 `PuddingSsh.Probe` 不设断言（探针不是组件）。
