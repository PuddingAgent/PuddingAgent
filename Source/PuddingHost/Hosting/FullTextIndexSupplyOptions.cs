namespace PuddingHost.Hosting;

/// <summary>
/// U4-7：全文索引「供给参数」—— 由**配置文件**决定「是否建索引、建哪些目录、体积上限多大、最小重建间隔多长」。
/// <para>
/// 配置节：<c>FullTextIndex</c>，落在 **Data 目录的 <c>system.json</c>**
/// （宿主启动时加载 <c>PuddingApplicationHost.CreateBuilder</c> 里那句
/// <c>AddJsonFile(dataPaths.SystemConfigFile("system.json"), optional: true, reloadOnChange: true)</c>）。
/// 与 <c>GoalRuns</c> / <c>TaskAutoDispatch</c> 同一条链：改配置文件即可换值，**不改代码**。
/// </para>
/// <para>
/// **默认即关闭**：<see cref="Enabled"/> = <c>false</c> ⇒ <c>IndexPrebuildService.StartAsync</c> 立即返回、
/// 不建索引、零索引 I/O。因此「现网不写这个配置节」时行为**逐字不变**（该 hosted service 此前一直是
/// <c>HOSTED-DISABLED</c>）。
/// </para>
/// <para>
/// ⚠️ R2 单一真源：「1 GiB」这个默认值**只允许出现在 <see cref="DefaultMaxIndexBytes"/> 一处**；
/// 校验、日志、文档、测试一律从这里取。**1 GiB = 2^30**，**不是十进制 1 GB = 1e9** ——
/// ADR-089 已裁定「必须固化为精确字节数，不能混用 GB/GiB」。
/// </para>
/// <para>
/// ⚠️ 留白（R5）：体积护栏的**执行**行为（达到 <see cref="MaxIndexBytes"/> 时告警 / 拒写 / GC）
/// **尚未实现**，属后续切片（ADR-089 §7.2「触发 GC 留到后续切片」）。本类型只提供**参数 + 校验**。
/// </para>
/// </summary>
public sealed class FullTextIndexSupplyOptions
{
    /// <summary>配置节名（唯一）：Data 目录 <c>system.json</c> 的顶层节点。</summary>
    public const string SectionName = "FullTextIndex";

    /// <summary>
    /// 索引体积上限默认值：**1 GiB = 2^30 = 1,073,741,824 B**（不是十进制 1 GB = 1e9）。
    /// 单一真源：要换成 XXGB 请改配置文件，**不要**改这个常量，更不要在别处再写一遍字面量。
    /// </summary>
    public const long DefaultMaxIndexBytes = 1_073_741_824;

    /// <summary>索引体积上限的**硬天花板**（1 TiB = 2^40 = 1,099,511,627,776 B）：配置超过它一律拒绝。</summary>
    public const long MaxAllowedIndexBytes = 1L << 40;

    /// <summary>
    /// 总开关，**默认 <c>false</c>**：不建索引、零索引 I/O、<c>StartAsync</c> 立即返回。
    /// 只有配置文件里显式写成 <c>true</c>（且校验通过）才进入供给路径。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 目标 scope 列表（要建索引的目录）。元素可以是**绝对目录**，也可以是**相对
    /// <see cref="WorkspaceRoot"/>** 的相对目录；相对项必须显式声明基准，**绝不使用进程 CWD**
    /// （历史缺陷：旧实现拿 <c>Directory.GetCurrentDirectory()</c> 当目标，本机实测 = 运行时 bin 目录）。
    /// </summary>
    public IReadOnlyList<string> Scopes { get; set; } = [];

    /// <summary>
    /// 相对 scope 的解析基准（须为绝对目录）。为空 / 非绝对 ⇒ 相对 scope 逐条拒绝（fail-closed，不猜）。
    /// 这条就是 R1 要求的「若选相对，必须**显式声明基准**」的落地：基准来自配置，不来自进程环境。
    /// </summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>
    /// 单个索引库的体积上限（字节）。默认 <see cref="DefaultMaxIndexBytes"/>（1 GiB）。
    /// 必须 &gt; 0 且 ≤ <see cref="MaxAllowedIndexBytes"/>，否则拒绝。⚠️ 护栏的**执行**未实现（见类型注释）。
    /// </summary>
    public long MaxIndexBytes { get; set; } = DefaultMaxIndexBytes;

    /// <summary>
    /// 最小重建间隔：**已有索引足够新则跳过重建**。JSON 里写 <c>"hh:mm:ss"</c>（如 <c>"12:00:00"</c>，
    /// 也接受 <c>"1.00:00:00"</c> 这种 <c>d.hh:mm:ss</c>）。允许 <c>TimeSpan.Zero</c>（= 每次都重建）；
    /// 负值一律拒绝。
    /// </summary>
    public TimeSpan MinRebuildInterval { get; set; } = TimeSpan.FromHours(12);
}
