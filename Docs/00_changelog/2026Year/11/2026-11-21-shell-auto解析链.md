---
title: shell auto 解析链改为 pwsh7 > ps5 > cmd / bash > sh
author: hyfree
date: 2026-11-21
last_reviewed: 2026-11-21
status: active
description: 修正 shell 工具的 auto 模式：Windows 按 pwsh7 → Windows PowerShell 5.1 → cmd 解析，非 Windows 按 bash → sh；缺失时降级而不是失败。
categories: [changelog, tools, shell]
tags: [shell, auto, pwsh, powershell, bash]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Shell/HostShellExecutor.cs, Source/PuddingRuntime/Tools/BuiltIns/Shell/HostShellTool.cs, Source/PuddingRuntimeTests/Tools/HostShellToolTests.cs]
slug: shell-auto-resolution-chain
draft: false
---

# shell auto 解析链

## 改动

`HostShellExecutor.ResolveShell` 的 `auto` 分支：

```csharp
if (!OperatingSystem.IsWindows())
    return CommandExists("bash") ? "bash" : "sh";

return CommandExists("pwsh") || CommandExists("powershell.exe")
    ? "powershell"
    : "cmd";
```

配套：`BuildStartInfo` 增加 `case "sh"`（与 `bash` 共用 `-lc`，只作内部降级目标，
不对外暴露为可传值）；`HostShellTool` 的参数描述写明解析链。

## 「pwsh 优先于 ps5」原本就是对的

`ResolvePowerShellExecutable` 已经优先 `pwsh`、再退回 `powershell.exe`。
真正缺的是两件事：

1. 两种 PowerShell 都不存在时**直接失败**。auto 的语义是「选一个能跑的」，
   不是「必须给我 PowerShell」，此时应降级到 `cmd`；
2. 非 Windows 上 `bash` 缺失也直接失败，未退回 `sh`。

## 为什么 auto 不选 wsl

曾考虑把 wsl 也纳入 auto 探测（优先级排在 PowerShell 之前）。否掉的理由是本机实测四条：

| 问题 | 后果 |
|---|---|
| 路径形态 | WSL 只认 `/mnt/d/...`，而模型写的是 `D:\...` |
| `cwd` 传参 | 传 `D:\...` 给 WSL 直接失败，除非逐处转换 |
| git 行为 | WSL 侧 `core.autocrlf` 未设、Windows 为 `true`，同一仓库两侧 `git status` 不同 |
| SDK | Windows 侧有 .NET 10 且已构建，WSL 里没有 |

auto 该选的是「与平台默认一致」，不是「能力最强」——模型的先验是平台默认。
`wsl` 与 `git bash` 保留为显式选项。

## 可观测性

结果里的 `Shell` 与 `Executable` 分别记录「解析出的方言」与「实际 exe」，
所以 auto 选中 pwsh 还是 powershell.exe 可以直接看出来，不需要猜。

## 验证

`HostShellToolTests` **16/16** 通过，含新增
`HostShellExecutor_AutoMode_OnWindows_PrefersPwsh7`：断言 `Shell=powershell` 且
`Executable=pwsh`（本机 PowerShell 7.7.0）。这条断言会强制「有 pwsh 时必须用 7」。
