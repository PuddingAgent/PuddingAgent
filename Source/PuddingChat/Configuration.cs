namespace PuddingChat;

public enum SecretChange { Keep, Replace, Clear }
public sealed record ProviderSettings(string Id, string Name, string BaseUrl, bool Enabled, bool HasKey, ModelSettings[] Models);
public sealed record ModelSettings(string Id, string Name, string Protocol, int ContextTokens, int OutputTokens);
public sealed record ProviderModelEdit(string ProviderId, string ProviderName, string BaseUrl, bool Enabled,
    ModelSettings Model, SecretChange KeyChange, string? NewKey)
{
    public override string ToString() => $"ProviderModelEdit({ProviderId}, {Model.Id}, {KeyChange}, [redacted])";
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProviderId) || ProviderId.Length > 80 || ProviderId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("服务商标识只能包含字母、数字、下划线或连字符。");
        if (string.IsNullOrWhiteSpace(ProviderName) || string.IsNullOrWhiteSpace(Model.Id) || string.IsNullOrWhiteSpace(Model.Name))
            throw new ArgumentException("请填写服务商名称、模型标识与名称。");
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("服务地址必须是 HTTP/HTTPS 地址，不含账号、查询参数或片段。");
        if (Model.Protocol is not ("openai" or "responses" or "anthropic") || Model.ContextTokens <= 0
            || Model.OutputTokens <= 0 || Model.OutputTokens > Model.ContextTokens)
            throw new ArgumentException("请选择协议并填写有效的上下文与输出 token 上限。");
        if (!Enum.IsDefined(KeyChange) || KeyChange == SecretChange.Replace && string.IsNullOrWhiteSpace(NewKey))
            throw new ArgumentException("替换密钥时必须填写新密钥。");
    }
}
public sealed record RoleSettings(RoleKey Key, string Name, string Description, bool Enabled, string Role,
    string SystemPrompt, string? ProviderId, string? ModelId);
public interface IConfigurationClient
{
    Task<ProviderSettings[]> GetProvidersAsync(CancellationToken ct);
    Task SaveProviderModelAsync(ProviderModelEdit edit, CancellationToken ct);
    Task<RoleSettings> GetRoleSettingsAsync(RoleKey role, CancellationToken ct);
    Task SaveRoleSettingsAsync(RoleSettings edit, CancellationToken ct);
}
