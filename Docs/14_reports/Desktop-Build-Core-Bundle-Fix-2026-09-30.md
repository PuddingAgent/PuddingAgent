# 普通 Desktop 构建缺少 Core：修复记录

## 问题与原因

用户在 VS 的普通 Debug 输出启动 WinUI，工作台提示 `Cannot find PuddingAgent.exe`。恢复实现只在 Publish 后打包 Core，普通 Build 没有配套子进程。上轮 smoke 显式配置了其他构建的 Core 路径，因此未覆盖这条路径。

另一个问题是启动设置允许把 `Source/PuddingAgent/bin/Debug/net10.0` 这样的目录字符串保存为 CoreExecutablePath，并显示保存成功；实际上启动器需要完整的可执行文件路径。

## 修复

- `BuildCoreBundle` 在普通 Build 后构建并打包本次源码的独立 Core 到 Desktop 输出的 `core/`，含配套 Web 静态文件。Core 中间产物位于 `temp/build/desktop-launcher-core`；不通过程序集引用把业务 Host 装入 Shell。
- 设计时构建不执行打包；关闭 VS 快速跳过构建的检查，让只有 Core 源码改变时也能进入 MSBuild 并更新配套 Core。
- 自定义路径保存前检查扩展名、文件存在性和目录误填，在修改配置文件前返回错误；显式错误路径不会悄悄退回另一个 Core。留空使用配套 Core。
- 生命周期脚本默认将 Core 路径留空，并断言实际所选路径是该 Desktop 的 `core/PuddingAgent.exe`；保留显式 `-CoreExe` 用于有意的诊断覆盖。

## 验证

验证只使用隔离 DesktopHome、DataRoot 和端口，不停止用户现有 Desktop、不修改本机已保存的路径、不启动 `D:\data` 上的 Core。

- 普通 `dotnet build Source/PuddingDesktop/PuddingDesktop.csproj --artifacts-path temp/build/desktop-startup-0930` 成功，0 错误；208 条既有依赖与编译警告。输出包含 `core/PuddingAgent.exe` 与 `core/wwwroot/admin/index.html`。该命令没有调用 Desktop Publish。
- 配套 `PuddingAgent.dll` 与本次 Core 构建产物 SHA-256 一致：`32812D90F7CF332331BF0862C1C0416CBF1B11E097ABBA591979AE2C836B5CCF`。
- Desktop 启动器回归 **250 / 250 通过**，含新增的存在/不存在目录误填、显式可执行文件缺失时不回退到其他 Core 的 3 项回归。
- 默认调用 `TestScripts/test-pudding-desktop-launcher.ps1`，没有传 `-CoreExe`；脚本断言自动选择该 Desktop 输出下的 `core/PuddingAgent.exe`。Shell PID `50000`，Core PID `19844 → 48976 → 44180`；工作台加载、托盘隐藏/恢复、浏览器交互、独立 Host、重启、崩溃后手动恢复均通过，外部确认全部测试进程退出、端口与 DataRoot 租约释放。
- 原始日志：`temp/desktop-startup-0930-build.log`、`temp/desktop-startup-0930-tests.log`、`temp/desktop-startup-0930-smoke.log`。未用运行中的旧窗口代替新构建验收，未声称验证了用户的生产数据或直接操作了 VS 调试器。

使用修复后的构建时，退出旧的托盘实例，重新编译并启动 Desktop；将“Core 可执行文件”留空后保存。若有意使用外部 Core，则填写完整 `.exe` 路径。
