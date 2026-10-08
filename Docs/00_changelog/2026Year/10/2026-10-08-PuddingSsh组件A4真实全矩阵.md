---
title: 2026-10-08 PuddingSsh 组件 A4：真实 OpenSSH 全矩阵与变异取红
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "按设计方案 A4/S4 交付真实协议全矩阵：明文/hash/撤销 known_hosts、两类 .ssh 作用域互不回退、错误账号、断线、服务端忽略 TERM；16 场景全绿（含服务端认证次数与客户端密钥指纹取证），边界断言变异取红 3/3；未接宿主。"
categories: [docs, changelog]
tags: [ssh, tools, componentization, known-hosts, testing]
related_docs: [Docs/12_features/SSH工具组件化设计与实施方案-2026-10-08.md, Docs/10_conventions/组件化交付规程.md, Docs/14_reports/2026-10-08-PuddingSsh-A4-真实OpenSSH全矩阵报告.md, Docs/14_reports/2026-10-08-PuddingSsh-A3-协调器与假传输故障矩阵报告.md]
related_files: [Source/PuddingSsh.Probe/Program.ScopeScenarios.cs, Source/PuddingSsh.Probe/ProbeOptions.cs, Source/PuddingSsh.Probe/ProbeSupport.cs, TestScripts/ssh-probe/run-ssh-probe.ps1, TestScripts/ssh-probe/prepare-scope-dirs.sh, Source/PuddingSshTests/ComponentBoundaryTests.cs, Source/PuddingSsh/code_map.md, code_map.md]
slug: changelog-pudding-ssh-a4-real-openssh-matrix
draft: false
---

# 2026-10-08 PuddingSsh 组件 A4：真实 OpenSSH 全矩阵与变异取红

## 目标 / 背景

执行[SSH 工具组件化设计与实施方案](../../../12_features/SSH工具组件化设计与实施方案-2026-10-08.md) 的 **A4 / S4**：
真实 OpenSSH 探针全矩阵 + 边界断言变异取红。按[组件化交付规程](../../../10_conventions/组件化交付规程.md)，
**S5 之前不改宿主**：未登记 `PuddingAgentNetwork.slnx`、未加消费方引用、未进 DI、未改 Runtime/Host/Desktop。

## 改动

### 探针新增五个真实协议场景（`Source/PuddingSsh.Probe/`）

- `Program.ScopeScenarios.cs` — 新增（`Program` 改为 `partial`）：
  - `knownhosts`（`--expect accept|revoked|rotated`）：真实 known_hosts 记录 / `@revoked` / 换 key；
  - `dirs`：两类 `.ssh` 形态互不回退、缺文件不回退、换 key 拒绝、两目录只读（快照比对）；
  - `ignoreterm`：服务端 `trap '' TERM HUP`，取消后必须 `execution_state=unknown`，并核对远端进程仍存活后清理；
  - `disconnect`：命令执行中 `kill -9 $PPID` 切断连接 ⇒ `connection_lost` + 状态未知 + 不重放；
  - `auth`：错误账号 ⇒ `authentication_failed`，不提交命令。
- `ProbeOptions.cs` / `ProbeSupport.cs` / `Program.cs` — 升级：新增 `--known-hosts` / `--identity-dir` /
  `--identity-name` / `--scope-a` / `--scope-b` / `--rotated-known-hosts`；`--expect` 扩展为四种取值；
  身份提供者改为**严格按请求里的（绝对目录 + 文件名）读取**，不再持有固定路径（更接近交付路径的冻结语义）。

### 运行器与夹具

- `TestScripts/ssh-probe/prepare-scope-dirs.sh` — 新增：生成 `scope-a`（current_user 形态）、`scope-b`
  （agent_private 形态，另一把客户端密钥，两把公钥都授权）、真实 `ssh-keyscan` 记录、`ssh-keygen -H` 哈希记录、
  换 key 变体，并打印两把客户端密钥指纹。
