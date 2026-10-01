# Desktop 图标与启动样式修复（2026-10-01）

## 改动

- 复用 `Source/PuddingPlatformAdmin/public/assets/images/logo.png` 的 Pudding 品牌头像，转换为 `Source/PuddingDesktop/Assets/Pudding.ico`，含 16、20、24、32、40、48、64、128、256px 九档 PNG 帧。原图为 128px；256px 帧为缩放版本。`TestScripts/update-pudding-desktop-icon.ps1` 可重建资源，不需要额外依赖。
- `ApplicationIcon` 嵌入 EXE 文件图标；ICO 随 Build / Publish 复制到 `Assets`。`MainWindow` 通过 `AppWindow.SetIcon` 设置窗口/任务栏图标。
- `DesktopIcon.LoadTrayIcon` 按系统小图标尺寸调用 Win32 `LoadImage`；托盘复用该句柄，包括 Explorer 重启后的重新注册。释放托盘后销毁图标；安装回调失败也释放句柄；Dispose 可重复调用。
- 两处工具区 DropDownButton 样式引用不存在的 `DefaultDropDownButtonStyle`，在主窗口加载时触发 `XamlParseException`。移除 `BasedOn`，保留控件提供的默认模板及现有属性设置。

## 编译问题的实际结论

修改前 Desktop 定向构建与全解决方案构建均为 **0 错误**，未复现 C# / XAML 编译错误。实际复现并修复的是上述 **运行时 XAML 资源解析错误**；编译成功不能发现这个问题。没有为不存在的编译错误改动 Core 或依赖包。

## 验证

串行执行 Desktop 构建、发布和生命周期验证，所有测试使用隔离 DesktopHome、DataRoot 和端口，不访问生产 `D:\data`：

- Desktop Debug 构建通过；Debug 与 Release 完整发布均成功，两个发布包均含 `Assets/Pudding.ico` 和独立 Core/Web。
- 最终 `dotnet build PuddingAgentNetwork.slnx --artifacts-path temp/build/recovery --no-restore -m:1 --nologo`：**0 错误、63 条既有警告**。首次全量构建为 0 错误、1815 条警告；增量结果不表示这些警告已修复。
- Windows `Icon.ExtractAssociatedIcon` 能从 Debug EXE 提取 32px 图标；ICO 目录解析确认九档尺寸。
- `TestScripts/test-pudding-desktop-launcher.ps1` 对新 Debug 发布包通过。Shell PID `42000`；Core PID `37780 → 34264 → 17488`。覆盖主窗口/Web 加载、托盘隐藏/恢复、运行中心、工具标签页/分栏、Core 重启/崩溃恢复、停止后 Shell 可用。
- 外部确认 Shell 与三个 Core PID 均退出、DataRoot 租约释放、端口释放。启动器成功注册的是加载自 Pudding ICO 的托盘句柄，已不使用系统默认图标。

证据保存在忽略的 `temp/desktop-icon-*.log`、`temp/desktop-icons-acceptance.json`。收尾尝试删除本任务专用发布目录与隔离测试目录（先校验路径位于仓库 temp 内），自动审批返回 `blocked by policy`，未绕过；这些目录仍保留在 temp 中，不入库。共享构建缓存保留，避免删除其他任务产物。没有部署到生产或推送远端，没有执行真实模型 smoke；没有将此验证扩大为所有 DPI / Explorer 重启 / 任务栏固定快捷方式缓存的视觉验收。
