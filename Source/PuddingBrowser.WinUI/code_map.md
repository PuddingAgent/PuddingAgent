# WinUI browser surface

`WinUiBrowserSurfaceHost` supplies WinUI controls and DispatcherQueue to the existing WebView2 driver. Runtime, context, page, DOM and element operations compile from the same source files as the WPF adapter. Only browser abstractions are referenced; no Core Host or Desktop business dependency. Trusted workbench uses a separate environment in Shell.
