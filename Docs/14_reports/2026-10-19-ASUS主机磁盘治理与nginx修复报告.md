---
title: ASUS 主机磁盘治理与 nginx 修复报告（2026-10-19）
author: 默认助手
date: 2026-10-19
last_reviewed: 2026-10-19
status: active
description: 记录 ASUS（192.168.100.10）根分区 100% 占满的根因定位、35G 空间回收、LVM 扩容至 232G、nginx 容器循环重启修复的完整证据链与可复用命令。
categories:
  - operations
  - report
tags:
  - ssh
  - disk
  - lvm
  - docker
  - nginx
  - rsyslog
related_docs:
  - ../../Docs/10_conventions/组件化交付规程.md
related_files:
  - D:/Keys/ASUS.md
slug: asus-disk-remediation-2026-10-19
draft: false
---

# ASUS 主机磁盘治理与 nginx 修复报告

> 目标主机：`192.168.100.10`（`asus`，Ubuntu 24.04.4 LTS，ASUSTeK MINIPC PN51-E1，AMD Ryzen 5 5500U / 30 GiB RAM）
> 处置时间：2026-10-19 16:35–16:47 CST
> 执行方式：`ssh root@…`（密码认证经 `SSH_ASKPASS` + `SSH_ASKPASS_REQUIRE=force`），远端脚本以 **base64 传输**避免引号注入

## 1. 结论摘要

| 指标 | 处置前 | 处置后 |
|---|---|---|
| `/` 容量 | 98G | **232G** |
| `/` 已用 | 94G | **59G** |
| `/` 可用 | **0** | **162G** |
| `/` 使用率 | **100%** | **27%** |
| rsyslog 写盘 | `No space left on device`（3 分钟 **83,706** 条） | **0 条** |
| `nginx` 容器 | `Restarting (1)` | **Up（RestartCount=0）**，`HTTP80=200` / `HTTPS443=200` |

净释放磁盘 **35G**，另扩容 **135G**（`ubuntu-vg` 原本有 135.42G 从未分配）。

## 2. 诊断：空间去向

`df -h /` → 94G used / 0 avail / 100%；`df -i /` → inode 仅 11%（**不是 inode 耗尽**）。

`du -x -h -d1 /` 排序后：

| 路径 | 大小 | 性质 |
|---|---|---|
| `/home/hyfree/.openclaw.uninstalled-data-20260831-103423` | **29G** | OpenClaw 卸载残留（`agents/` 28G） |
| `/home/hyfree/.local` | 9.8G | 用户级安装（`lib/` 7.6G） |
| `/usr` | 8.2G | 系统 + 多套内核 |
| `/var/lib/snapd` | 5.3G | 含 **11 个 disabled** 版本 |
| `/root` | 5.4G | `.local` 1.7G / `.vscode-server` 1.6G / `.bun` 948M |
| `/home/hyfree/apitable` | 4.8G | 项目数据 |
| `/home/hyfree/.cache` | 4.6G | 缓存 |
| `/home/hyfree/.npm` | 3.4G | npm 缓存 |
| `/var/log/journal` | 2.9G | `journald.conf` **无任何上限配置** |
| `/opt` | 2.9G | 第三方软件 |
| `/var/log/syslog.1` | 191M | 2026-06-07 的旧日志 |
| `/var/log/btmp` + `btmp.1` | 83M + 77M | 登录失败记录 |

## 3. 处置动作与回收

| # | 动作 | 回收 | 关键证据 |
|---|---|---|---|
| 1 | tar 备份关键小目录后删除 OpenClaw 残留 | **29G** | `TGZ_BYTES=504509` / `TGZ_ENTRIES=113` / `BACKUP_VERIFIED=yes` → 才 `rm -rf` |
| 2 | `journalctl --vacuum-size=500M` | 2.4G | 2.9G → **476.2M** |
| 3 | 删 `syslog.1`(191M) + `btmp.1`(77M) | 268M | `ROTATED_LOGS_REMOVED` |
| 4 | `snap remove` 11 个 disabled 版本 | ~2.5–3G | `SNAP_DISABLED_AFTER=0` |
| 5 | `rm -rf ~/.npm/_cacache` | 3.4G | 3.4G → **23M** |
| 6 | `apt-get clean` | 少量 | archives 已 76K |
| 7 | 新增 `/etc/systemd/journald.conf.d/limits.conf` | 防复发 | `SystemMaxUse=1G` / `SystemKeepFree=2G` |

