using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PuddingPathFiltering;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// 检查输入清单条目：<see cref="RelativePath"/> 一律使用 <c>/</c> 分隔、相对仓库根、保持原始大小写；
/// <see cref="ContentSha256"/> 为条目内容的 SHA-256，格式 <c>sha256:&lt;lowerhex&gt;</c>（小写十六进制）。
/// </summary>
public sealed record GoalCheckInputEntry(string RelativePath, string ContentSha256);

/// <summary>
/// 检查输入清单（ADR-092 决策 10 的「显式输入清单」）。
/// <para>
/// <see cref="Identity"/> 是清单的确定性内容哈希：相关输入变化 ⇒ Identity 变化 ⇒ 旧检查结论（成功与失败）一并失效；
/// 明确依赖范围内的无关变化不影响 Identity ⇒ 旧结论可复用。<see cref="ProvenanceHead"/> 仅作来源信息记录，
/// 不参与 <see cref="Identity"/> 计算（ADR-092：HEAD 不得单独作为复用依据，模糊的 dirty 标记同样不足）。
/// </para>
/// </summary>
public sealed record GoalCheckInputManifest(
    IReadOnlyList<GoalCheckInputEntry> Entries,
    string Identity,
    string ProvenanceHead);

/// <summary>
/// ADR-092 决策 10 · P1（只读采集）：Goal 检查输入身份采集组件。
/// <para>
/// 显式收集一次检查的全部输入分量并计算确定性、跨平台稳定的身份：
/// <b>sources</b>——目标项目目录下全部 <c>*.cs</c>（递归，排除构建产物路径段）；
/// <b>config</b>——目录链上的 MSBuild/SDK 配置文件、目标 <c>.csproj</c> 自身，及其一层
/// <c>ProjectReference</c> 解析出的被引用项目；<b>deps</b>——NuGet 锁文件
/// <c>packages.lock.json</c>（存在才纳入，缺失不算错误）；<b>checks</b>——检查定义引用的虚拟条目
/// （hash 取自 <see cref="GoalCheckDefinitionRegistry.TryGetDefinitionHash"/>，未登记的退化为引用原文哈希）。
/// </para>
/// <para>
/// 本组件只读文件系统：不启动进程、不访问网络、不做任何 git 操作（<c>provenanceHead</c> 由调用方传入）。
/// 内容哈希对文件 UTF-8 字节直接计算，不做换行归一，避免与「内容是否变化」的语义混淆。
/// P1 阶段仅提供采集能力，尚未接入 Planner/Store/Runner（零接线、零行为改变）。
/// </para>
/// </summary>
public static partial class GoalCheckInputIdentity
{
    /// <summary>
    /// 任何路径段命中这些名字的文件一律排除在源码分量之外（大小写不敏感）。
    /// ADR-089 U4-4 D4：原先是一份 8 项私有副本，现派生自单一真源
    /// <see cref="PathNoiseRules.DirectoryNames"/>（52 项）。
    /// </summary>
    private static IReadOnlySet<string> ExcludedPathSegments => PathNoiseRules.DirectoryNames;

    /// <summary>目录链上按同名收集的构建/运行配置文件。</summary>
    private static readonly string[] ChainConfigFileNames =
    [
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json",
    ];

