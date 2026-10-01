# 桌面能力通道：交付交接与外部验收单（ready-for-external-deploy）

> 状态：**`ready-for-external-deploy`**（2026-10-01，第 49 轮）
> 依据：AGENTS.md 的两段式验收——内部开发 Agent 交付 `ready-for-external-deploy`，
> 由**进程外控制器**重启到明确的新构建，随后由 Pudding 内新会话完成功能 smoke，
> 最终启动/重启/崩溃恢复/退出回收结论仍由外部控制器判定。

## 1. 一句话结论

能力通道（Desktop 主动 gRPC 双向流 + Core 下发能力命令）的**全部组件、协议、双侧配置、
集中不变量、可执行验收手段**已完成并验证；**唯一剩余动作是组合根装配 + 重启验收**，
它必须由外部控制器在重启窗口内执行。

## 2. 已验证的证据（可复现）

| 项 | 结果 | 复现命令 |
|---|---|---|
| 组件独立测试 | **424 用例全绿**（Contracts 96 / Rpc.Protocol 20 / DesktopConnection 80 / DesktopService 124 / CapabilityBroker 78 / AspNetCore 26） | `dotnet test <各 csproj> -c Release --artifacts-path temp\build\recovery` |
| 真实端点探针 | **53/53，exit 0**（Named Pipe 与 Loopback h2c 各一轮；含跨侧能力集合一致性守卫） | `dotnet temp\build\recovery\bin\Pudding.Rpc.IpcProbe\release\Pudding.Rpc.IpcProbe.dll` |
| WinUI 调度器适配器 | 0 错误 0 警告（`--no-incremental`） | `dotnet build Source\PuddingDesktop.CapabilityHost\... --no-incremental` |
| 窗口检查脚本 | 四场景与期望一致（2/2/1/1），输出无凭据 | `powershell -File TestScripts\test-capability-channel-window.ps1 -DataRoot <路径> [-Endpoint <描述>]` |

> 构建/测试输出只落 `temp\build`、`temp\test-out`。**本系列全程未停止或重启任何运行中的 Pudding 进程。**

## 3. 已交付的组件（全部独立可测，未接入产品）

| 程序集 | 职责 |
|---|---|
| `Pudding.Contracts` | 平台无关接口 / DTO / 消息定义（BCL-only 叶子；编译期边界测试） |
| `Pudding.Rpc.Protocol` | proto 与生成代码（字段号 / oneof 分支名 / 分支计数由快照测试冻结） |
| `Pudding.DesktopConnection` | Desktop 侧：主动建流、握手、世代、取消/期限、幂等、背压、字节预算 |
| `Pudding.DesktopService` | 准入 → DispatcherQueue → UI 线程；预算/版本推进不变量；DOM 脚本生成与解析 |
| `Pudding.CapabilityBroker`(+`.AspNetCore`) | Core 侧：会话、关联、授权接缝、Kestrel 装配、配置绑定、启动预检、迁移路由与使用统计 |
| `PuddingDesktop.CapabilityHost` | WinUI 调度器适配器（窗口期接真实 `CoreWebView2`） |
| `Pudding.Rpc.IpcProbe` | 真实端点探针（14 项能力端到端断言） |

**14 项能力全部端到端可用**：`webview.navigate / webview.execute_javascript / webview.page_state /
shell.notification / shell.status / shell.dialog / shell.file_picker / shell.clipboard（只读）/
browser.snapshot / browser.locate / browser.interact / browser.wait_for / browser.contexts / browser.tabs`。

## 4. 外部控制器需执行的步骤

详细补丁位置与配置样例见
[接线手册](Desktop-Capability-Channel-Wiring-Runbook-2026-10-01.md)。摘要：

1. **装配（Core 侧已应用，2026-10-01）**：`PuddingApplicationHost` 绑定
   `Desktop:CapabilityChannel` 配置 → `AddCapabilityChannel` → **显式绑定 REST 与能力通道两者**
   （`Kestrel.Listen*` 会覆盖 `UseUrls`）→ `MapCapabilityChannel`；实现
   `ControlTokenCapabilityAuthenticator`（复用常数时间校验，**不得使用 AllowAll**）与
   `ToolRuntimeDesktopCapabilityAuthorizer`；在就绪协议里发布端点描述。
