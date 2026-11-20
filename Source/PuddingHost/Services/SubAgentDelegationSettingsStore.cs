using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using PuddingCode.Configuration;

namespace PuddingAgent.Services;

/// <summary>
/// 子代理委派策略的读写口 —— 需求 3「配置界面」的服务端出口。
///
/// <para>
/// **读**：四个键与 <c>SubAgentTool</c> 使用的键逐字一致，且该工具在**每次派发时**实时读取
/// <see cref="IConfiguration"/>，因此这里读到的就是「下一次派发会生效的值」，不是启动快照。
/// API Key / 路由那类「启动时一次性加载」的配置不适用于本类。
/// </para>
/// <para>
/// **写**：只改 <c>data/config/system.json</c> 的 <c>SubAgentDelegation</c> 段，其余键原样保留。
/// 该文件以 <c>reloadOnChange: true</c> 接入配置链（见 PuddingApplicationHost 的 AddJsonFile），
/// 落盘后由配置框架自动重载 —— **不需要重启 Core**。
/// </para>
/// <para>
/// **失败语义**：全部 fail closed。校验不过抛 <see cref="SubAgentDelegationValidationException"/>，
/// 一个字节都不写；落盘先写临时文件再原子替换，避免半截 JSON 让下一次重载抛出配置异常。
/// </para>
/// <para>
/// **为什么在这里再校验一次联动约束**：<c>AllowModelSelection=false</c> 而
/// <c>DefaultModelRoute</c> 为空时，<c>SubAgentTool.ResolveChildLlmRouteAsync</c> 会在派发时抛异常，
/// 表现为「所有子代理都起不来」。这是配置可以单独写坏、但只有运行到才暴露的组合，
/// 所以在写入口就拒绝，而不是留给运行期。
/// </para>
/// </summary>
public sealed class SubAgentDelegationSettingsStore
{
    /// <summary>配置文件中的段名（与 SubAgentTool 读取的键前缀逐字一致）。</summary>
    public const string SectionName = "SubAgentDelegation";

    /// <summary>代码默认：1 = 仅主 Agent 可创建子代理。</summary>
    public const int DefaultMaxDepth = 1;

    /// <summary>代码默认：0 = 不限制（保留既有行为）。</summary>
    public const int DefaultMaxConcurrentSubAgents = 0;

    /// <summary>代码默认：允许 Agent 为子代理选择模型。</summary>
    public const bool DefaultAllowModelSelection = true;

    /// <summary>MaxDepth 的可接受上界。守卫按值放行，放宽不需要改代码。</summary>
    public const int MaxAllowedDepth = 8;

    /// <summary>MaxConcurrentSubAgents 的可接受上界。</summary>
    public const int MaxAllowedConcurrent = 128;

    private const string MaxDepthKey = SectionName + ":MaxDepth";
    private const string MaxConcurrentKey = SectionName + ":MaxConcurrentSubAgents";
    private const string AllowModelSelectionKey = SectionName + ":AllowModelSelection";
    private const string DefaultModelRouteKey = SectionName + ":DefaultModelRoute";

    private readonly IConfiguration _configuration;
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<SubAgentDelegationSettingsStore> _logger;

