# WPF 迁移测试基线

此目录保留原 `Source/PuddingDesktop` 的已跟踪源码，命名空间不变，程序集改为 `PuddingDesktop.WpfArchive`，以保证已有 Desktop 测试仍可验证旧生命周期与 Browser Bridge 合同。

新产品入口是 `../PuddingDesktop/PuddingDesktop.csproj`（WinUI 3），不是本工程。本目录不作为永久并行壳推进新功能，旧 `code_map.md` 为迁移前结构记录。运行中的旧产品未因此移动或重启；原目录内已有 ignored bin/obj 也没有清理。

定向测试：`dotnet test Tests/PuddingDesktop.Tests/PuddingDesktop.Tests.csproj --artifacts-path temp/build/wpf-archive-tests -p:CollectCoverage=false --results-directory temp/test-out/wpf-archive-tests`。Core DLL 接入完成后逐组迁移必要合同测试，再退役归档。不要从此处自动启动生产 Core。
