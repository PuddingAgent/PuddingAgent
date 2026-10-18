namespace PuddingConversationReadModel;

/// <summary>
/// 历史消息页预算器内部实现（Plan §5.2、§5.4）：条数上限、8 KiB 单条预览、128 KiB 页预算、
/// messageId 去重与不可显示候选跳过。
/// </summary>
internal static class HistoryPageBounder
{
    internal static BoundedHistoryPage Bound(
        IReadOnlyList<HistoryPageCandidate> candidates,
        int? requestedLimit,
        ConversationReadModelLimits limits)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var itemLimit = limits.NormalizeOpenMessageLimit(requestedLimit);
        var items = new List<BoundedHistoryPageItem>(Math.Min(itemLimit, candidates.Count));
        var seenMessageIds = new HashSet<string>(StringComparer.Ordinal);

        var skippedNonDisplayable = 0;
        var duplicates = 0;
        var omittedForItemLimit = 0;
        var omittedForByteBudget = 0;
        long totalPreviewBytes = 0;
        long accountedPageBytes = 0; // 预览字节 + 每条固定 JSON 开销估算

        var itemLimitReached = false;
        var byteBudgetReached = false;

        foreach (var candidate in candidates)
        {
            if (!candidate.IsDisplayable)
            {
                skippedNonDisplayable++;
                continue;
            }

            if (!seenMessageIds.Add(candidate.MessageId))
            {
                duplicates++;
                continue;
            }

            var content = candidate.Content ?? string.Empty;
            var originalContentBytes = TextBudget.Utf8Length(content);
            var preview = TextBudget.TakeUtf8Prefix(content, limits.SingleMessagePreviewBytes);
            var previewBytes = TextBudget.Utf8Length(preview);

            if (itemLimitReached)
            {
                omittedForItemLimit++;
                continue;
            }

            if (byteBudgetReached)
            {
                omittedForByteBudget++;
                continue;
            }

            if (items.Count >= itemLimit)
            {
                itemLimitReached = true;
                omittedForItemLimit++;
                continue;
            }

            var candidateAccountedBytes = accountedPageBytes + ConversationReadModelLimits.MessageJsonOverheadBytes + previewBytes;
            if (candidateAccountedBytes > limits.OpenPageMaxBytes)
            {
                byteBudgetReached = true;
                omittedForByteBudget++;
                continue;
            }

            accountedPageBytes = candidateAccountedBytes;
            totalPreviewBytes += previewBytes;
            items.Add(new BoundedHistoryPageItem(
                candidate.MessageId,
                candidate.Role,
                preview,
                originalContentBytes > previewBytes,
                originalContentBytes,
                candidate.TurnId));
        }

        return new BoundedHistoryPage(
            items,
            candidates.Count,
            skippedNonDisplayable,
            duplicates,
            omittedForItemLimit,
            omittedForByteBudget,
            (int)totalPreviewBytes,
            omittedForItemLimit + omittedForByteBudget > 0);
    }
}
