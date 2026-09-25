using Lucene.Net.Store;
using Directory = Lucene.Net.Store.Directory;

namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// **写入期配额包装层**（方案 §4.2：`Lucene Directory 输出由 quota wrapper 统计实际增长，在超限前拒绝继续写`）。
/// <para>
/// 做法与 Lucene 自带的 <c>TrackingDirectoryWrapper</c> 同源：包住真实 <c>FSDirectory</c>，
/// 只覆写必须的成员 —— <see cref="CreateOutput"/> 返回一个**计数代理** <c>IndexOutput</c>，
/// 每次写出（<c>WriteByte</c> / <c>WriteBytes</c>）先记账、再落盘；累计值越过
/// 「本批允许增长」时抛出 <see cref="IndexWriteQuotaExceededException"/>（<b>该次写入不发生</b>）。
/// </para>
/// <para>
/// <b>为什么覆盖足够</b>：Lucene 的 <c>DataOutput</c> 只把 <c>WriteByte</c> 与
/// <c>WriteBytes(byte[],int,int)</c> 声明为抽象，<c>WriteInt16/32/64</c>、<c>WriteVInt32/64</c>、
/// <c>WriteString</c>、<c>CopyBytes</c> 等全部经由这两个原语实现 —— 计数覆盖所有写出路径，
/// 包括刷新段（flush）与<b>自动合并（merge）写出的新段文件</b>（两者都走同一个 <see cref="CreateOutput"/>）。
/// </para>
/// <para>
/// <b>为什么记账是保守的（上界）</b>：只累计「写出字节」，不抵扣删除（合并回收的旧段文件、被替换的
/// <c>segments_N</c>）。因为「净增长 ≤ 写出字节」，所以「写出字节 ≤ 允许增长」是「净增长 ≤ 允许增长」的
/// 充分条件；回退（seek 重写）也会被重复计数，方向同样偏保守。
/// </para>
/// <para>
/// ⚠️ 本类**不**改索引语义：预算充足时（允许增长 ≥ 实际输出）所有行为与不包装逐位一致
/// （写入字节、文件集合、commit 结果均不变）。
/// </para>
/// </summary>
internal sealed class QuotaEnforcingDirectory : FilterDirectory
{
    private readonly Directory _inner;
    private readonly object _gate = new();
    private readonly long _budgetBytes;
    private readonly long _allowedGrowthBytes;
    private readonly List<string> _createdFiles = new();
    private readonly List<string> _deletedFiles = new();

    private long _bytesWritten;
    private IndexWriteQuotaExceededException? _violation;

    /// <param name="in">被包装的真实目录（调用方负责它的生命周期，本类只做委托 + 计数）。</param>
    /// <param name="budgetBytes">集合预算（<c>FullTextMutationBudget.MaxIndexBytes</c>；仅用于诊断与报告）。</param>
    /// <param name="allowedGrowthBytes">
    /// 本批允许的**输出增长**上限（= 集合预算 − 本批开始前全部 live scope 索引字节）。
    /// 允许为 0（合同语义：本批不得写出任何新字节）；负数一律拒绝（上游量错了，不猜测）。
    /// </param>
    internal QuotaEnforcingDirectory(Directory @in, long budgetBytes, long allowedGrowthBytes)
        : base(@in)
    {
        _inner = @in ?? throw new ArgumentNullException(nameof(@in));
        if (budgetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(budgetBytes), $"预算必须是正的字节数，收到 {budgetBytes}。");
        if (allowedGrowthBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(allowedGrowthBytes), $"允许增长不得为负，收到 {allowedGrowthBytes}。");

        _budgetBytes = budgetBytes;
        _allowedGrowthBytes = allowedGrowthBytes;
    }

    /// <summary>集合预算（诊断 / 报告用）。</summary>
    internal long BudgetBytes => _budgetBytes;

    /// <summary>本批允许的输出增长上限。</summary>
    internal long AllowedGrowthBytes => _allowedGrowthBytes;

    /// <summary>本批累计写出字节（只增不减；上界口径）。</summary>
    internal long BytesWritten
    {
        get { lock (_gate) { return _bytesWritten; } }
    }

    /// <summary>是否已经越过允许增长（<b>超限事实的唯一判定依据</b>，与异常如何被包装无关）。</summary>
    internal bool QuotaExceeded
    {
        get { lock (_gate) { return _violation is not null; } }
    }

    /// <summary>第一次越界时构造的异常（未越界为 <c>null</c>）。</summary>
    internal IndexWriteQuotaExceededException? Violation
    {
        get { lock (_gate) { return _violation; } }
    }

    /// <summary>本会话创建过的文件数（含后续被回滚删除的）。</summary>
    internal int CreatedFileCount
    {
        get { lock (_gate) { return _createdFiles.Count; } }
    }

    /// <summary>本会话删除过的文件数（含回滚清理）。</summary>
    internal int DeletedFileCount
    {
        get { lock (_gate) { return _deletedFiles.Count; } }
    }

