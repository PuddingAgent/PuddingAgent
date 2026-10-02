using System.Collections.Generic;

namespace PuddingCodeIndex.Contracts;

/// <summary>
/// 一个消费者（语言 provider）对**一个文件**的处理结果（D3，2026-10-02）。
/// <para>
/// 四态各有明确含义，互不替代：
/// <list type="bullet">
///   <item><description><see cref="Applied"/>：已处理该文件（<c>Payload</c> 非空时表示「由调用方提交」）。</description></item>
///   <item><description><see cref="NotApplicable"/>：**能力路由结果**——该文件不属于这个消费者
///     （扩展名不在它的能力范围内，或不在它负责的项目里）。它不是索引成功、不是失败，
///     也不是「忽略整个文件」的同义词。</description></item>
///   <item><description><see cref="Retryable"/>：本轮没能处理（解析失败、工具链暂时不可用、内容正在变化），
///     带路径级原因，应按退避重试；<b>不得</b>因此升级成整仓重建。</description></item>
///   <item><description><see cref="ScopeRunRequired"/>：该消费者暂时只能给出**项目级**结论
///     （例如它没有按文件能力），带原因与实际成本说明，由调用方决定是否发起 scope 级运行。</description></item>
/// </list>
/// </para>
/// </summary>
public enum CodeIndexConsumerStatus
{
    /// <summary>已处理。</summary>
    Applied = 0,

    /// <summary>不适用于该文件（能力路由结果）。</summary>
    NotApplicable = 1,

    /// <summary>本轮未能处理，应退避重试（带路径级原因）。</summary>
    Retryable = 2,

    /// <summary>只能给出项目级结论，需要调用方决定是否发起 scope 级运行。</summary>
    ScopeRunRequired = 3,
}

/// <summary>
/// 一个文件本次提取出的索引内容（**不写库**）：由调用方在同一个事务里提交
/// （见 <see cref="CodeSourceFileReplacement"/>），从而保证「索引结果 + 源指纹」原子落地。
/// </summary>
/// <param name="FilePath">绝对路径。</param>
/// <param name="Symbols">提取到的符号（空表示该文件没有可索引符号）。</param>
/// <param name="References">该文件拥有的引用。</param>
/// <param name="Relations">该文件拥有的关系。</param>
public sealed record CodeFileIndexPayload(
    string FilePath,
    IReadOnlyList<CodeSymbolRecord> Symbols,
    IReadOnlyList<CodeReferenceRecord> References,
    IReadOnlyList<CodeRelationRecord> Relations);

/// <summary>一个文件的处理结果。</summary>
/// <param name="FilePath">绝对路径。</param>
/// <param name="Status">四态结果。</param>
/// <param name="Payload">仅 <see cref="CodeIndexConsumerStatus.Applied"/> 且由调用方提交时非空。</param>
/// <param name="Reason">非 Applied 时的路径级原因（诊断 + 退避依据）。</param>
public sealed record CodeFileIndexOutcome(
    string FilePath,
    CodeIndexConsumerStatus Status,
    CodeFileIndexPayload? Payload = null,
    string? Reason = null);

/// <summary>
/// 一个批次的消费者输入：**同一个批次只复用一次工程/编译快照**的键。
/// <para>
/// <see cref="ConfigurationFingerprint"/> 与 <see cref="ParserPolicyFingerprint"/> 变化时，
/// 消费者必须刷新自己的快照；<see cref="Generation"/> 用于让迟到的批次结果作废。
/// </para>
/// </summary>
/// <param name="ConfigurationFingerprint">项目/依赖/绑定配置指纹（语义输入）。</param>
/// <param name="ParserPolicyFingerprint">解析器/提取策略指纹（工具链与选项）。</param>
/// <param name="Generation">调用方世代号：与当前世代不符的结果视为过期。</param>
public sealed record CodeIndexBatchContext(
    string ConfigurationFingerprint,
    string ParserPolicyFingerprint,
    long Generation);

/// <summary>一个批次的处理结果。</summary>
/// <param name="Outcomes">逐文件结果（每个请求路径一条）。</param>
/// <param name="ConfigurationFingerprint">消费者本次实际使用的配置指纹（便于诊断快照是否被复用/刷新）。</param>
/// <param name="SessionKey">
/// 消费者本次复用的工程/编译快照标识（诊断用）：同一批次内多个文件必须是同一个 key，
/// 否则说明「批内仍重复打开工程」这一放大又回来了。
/// </param>
public sealed record CodeIndexFileBatchResult(
    IReadOnlyList<CodeFileIndexOutcome> Outcomes,
    string? ConfigurationFingerprint = null,
    string? SessionKey = null);

/// <summary>
/// **语言侧批量更新接缝**（D4，2026-10-02）：一次调用处理一批文件，实现必须
/// <b>按批次复用一个有界工程/编译快照</b>，而不是每个文件各打开一次工程。
/// <para>
/// 与 <see cref="ICodeIndexFileUpdater"/> 的分工：后者是「逐文件、自己写库」的旧接缝；
/// 本接缝产出 <see cref="CodeFileIndexPayload"/> 交给调用方原子提交，因此
/// 「提取失败 ⇒ 旧结果被删掉」这类窗口在结构上不存在。
/// </para>
/// <para>
/// 可选能力：不实现它的语言仍可只提供逐文件或全量能力，由
/// <c>CompositeCodeIndexer</c> 按能力路由并在结果里如实说明（<c>NotApplicable</c> /
/// <c>ScopeRunRequired</c>），调用方不需要为旧实现加兼容适配层。
/// </para>
/// </summary>
public interface ICodeIndexFileBatchUpdater
{
    /// <summary>处理一批文件（同批共用一个工程/编译快照）。</summary>
    /// <param name="workspace">工作区描述符（同批所有文件同属一个 scope）。</param>
    /// <param name="filePaths">本批路径（调用方按自己的语义范围给出）。</param>
    /// <param name="context">批次的配置/策略指纹与世代号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CodeIndexFileBatchResult> UpdateFilesAsync(
        CodeWorkspaceDescriptor workspace,
        IReadOnlyCollection<string> filePaths,
        CodeIndexBatchContext context,
        CancellationToken cancellationToken = default);
}