    /// <summary>
    /// 收集一次 Goal 检查的输入清单并计算身份。
    /// </summary>
    /// <param name="repositoryRoot">仓库根目录。</param>
    /// <param name="projectRelativePath">目标项目目录相对仓库根的路径（如 <c>Source/PuddingPlatform</c>）。</param>
    /// <param name="checkDefinitionRefs">检查定义引用（如 <c>checks/build.md#dotnet-build</c>）；可为空。</param>
    /// <param name="provenanceHead">git HEAD 提交标识，仅作来源信息记录，不参与 Identity 计算；可为空。</param>
    /// <param name="declaredRelevantPaths">
    /// 【设计 §6】**检查声明的相关文件**，相对仓库根。
    /// 存在的文件按其**原始字节**直接哈希；声明的路径不存在时写入一条「缺席」条目——
    /// 使其随后出现也会改变 Identity（范围不确定 ⇒ 保守处理，不静默忽略）。
    /// 绝对路径、<c>..</c> 逃逸与越出仓库根的声明一律忽略。可为空。
    /// </param>
    /// <param name="buildId">
    /// 【设计 §6】适用环境 / BuildId。仅以 <c>build-id</c> 虚拟条目记录其 **SHA-256**，
    /// 不把原始值写入清单（避免将环境值当明文落盘）。可为空。
    /// </param>
    /// <returns>输入清单与身份（格式 <c>sha256:&lt;lowerhex&gt;</c>）。</returns>
    /// <exception cref="ArgumentNullException">必填参数为 null。</exception>
    /// <exception cref="ArgumentException">
    /// 仓库根不存在；<paramref name="projectRelativePath"/> 为绝对路径、包含 <c>..</c> 逃逸、
    /// 越出仓库根，或项目目录不存在。
    /// </exception>
    public static GoalCheckInputManifest Collect(
        string repositoryRoot,
        string projectRelativePath,
        IReadOnlyCollection<string>? checkDefinitionRefs = null,
        string? provenanceHead = null,
        IReadOnlyCollection<string>? declaredRelevantPaths = null,
        string? buildId = null)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        ArgumentNullException.ThrowIfNull(projectRelativePath);

