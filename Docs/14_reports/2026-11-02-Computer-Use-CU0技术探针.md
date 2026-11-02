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

## 第二轮：GDI 采集与 DPI（探针 #2）

只读屏幕、不产生任何输入，不干扰用户窗口。

```
VIRTUAL_SCREEN  0,0 4608x1440
PRIMARY_SCREEN  2560x1440
DPI             system=96 deviceCaps=(96,96) scale=1.00x monitors=2
DPI_AWARENESS   code=0
CAPTURE         ok=True rect=(2104,600,400,240) ms=21
PIXELS          sampled=96000 meanLuma=248.3 nonBlackRatio=1.000
MAPPING         imageX=200 -> screenX=2304 (expect 2304)
RESULT: OK
```

### 关键发现：默认进程是 **DPI unaware**

`DPI_AWARENESS code=0` 即 `DPI_AWARENESS_UNAWARE`。当前系统 DPI 是 96（1.00x），
所以还看不出偏差；一旦用户把显示缩放调成 125% / 150%（Windows 上很常见），
unaware 进程会同时吃到两个错误：

- 拿到的窗口/光标坐标是**被系统虚拟化过的逻辑坐标**，与真实物理像素不一致；
- `BitBlt` 截出的图会被系统拉伸，尺寸也与实际不符。

设计 §9.3 写的是「DPI 上下文的设置/抓图/恢复必须在同一线程同步完成」——方向是对的，
但对 unaware 进程来说**坐标系从起点就是错的，事后再同步设置也补不回来**。
因此 CU1/CU3 应把这条前置条件写死：**Worker 进程启动时即声明 per-monitor-v2 awareness**，
并配一条断言测试（读取自身 awareness 必须等于 per-monitor-v2）。

### 其它结论

| 项 | 值 | 说明 |
|---|---|---|
| 虚拟屏幕 | 0,0 — 4608x1440（双屏） | 副屏在主屏右侧 |
| 主屏 | 2560x1440 | — |
| GDI BitBlt 400x240 | **21 ms** | 单帧开销可接受 |
| 画面非黑比例 | **1.000** | 该区域无 GPU/受保护内容黑屏 |

### 本轮暴露的未覆盖项

- **负坐标副屏未验证**：本机副屏在主屏右侧，虚拟屏原点仍是 (0,0)，
  设计 §7.1「左侧/上方副屏可为负数」的分支在本机无法覆盖，需要多显示器布局或模拟。
- Unicode 输入送达率、取消语义、Worker 回收仍未验证。

## 第三轮：Job Object 回收语义（探针 #3）

设计 §9.2 有一条硬约束：「目标应用不能加入 Worker 的 Job Object，否则停止执行器会杀掉用户应用」。
它到底有多危险，取决于 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 是否真的连带成员进程。实测：

```
JOB created handle=756
JOB kill_on_close set=True
CHILD pid=57084
JOB assigned=True
CHILD exited=True afterMs=5 exitCode=0
RESULT: OK killed_on_job_close
```

**5 毫秒**内成员进程就被回收（子进程是一个无害的 `timeout /t 30`，未碰任何用户窗口）。

### 含义

1. 这条约束**不能只写在文档里**：应用进程一旦误入 Job，用户未保存的文档会被直接终结，
   而且没有补救窗口（5 ms）。
2. 设计 §14 说「应用启动必须由 Supervisor 的专用应用启动端口执行，目标应用不能加入 Worker 的
   Job Object」——这一条必须落成**代码级不变量**：`AssignProcessToJobObject` 只允许对 Worker
   自身调用，并配一条断言测试（构造一个"尝试把外部进程加入 Job"的路径，必须被拒绝）。

### 仍未覆盖

- **取消语义**：设计 §9.1 要求「宿主超时只表示不再等待，不能宣称 COM 已取消」。
  这条要验证的是 FlaUI/UIA 调用在 `Task.WhenAny` 返回后**确实仍在执行**，需要专门的探针。
- Unicode 输入送达率（需短暂抢焦点，等用户确认时机）。
- 负坐标副屏。
