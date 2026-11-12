# WinUI browser surface
> 源指纹: *.cs=2-29e35a419e04, *.csproj=1-bff405e31c65 · 条目数: 0 · 最近整理: 2026-10-19

`WinUiBrowserSurfaceHost` supplies WinUI controls and DispatcherQueue to the existing WebView2 driver. Its screenshot readiness checks layout and ancestor visibility, including a collapsed tool panel.
Runtime, context, page, DOM and element operations compile from the same source files as the WPF adapter.
Only browser abstractions are referenced; no Core Host or Desktop business dependency.
Trusted workbench uses a separate environment in Shell.