        var rootFullPath = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(rootFullPath))
            throw new ArgumentException($"repositoryRoot 不存在：{repositoryRoot}", nameof(repositoryRoot));

        var projectDir = ResolveProjectDirectory(rootFullPath, projectRelativePath);

        // 同一 RelativePath 只保留一条（显式条目后写覆盖），保证清单稳定无重复。
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        AddSourceFiles(entries, rootFullPath, projectDir);
        AddConfigEntries(entries, rootFullPath, projectDir);
        AddDependencyLockEntries(entries, rootFullPath, projectDir);
        AddCheckDefinitionEntries(entries, checkDefinitionRefs);
        AddDeclaredRelevantEntries(entries, rootFullPath, declaredRelevantPaths);
        AddBuildIdEntry(entries, buildId);

        var ordered = entries
            .Select(pair => new GoalCheckInputEntry(pair.Key, pair.Value))
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();

        return new GoalCheckInputManifest(ordered, ComputeIdentity(ordered), provenanceHead ?? string.Empty);
    }

    /// <summary>校验并解析项目目录：拒绝绝对路径、<c>..</c> 逃逸与越出仓库根。</summary>
    private static string ResolveProjectDirectory(string rootFullPath, string projectRelativePath)
    {
        if (Path.IsPathRooted(projectRelativePath))
            throw new ArgumentException(
                $"projectRelativePath 必须是相对路径，拒绝绝对路径：{projectRelativePath}",
                nameof(projectRelativePath));

        var segments = projectRelativePath.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Contains("..", StringComparer.Ordinal))
            throw new ArgumentException(
                $"projectRelativePath 不得包含 '..' 逃逸：{projectRelativePath}",
                nameof(projectRelativePath));

        string projectDir;
        try
        {
            projectDir = Path.GetFullPath(Path.Combine(rootFullPath, projectRelativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(
                $"projectRelativePath 无法解析：{projectRelativePath}",
                nameof(projectRelativePath), ex);
        }

        if (!IsInsideRoot(rootFullPath, projectDir))
            throw new ArgumentException(
                $"projectRelativePath 越出仓库根：{projectRelativePath}",
                nameof(projectRelativePath));
        if (!Directory.Exists(projectDir))
            throw new ArgumentException(
                $"项目目录不存在：{projectRelativePath}",
                nameof(projectRelativePath));

        return projectDir;
    }

    /// <summary>sources 分量：项目目录下全部 <c>*.cs</c>（递归，排除构建产物路径段）。</summary>
    private static void AddSourceFiles(Dictionary<string, string> entries, string rootFullPath, string directory)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", options))
        {
            // Windows 通配符在扩展名形似时可能放行更长的扩展名（如 *.csx），这里显式收紧为精确 .cs。
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsExcludedPath(rootFullPath, file))
                continue;
            AddFileEntry(entries, rootFullPath, file);
        }
    }

    /// <summary>
    /// config 分量：目录链上的同名配置文件、目标 <c>.csproj</c> 自身，以及 <b>传递闭合</b> 的 ProjectReference 展开。
    /// <para>
    /// 必须是传递闭包而非一层：ADR-092 决策 10 规定「无法判定相关性 ⇒ 保守重跑」。
    /// 只展开一层时，二层及更深被引用项目的源码变化不进入身份 ⇒ 旧绿灯会被错误复用。
    /// 用已访问集合做环保护，保证自引用/互相引用不会无限展开。
    /// </para>
    /// </summary>
    private static void AddConfigEntries(Dictionary<string, string> entries, string rootFullPath, string projectDir)
    {
        foreach (var directory in EnumerateDirectoryChain(rootFullPath, projectDir))
        foreach (var name in ChainConfigFileNames)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                AddFileEntry(entries, rootFullPath, candidate);
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(
            Directory.EnumerateFiles(projectDir, "*.csproj").OrderBy(path => path, StringComparer.Ordinal));

        while (pending.Count > 0)
        {
            var projectFile = pending.Dequeue();
            if (!visited.Add(projectFile))
                continue; // 环保护：自引用或互相引用只展开一次
            AddFileEntry(entries, rootFullPath, projectFile);
            AddSourceFiles(entries, rootFullPath, Path.GetDirectoryName(projectFile)!);
            foreach (var referenced in ResolveProjectReferences(rootFullPath, projectFile))
                pending.Enqueue(referenced);
        }
    }

    /// <summary>deps 分量：NuGet 锁文件（仓库根或目标项目目录），存在才纳入，缺失不算错误。</summary>
    private static void AddDependencyLockEntries(Dictionary<string, string> entries, string rootFullPath, string projectDir)
    {
        var rootLock = Path.Combine(rootFullPath, "packages.lock.json");
        if (File.Exists(rootLock))
            AddFileEntry(entries, rootFullPath, rootLock);

        var projectLock = Path.Combine(projectDir, "packages.lock.json");
        if (File.Exists(projectLock))
            AddFileEntry(entries, rootFullPath, projectLock);
    }

    /// <summary>
    /// checks 分量：每个引用生成虚拟条目 <c>check:&lt;ref&gt;</c>；
    /// 已登记的定义取注册表内容 hash，未登记的退化为引用原文的 SHA-256。
    /// </summary>
    private static void AddCheckDefinitionEntries(
        Dictionary<string, string> entries,
        IReadOnlyCollection<string>? checkDefinitionRefs)
    {
        if (checkDefinitionRefs is null)
            return;
        foreach (var definitionRef in checkDefinitionRefs)
        {
            if (string.IsNullOrWhiteSpace(definitionRef))
                continue;
            entries["check:" + definitionRef] =
                GoalCheckDefinitionRegistry.TryGetDefinitionHash(definitionRef, out var definitionHash)
                    ? definitionHash
                    : ToSha256Hex(Encoding.UTF8.GetBytes(definitionRef));
        }
    }

    /// <summary>
    /// 检查声明的相关文件（设计 §6）：存在的按原始字节哈希；缺席的写一条
    /// <c>declared-absent:&lt;relPath&gt;</c> 条目，使其随后出现也能改变 Identity。
    /// 绝对路径、<c>..</c> 逃逸、越出仓库根与非法路径一律忽略。
    /// </summary>
    private static void AddDeclaredRelevantEntries(
        Dictionary<string, string> entries,
        string rootFullPath,
        IReadOnlyCollection<string>? declaredRelevantPaths)
    {
        if (declaredRelevantPaths is null)
            return;

        foreach (var declared in declaredRelevantPaths)
        {
            if (string.IsNullOrWhiteSpace(declared))
                continue;

            var normalized = declared.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(declared))
                continue;
            if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Contains("..", StringComparer.Ordinal))
                continue;

            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(rootFullPath, declared));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!IsInsideRoot(rootFullPath, candidate))
                continue;

            if (File.Exists(candidate))
            {
                AddFileEntry(entries, rootFullPath, candidate);
                continue;
            }

            // 声明但不存在：记为「缺席」，使该文件随后出现也能改变身份（保守而不错过变化）。
            entries["declared-absent:" + normalized] = ToSha256Hex(Encoding.UTF8.GetBytes("absent"));
        }
    }

    /// <summary>
    /// 适用环境 / BuildId：只记录其 SHA-256，不落盘原始值（设计 §6「不得记录秘密明文」）。
    /// </summary>
    private static void AddBuildIdEntry(Dictionary<string, string> entries, string? buildId)
    {
        if (string.IsNullOrWhiteSpace(buildId))
            return;
        entries["build-id"] = ToSha256Hex(Encoding.UTF8.GetBytes(buildId.Trim()));
    }

    /// <summary>解析本 csproj 的<b>直接</b> ProjectReference 连线（传递闭合由调用方负责）；越出仓库根或文件不存在的引用忽略。</summary>
    private static IEnumerable<string> ResolveProjectReferences(string rootFullPath, string projectFileFullPath)
    {
        string content;
        try
        {
            content = File.ReadAllText(projectFileFullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        var projectDirectory = Path.GetDirectoryName(projectFileFullPath)!;
        foreach (Match match in ProjectReferenceRegex().Matches(content))
        {
            var include = match.Groups["include"].Value.Trim();
            if (include.Length == 0)
                continue;

            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(projectDirectory, include));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!IsInsideRoot(rootFullPath, candidate))
                continue;
            if (File.Exists(candidate))
                yield return candidate;
        }
    }

    [GeneratedRegex(
        @"<ProjectReference\s+[^>]*?Include\s*=\s*[""'](?<include>[^""']+)[""']",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProjectReferenceRegex();

    /// <summary>从仓库根沿目录链枚举到项目目录（含两端）。</summary>
    private static IEnumerable<string> EnumerateDirectoryChain(string rootFullPath, string projectDir)
    {
        yield return rootFullPath;
        var current = rootFullPath;
        var relative = Path.GetRelativePath(rootFullPath, projectDir);
        foreach (var segment in relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    /// <summary>路径（相对仓库根）的任何段命中排除名单即排除。</summary>
    private static bool IsExcludedPath(string rootFullPath, string fullPath)
    {
        var relative = Path.GetRelativePath(rootFullPath, fullPath);
        foreach (var segment in relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (ExcludedPathSegments.Contains(segment))
                return true;
        }

        return false;
    }

    /// <summary>候选路径必须等于仓库根或位于仓库根之内（Windows 大小写不敏感语义）。</summary>
    private static bool IsInsideRoot(string rootFullPath, string candidate)
        => string.Equals(candidate, rootFullPath, StringComparison.OrdinalIgnoreCase)
           || candidate.StartsWith(rootFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void AddFileEntry(Dictionary<string, string> entries, string rootFullPath, string fullPath)
    {
        var relativePath = Path.GetRelativePath(rootFullPath, fullPath).Replace('\\', '/');
        entries[relativePath] = HashFile(fullPath);
    }

    /// <summary>对文件字节直接哈希：不做换行归一，避免与「内容是否变化」的语义混淆。</summary>
    private static string HashFile(string fullPath) => ToSha256Hex(File.ReadAllBytes(fullPath));

    /// <summary>
    /// 身份计算：按 RelativePath（Ordinal 升序）以 <c>relPath\tsha256\n</c> 逐行拼接后整体哈希。
    /// </summary>
    private static string ComputeIdentity(IReadOnlyList<GoalCheckInputEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
            builder.Append(entry.RelativePath).Append('\t').Append(entry.ContentSha256).Append('\n');
        return ToSha256Hex(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string ToSha256Hex(byte[] data)
        => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(data))}";
}
