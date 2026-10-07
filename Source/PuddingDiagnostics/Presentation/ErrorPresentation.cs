namespace PuddingCode.Diagnostics;

/// <summary>错误严重度（线路字符串，前端配色/图标按此分组）。</summary>
public static class ErrorSeverities
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";
}

/// <summary>
/// 面向界面的**简略呈现**（可诊断基础设施设计 §12）。
/// <para>
/// 三条硬约束：
/// ① <see cref="Title"/> 必须一眼可识别「哪一类错误」，不允许出现异常类型名或英文原文；
/// ② <see cref="ShortCause"/> 只讲大概原因，技术细节留给「复制现场」的完整载荷；
/// ③ 未登记的码必须把**原码**显示出来（沿用 `GOAL_BLOCKER_CODES` 的既有纪律），
///    不允许把未知折叠成一个人畜无害的通用文案。
/// </para>
/// </summary>
public sealed record ErrorPresentation
{
    /// <summary>错误标题（短，一眼可识别）。</summary>
    public required string Title { get; init; }

    /// <summary>大概原因（一句话，不含异常类型名）。</summary>
    public required string ShortCause { get; init; }

    public required string Severity { get; init; }

    /// <summary>是否可重试（决定界面是否显示「重试」）。</summary>
    public required bool Retryable { get; init; }

    /// <summary>建议的下一步动作（短按钮/提示文案）。</summary>
    public required string PrimaryAction { get; init; }

    /// <summary>稳定因果码（未知码也原样显示）。</summary>
    public required string CauseCode { get; init; }

    /// <summary>类别（transport/provider/local/vision/unknown），供分组与配色。</summary>
    public required string Category { get; init; }
}

/// <summary>码 → 界面呈现的目录（未知码走 category 兜底并保留原码）。</summary>
public static class ErrorPresentationCatalog
{
    public static ErrorPresentation Describe(string? causeCode, bool retryable)
    {
        var category = DiagnosticCauseCode.CategoryOf(causeCode);
        var (title, action, severity) = causeCode switch
        {
            DiagnosticCauseCode.RequestUploadReset or DiagnosticCauseCode.ResponseReadReset =>
                ("模型服务连接中断", "重试", ErrorSeverities.Error),
            DiagnosticCauseCode.ConnectFailed or DiagnosticCauseCode.DnsFailed =>
                ("无法连接模型服务", "检查网络后重试", ErrorSeverities.Error),
            DiagnosticCauseCode.TlsFailed =>
                ("模型服务安全连接失败", "检查代理/证书", ErrorSeverities.Error),
            DiagnosticCauseCode.FirstChunkTimeout or DiagnosticCauseCode.HttpClientTimeout =>
                ("模型响应超时", "重试", ErrorSeverities.Warning),
            DiagnosticCauseCode.StreamIdleTimeout =>
                ("模型输出中断", "重新发起一轮", ErrorSeverities.Warning),
            DiagnosticCauseCode.UnclassifiedTransport =>
                ("模型调用在传输层失败", "重试一次", ErrorSeverities.Error),
            DiagnosticCauseCode.Http401Or403 =>
                ("模型服务拒绝鉴权", "检查密钥与模型权限", ErrorSeverities.Error),
            DiagnosticCauseCode.Http400 =>
                ("模型服务拒绝了本次请求", "检查模型/协议配置", ErrorSeverities.Error),
            DiagnosticCauseCode.Http404 =>
                ("模型或端点不存在", "核对 baseUrl 与模型名", ErrorSeverities.Error),
            DiagnosticCauseCode.Http429 =>
                ("模型服务限流", "稍后重试", ErrorSeverities.Warning),
            DiagnosticCauseCode.Http5xx =>
                ("模型服务故障", "退避重试或切换服务商", ErrorSeverities.Error),
            DiagnosticCauseCode.ProtocolInvalid =>
                ("模型协议不匹配", "核对协议配置", ErrorSeverities.Error),
            DiagnosticCauseCode.RequestTooLarge =>
                ("请求体超过限制", "精简上下文或图片", ErrorSeverities.Error),
            DiagnosticCauseCode.Cancelled =>
                ("本轮已取消", "重新发起", ErrorSeverities.Info),
            DiagnosticCauseCode.CircuitOpen =>
                ("服务商熔断中", "等待恢复或切换服务商", ErrorSeverities.Warning),
            DiagnosticCauseCode.RateLimitWaitCancelled =>
                ("等待并发槽位被取消", "稍后重试", ErrorSeverities.Info),
            DiagnosticCauseCode.UnclassifiedLocal =>
                ("模型调用失败", "复制现场并反馈", ErrorSeverities.Error),
            _ => (category switch
            {
                DiagnosticCauseCategory.Vision => ("图片处理失败", "复制现场并反馈", ErrorSeverities.Error),
                DiagnosticCauseCategory.Transport => ("模型服务连接异常", "复制现场并反馈", ErrorSeverities.Error),
                DiagnosticCauseCategory.Provider => ("模型服务返回错误", "复制现场并反馈", ErrorSeverities.Error),
                _ => ("模型调用失败", "复制现场并反馈", ErrorSeverities.Error),
            }),
        };

        var descriptor = DiagnosticCauseCatalog.Describe(causeCode);

        return new ErrorPresentation
        {
            Title = title,
            ShortCause = descriptor.UserMessage,
            Severity = severity,
            Retryable = retryable,
            PrimaryAction = action,
            // 未知码原样显示：把「未知」伪装成已知会直接误导定位方向。
            CauseCode = string.IsNullOrWhiteSpace(causeCode) ? DiagnosticCauseCode.UnclassifiedLocal : causeCode!,
            Category = category,
        };
    }

    /// <summary>从一次失败的完整因果结论生成呈现。</summary>
    public static ErrorPresentation Present(DiagnosticCause cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        return Describe(cause.Code, cause.Retryable);
    }
}
