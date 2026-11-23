# PuddingBrowser.WinUITests
> 源指纹: *.csproj=1-e3c9c7e2e194 · 条目数: 0 · 最近整理: 2026-11-23

本工程目录内**没有自己的 `.cs`** —— 全部用例来自一条 `<Compile Include="../../Tests/PuddingDesktop.Tests/Browser/WebView2DomClientTests.cs" Link="WebView2DomClientTests.cs" />`（见 csproj），
即与 `Tests/PuddingDesktop.Tests` 共用同一份源码，只是换到 `net10.0-windows` + xunit 下运行。

`WebView2DomClientTests.cs`（链接引入，22 条用例）覆盖 WebView2 DOM 客户端的两块契约：

- **CDP 参数归一化**：无参命令把 null 序列化成空对象、保留调用方给的对象但把 JSON null 归一、把 undefined 归一为空对象
- **定位器与快照校验**：`ValidateLocator` 接受当前版本引用、拒绝过期引用的稳定错误码、拒绝同一版本下另一页面的引用、拒绝旧的无作用域前缀格式、错误消息不泄漏页面 token；`Normalize` 对快照预算做钳制；通配匹配保持稳定且大小写不敏感语义；定位与交互脚本会回报表单值与选中值；定位脚本默认排除隐藏元素

维护要点：改 `Tests/PuddingDesktop.Tests/Browser/WebView2DomClientTests.cs` 会同时影响本工程与 `PuddingDesktop.Tests` 两边 ——
本文件没有独立源码，所以它的指纹只覆盖 csproj；源码指纹在 `Tests/PuddingDesktop.Tests/code_map.md` 一侧。