- `TestScripts/ssh-probe/run-ssh-probe.ps1` — 升级：新增 8 个场景（`knownhosts-accept/revoked/rotated/hashed`、
  `dirs`、`ignoreterm`、`disconnect`、`auth`），并为**每个场景**记录服务端认证次数与**实际使用的客户端密钥指纹**；
  修复三处缺陷（见下）。

### 文档与索引

- `Docs/14_reports/2026-10-08-PuddingSsh-A4-真实OpenSSH全矩阵报告.md` — 新增（判据逐条对照 + 取证）。
- `Source/PuddingSsh/code_map.md`、`code_map.md` — 状态与门禁更新。

## 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 独立测试 | `dotnet test Source/PuddingSshTests/PuddingSshTests.csproj --artifacts-path temp/build/ssh --results-directory temp/test-out/ssh` | **122/122 通过**，0 警告 0 错误 |
| 真实协议全矩阵 | `powershell -File TestScripts/ssh-probe/run-ssh-probe.ps1` | **16 场景全 exit 0、`failures = 0`** |
| 边界变异取红 | 临时加 `ProjectReference → PuddingContextPolicy`（测试工程 / 组件各一次） | 两次都 **3/5 变红**；复原后 csproj 哈希逐位相同、全量复绿 |
| 脚本编码门禁 | `TestScripts/check-script-encodings.ps1 -Recurse -AllowUnrunnable` | 新增 `.sh` 为 LF；`.ps1` 保持 UTF-8 BOM、解析通过 |

关键实测：

- `knownhosts-revoked` / `knownhosts-rotated`：`ssh.host_key_revoked` / `ssh.host_key_mismatch`，**0 次认证**。
- `knownhosts-hashed`：OpenSSH `ssh-keygen -H` 的 `|1|salt|hash` 记录在真实服务端上通过（1 次认证）。
- `dirs`：`auth_delta=2`，服务端日志显示目录 A 与目录 B 的**客户端密钥指纹各出现一次**；缺文件 ⇒
  `ssh.credential_unavailable`（phase=credentials，不回退）；两目录快照前后逐字节相同。
- `ignoreterm`：`execution_state=unknown`、无退出码/信号，取消 3 秒后远端进程仍 `SHELL_ALIVE`。
- `disconnect`：`ssh.connection_lost` + `unknown` + `retry_safe=false`，10 ms 收敛。
- `auth`：`ssh.authentication_failed`；sshd 日志 `Connection closed by invalid user … [preauth]`。

## 过程中修掉的问题

1. `prepare-scope-dirs.sh` 的授权写入误把公钥内容当文件名（`cat "$pub"`）⇒ 改为按文件追加。
2. 上一轮收紧私钥 ACL 后，下一轮 WSL 无法覆盖 ⇒ 每次运行先 `icacls /reset /T` 再重建 scope 目录。
3. `ConvertTo-WslPath` 依赖 `Resolve-Path`，对「尚不存在的目标」直接报错 ⇒ 改为支持不存在的路径。
4. **`edit` 工具改写 `.ps1` 会丢掉 UTF-8 BOM**（PS 5.1 随即按 ANSI 解码、脚本解析失败）⇒ 每次编辑后重新写回 BOM，
   并把 `check-script-encodings.ps1` 当作必跑门禁。此条对后续所有脚本编辑都适用。

## 未完成 / 后续

- **A5（S5）**：登记解决方案、Runtime 适配（`.ssh` 目录解析、identity 文件名与 path 逃逸校验、主机 ACL/revision、
  凭据读取与清零）、三个 Agent 工具（`ssh_hosts`/`ssh_test`/`ssh_execute`）与 DI 装配、宿主组合测试。
- 明确不在本切片：Windows OpenSSH 服务端（编码/引号合同另行交付）、网络层故障、跳板机/端口转发/PTY。
- 产品态验收（新构建是否被运行实例加载、Core 停止后的连接回收）由外部控制器在 A5/A6 判定。
- 未提交任何 `temp/` 产物；未读写 `D:\data`；未使用生产主机；未触碰宿主任何工程。
