using System.Security.Cryptography;
using System.Text;
using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// 「本 scope 有哪些消费者、它们的输入指纹是什么」的**生产者端口**（D4，2026-10-02）。
/// <para>
/// 维护链需要它做两件事：① 配置/工具链变化时**只重新绑定**（不重新提取）；
/// ② 提交时按消费者分别推进水位。两者都要**稳定且有意义**的指纹：
/// 永远变的指纹会让每轮都重绑（白读盘），永远不变的指纹会漏掉真实变化。
/// </para>
/// </summary>
public interface ICodeSourceConsumerInputProvider
{
    /// <summary>取本 scope 当前的消费者输入指纹（稳定排序）。</summary>
    /// <param name="workspaceId">工作空间。</param>
    /// <param name="projectId">范围。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyCollection<CodeConsumerInputFingerprint>> GetConsumerInputsAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 默认实现：一个注册的语言实现 = 一个消费者。
/// <para>
/// <b>解析器/策略指纹</b>：语言实现所在程序集的版本 + 本类的策略版本常量。
/// 工具链升级（Roslyn / 提取器脚本随程序集发布）会改变它 ⇒ 该语言的路径被重新提取，符合「解析器变了结果可能变」。
/// </para>
/// <para>
/// <b>语义输入指纹</b>：该 scope 的**工程/配置文件**（<c>.csproj</c>/<c>.sln</c>/<c>tsconfig</c>/<c>pyproject</c> 等）
/// 的内容 hash 排序后合成。改工程文件（引用、编译选项）只让**绑定**失效，不必重读每个源文件正文 ——
/// 这正是「配置变更先比较有效输入」。没有任何工程文件时用固定标记（确定性，不用随机值）。
/// </para>
/// </summary>
public sealed class LanguageCodeSourceConsumerInputProvider : ICodeSourceConsumerInputProvider
{
    /// <summary>策略版本：提取逻辑或指纹语义变化时手动 +1（无需改动每个语言实现）。</summary>
    public const string PolicyVersion = "code-source-policy-v1";

    /// <summary>该 scope 没有可识别的工程/配置文件时的语义输入标记。</summary>
    public const string NoSemanticInputsMarker = "no-semantic-inputs";

    private static readonly HashSet<string> SemanticInputNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".props", ".targets",
        "tsconfig.json", "jsconfig.json", "package.json",
        "pyproject.toml", "setup.py", "requirements.txt", "Pipfile",
        "Directory.Build.props", "Directory.Build.targets", "global.json", "nuget.config",
    };

    private readonly IReadOnlyList<(string ProviderId, string PolicyFingerprint)> _providers;
    private readonly Func<string, string, CancellationToken, Task<string>> _semanticFingerprintAsync;

    /// <summary>创建提供者（语义输入指纹由工程注册表的 scope 根算出）。</summary>
    /// <param name="indexers">注册的语言实现（每个 = 一个消费者，顺序无关）。</param>
    /// <param name="projectRegistry">工程注册表（用 scope 根定位工程/配置文件）；缺失时用确定性标记。</param>
    public LanguageCodeSourceConsumerInputProvider(
        IEnumerable<ILanguageCodeIndexer> indexers,
        ICodeProjectRegistry? projectRegistry = null)
        : this(indexers, projectRegistry, semanticFingerprintAsync: null)
    {
    }

    /// <summary>创建提供者（语义输入指纹可替换，便于确定性测试）。</summary>
    /// <param name="indexers">注册的语言实现。</param>
    /// <param name="projectRegistry">工程注册表（用 scope 根定位工程/配置文件）。</param>
    /// <param name="semanticFingerprintAsync">自定义语义输入指纹函数（workspaceId, projectId → 指纹）。</param>
    public LanguageCodeSourceConsumerInputProvider(
        IEnumerable<ILanguageCodeIndexer> indexers,
        ICodeProjectRegistry? projectRegistry,
        Func<string, string, CancellationToken, Task<string>>? semanticFingerprintAsync)
    {
        ArgumentNullException.ThrowIfNull(indexers);

        _providers = indexers
            .Where(indexer => indexer is not null && !string.IsNullOrWhiteSpace(indexer.Language))
            .Select(indexer => (
                ProviderId: indexer.Language,
                PolicyFingerprint: $"{PolicyVersion}:{indexer.GetType().Assembly.GetName().Version}"))
            .DistinctBy(provider => provider.ProviderId, StringComparer.Ordinal)
            .OrderBy(provider => provider.ProviderId, StringComparer.Ordinal)
            .ToArray();

        _semanticFingerprintAsync = semanticFingerprintAsync
            ?? ((workspaceId, projectId, cancellationToken) =>
                ComputeViaRegistryAsync(projectRegistry, workspaceId, projectId, cancellationToken));
    }

    /// <summary>本 scope 的消费者数量（诊断用）。</summary>
    public int ProviderCount => _providers.Count;

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<CodeConsumerInputFingerprint>> GetConsumerInputsAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        if (_providers.Count == 0)
            return [];

        // 语义输入指纹与具体语言无关（同一个 scope 共享一份工程配置），但每个消费者各自持有一份，
        // 这样将来某个语言有自己的输入时不必改签名。
        var semantic = await _semanticFingerprintAsync(workspaceId, projectId, cancellationToken)
            .ConfigureAwait(false);

        return _providers
            .Select(provider => new CodeConsumerInputFingerprint(
                provider.ProviderId, provider.PolicyFingerprint, semantic))
            .ToArray();
    }

    private static async Task<string> ComputeViaRegistryAsync(
        ICodeProjectRegistry? projectRegistry,
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken)
    {
        if (projectRegistry is null)
            return NoSemanticInputsMarker;

        var project = await projectRegistry.GetProjectAsync(workspaceId, projectId, cancellationToken)
            .ConfigureAwait(false);

        return project is null || string.IsNullOrWhiteSpace(project.ProjectPath)
            ? NoSemanticInputsMarker
            : await ComputeSemanticFingerprintForRootAsync(project.ProjectPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>该路径是否算「语义输入」文件（工程/依赖/工具链配置）。</summary>
    public static bool IsSemanticInputFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        return SemanticInputNames.Contains(fileName)
            || fileName.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 语义输入指纹：<paramref name="rootPath"/>（**不递归**，只看根这一层）的语义输入文件按名排序后
    /// 「文件长度 + 内容 hash」合成。读不到任何语义输入时返回<see cref="NoSemanticInputsMarker"/>。
    /// </summary>
    public static async Task<string> ComputeSemanticFingerprintForRootAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return NoSemanticInputsMarker;

        var entries = new List<string>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(rootPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!IsSemanticInputFile(Path.GetFileName(path)))
                    continue;

                var info = new FileInfo(path);
                byte[] content;

                try
                {
                    content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 读不到就把它记成不可读：指纹仍然确定，且不会因为一次瞬时失败就静默当成「没变」。
                    entries.Add($"{info.Name}|unreadable");
                    continue;
                }

                entries.Add($"{info.Name}|{content.LongLength}|{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NoSemanticInputsMarker;
        }

        if (entries.Count == 0)
            return NoSemanticInputsMarker;

        entries.Sort(StringComparer.Ordinal);

        var joined = string.Join('\n', entries);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }
}