    /// <summary>本会话创建过的段数据文件数（Lucene 段文件以 <c>_</c> 开头；不含 <c>segments_N</c> / <c>write.lock</c>）。</summary>
    internal int SegmentFileCreatedCount
    {
        get { lock (_gate) { return _createdFiles.Count(IsSegmentDataFile); } }
    }

    /// <summary>本会话删除过的段数据文件数（合并回收 + 回滚清理都算）。</summary>
    internal int SegmentFileDeletedCount
    {
        get { lock (_gate) { return _deletedFiles.Count(IsSegmentDataFile); } }
    }

    /// <summary>本会话创建 / 删除过的文件名快照（证据用；顺序即发生顺序）。</summary>
    internal (string[] Created, string[] Deleted) FileNameSnapshot()
    {
        lock (_gate)
        {
            return (_createdFiles.ToArray(), _deletedFiles.ToArray());
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 返回计数代理（<see cref="QuotaEnforcingIndexOutput"/>）而不是未包装的 <c>IndexOutput</c>；
    /// 这是本层唯一的「拦截点」—— 一旦返回未包装实例，计数与超限判定全部失效（M3 靶点）。
    /// </remarks>
    public override IndexOutput CreateOutput(string name, IOContext context)
    {
        var inner = _inner.CreateOutput(name, context);
        lock (_gate)
        {
            _createdFiles.Add(name);
        }

        return new QuotaEnforcingIndexOutput(this, inner, name);
    }

    /// <inheritdoc />
    /// <remarks>删除照旧委托（不改语义），仅登记文件名以便报告「段文件回收 / 回滚清理」。</remarks>
    public override void DeleteFile(string name)
    {
        _inner.DeleteFile(name);
        lock (_gate)
        {
            _deletedFiles.Add(name);
        }
    }

    /// <summary>
    /// 记账并**在超限前**中止：先累加，越过允许增长即抛出（调用方的该次写出不执行）。
    /// <para>抛出发生在锁内 —— 锁会随异常正常释放；后续任何写出都会被同一个已记录的越界事实拒绝。</para>
    /// </summary>
    internal void AccountWrite(string fileName, int byteCount)
    {
        lock (_gate)
        {
            _bytesWritten += byteCount;
            if (_bytesWritten > _allowedGrowthBytes)
            {
                _violation ??= new IndexWriteQuotaExceededException(
                    fileName,
                    _bytesWritten,
                    _allowedGrowthBytes,
                    _budgetBytes);

                throw _violation;
            }
        }
    }

    private static bool IsSegmentDataFile(string name) =>
        name.Length > 0 && name[0] == '_';

    /// <summary>
    /// 计数代理：把 <c>IndexOutput</c> 的写出原语转成「记账 + 委托」。
    /// <para>
    /// 只覆写写出原语（<c>WriteByte</c> / <c>WriteBytes</c>）与必须实现的抽象成员；
    /// <c>WriteInt16/32/64</c>、<c>WriteVInt32/64</c>、<c>WriteString</c> 等由 <c>DataOutput</c>
    /// 经由这两个原语实现 ⇒ 不会被漏计，也不会被重复计数（本类不覆写它们，它们调用的是本类的
    /// <see cref="WriteByte"/>，而不是 <c>_inner</c> 的）。
    /// </para>
    /// </summary>
    private sealed class QuotaEnforcingIndexOutput : IndexOutput
    {
        private readonly QuotaEnforcingDirectory _owner;
        private readonly IndexOutput _inner;
        private readonly string _name;

        internal QuotaEnforcingIndexOutput(QuotaEnforcingDirectory owner, IndexOutput inner, string name)
        {
            _owner = owner;
            _inner = inner;
            _name = name;
        }

        /// <inheritdoc />
        public override void WriteByte(byte b)
        {
            _owner.AccountWrite(_name, 1);
            _inner.WriteByte(b);
        }

        /// <inheritdoc />
        public override void WriteBytes(byte[] b, int offset, int length)
        {
            _owner.AccountWrite(_name, length);
            _inner.WriteBytes(b, offset, length);
        }

        /// <inheritdoc />
        public override void Flush() => _inner.Flush();

        /// <inheritdoc />
        protected override void Dispose(bool disposing) => _inner.Dispose();

        /// <inheritdoc />
        public override long Position => _inner.Position;

        /// <inheritdoc />
        // Lucene.NET 4.8 的 IndexOutput.Seek(long) 已被标 Obsolete（5.0 移除），但仍是抽象成员 ⇒ 必须实现。
        // 本类只做委托，不引入新的使用点（编译器找不到非过时重载，故就地抑声）。
#pragma warning disable CS0672, CS0618
        public override void Seek(long pos) => _inner.Seek(pos);
#pragma warning restore CS0672, CS0618

        /// <inheritdoc />
        public override long Checksum => _inner.Checksum;

        /// <inheritdoc />
        public override long Length
        {
            get => _inner.Length;
            set => _inner.Length = value;
        }
    }
}
