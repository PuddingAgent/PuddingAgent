---
title: 2026-11-02 Computer Use CU0 技术探针（第一轮）
author: 鲸鲸
date: 2026-11-02
last_reviewed: 2026-11-02
status: draft
description: "按 Computer Use 设计文档 §14 的 CU0 切片做技术探针：验证 FlaUI.UIA3 在 .NET 10 本机可用，锁定依赖版本，并记录尚未验证的探针项。"
categories: [docs, report]
tags: [computer-use, uia, flaui, cu0, probe]
related_docs: [Docs/12_features/Computer-Use代码设计与分阶段施工方案-2026-10-20.md]
related_files: []
slug: report-2026-11-02-computer-use-cu0-probe
draft: false
---

# CU0 技术探针（第一轮）

## 背景

设计文档 §14 把 CU0 定义为「技术探针：FlaUI/UIA3、Unicode、GDI、DPI、取消与 Worker 回收；
**只在 temp/ 原型或独立测试夹具验证，不改宿主**，选定并锁定依赖版本」。
§15 进一步把「UIA 依赖」列为进入实现前必须冻结的事项：「FlaUI.UIA3 候选，CU0 验证 .NET 10 和目标应用后锁定版本」。

也就是说，**先验证 UIA 依赖能否在 .NET 10 上跑**，否则后面 CU1–CU4 的契约设计都建立在假设上。

## 本轮做了什么

在 `temp/cu0-probe/` 建了一个**不进解决方案、不参与产品构建**的最小控制台工程
（`net10.0-windows`，`EnableDefaultCompileItems=false`，只引用 `FlaUI.UIA3`），
枚举桌面顶层窗口并打印名称与类名。

## 结果

```
FLAUI_OK framework=10.0.12
windows=21 enumMs=1647
  WIN name=任务栏            class=Shell_TrayWnd
  WIN name=软件更新          class=#32770
  WIN name=微信              class=mmui::LoginWindow
  WIN name=Pudding           class=WinUIDesktopWin32WindowClass
  WIN name=deepseek-harness - 文件资源管理器  class=CabinetWClass
  WIN name=ChatGPT           class=Chrome_WidgetWin_1
  WIN name=新升级提示        class=new_update_tip
  WIN name=Ndd               class=CCNotePad
RESULT: OK
```

## 结论

| 项 | 结论 |
|---|---|
| FlaUI.UIA3 5.0.0 + .NET 10.0.12 | **可用** |
| 中文/Unicode 窗口名 | **正常**（任务栏、微信、软件更新等直接读出，无乱码） |
| 顶层窗口枚举 | 21 个，耗时 **1647 ms** |
| UIA 依赖版本 | 可锁定 **FlaUI.UIA3 5.0.0** |

顺带一条对设计的实证支持：枚举结果里同时出现 `WinUIDesktopWin32WindowClass`（本产品窗口的类名）
与 `Chrome_WidgetWin_1`、`CabinetCWall` 等——说明设计 §1「本 Pudding 进程排除、只操作用户已打开并授权的窗口」
**是可以按类名/进程身份判定的**，不需要额外机制。

## 本轮未验证（CU0 剩余项）

设计把 CU0 写成一组探针，本轮只做了 UIA 依赖这一项。仍未验证：

- **Unicode 文本输入**（`SendInput` 对中文/emoji 的送达率）
- **GDI 截图**与 DPI 缩放下的坐标换算
- **取消**语义（动作执行中途取消）
- **Worker 进程回收**（Job Object 与用户应用进程归属的边界）

## 下一步

CU0 剩余四项探针 → 全部通过后进入 CU1（Contracts/Computer + Automation，
S1–S4 独立门禁，只加新类型、不挂 union、不动宿主）。

CU5 才接宿主，且以前面所有独立门禁通过为前提。
