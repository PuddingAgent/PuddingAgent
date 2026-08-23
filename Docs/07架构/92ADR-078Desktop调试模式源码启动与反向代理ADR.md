# ADR-078：PuddingDesktop 调试模式——源码启动前后端与本机反向代理

> 状态：Accepted（代码与单元/集成测试已落地；真实 pnpm/dotnet 端到端 smoke 由开发者手动执行）
> 日期：2026-08-23
> 范围：`Source/PuddingDesktop`（Debug 组件、协调器、设置 UI）、`desktop.json` 配置模型、`Tests/PuddingDesktop.Tests/Debug`
> 目标：Desktop 一键进入「前端源码 + 后端源码」调试形态，本机 80 端口提供统一入口，替代 dev-up.py 的开发态角色

## 1. 决策摘要

1. Desktop 新增调试模式（设置页开关，持久化在 `%LOCALAPPDATA%\Pudding\desktop.json` 的 `debug` 节，配置文件优先）。启用并重启 Core 后，Desktop 用 `dotnet build` 从源码构建后端、用 `pnpm run start:dev` 拉起前端开发服务器，并在 `http://127.0.0.1:{ProxyPort}`（默认 80）提供反向代理统一入口。
2. **后端不换进程模型**：源码构建产物 `bin/Debug/net10.0/PuddingAgent.exe` 仍以 `--desktop-child` 协议由现有 `CoreProcessSupervisor` 监督（Ready 握手、健康检查、优雅关停、崩溃恢复、Browser Bridge 全部复用）；仅新增 `CoreProcessStartOptions.EnvironmentName` 把子进程环境从强制的 `Production` 覆盖为 `Development`（对齐 dev-up）。
3. **反向代理用 `System.Net.HttpListener` 实现，不引入 ASP.NET Core / YARP**：Desktop 的产品边界（AGENTS.md：不引用 ASP.NET Core）保持不变；代理与现有 `DesktopBootstrapHttpEndpoint` 同一技术栈。SSE 逐块 Flush 转发，WebSocket（前端 HMR）以 `AcceptWebSocketAsync + ClientWebSocket` 全双工消息中继，上游失败返回 502 文本。
4. **路由语义与 dev-up.py 的 Python 代理逐条对齐**（`BACKEND_PREFIXES` + SPA fallback），保证调试形态与开发态行为一致：`/api /swagger /health /healthz /metrics /assets /connectors /session-events` → Core；其余 → 前端 dev server；GET/HEAD 的 `/admin/xxx`（无扩展名深链）重写为 `/admin/`。
5. 状态机引入 `DesktopStartupState.DebugFailed`（构建失败 / pnpm 失败 / 80 被占），事件参数拆分 `CoreAddress`（控制面，Storage/Bridge/健康检查用）与 `WorkbenchAddress`（WebView2 加载源：调试模式=代理，生产=Core）。三组件（Core、前端、代理）全部就绪前 Workbench 不绑定。
6. 调试组件与 Core 生命周期解耦：停止 Core 不停前端与代理（用户可继续看静态 UI，/api 临时 502）；重启 Core 会重建后端但保持前端/代理运行；Desktop 退出时停代理 → 杀前端进程树 → 现有 Core 回收顺序。
7. 仓库根从 Desktop 可执行位置向上自动解析（需同时存在 `Source/PuddingAgent/PuddingAgent.csproj` 与 `Source/PuddingPlatformAdmin/package.json`），`debug.repositoryRoot`、`debug.frontendWorkingDirectory`、`debug.backendProjectPath` 可显式覆盖。

## 2. 关键实现

```
DesktopApplicationCoordinator
 ├─ BuildDebugBackendAsync   (stage 1: 校验+dotnet build，失败→DebugFailed)
 ├─ CoreProcessSupervisor    (bin/Debug/net10.0/PuddingAgent.exe --desktop-child, Development)
 ├─ StartDebugComponentsAsync(stage 2, fire-and-forget: 代理 + pnpm 前端, 与 Core 并行)
 │    ├─ DesktopReverseProxy (HttpListener 127.0.0.1:{ProxyPort})
 │    └─ FrontendDevSupervisor (cmd /c pnpm run start:dev -- --host 127.0.0.1 --port {FrontendPort})
 └─ GetWorkbenchAddress()    (调试=代理|生产=Core；DebugFailed 或前端未 Ready → null)
```

新增文件：`Source/PuddingDesktop/Debug/{ProxyRoutePlanner, DesktopReverseProxy, DebugRepositoryResolver, DebugBackendLauncher, FrontendDevSupervisor}.cs`；测试 `Tests/PuddingDesktop.Tests/Debug/*`（含真实回环监听器的代理集成测试：路由/头转发/404 透传/502/SSE 渐进到达/WS 中继回声）。

## 3. 约束与已知取舍

- 代理只绑 `127.0.0.1`（非管理员可用，与 8199 先例一致）；不做 0.0.0.0（http.sys 需要 URL ACL）。80 被占（dev-up、IIS）时进入 DebugFailed 并给出明确提示。
- WebSocket 中继不透传 permessage-deflate（.NET WebSocket 默认不压缩）；HMR 不受影响。
- `node_modules` 缺失时自动 `pnpm install`（10 分钟超时）；pnpm/dotnet 不在 PATH 时快速失败，错误信息自带日志尾部。
- 端口互斥在配置校验层拦截：代理端口不得等于前端端口或 Core 端口；与 dev-up 的互斥靠使用约定（先 `dev-up.py --down`）。
- 调试模式不改变产品发布路径（PublishCoreBundle、core/ 目录布局不受影响）。

## 4. 使用与诊断

见 `How-Debuge.md` §11.34（开启方式、端口互斥、日志位置、常见症状）。
