namespace PuddingCode.Diagnostics;

/// <summary>
/// 失败发生在调用链的**哪个阶段**（可诊断基础设施设计 §5.3）。
/// <para>
/// 阶段是回答 Q2「为什么」的必要条件：同样是 socket 重置，
/// 「写请求体」与「读响应流」的处置完全不同。
/// </para>
/// </summary>
public enum DiagnosticPhaseKind
{
    Unknown = 0,

    /// <summary>组装请求（上下文/工具 schema/图片引用）。</summary>
    Build = 1,

    /// <summary>序列化请求体（含 base64 / 文件重建）。</summary>
    Serialize = 2,

    /// <summary>写出请求体。对端在此阶段重置即 2026-10-07 事故。</summary>
    RequestUpload = 3,

    /// <summary>请求体已发完，等待响应头/首字节。</summary>
    AwaitResponseHeaders = 4,

    /// <summary>读响应体 / SSE 流。</summary>
    ReadResponseStream = 5,

    /// <summary>落库或投影阶段（终态写入失败等）。</summary>
    Persist = 6,

    /// <summary>等待并发槽位（请求尚未构造）。</summary>
    Queued = 7,
}

/// <summary>阶段与线路字符串的映射（落库/日志取值稳定性由用例锁住）。</summary>
public static class DiagnosticPhases
{
    public const string UnknownWire = "unknown";
    public const string BuildWire = "build";
    public const string SerializeWire = "serialize";
    public const string RequestUploadWire = "request_upload";
    public const string AwaitResponseHeadersWire = "await_response_headers";
    public const string ReadResponseStreamWire = "read_response_stream";
    public const string PersistWire = "persist";
    public const string QueuedWire = "queued";

    public static string ToWire(DiagnosticPhaseKind phase) => phase switch
    {
        DiagnosticPhaseKind.Build => BuildWire,
        DiagnosticPhaseKind.Serialize => SerializeWire,
        DiagnosticPhaseKind.RequestUpload => RequestUploadWire,
        DiagnosticPhaseKind.AwaitResponseHeaders => AwaitResponseHeadersWire,
        DiagnosticPhaseKind.ReadResponseStream => ReadResponseStreamWire,
        DiagnosticPhaseKind.Persist => PersistWire,
        DiagnosticPhaseKind.Queued => QueuedWire,
        _ => UnknownWire,
    };

    public static DiagnosticPhaseKind FromWire(string? wire) => wire switch
    {
        BuildWire => DiagnosticPhaseKind.Build,
        SerializeWire => DiagnosticPhaseKind.Serialize,
        RequestUploadWire => DiagnosticPhaseKind.RequestUpload,
        AwaitResponseHeadersWire => DiagnosticPhaseKind.AwaitResponseHeaders,
        ReadResponseStreamWire => DiagnosticPhaseKind.ReadResponseStream,
        PersistWire => DiagnosticPhaseKind.Persist,
        QueuedWire => DiagnosticPhaseKind.Queued,
        _ => DiagnosticPhaseKind.Unknown,
    };

    /// <summary>中文短标签，用于事故结论句。</summary>
    public static string Label(DiagnosticPhaseKind phase) => phase switch
    {
        DiagnosticPhaseKind.Build => "组装请求",
        DiagnosticPhaseKind.Serialize => "序列化请求体",
        DiagnosticPhaseKind.RequestUpload => "上传请求体",
        DiagnosticPhaseKind.AwaitResponseHeaders => "等待响应头",
        DiagnosticPhaseKind.ReadResponseStream => "读取响应流",
        DiagnosticPhaseKind.Persist => "写入终态",
        DiagnosticPhaseKind.Queued => "等待并发槽位",
        _ => "未知阶段",
    };
}
