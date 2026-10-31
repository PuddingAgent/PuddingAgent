using System;
using System.Collections.Generic;

namespace PuddingCodeIndex.Contracts;

// 从 PuddingCodeIndex/Contracts/CodeSourceManifestContracts.cs 下沉（2026-10-31）。
// 命名空间刻意保持不变：调用方零改动，只把程序集依赖方向摆正（PuddingRetrieval 需要这三个值类型）。

/// <summary>
/// 一个已索引文件的**源状态指纹**（D2，2026-10-02 高磁盘读取修复）。
/// <para>
/// 指纹存在的意义：<see cref="CodeFileRecord.LastIndexedAtUtc"/> 是「索引时刻」，<b>不是</b>源文件修改时刻，
/// 因此不能当作源版本；没有源版本就无法判断「磁盘上的这个文件是否还需要处理」。
/// </para>
/// <para>
/// <b><see cref="ContentHash"/> 来自实际参与提取/解析的那一份内容</b>：不能把旧内容的 hash 与新读到的
/// stat 拼在一起提交（那会把「读到的是新内容、解析的是旧快照」记成一致的版本）。
/// </para>
/// </summary>
/// <param name="LastWriteTimeUtc">观察到的最后写入时刻（UTC）。</param>
/// <param name="Length">观察到的字节长度。</param>
/// <param name="ContentHash">稳定读取后的内容 hash（大小写不敏感的比较无意义，按 Ordinal 比较）。</param>
public sealed record SourceFingerprint(
    DateTimeOffset LastWriteTimeUtc,
    long Length,
    string ContentHash)
{
    /// <summary>stat 是否一致（mtime + length）。内容是否一致必须看 <see cref="HasSameContent"/>。</summary>
    public bool HasSameStat(SourceFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return LastWriteTimeUtc == other.LastWriteTimeUtc && Length == other.Length;
    }

    /// <summary>stat 与内容都一致。</summary>
    public bool HasSameContent(SourceFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return HasSameStat(other)
            && string.Equals(ContentHash, other.ContentHash, StringComparison.Ordinal);
    }
}

/// <summary>
/// 一个消费者（全文 / 某个语言 provider）在该文件上**已提交**的版本。
/// <para>
/// 不能用一个成功标记替代所有消费者：全文与各语言 provider 分别推进自己的水位，一个消费者提交成功
/// 不代表另一个也完成。两个指纹（解析器策略、语义输入）则是「内容没变但结论已失效」的判据：
/// 工具链升级、项目/依赖配置变化都会改变同样的内容应当绑定到的结果。
/// </para>
/// </summary>
/// <param name="ProviderId">消费者标识（如 <c>fulltext</c> / <c>csharp</c> / <c>typescript</c>）。</param>
/// <param name="ParserPolicyFingerprint">解析器/提取策略指纹（工具链版本、提取选项）。</param>
/// <param name="SemanticInputFingerprint">语义输入指纹（项目文件、依赖、绑定选项等实际参与解析的输入）。</param>
/// <param name="AppliedVersion">该消费者已提交的维护版本（账本内的单调序号）。</param>
public sealed record AppliedFileVersion(
    string ProviderId,
    string ParserPolicyFingerprint,
    string SemanticInputFingerprint,
    long AppliedVersion);

/// <summary>持久 manifest 的一行：一个 scope 内一条规范化路径的源状态与各消费者水位。</summary>
/// <param name="FilePath">绝对路径（保留观察方给的大小写；比对按组件的路径比较器）。</param>
/// <param name="Fingerprint">已提交的源指纹；从未成功提交过时为 null（<b>首次没有基线必须处理</b>）。</param>
/// <param name="AppliedVersions">各消费者已提交的版本；空表示还没有任何消费者提交过。</param>
/// <param name="Complete">
/// <c>false</c> 表示这一行来自一次不完整/失败的提交（例如只写了 manifest、索引未提交）。
/// 不完整的行不得当作「已应用」，必须重新核对。
/// </param>
public sealed record CodeSourceEntry(
    string FilePath,
    SourceFingerprint? Fingerprint,
    IReadOnlyList<AppliedFileVersion> AppliedVersions,
    bool Complete = true);
