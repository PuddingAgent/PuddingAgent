# Desktop Foundation

独立 BCL 叶组件，包含 Shell 展示状态与可独立测试的内核生命周期协调；不引用 Host、不打开业务数据库。

| 文件 | 职责 |
|---|---|
| `WorkContext.cs` | 角色实例、主会话、文档来源和类型合同 |
| `ShellState.cs` | 角色选择代次、草稿隔离、文档身份/归属与关闭行为 |
| `ShellLayout.cs` | 有界宽度与窄窗口自动折叠，不覆盖用户偏好 |
| `SettingsCatalog.cs` / `SettingsCatalog.json` | 原生设置的 17 分类 / 49 页签 / 64 卡片、字段搜索与迁移任务来源；纯静态目录，无业务调用 |
| `SkeletonSettingsStore.cs` | 隔离骨架配置原子保存；坏配置保留并报告 |
| `PuddingDesktop.Foundation.csproj` | 编译期拒绝任何项目/包引用；输出限于 temp/build/winui3 |

独立测试：`Source/PuddingDesktop.FoundationTests`。组件不负责真实角色注册、执行授权或 Host 装配。`InProcessKernel` 串行化启动/停止、取消与失败恢复；`IDesktopServices` 提供展示端口；`IKernelSessionFactory` 由 Composition 适配 PuddingHost。18 项独立测试通过。