2. **重启到新构建**（默认 `Enabled=false`，行为与今天一致）。
3. 打开 `Enabled` 重启，跑窗口检查脚本：
   `test-capability-channel-window.ps1 -DataRoot <路径> -Endpoint <就绪描述>`；
   **`IsHealthy` 必须为真**（脚本会明确区分"REST 被 `Listen*` 覆盖"与"能力端点没绑上"）。
4. 按脚本列出的 5 项 MANUAL 清单逐条确认：Desktop 拨入 / 无凭据拒绝 / 断连收尾 / 注册表清空 / 回滚演练。
5. 回滚：`Enabled=false` 重启即回到今天的行为（可秒级回退）。

## 4.1 关键事实：第 4/6/7 步**不需要** PuddingDesktop 也能验收

探针已支持真实 Core 端点模式（`--endpoint "<就绪描述>"`），它本身就是**一个 Desktop 侧对端**，
因此下列验收项可在**不改动 PuddingDesktop** 的情况下先行完成：

| 验收项 | 探针覆盖方式 |
|---|---|
| 4 Desktop 拨入与握手 | 探针连真实 Core：世代 ≥ 1、能力交集符合 `Grantable` |
| 6 无凭据/错凭据被拒 | 探针用错误令牌连接 ⇒ `unauthenticated`，且 Core 侧不产生会话 |
| 7 断连收尾 | 探针退出 ⇒ Core 注册表清空、管道释放、Core 存活 |

需要真实 PuddingDesktop 的只有「**用产品 UI 走完整业务操作**」（第 8 步的功能 smoke）——
那需要 WinUI 表面（已完成 Shell 侧端口 + 适配层；浏览器侧待映射到既有 `IBrowserRuntime` 抽象）。

## 5. 尚未完成（明确登记，均依赖第 4 节）


| 项 | 状态 | 依赖 |
|---|---|---|
| `PuddingHost` 组合根装配 | ✅ **已应用**（`Enabled=false` 时零行为变更；`PuddingAgent` 编译通过） | — |
| `PuddingDesktop` 组合根装配 + `WebView2DesktopUiSurface` | ⛔ 未应用 | 需真实 `CoreWebView2` 与 `DispatcherQueue`（下一步） |
| 能力授权器接 Tool Runtime 准入 | ⛔ 暂用 `DenyAll`（fail closed） | 需切片 D 在调用点提供可信身份 |
| `WebView2DesktopUiSurface` 接真实 `CoreWebView2` | ⛔ 未实现 | 需真实 `DispatcherQueue` 验证；平台无关逻辑（DOM 脚本、预算、版本不变量）已完成并测试 |
| 切片 D：七个浏览器工具调用点迁移 | ⛔ 未开始 | C-3 上线 |
| 切片 F：默认传输切换 + 退役旧 Bridge | 🟡 判据就绪 | C-3 上线且通道被证明在用（`DesktopTransportUsage.CanRetireLegacyBridge`） |

## 6. 风险与注意事项

- **跨传输回退会重复执行**：`DesktopTransportRouting` 已强制"本次操作尝试过通道后绝不回退"；
  调用点迁移时必须使用该规则，不得自行加 fallback。
- **`Listen*` 覆盖 `UseUrls`**：装配时必须显式绑定 REST，否则 REST 会静默消失（有预检与测试守）。
- **剪贴板/路径属隐私**：内容与路径只回传调用方，不进日志与审计（契约层 `ToString()` 只给形状）。
- **交互类单窗口互斥**：对话框与文件选择器共用交互槽位，第二个并发请求被拒（`ui_unavailable`）。
- **不可提交项**：`Docs/Features/Index-Retrieval-Known-Defects-2026-10-01.md` 属他方并行 WIP，本系列从未触碰。

## 7. 本系列刻意记录的三类真实教训

1. **"能编译 ≠ 已测试"具体化**：探针（真实端点）抓到 4 个单测看不见的缺陷——取消不响应、
   正常关闭被标 `Faulted` + 槽位泄漏、联合缺字段导致陈旧 Ref 保护静默失效、交互结果未记录版本。
2. **新增分支/实现方必须一次改全**：能力声明集合 6 处、接口全部实现方、映射的多处聚合点；
   曾因"接口新增成员未同步测试夹具"提交过红状态，随后以"接口 + 全部实现 + 验证同一提交"闭环纠正。
3. **临时脚本与编码陷阱**：PS 5.1 读无 BOM 的 UTF-8 脚本按 ANSI（脚本须带 BOM）；
   `ErrorActionPreference=Stop` 下原生命令 stderr 变终止错误；扫描构建产物须排除 `obj\` 下 ref/refint。
