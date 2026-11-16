namespace HarnessAgent.Core.Secrets;

/// <summary>
/// KeyVault 主密钥的来源。
///
/// <para>存在的理由是一个具体的故障：<c>KeyVaultService</c> 在环境变量缺失时
/// 会随机生成 32 字节主密钥，并只写进<b>进程</b>环境——重启即换，已存的密钥全部解不开。
/// 代码自己的日志已经警告过这件事，但没有默认的持久化路径。</para>
///
/// <para>抽成接口是因为"主密钥放哪"是平台差异：Windows 用 DPAPI 封装成文件最自然，
/// 其他宿主可以换成 KMS、密钥文件或环境变量。KeyVaultService 只依赖这个接口。</para>
/// </summary>
public interface IMasterKeyProvider
{
    /// <summary>
    /// 取已存在的主密钥；没有可用主密钥时返回 <see langword="null"/>。
    ///
    /// <para>返回 null 而不是抛异常，是为了让调用方沿用原有的降级路径
    /// （生成临时密钥并告警），不改变启动语义。</para>
    /// </summary>
    byte[]? TryResolve();

    /// <summary>
    /// 持久化一个刚生成的主密钥。失败应抛异常——静默失败会重演"重启失联"。
    /// </summary>
    void Persist(ReadOnlySpan<byte> masterKey);
}
