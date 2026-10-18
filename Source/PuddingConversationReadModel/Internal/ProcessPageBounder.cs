namespace PuddingConversationReadModel;

/// <summary>
/// 过程明细页预算器内部实现（Plan §5.3、§5.4）：从最新（尾部）向过去扫描有界候选，
/// 条数/字节/候选扫描三重预算，输出仍按 sequence 升序。
/// <para>停止时 <see cref="BoundedProcessPage.NextBeforeSequence"/> 取「已实际扫描到的最低 sequence」
/// 作为排他上界；因字节预算被拒绝的候选尚未消费，因此不推进扫描边界，下一页会用
/// <c>&lt; NextBeforeSequence</c> 重新取到它。若第一个候选就被字节预算拒绝（尚未消费任何候选），
/// 边界取该候选 sequence + 1，确保下一页能重新取回该候选而不是停在 0。</para>
/// </summary>
internal static class ProcessPageBounder
{
    internal static BoundedProcessPage Bound(
        IReadOnlyList<ProcessItemCandidate> candidates,
        int? requestedLimit,
        ConversationReadModelLimits limits)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var itemLimit = limits.NormalizeDetailItemLimit(requestedLimit);
        var candidateScanLimit = Math.Max(1, limits.DetailPageCandidateScanLimit);
        var items = new List<BoundedProcessItem>(Math.Min(itemLimit, candidates.Count));

        var truncation = DetailPageTruncation.None;
        long scannedBackThroughSequence = 0;
        var scannedCount = 0;
        var omittedNonDisplayable = 0;
        long totalPreviewBytes = 0;
        long accountedPageBytes = 0;
        var hasMoreBefore = false;
        long? nextBeforeSequence = null;

        for (var index = candidates.Count - 1; index >= 0; index--)
        {
            var candidate = candidates[index];

            if (items.Count >= itemLimit)
            {
                truncation = DetailPageTruncation.ItemLimit;
                hasMoreBefore = true;
                break;
            }

            if (scannedCount >= candidateScanLimit)
            {
                truncation = DetailPageTruncation.ScanBudget;
                hasMoreBefore = true;
                break;
            }

            if (candidate.IsDisplayable)
            {
                var previewContent = candidate.Preview ?? string.Empty;
                var preview = TextBudget.TakeUtf8Prefix(previewContent, limits.GiantEventPreviewBytes);
                var previewBytes = TextBudget.Utf8Length(preview);
                var candidateAccountedBytes = accountedPageBytes + ConversationReadModelLimits.ProcessItemJsonOverheadBytes + previewBytes;
                if (candidateAccountedBytes > limits.DetailPageMaxBytes)
                {
                    // 该候选尚未消费：不推进扫描边界，下一页重新取它。
                    truncation = DetailPageTruncation.ByteBudget;
                    hasMoreBefore = true;
                    if (scannedCount == 0)
                    {
                        // 尚未消费任何候选：边界取该候选 sequence + 1，否则 <0 永远取不到它。
                        scannedBackThroughSequence = candidate.Sequence;
                        nextBeforeSequence = candidate.Sequence + 1;
                    }
                    break;
                }

                accountedPageBytes = candidateAccountedBytes;
                totalPreviewBytes += previewBytes;
                items.Add(new BoundedProcessItem(
                    candidate.Sequence,
                    candidate.EventId,
                    candidate.Kind,
                    preview,
                    TextBudget.Utf8Length(previewContent) > previewBytes,
                    candidate.PayloadBytes));
            }
            else
            {
                omittedNonDisplayable++;
            }

            scannedCount++;
            scannedBackThroughSequence = candidate.Sequence;
        }

        items.Reverse();

        if (truncation != DetailPageTruncation.None && nextBeforeSequence is null)
            nextBeforeSequence = scannedBackThroughSequence;

        return new BoundedProcessPage(
            items,
            scannedBackThroughSequence,
            nextBeforeSequence,
            hasMoreBefore,
            omittedNonDisplayable,
            (int)totalPreviewBytes,
            truncation);
    }
}
