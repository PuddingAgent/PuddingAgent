namespace PuddingPlatform.Data.Dtos;

/// <summary>单条密钥的使用情况（管理界面用，不含密钥值）。</summary>
public record KeyVaultUsageEntryDto(
    string Name,
    int ReferenceCount,
    List<string> ReferencedBy,
    string? DuplicateGroupId
);

/// <summary>密钥库使用情况汇总。</summary>
public record KeyVaultUsageReportDto(
    int TotalEntries,
    int OrphanCount,
    int DuplicateGroupCount,
    List<KeyVaultUsageEntryDto> Entries
);
