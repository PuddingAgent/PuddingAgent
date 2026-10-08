---
title: 2026-10-08 PuddingSsh A4 真实 OpenSSH 全矩阵报告
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: active
description: "PuddingSsh A4/S4 证据：真实 OpenSSH 上 16 个场景全绿（撤销/换 key/哈希记录、两类 .ssh 作用域互不回退、错误账号、断线、服务端忽略 TERM），并以客户端密钥指纹与目录快照取证；边界断言变异取红 3/3。"
categories: [docs, reports]
tags: [ssh, componentization, known-hosts, revocation, evidence]
related_docs: [Docs/12_features/SSH工具组件化设计与实施方案-2026-10-08.md, Docs/10_conventions/组件化交付规程.md, Docs/14_reports/2026-10-08-PuddingSsh-A3-协调器与假传输故障矩阵报告.md, Docs/14_reports/2026-10-08-PuddingSsh-A2-独立测试与信任验证报告.md, Docs/00_changelog/2026Year/10/2026-10-08-PuddingSsh组件A4真实全矩阵.md]
related_files: [TestScripts/ssh-probe/run-ssh-probe.ps1, TestScripts/ssh-probe/prepare-scope-dirs.sh, TestScripts/ssh-probe/setup-wsl-sshd.sh, Source/PuddingSsh.Probe/Program.ScopeScenarios.cs, Source/PuddingSshTests/ComponentBoundaryTests.cs, Source/PuddingSsh/code_map.md]
slug: reports-pudding-ssh-a4-real-openssh-matrix-2026-10-08
draft: false
---

# 2026-10-08 PuddingSsh A4 真实 OpenSSH 全矩阵报告

本报告是 [SSH 工具组件化设计与实施方案](../12_features/SSH工具组件化设计与实施方案-2026-10-08.md) **A4 / S4** 的执行证据：
「真实 OpenSSH 探针、边界断言与变异取红」，判据为
「两类 `.ssh`、明文/hash/撤销 known_hosts、加密私钥明确错误、错指纹、取消、非零码、断线、输出预算真实验证；
边界断言能检出故意加入的上层引用」。

## 1. 判据逐条对照

| 判据 | 场景 | 结果 |
|---|---|---|
| 两类 `.ssh` | `dirs` | `scope-a`（current_user 形态）与 `scope-b`（agent_private 形态）**各自用自己的密钥 + 自己的 known_hosts** 成功执行；缺文件 ⇒ `ssh.credential_unavailable`（phase=credentials），**不回退**到另一目录；两目录内容逐字节不变 |
| 明文 known_hosts | `knownhosts --expect accept` | 真实 `ssh-keyscan` 记录 ⇒ 建连 + 执行成功 |
| hash known_hosts | `knownhosts-hashed` | OpenSSH `ssh-keygen -H` 产出的 `\|1\|salt\|hash` 记录 ⇒ 建连 + 执行成功（1 次认证） |
| 撤销 known_hosts | `knownhosts --expect revoked` | 真实记录 + `@revoked` ⇒ `ssh.host_key_revoked`，**0 次认证** |
| 换 key | `knownhosts --expect rotated`、`dirs` 第 4 步 | 同一主机名记录了别的公钥 ⇒ `ssh.host_key_mismatch`，**0 次认证** |
| 加密私钥明确错误 | `encrypted` | `ssh.passphrase_required`，错误文本不含口令，0 次认证 |
| 错指纹 | `fingerprint` / `hostkey-reject` | `ssh.host_key_untrusted` / `ssh.host_key_mismatch`，均在认证之前 |
| 取消 | `cancel` | 1.52 s 收敛、`exit_signal=TERM`、远端 `SHELL_GONE` + `MARKER_ABSENT` |
| 非零码 | `coordinator`（`exit 3`） | `failed` + `exit_code=3` + `ssh.remote_exit_nonzero` |
| 断线 | `disconnect` | `ssh.connection_lost` + `execution_state=unknown` + `retry_safe=false`，10 ms 内收敛 |
| 输出预算 | `throughput` | 240 MiB 双流下捕获恒为 64 KiB、截断标记正确、Unicode 截断无 U+FFFD |
| **服务端忽略 TERM**（设计 §9.2 的关键语义） | `ignoreterm` | `cancelled` + `execution_state=**unknown**` + 无退出码/信号；3 s 后远端进程 `SHELL_ALIVE`，随后由探针清理 |
| 错误账号 | `auth`（`--user nosuchuser_xyz`） | `ssh.authentication_failed`，未提交命令；sshd 日志为 `Connection closed by invalid user … [preauth]` |
| 边界断言可检出上层引用 | 变异 A / B | 见 §3，**3/3 变红** |

