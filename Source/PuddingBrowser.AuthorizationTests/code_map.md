# PuddingBrowser.AuthorizationTests
> 源指纹: *.cs=2-cbc9534d06f2, *.csproj=1-5dea3b2c83f9 · 条目数: 0 · 最近整理: 2026-11-23

只引用 PuddingBrowser 与被测契约组件；授权语义全部用内存驱动与假对端验证，不需要真实浏览器、WebView2 或宿主进程。

`AgentTargetSessionAuthorityTests.cs` 验证 agent 目标会话的授权生命周期：握手完成前授权不可用、换成另一个页面会作废已声明的会话及其许可、撤销会清掉待处理的交接、关闭被交付的页面会清掉待处理交接、桥接断开后交接待处理清空且持有的 owner 无法再声明。

`ComponentBoundaryTests.cs` 是组件边界守卫：被测组件只引用契约程序集、测试工程不得引用禁止的程序集、状态机在没有宿主的情况下可测（保证授权判定能独立单测而非只能靠端到端）。
