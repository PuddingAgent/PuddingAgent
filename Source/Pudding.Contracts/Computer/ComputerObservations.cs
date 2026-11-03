namespace Pudding.Contracts.Computer;

/// <summary>屏幕物理像素矩形。原点在主屏左上角，副屏在左侧/上方时为负。</summary>
public readonly record struct PhysicalRect(int X, int Y, int Width, int Height);

/// <summary>图像内部的点（像素坐标，原点为该图左上角）。**不是屏幕坐标**，两者不可混用。</summary>
public readonly record struct ImagePoint(double X, double Y);

/// <summary>
/// 屏幕物理像素点（虚拟桌面坐标系，原点是主显示器左上角；左侧/上方显示器坐标可为负）。
/// <para>
/// **与 <see cref="ImagePoint"/> 是两个不同坐标系，故意做成不同类型，
/// 让“把图内坐标直接拿去点击”在编译期就失败。**
/// </para>
/// <para>
/// 合法获取途径只有两种：
/// <list type="number">
/// <item><c>ComputerCoordinateMapper.Map(...)</c> 把图内点经几何映射得出</item>
/// <item>直接调 Win32 几何 API（如 <c>GetCursorPos</c>）读到的实际位置</item>
/// </list>
/// 不要从 <see cref="ImagePoint"/> 强转，也不要手写 <c>new ScreenPoint(result.X, result.Y)</c> ——
/// 未经映射的坐标点下去会偏几百像素，而它不会报错，只会“点到了别的地方”。
/// </para>
/// </summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>
/// 采集方式。
/// <para>
/// <see cref="ScreenPixels"/> 是屏幕合成结果，可用于坐标输入；
/// <see cref="WindowRender"/> 抓窗口自身渲染（可理解被遮挡窗口），**不签发可点击坐标** ——
/// 它绕过了遮挡关系，按它落点等于假设"窗口在最前"，而这个假设往往不成立。
/// </para>
/// </summary>
public enum ComputerCaptureMode
{
    ScreenPixels,
    WindowRender,
}

/// <summary>
/// 一次观察的凭据。
/// <para>
/// 四个 revision 各自独立演进：显示布局、窗口几何、内容、时间。
/// 「还可不可信」由登记表按这些事实判定，**不能只靠时间戳**——窗口一移动，截图就作废了，
/// 而时间可能还没到。
/// </para>
/// </summary>
public sealed record ComputerObservation(
    ComputerObservationId ObservationId,
    ComputerExecutionEpoch Epoch,
    ComputerWindowIdentity? Window,
    long DisplayLayoutRevision,
    long WindowGeometryRevision,
    long ContentRevision,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// 图像几何：把图内坐标映射回屏幕坐标所需的一切。
/// <para>
/// 换算（设计 §7.1）：
/// <c>screenX = SourceRect.X + imageX * SourceRect.Width / ImageWidth</c>，Y 同理。
/// 换算前必须先检查 <c>0 ≤ imageX &lt; ImageWidth</c>，否则会静默算出界外坐标。
/// </para>
/// <para>
/// <see cref="CoordinateInputAllowed"/> 由采集方式决定：只有 <see cref="ComputerCaptureMode.ScreenPixels"/>
/// 才为 true。局部裁剪/派生图片必须带**自己的**几何，不能沿用原图坐标。
/// </para>
/// </summary>
public sealed record ComputerImageGeometry(
    PhysicalRect SourceRect,
    int ImageWidth,
    int ImageHeight,
    ComputerCaptureMode CaptureMode,
    bool CoordinateInputAllowed);

/// <summary>动作目标。三种形态互斥：结构元素、图像点、当前焦点。</summary>
public abstract record ComputerActionTarget;

/// <summary>
/// 结构元素目标。<c>ElementRef</c> 是观察内的稳定引用，执行前必须重新解析并校验
/// 窗口归属、RuntimeId/关键属性与可用状态；解析到 0 个或多个候选一律拒绝。
/// </summary>
public sealed record ComputerElementTarget(
    ComputerWindowId WindowId,
    ComputerObservationId SnapshotId,
    string ElementRef) : ComputerActionTarget;

/// <summary>
/// 图像点目标（视觉落点）。坐标来自某次观察的图内坐标，须经几何映射后才能点击。
/// </summary>
public sealed record ComputerPixelTarget(
    ComputerWindowId WindowId,
    ComputerObservationId ObservationId,
    ImagePoint Point) : ComputerActionTarget;

/// <summary>当前焦点目标（不发坐标，直接作用于已聚焦元素）。</summary>
public sealed record ComputerFocusedTarget(
    ComputerWindowId WindowId,
    ComputerObservationId ObservationId) : ComputerActionTarget;