## 2. 运行与结果

```powershell
powershell -File TestScripts/ssh-probe/run-ssh-probe.ps1
```

**16 个场景全部 exit 0、`failures = 0`**（服务端：WSL2 `Ubuntu 26.04.1 LTS` + `OpenSSH_10.2p1 Ubuntu-2ubuntu3.7`，夹具每次重建密钥）。
运行器为每个场景记录 `accepted_publickey_delta` **和实际使用的客户端密钥指纹**，因此「哪把密钥被用了」不是自述而是服务端证据。

`dirs` 场景的取证（本轮实测）：

```
scope_a_client_fp = SHA256:7frCJp0767ErtOVwBshn6Y4iuO2oqniTPo8VRABWSDU
scope_b_client_fp = SHA256:bJsvnHQcg9wYokY2iXSfo0TV2+qPiLbq+GsqubsZ5w8
dirs.auth_delta = 2   client_keys = 7frCJ…,bJsvn…
```

即：目录 A 的密钥与目录 B 的密钥**各被使用一次**，没有串用、没有回退；两目录快照（文件名 + SHA256）前后一致，
证明组件不写 `.ssh`（不新建目录、不改 key/known_hosts）。

`ignoreterm` 场景的取证：

```
status=cancelled  execution_state=unknown  exit_code=null  exit_signal=null
remote_evidence_after_cancel = SHELL_ALIVE|MARKER_ABSENT
```

远端进程在收到 TERM 后**仍然存活** ⇒ 「取消只是发送信号、不得声称远端已消失」不是保守措辞，而是被实测确认的事实。

## 3. 变异取红（S4）

| 变异 | 结果 |
|---|---|
| A：给**测试工程**加一条指向 `PuddingContextPolicy` 的 `ProjectReference` | 边界套 **3/5 变红**（`TestProject_MustOnlyReferenceTheComponent`、`Test_Dependency_Closure_…`、`Test_Process_Must_Not_Load_…`） |
| B：给**组件**加同样的 `ProjectReference` | 边界套 **3/5 变红**（同上；组件未使用该类型时元数据引用不出现，但声明闭包与进程闭包都抓到了） |
| 复原 | 两个 csproj `Get-FileHash` **逐位相同**；全量 **122/122** 复绿 |

## 4. 夹具与可复现性

- `setup-wsl-sshd.sh`：一次性 sshd（`/tmp/pudding-ssh-probe`，不碰系统 sshd；系统 `ssh.socket`/`ssh.service` 已在 A2 关闭）。
- `prepare-scope-dirs.sh`：生成两类 `.ssh` 目录（各一把客户端密钥，两把公钥都授权）、真实 `ssh-keyscan` 记录、
  `ssh-keygen -H` 哈希记录、`@revoked` 与「换 key」变体，并打印两把客户端密钥的指纹。
- 运行器：起停夹具、收紧私钥 ACL、逐场景断言、按场景核对服务端认证次数与客户端密钥。

## 5. 本次**未**覆盖（诚实边界）

- **Windows OpenSSH 服务端**（PowerShell/CMD 的编码与引号合同）：设计明确将其列为**另行交付**的切片，本次不测。
- **ACL/owner 检查与 `.ssh` 目录解析**（`current_user` 的 `Environment.GetFolderPath` 折衷、`agent_private` 的
  `AgentInstanceRoot`、Windows ACL 与 Unix 权限建议）：属引用方（Runtime，A5）。本次只验证「两类目录形状在组件侧
  互不回退、组件只读」，不验证宿主如何解析路径。
- 网络层故障（丢包、MTU、代理）、跳板机、PTY、Windows 服务端编码：不在切片 A 范围。
- 产品态验收（新构建是否已被运行实例加载、Core 停止时的连接回收）：A5/A6，必须由外部控制器判定。

## 6. 过程中修掉的运行器缺陷

1. `prepare-scope-dirs.sh` 的授权写入把公钥内容当文件名（`cat "$pub"`）⇒ 改为 `cat <file> >> authorized_keys`。
2. 上一轮给私钥收紧 ACL 后，下一轮 WSL 无法覆盖该文件 ⇒ 每次运行先 `icacls /reset` 再重建 scope 目录。
3. `ConvertTo-WslPath` 依赖 `Resolve-Path`，对「尚不存在的目标」报错 ⇒ 支持不存在的路径（纯字符串换算）。
4. `edit` 工具改写 `.ps1` 会**丢掉 UTF-8 BOM**，导致 PS 5.1 按 ANSI 解码并解析失败 ⇒ 每次编辑后必须重新写回 BOM；
   `TestScripts/check-script-encodings.ps1` 正是这条门禁。
