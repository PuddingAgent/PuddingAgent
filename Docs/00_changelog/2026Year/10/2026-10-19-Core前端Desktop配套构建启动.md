---
title: Core、前端与 Desktop 配套构建启动
author: hyfree
date: 2026-10-19
last_reviewed: 2026-10-19
status: archived
description: "按用户要求重新编译前端、Core 和 Desktop，部署同源配套包并确认启动及 Bridge 握手。"
categories: [docs, changelog]
tags: [browser, desktop, build, deployment]
related_docs: [Docs/07_architecture/111ADR-097Agent浏览器控制一期闭环与单一执行链ADR.md]
related_files: [TestScripts/build-browser-companion.ps1, Source/PuddingDesktop/PuddingDesktop.csproj, Source/PuddingHost/Build/PuddingHostContent.props, Source/PuddingPlatformAdmin/package.json]
slug: changelog-core-frontend-desktop-build-launch-20261019
draft: false
---

# Core、前端与 Desktop 配套构建启动

## 执行

按用户本轮“编译 Core、前端、Desktop，启动 Desktop”的要求，以 `20158a0cb8da1f92ae44636fb4697e6100c91bbf` 构建。未修改产品源码、前端版本或运行配置。工作树含其他未提交改动，配套清单明确标记 `dirty=true / development-repair`。

1. 前端 `pnpm run build`，通过 `PUDDING_ADMIN_OUTPUT_PATH` 输出到 `temp/build/browser-frontend-20158a0-full`；版本仍为 `6.9.6`。
2. 设置 `PuddingAdminDistPath` 指向该新前端目录，执行 `TestScripts/build-browser-companion.ps1 -OutputRoot temp/build/browser-live-20158a0-full -AllowDirty`。脚本串行 restore/build Desktop，并由 `BuildCoreBundle` 构建、发布独立 Core。
3. 确认没有运行中的 Desktop/Core 后，将旧安装目录备份到 `temp/build/browser-before-20158a0-full`，部署完整新包到现有 `temp/build/winui3/bin/PuddingDesktop/Debug/net10.0-windows10.0.19041.0/win-x64`，校验后启动 Desktop。沿用已保存的 `D:\data`，Core 由 Desktop 拉起。

## 验证

- 前端构建成功，包体积门禁通过：同步包 1,383,742 B、chat 364,693 B、common 437,649 B。
- Core/Desktop 配套构建：0 错误、31 个警告（包含现有 NuGet 安全公告等）；包内 1,624 个文件的 SHA-256 校验通过，两端协议均为 3。
- 新前端 159 个文件与 Core 的 `wwwroot/admin` 逐个一致。运行时 HTTP 返回的 `umi.1ba8d706.js` 哈希与本次构建一致；构建时间为 `2026-10-19T01:24:16.014Z`。
- Desktop PID 92516，窗口标题为 Pudding；其 Core 子进程 PID 107276、父 PID 92516。`/health/ready` 返回 HTTP 200。
- 09:26:02 的 Bridge Hello 成功，revision 为上述构建提交，protocol=3；两进程实际加载的 8 个关键模块均匹配配套清单。
- [启动与文件身份证据](../../../14_reports/Core前端Desktop配套构建启动-2026-10-19.json)。原始构建日志在 `temp/frontend-20158a0-full-build.log`、`temp/browser-20158a0-full-build.log`。

## 边界与回滚

本轮完成编译、部署、启动与握手验证，未新增真实模型测试消息；此前待验证的浏览器操作闭环仍待独立验收，不能据此宣布一期全部完成。决策边界见 [ADR-097](../../../07_architecture/111ADR-097Agent浏览器控制一期闭环与单一执行链ADR.md)。

运行包和回滚副本保留在 `temp/build`。回滚须先正常退出 Desktop 并确认 Core 退出，再恢复整份备份；不得只替换一侧协议程序集。
