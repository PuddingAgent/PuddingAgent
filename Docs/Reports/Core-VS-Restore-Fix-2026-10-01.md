# Visual Studio Core 还原 NU1105 修复（2026-10-01）

## 问题与修复

Visual Studio 在 PuddingHost、PuddingAgent 及其测试项目中报告 NU1105，无法取得 src/HarnessAgent/Core/HarnessAgent.Core.csproj 的项目信息。该文件有效且已被 PuddingHost 引用，但 PuddingAgentNetwork.slnx 漏登了它；单独命令行构建可递归发现引用，不能覆盖 Visual Studio 的解决方案项目发现问题。

在解决方案 Source 分组补登记 HarnessAgent.Core；不修改业务代码或项目引用。

## 验证

- dotnet restore PuddingAgentNetwork.slnx --artifacts-path temp/build/recovery --nologo：退出码 0。
- dotnet build Source/PuddingAgent/PuddingAgent.csproj --artifacts-path temp/build/recovery --no-restore --nologo：退出码 0，175 个现有警告、0 个错误。
- 日志位于 temp/core-solution-restore.log 与 temp/core-build-verify.log；构建产物按仓库纪律清理。

尚未操作 Visual Studio 验证其错误列表刷新；已有窗口应重新加载解决方案并执行还原。未启动 Core，也未访问 D:\data。
## 后续：Files 非法路径缓存

新报错来自 src/HarnessAgent/Core/obj/Debug/net10.0/HarnessAgent.Core.csproj.FileListAbsolute.txt，含 545 条带字面双引号的历史 E: 盘输出记录。扫描 Source、src、Tests 的构建清单，仅该文件含非法引号。已备份至 temp/HarnessAgent.Core.FileListAbsolute.before.txt，并仅移除含引号的非法记录，保留合法条目。

使用 Visual Studio 18 Community 的 MSBuild.exe 执行 ReadLinesFromFile + FindUnderPath 探针：修复前退出 1，修复后退出 0；Core 完整构建退出 0，193 个警告、0 个错误。日志：temp/filelist-probe-before.log、temp/filelist-probe-after.log、temp/core-build-cache-fix.log。缓存属于忽略文件，无源码补丁；诊断原则沿用 MSBuild非法输出路径缓存修复-2026-09-12.md。
