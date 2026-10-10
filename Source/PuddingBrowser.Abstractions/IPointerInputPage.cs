namespace PuddingBrowser.Abstractions;

/// <summary>§8.2 坐标输入的动作种类（坐标统一为 **CSS viewport 像素**）。</summary>
public enum PointerInputKind
{
    /// <summary>坐标点击：按下 → 抬起（成对）。</summary>
    Click,

    /// <summary>坐标移动：只移动，不按下。</summary>
    Move,

    /// <summary>坐标拖拽：起点按下 → 分步移动 → 终点抬起。</summary>
    Drag,
}

/// <summary>一次坐标输入。坐标为 **CSS viewport 像素**；<c>ToX/ToY</c> 仅拖拽使用。</summary>
public readonly record struct PointerInput(
    PointerInputKind Kind,
    double X,
    double Y,
    double ToX = 0,
    double ToY = 0,
    int ClickCount = 1,
    int Steps = 8);

/// <summary>
/// **可选能力**：页面是否支持"按坐标输入"（§8.2）。
///
/// 为什么是可选接口而不是往 <see cref="IBrowserPage"/> 加方法：加进主接口会让**全部实现与所有测试替身**
/// 都必须同步（编译期强制），而坐标输入本身在多数表面（浏览器桥、假页）上就是**不可用**的。
/// 可选接口让"不支持"成为**显式负面路径**（表面必须返回 unsupported，**不得假装支持**），
/// 同时不必为不支持的表面对齐一套空实现。
/// </summary>
public interface IPointerInputPage
{
    /// <summary>
    /// 按坐标输入。**必须成对**（点击/拖拽的按下与抬起都要发出）；不支持时**明确失败**，不静默降级。
    /// </summary>
    Task PointerAsync(PointerInput input, CancellationToken cancellationToken);
}