**备份位置**：`/raid10/backup/openclaw-residual-20261019/keydirs.tgz`
覆盖 `credentials` `device_keys` `cron` `tasks` `bridge-device` `canvas` `completions` `flows`（113 条目）。
其余 29G（主体为 `agents/`）**不可恢复**。

## 4. LVM 扩容

```
vgs:  ubuntu-vg  235.42g  VFree 135.42g      ← 135G 从未使用
lvs:  ubuntu-lv  100.00g
lvextend -l +100%FREE /dev/ubuntu-vg/ubuntu-lv
  → changed from 100.00 GiB (25600 extents) to 235.42 GiB (60268 extents)
resize2fs /dev/ubuntu-vg/ubuntu-lv
  → on-line resizing required; now 61714432 (4k) blocks long
```

在线扩容，**未卸载、未重启、未中断服务**。完成后 `df -h /` → 232G / 59G / 162G / 27%。

## 5. nginx 循环重启修复

**根因**（`docker logs nginx`）：

```
[emerg] open() "/etc/nginx/logs/rustdesk/error.log" failed (2: No such file or directory)
```

**挂载关系**（`docker inspect nginx`）：

```
bind /raid10/docker/nginx/logs        -> /etc/nginx/logs        ro=true
bind /raid10/docker/nginx/conf.d      -> /etc/nginx/conf.d      ro=true
```

host 侧缺 `rustdesk/` 子目录 → nginx 启动即 `[emerg]` 退出 → 无限重启。
容器内是**只读挂载**，必须在 host 侧创建：

```bash
mkdir -p /raid10/docker/nginx/logs/rustdesk
docker restart nginx
```

验证（**等待 40 秒后复查，不取 1 秒快照**）：

```
nginx|Up 57 seconds
RESTART_COUNT=0
HTTP80=200
HTTPS443=200
最近 2 分钟 [emerg] 计数 = 0
```

## 6. 两处必须澄清的误判

**① `/memfd:doublemapper (deleted)` 的 2TB 不占磁盘**
`lsof +L1` 显示 `dotnet 4352 … 2199023255552 /memfd:doublemapper (deleted)`，进程为 `MoreNote.dll` / `Tasks-List-Server.dll`。`/memfd:` 是内存文件描述符（tmpfs），`SIZE/OFF` 为虚拟映射大小，**不落在 `/` 上**。按 2TB 追查会得到错误结论。

**② `ps -eo pcpu` 不是瞬时值**
`ps` 显示 `rsyslogd 123%`，但 `top -bn1` 中为 **0.0%**——`ps` 的 `%CPU` 是**进程生命周期平均值**（该进程已运行 16 天）。判瞬时必须用 `top -bn1` 或 `/proc/<pid>/stat`。

## 7. 未处理项（需逐项确认）

| 项 | 预期 | 风险 |
|---|---|---|
| `/root` 5.4G | 最多 4G | 中（丢 `.vscode-server` 缓存与 `.bun`） |
| `/home/hyfree/.cache` 4.6G | 最多 4.6G | 中（应用缓存重建） |
| `apt autoremove`（旧内核 6.8.0-124、5.15.x） | ~170M | 低但会卸载包集合，建议先 `--dry-run` |
| Docker 镜像 85 个 | **0**（数据在 `/raid10`） | 清了不释放 `/`，无必要 |

## 8. 复用手册（只读诊断）

```bash
df -h / ; df -i /
du -x -h -d1 / | sort -rh | head -20
du -x -h -d1 /var /var/lib /var/log | sort -rh | head -20
journalctl --disk-usage
lsof +L1 | awk '$7+0 > 1000000000'          # 注意 /memfd 是内存，不是磁盘
find /var/log -xdev -type f -size +30M -printf '%s\t%p\n' | sort -rn | head
snap list --all | grep -c disabled
top -bn1 | head -12                          # 判瞬时 CPU
docker ps -a --format '{{.Names}}|{{.Status}}|{{.Ports}}'
```

## 9. 纪律与边界

- 所有删除动作**先取 `df`/`du` 前值，后取后值**，数字均来自实测
- 唯一新增配置：`/etc/systemd/journald.conf.d/limits.conf`
- 除 `nginx` 外，未重启任何业务容器；未触碰 `/raid10` 数据；未修改 Docker 配置
- 主机信息与处置结论已同步至 `D:/Keys/ASUS.md`