    /// <summary>构造。</summary>
    /// <param name="configuration">根配置（system.json 已以 reloadOnChange 接入）。</param>
    /// <param name="paths">数据根路径解析。</param>
    /// <param name="logger">日志。</param>
    public SubAgentDelegationSettingsStore(
        IConfiguration configuration,
        PuddingDataPaths paths,
        ILogger<SubAgentDelegationSettingsStore> logger)
    {
        _configuration = configuration;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>配置文件绝对路径（界面用来告诉用户「改到哪去了」）。</summary>
    public string ConfigFile => _paths.SystemConfigFile("system.json");

    /// <summary>
    /// 读取当前生效值。每个字段都带 <c>*FromConfigFile</c> 标记，
    /// 用来区分「文件里没写、取的是代码默认」与「文件里写了这个值」——
    /// 两者在界面上必须看得出差别，否则用户无从判断自己改过没有。
    /// </summary>
    public SubAgentDelegationSnapshot Read()
    {
        var maxDepth = ReadInt(MaxDepthKey, DefaultMaxDepth);
        var maxConcurrent = ReadInt(MaxConcurrentKey, DefaultMaxConcurrentSubAgents);
        var allowModelSelection = ReadBool(AllowModelSelectionKey, DefaultAllowModelSelection);
        var defaultModelRoute = ReadString(DefaultModelRouteKey);

        return new SubAgentDelegationSnapshot(
            MaxDepth: maxDepth,
            MaxConcurrentSubAgents: maxConcurrent,
            AllowModelSelection: allowModelSelection,
            DefaultModelRoute: defaultModelRoute,
            ConfigFile: ConfigFile,
            MaxDepthFromConfigFile: _configuration[MaxDepthKey] is not null,
            MaxConcurrentSubAgentsFromConfigFile: _configuration[MaxConcurrentKey] is not null,
            AllowModelSelectionFromConfigFile: _configuration[AllowModelSelectionKey] is not null,
            DefaultModelRouteFromConfigFile: _configuration[DefaultModelRouteKey] is not null,
            Warnings: CollectWarnings(allowModelSelection, defaultModelRoute));
    }

    /// <summary>
    /// 写入配置并返回写入后的目标值。
    ///
    /// <para>
    /// 未提供的字段（<c>null</c>）保持原样；<see cref="SubAgentDelegationUpdate.DefaultModelRoute"/>
    /// 传<b>空字符串</b>表示删除该键（而不是写入空串）。
    /// </para>
    /// <para>
    /// 返回的是**落盘的目标值**，不是重新读配置的结果：配置重载是文件变更后的异步行为，
    /// 立刻回读可能仍拿到旧值，那会把「写成功」误报成「没生效」。
    /// </para>
    /// </summary>
    /// <exception cref="SubAgentDelegationValidationException">任一校验不通过；此时不会写入任何字节。</exception>
    public async Task<SubAgentDelegationSnapshot> UpdateAsync(
        SubAgentDelegationUpdate update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var current = Read();
        var nextMaxDepth = update.MaxDepth ?? current.MaxDepth;
        var nextMaxConcurrent = update.MaxConcurrentSubAgents ?? current.MaxConcurrentSubAgents;
        var nextAllowModelSelection = update.AllowModelSelection ?? current.AllowModelSelection;
        var nextDefaultRoute = update.DefaultModelRoute is null
            ? current.DefaultModelRoute
            : (string.IsNullOrWhiteSpace(update.DefaultModelRoute) ? null : update.DefaultModelRoute.Trim());

        var errors = Validate(nextMaxDepth, nextMaxConcurrent, nextAllowModelSelection, nextDefaultRoute);
        if (errors.Count > 0)
            throw new SubAgentDelegationValidationException(errors);

        await WriteAsync(
            update.MaxDepth is not null ? nextMaxDepth : null,
            update.MaxConcurrentSubAgents is not null ? nextMaxConcurrent : null,
            update.AllowModelSelection is not null ? nextAllowModelSelection : null,
            update.DefaultModelRoute is not null ? nextDefaultRoute : null,
            clearDefaultRoute: update.DefaultModelRoute is not null && nextDefaultRoute is null,
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "[SubAgentDelegation] 配置已写入 {File}：MaxDepth={MaxDepth} MaxConcurrentSubAgents={MaxConcurrent} " +
            "AllowModelSelection={AllowModelSelection} DefaultModelRoute={DefaultRoute}",
            ConfigFile, nextMaxDepth, nextMaxConcurrent, nextAllowModelSelection, nextDefaultRoute ?? "(未设置)");

        return new SubAgentDelegationSnapshot(
            MaxDepth: nextMaxDepth,
            MaxConcurrentSubAgents: nextMaxConcurrent,
            AllowModelSelection: nextAllowModelSelection,
            DefaultModelRoute: nextDefaultRoute,
            ConfigFile: ConfigFile,
            MaxDepthFromConfigFile: true,
            MaxConcurrentSubAgentsFromConfigFile: true,
            AllowModelSelectionFromConfigFile: true,
            DefaultModelRouteFromConfigFile: nextDefaultRoute is not null,
            Warnings: CollectWarnings(nextAllowModelSelection, nextDefaultRoute));
    }

    /// <summary>
    /// 校验一组值。纯函数，便于单测直接取红。
    /// </summary>
    /// <returns>错误消息列表；空列表表示通过。</returns>
    public static IReadOnlyList<string> Validate(
        int maxDepth,
        int maxConcurrentSubAgents,
        bool allowModelSelection,
        string? defaultModelRoute)
    {
        var errors = new List<string>();

        if (maxDepth < 0 || maxDepth > MaxAllowedDepth)
        {
            errors.Add(
                $"{MaxDepthKey} 必须在 0..{MaxAllowedDepth} 之间（0 = 不限制深度）；实际 {maxDepth}。");
        }

        if (maxConcurrentSubAgents < 0 || maxConcurrentSubAgents > MaxAllowedConcurrent)
        {
            errors.Add(
                $"{MaxConcurrentKey} 必须在 0..{MaxAllowedConcurrent} 之间（0 = 不限制）；实际 {maxConcurrentSubAgents}。");
        }

        if (defaultModelRoute is not null)
        {
            // 路由必须是 provider/model 形式：裸 modelId 在多 provider 注册时会解析失败，
            // 这里提前拦掉，而不是等派发时才报「exists under multiple providers」。
            if (!defaultModelRoute.Contains('/', StringComparison.Ordinal)
                || defaultModelRoute.StartsWith('/')
                || defaultModelRoute.EndsWith('/')
                || defaultModelRoute.Any(char.IsWhiteSpace))
            {
                errors.Add(
                    $"{DefaultModelRouteKey} 必须是 providerId/modelId 形式（如 fastrouter/gpt-6.2-sol）；实际 \"{defaultModelRoute}\"。");
            }
        }

        // 联动约束：关闭模型选择却不给默认路由 ⇒ 派发必然抛异常。
        if (!allowModelSelection && defaultModelRoute is null)
        {
            errors.Add(
                $"关闭 {AllowModelSelectionKey} 时必须同时设置 {DefaultModelRouteKey}，" +
                "否则每次派发都会因「无法确定子代理使用哪个模型」而失败。");
        }

        return errors;
    }

    private static IReadOnlyList<string> CollectWarnings(bool allowModelSelection, string? defaultModelRoute)
    {
        var warnings = new List<string>();

        if (allowModelSelection && defaultModelRoute is not null)
        {
            warnings.Add(
                $"{AllowModelSelectionKey}=true 时 {DefaultModelRouteKey} 不生效（仅在选择被关闭时使用）。");
        }

        if (defaultModelRoute is not null && !allowModelSelection)
        {
            warnings.Add(
                $"{AllowModelSelectionKey}=false：所有子代理都会使用 {defaultModelRoute}，Agent 传入的 model 参数被忽略。");
        }

        return warnings;
    }

    private int ReadInt(string key, int fallback)
    {
        var raw = _configuration[key];
        return int.TryParse(raw, out var value) ? value : fallback;
    }

    private bool ReadBool(string key, bool fallback)
    {
        var raw = _configuration[key];
        return bool.TryParse(raw, out var value) ? value : fallback;
    }

    private string? ReadString(string key)
    {
        var raw = _configuration[key];
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private async Task WriteAsync(
        int? maxDepth,
        int? maxConcurrentSubAgents,
        bool? allowModelSelection,
        string? defaultModelRoute,
        bool clearDefaultRoute,
        CancellationToken ct)
    {
        var path = ConfigFile;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null;
        var root = string.IsNullOrWhiteSpace(existing)
            ? new JsonObject()
            : JsonNode.Parse(existing) as JsonObject
              ?? throw new InvalidOperationException($"{path} 的顶层不是 JSON 对象，拒绝改写。");

        if (root[SectionName] is not JsonObject section)
        {
            section = new JsonObject();
            root[SectionName] = section;
        }

        if (maxDepth is not null)
            section[nameof(SubAgentDelegationSnapshot.MaxDepth)] = maxDepth.Value;
        if (maxConcurrentSubAgents is not null)
            section[nameof(SubAgentDelegationSnapshot.MaxConcurrentSubAgents)] = maxConcurrentSubAgents.Value;
        if (allowModelSelection is not null)
            section[nameof(SubAgentDelegationSnapshot.AllowModelSelection)] = allowModelSelection.Value;

        if (clearDefaultRoute)
            section.Remove(nameof(SubAgentDelegationSnapshot.DefaultModelRoute));
        else if (defaultModelRoute is not null)
            section[nameof(SubAgentDelegationSnapshot.DefaultModelRoute)] = defaultModelRoute;

        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentSize = 2,
        }) + Environment.NewLine;

        // 先写临时文件再原子替换：配置框架会监听该文件，半截 JSON 会让重载失败并把
        // 整条配置链回退到上一个快照，症状是「改了没生效且没有任何错误」。
        // 重试是必要的：reloadOnChange 的 FileSystemWatcher 会短暂持句柄，
        // Windows 下会以 sharing violation 拒绝替换（实测概率低但会偶发）。
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>子代理委派策略的当前生效快照。</summary>
/// <param name="MaxDepth">委派深度上限（0 = 不限制）。</param>
/// <param name="MaxConcurrentSubAgents">同一主 Agent 下所有层级存活子代理上限（0 = 不限制）。</param>
/// <param name="AllowModelSelection">是否允许 Agent 为子代理选择模型。</param>
/// <param name="DefaultModelRoute">模型选择关闭时使用的路由；未设置为 null。</param>
/// <param name="ConfigFile">配置文件绝对路径。</param>
/// <param name="MaxDepthFromConfigFile">该值是否来自配置文件（false = 代码默认）。</param>
/// <param name="MaxConcurrentSubAgentsFromConfigFile">同上。</param>
/// <param name="AllowModelSelectionFromConfigFile">同上。</param>
/// <param name="DefaultModelRouteFromConfigFile">同上。</param>
/// <param name="Warnings">可写但当前不生效 / 有副作用的组合提示。</param>
public sealed record SubAgentDelegationSnapshot(
    int MaxDepth,
    int MaxConcurrentSubAgents,
    bool AllowModelSelection,
    string? DefaultModelRoute,
    string ConfigFile,
    bool MaxDepthFromConfigFile,
    bool MaxConcurrentSubAgentsFromConfigFile,
    bool AllowModelSelectionFromConfigFile,
    bool DefaultModelRouteFromConfigFile,
    IReadOnlyList<string> Warnings);

/// <summary>部分更新请求：null = 保持原值；<see cref="DefaultModelRoute"/> 传空串 = 删除该键。</summary>
/// <param name="MaxDepth">委派深度上限。</param>
/// <param name="MaxConcurrentSubAgents">并发上限。</param>
/// <param name="AllowModelSelection">是否允许选择模型。</param>
/// <param name="DefaultModelRoute">默认模型路由。</param>
public sealed record SubAgentDelegationUpdate(
    int? MaxDepth = null,
    int? MaxConcurrentSubAgents = null,
    bool? AllowModelSelection = null,
    string? DefaultModelRoute = null);

/// <summary>校验失败（对应 HTTP 400，且保证未写入任何字节）。</summary>
public sealed class SubAgentDelegationValidationException : Exception
{
    /// <summary>构造。</summary>
    /// <param name="errors">逐条错误消息。</param>
    public SubAgentDelegationValidationException(IReadOnlyList<string> errors)
        : base(string.Join("；", errors))
    {
        Errors = errors;
    }

    /// <summary>逐条错误消息。</summary>
    public IReadOnlyList<string> Errors { get; }
}
