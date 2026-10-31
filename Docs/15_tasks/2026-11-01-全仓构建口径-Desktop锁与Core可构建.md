# 2026-11-01 全仓构建口径：Desktop 锁 vs Core 可构建

## 结论（实测）

| 构建目标 | 结果 |
|---|---|
| `PuddingAgentNetwork.slnx`（全仓） | **CS 错误 0** · MSB 错误 56 —— **全部**归属于 `PuddingDesktop.csproj`（56/56） |
| `Source/PuddingAgent/PuddingAgent.csproj`（Core 入口 / 部署目标） | **0 error / 44 warning**，warning 全为 `NU1902`(16) + `NU1903`(28)，无一来自编译器或分析器 |

## 那 56 条 MSB 是什么

`MSB3027` / `MSB3021`：复制产物到 `Source/Pudding.DesktopConnection/bin/Debug/...` 失败，因为**桌面端进程正在运行并锁住自己的 DLL**。

⇒ 这是「宿主在跑」的必然现象，**不是代码问题**，也**不影响 Core 部署**（Core 的构建链里不含 Desktop 产物）。

## 因此

1. **不要用 `slnx` 构建结果判断仓库健康** —— 它在桌面端运行时必然失败；
2. 需要一次干净的全仓构建时，先关掉 `PuddingDesktop`（pid 会变，按进程名找）；
3. 判断「能不能部署」看的是 **Core 入口工程能否 0 error 构建**，不是 slnx；
4. 那 44 条 warning 是 NuGet 漏洞通告（`NU1902`/`NU1903`），属依赖版本层面的已知项，与本次改动无关 —— 本次触碰的文件（`GrepLineMatch.cs` / `GrepMatcher.cs` / 两个 `ComponentBoundaryTests.cs` / 迁移后的 `CodeQueryToolHelperTests.cs`）**0 warning**。

## 复现

```
dotnet build PuddingAgentNetwork.slnx -v q --nologo
dotnet build Source\PuddingAgent\PuddingAgent.csproj -v q --nologo
```

第二条是部署前真正该看的那条。
