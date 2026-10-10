namespace Pudding.Contracts.Desktop;

/// <summary>控制面：申请把某任务绑定到用户选定的页面（§4.1）。载荷就是<b>任务绑定</b>本身。</summary>
public sealed record BrowserBindingApplyRequest(BrowserTaskBinding Binding);

/// <summary>控制面：撤销该任务在该页上的绑定（§4.1）。只按 (任务, 目标) 定位，不重复携带范围/期限。</summary>
public sealed record BrowserBindingRevokeRequest(BrowserCallerIdentity Task, DesktopPageTarget Target);