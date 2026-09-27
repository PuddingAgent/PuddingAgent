# Desktop Foundation

独立 BCL 叶组件，当前只包含 Shell 展示状态，不启动 Core、不打开业务数据库。

| 文件 | 职责 |
|---|---|
| `WorkContext.cs` | 角色实例、主会话、文档来源和类型合同 |
| `ShellState.cs` | 角色选择代次、草稿隔离、文档身份/归属与关闭行为 |
| `ShellLayout.cs` | 有界宽度与窄窗口自动折叠，不覆盖用户偏好 |
| `SkeletonSettingsStore.cs` | 隔离骨架配置原子保存；坏配置保留并报告 |
| `PuddingDesktop.Foundation.csproj` | 编译期拒绝任何项目/包引用；输出限于 temp/build/winui3 |

独立测试：`Source/PuddingDesktop.FoundationTests`。组件不负责真实角色注册、执行授权或 Core 生命周期；后续 DLL 内核通过独立合同装配。
