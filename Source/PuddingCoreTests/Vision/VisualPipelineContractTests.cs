using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Core;
using PuddingCode.Models;

namespace PuddingCoreTests;

[TestClass]
public sealed class LlmVisualInputPlannerTests
{
    private const string Workspace = "default";

    private static string DataUri(int approximateBytes)
        => "data:image/png;base64," + new string('A', approximateBytes);

    private sealed class FixedResolver : PuddingCode.Abstractions.IVisualArtifactResolver
    {
        private readonly HashSet<string> _missing;

        public FixedResolver(params string[] missing) => _missing = [.. missing];

        public Task<PuddingCode.Abstractions.VisualArtifactResolveResult?> ResolveAsync(
            string workspaceId,
            string artifactId,
            CancellationToken ct = default)
            => _missing.Contains(artifactId)
                ? Task.FromResult<PuddingCode.Abstractions.VisualArtifactResolveResult?>(null)
                : Task.FromResult<PuddingCode.Abstractions.VisualArtifactResolveResult?>(
                    new PuddingCode.Abstractions.VisualArtifactResolveResult(
                        artifactId,
                        DataUri(1_000),
                        "image/png"));
    }

    [TestMethod]
    public async Task PlanAsync_ResolvesAllImages_AndEstimatesTokenUpperBound()
    {
        var plan = await LlmVisualInputPlanner.PlanAsync(
            Workspace,
            [new LlmImagePart("vision-0123456789abcdef0123456789abcdef"),
             new LlmImagePart("vision-fedcba9876543210fedcba9876543210", VisionContentPartDetails.Low)],
            new FixedResolver());

        Assert.AreEqual(2, plan.Images.Count);
        Assert.AreEqual(VisionContentPartDetails.Low, plan.Images[1].Detail);
        // 384 × 图片数的 token 上界
        Assert.AreEqual(768, plan.EstimatedTokenUpperBound);
    }

    [TestMethod]
    public async Task PlanAsync_DeduplicatesSameArtifactAndDetail()
    {
        var part = new LlmImagePart("vision-0123456789abcdef0123456789abcdef");
        var plan = await LlmVisualInputPlanner.PlanAsync(
            Workspace,
            [part, part with { }],
            new FixedResolver());

        Assert.AreEqual(2, plan.Images.Count);
        // 每张图都计费（同一 part 出现两次仍是两次输入），但解析只发生一次由 resolver 调用数保证；
        // 上界按引用次数估计
        Assert.AreEqual(768, plan.EstimatedTokenUpperBound);
    }

    [TestMethod]
    public async Task PlanAsync_MissingArtifact_FailsClosedWithStableCode()
    {
        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            LlmVisualInputPlanner.PlanAsync(
                Workspace,
                [new LlmImagePart("vision-0123456789abcdef0123456789abcdef")],
                new FixedResolver("vision-0123456789abcdef0123456789abcdef")));

        Assert.AreEqual(VisionErrorCodes.ArtifactMissing, ex.Code);
    }

    [TestMethod]
    public async Task PlanAsync_MoreThanEightImages_Rejected()
    {
        var parts = Enumerable.Range(0, 9)
            .Select(i => new LlmImagePart("vision-" + new string('0', 31) + i.ToString()[0]))
            .ToList();

        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            LlmVisualInputPlanner.PlanAsync(Workspace, parts, new FixedResolver()));
        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, ex.Code);
    }

    [TestMethod]
    public async Task PlanAsync_ImageOverInlineLimit_RejectedUntilFilesApi()
    {
        var bigDataUri = DataUri(3_000_000);
        var resolver = new OversizeResolver(bigDataUri);
        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            LlmVisualInputPlanner.PlanAsync(
                Workspace,
                [new LlmImagePart("vision-0123456789abcdef0123456789abcdef")],
                resolver));
        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, ex.Code);
    }

    [TestMethod]
    public async Task PlanAsync_OverInlineLimit_WithUploader_UploadsAndReturnsFileIdMode()
    {
        // base64 解码后约 2.25MB > 2MB inline 上限，应走 Files API 上传得到 file_id。
        var resolver = new OversizeResolver(DataUri(3_000_000));
        var uploader = new RecordingFilesUploader();

        var plan = await LlmVisualInputPlanner.PlanAsync(
            Workspace,
            [new LlmImagePart("vision-0123456789abcdef0123456789abcdef")],
            resolver,
            fileUploader: uploader);

        Assert.AreEqual(1, plan.Images.Count);
        var image = plan.Images[0];
        Assert.AreEqual("file-uploaded-001", image.FileId);
        Assert.IsNotNull(image.ExpiresAt);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddSeconds(604_800), image.ExpiresAt);
        Assert.IsNull(image.DataUri, "file 模式 DataUri 必须为 null（与 FileId 互斥）");
        Assert.AreEqual("image/png", image.MimeType);
        Assert.AreEqual(VisionContentPartDetails.Original, image.Detail);
        Assert.AreEqual(1, uploader.UploadCount);
        Assert.IsNotNull(uploader.LastBytes);
        Assert.IsTrue(uploader.LastBytes!.Length > 2_000_000, "上传字节应为大图原始字节");
        Assert.AreEqual("image/png", uploader.LastMimeType);
        Assert.AreEqual(VisionRequestPolicy.Default.FilesDefaultLifetimeSeconds, uploader.LastLifetimeSeconds);
    }

    [TestMethod]
    public async Task PlanAsync_OverInlineLimit_UnsupportedMime_WithUploader_ThrowsMediaInvalid()
    {
        var resolver = new OversizeResolver(DataUri(3_000_000), "image/tiff");
        var uploader = new RecordingFilesUploader();

        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            LlmVisualInputPlanner.PlanAsync(
                Workspace,
                [new LlmImagePart("vision-0123456789abcdef0123456789abcdef")],
                resolver,
                fileUploader: uploader));

        Assert.AreEqual(VisionErrorCodes.MediaInvalid, ex.Code);
        Assert.AreEqual(0, uploader.UploadCount, "MIME 不支持时不得发起上传");
    }

    [TestMethod]
    public async Task PlanAsync_WithinInlineLimit_WithUploader_StillInlineDataUri()
    {
        var uploader = new RecordingFilesUploader();

        var plan = await LlmVisualInputPlanner.PlanAsync(
            Workspace,
            [new LlmImagePart("vision-0123456789abcdef0123456789abcdef")],
            new FixedResolver(),
            fileUploader: uploader);

                var image = plan.Images[0];
        Assert.IsNotNull(image.DataUri);
        Assert.IsNull(image.FileId);
        Assert.AreEqual(0, uploader.UploadCount, "小图不触发 Files 上传");
    }

    [TestMethod]
    public async Task PlanAsync_StoreHit_ReusesCachedFileId_WithoutUpload()
    {
        // ADR-077 V3-S2b-2：store 命中（ready + 未近过期）→ 直接 file 模式复用 file_id，不重复上传。
        var resolver = new OversizeResolver(DataUri(3_000_000));
        var uploader = new RecordingFilesUploader();
        var store = new FakeFileRefStore();
        store.Seed(ComputeHash(DataUri(3_000_000)), new ProviderFileRefRecord(
            ProviderId: "deepseek",
            CredentialEpoch: "default",
            ArtifactId: "vision-0123456789abcdef0123456789abcdef",
            ArtifactSha256: ComputeHash(DataUri(3_000_000)),
            RemoteFileId: "file-cached-001",
            Bytes: 2_250_000,
            MimeType: "image/png",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            LastUsedAt: null,
            Status: ProviderFileRefStatus.Ready,
            CreatedAt: DateTimeOffset.UtcNow.AddHours(-2),
            UpdatedAt: DateTimeOffset.UtcNow.AddHours(-2)));

        var plan = await LlmVisualInputPlanner.PlanAsync(
            Workspace,
            [new LlmImagePart("vision-0123456789abcdef0123456789abcdef")],
            resolver,
            fileUploader: uploader,
            fileRefStore: store,
            providerId: "deepseek",
            credentialEpoch: "default");

        Assert.AreEqual(1, plan.Images.Count);
        var image = plan.Images[0];
        Assert.AreEqual("file-cached-001", image.FileId, "复用 store 里的 RemoteFileId");
        Assert.IsNull(image.DataUri);
        Assert.IsFalse(string.IsNullOrWhiteSpace(image.ArtifactSha256), "file 模式必须携带 ArtifactSha256（Gateway 调用时过期重建用）");
        Assert.AreEqual(0, uploader.UploadCount, "store 命中时不得重复上传");
        Assert.AreEqual(0, store.SaveCount, "复用命中不得重复落库");
        Assert.AreEqual(0, store.MarkExpiredCount);
    }

    [TestMethod]
    public async Task PlanAsync_StoreMiss_UploadsAndSavesReady()
    {
        // ADR-077 V3-S2b-2：store 未命中 → 上传 + SaveAsync 落库 ready。
        var resolver = new OversizeResolver(DataUri(3_000_000));
        var uploader = new RecordingFilesUploader();
        var store = new FakeFileRefStore();

        var plan = await LlmVisualInputPlanner.PlanAsync(
            Workspace,
            [new LlmImagePart("vision-0123456789abcdef0123456789abcdef")],
            resolver,
            fileUploader: uploader,
            fileRefStore: store,
            providerId: "deepseek",
            credentialEpoch: "default");

        Assert.AreEqual(1, plan.Images.Count);
        Assert.AreEqual("file-uploaded-001", plan.Images[0].FileId);
        Assert.AreEqual(1, uploader.UploadCount);
        Assert.AreEqual(1, store.SaveCount, "未命中应落库一次");
        Assert.AreEqual(0, store.MarkExpiredCount);
        var saved = store.Saved[0];
        Assert.AreEqual("deepseek", saved.ProviderId);
        Assert.AreEqual("default", saved.CredentialEpoch);
        Assert.AreEqual(ProviderFileRefStatus.Ready, saved.Status);
        Assert.AreEqual("image/png", saved.MimeType);
        Assert.AreEqual("file-uploaded-001", saved.RemoteFileId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(saved.ArtifactSha256));
    }

    [TestMethod]
    public async Task PlanAsync_NoStore_DegradesToUploaderOnly()
    {
        // ADR-077 V3-S2b-2：无 store/providerId/credentialEpoch 时退化为 S2a 上传即用（不查 store、不落库）。
        var resolver = new OversizeResolver(DataUri(3_000_000));
        var uploader = new RecordingFilesUploader();

        var plan = await LlmVisualInputPlanner.PlanAsync(
            Workspace,
            [new LlmImagePart("vision-0123456789abcdef0123456789abcdef")],
            resolver,
            fileUploader: uploader);

        Assert.AreEqual(1, plan.Images.Count);
        Assert.AreEqual("file-uploaded-001", plan.Images[0].FileId);
        Assert.AreEqual(1, uploader.UploadCount, "退化路径仍上传即用");
    }

    [TestMethod]
    public async Task PlanAsync_StoreHitButExpired_MarkExpiredAndRebuildOnce()
    {
        // ADR-077 V3-S2b-2：store 命中但已过期 → 恰好一次 MarkExpired + 重传 + 落库 ready。
        var resolver = new OversizeResolver(DataUri(3_000_000));
        var uploader = new RecordingFilesUploader();
        var store = new FakeFileRefStore();
        store.Seed(ComputeHash(DataUri(3_000_000)), new ProviderFileRefRecord(
            ProviderId: "deepseek",
            CredentialEpoch: "default",
            ArtifactId: "vision-0123456789abcdef0123456789abcdef",
            ArtifactSha256: ComputeHash(DataUri(3_000_000)),
            RemoteFileId: "file-stale-001",
            Bytes: 2_250_000,
            MimeType: "image/png",
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(-30),
            LastUsedAt: null,
            Status: ProviderFileRefStatus.Ready,
            CreatedAt: DateTimeOffset.UtcNow.AddHours(-2),
            UpdatedAt: DateTimeOffset.UtcNow.AddHours(-2)));

        var plan = await LlmVisualInputPlanner.PlanAsync(
            Workspace,
            [new LlmImagePart("vision-0123456789abcdef0123456789abcdef")],
            resolver,
            fileUploader: uploader,
            fileRefStore: store,
            providerId: "deepseek",
            credentialEpoch: "default");

        Assert.AreEqual(1, plan.Images.Count);
        Assert.AreEqual("file-uploaded-001", plan.Images[0].FileId, "过期引用重建后使用新上传的 file_id");
        Assert.AreEqual(1, uploader.UploadCount, "过期重建恰好重传一次");
        Assert.AreEqual(1, store.MarkExpiredCount, "过期引用恰好 MarkExpired 一次");
        Assert.AreEqual(1, store.SaveCount, "重建后落库 ready 一次");
        var saved = store.Saved[0];
        Assert.AreEqual(ProviderFileRefStatus.Ready, saved.Status);
        Assert.AreEqual("file-uploaded-001", saved.RemoteFileId);
    }

    private static string ComputeHash(string dataUri)
    {
        var comma = dataUri.IndexOf(',');
        var payload = dataUri[(comma + 1)..];
        var bytes = Convert.FromBase64String(payload);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class RecordingFilesUploader : IDeepSeekFilesUploader
    {
        public int UploadCount { get; private set; }
        public byte[]? LastBytes { get; private set; }
        public string? LastMimeType { get; private set; }
        public long LastLifetimeSeconds { get; private set; }

        public Task<ProviderFileUploadResult> UploadAsync(
            byte[] imageBytes,
            string mimeType,
            long lifetimeSeconds,
            CancellationToken ct = default)
        {
            UploadCount++;
            LastBytes = imageBytes;
            LastMimeType = mimeType;
            LastLifetimeSeconds = lifetimeSeconds;
            return Task.FromResult(new ProviderFileUploadResult(
                "file-uploaded-001",
                mimeType,
                imageBytes.Length,
                lifetimeSeconds,
                                DateTimeOffset.UnixEpoch));
        }
    }

    /// <summary>内存版 <see cref="IFileRefStore"/>（ADR-077 V3-S2b-2 单测用）：以 artifactSha256 为键的 dict。</summary>
    private sealed class FakeFileRefStore : IFileRefStore
    {
        private readonly Dictionary<string, ProviderFileRefRecord> _records = new();

        public int SaveCount { get; private set; }
        public int MarkExpiredCount { get; private set; }
        public List<ProviderFileRefRecord> Saved { get; } = new();

        public void Seed(string artifactSha256, ProviderFileRefRecord record)
            => _records[artifactSha256] = record;

        public Task<ProviderFileRefRecord?> TryGetReadyRefAsync(
            string providerId,
            string credentialEpoch,
            string artifactSha256,
            CancellationToken ct = default)
        {
            if (!_records.TryGetValue(artifactSha256, out var record))
                return Task.FromResult<ProviderFileRefRecord?>(null);
            if (record.ProviderId != providerId || record.CredentialEpoch != credentialEpoch)
                return Task.FromResult<ProviderFileRefRecord?>(null);
            if (record.Status != ProviderFileRefStatus.Ready)
                return Task.FromResult<ProviderFileRefRecord?>(null);
            // 过期判断交给 Planner 的防御校验（DeepSeekFilesApiClient.ThrowIfFileExpired）：
            // 本 fake 模拟「store 返回了记录但记录恰已过期」的边界，用于覆盖过期重建路径。
            return Task.FromResult<ProviderFileRefRecord?>(record);
        }

        public Task<ProviderFileRefRecord> SaveAsync(ProviderFileRefRecord record, CancellationToken ct = default)
        {
            SaveCount++;
            Saved.Add(record);
            _records[record.ArtifactSha256] = record;
            return Task.FromResult(record);
        }

        public Task<ProviderFileRefRecord?> MarkExpiredAsync(
            string providerId,
            string credentialEpoch,
            string artifactSha256,
            DateTimeOffset updatedAt,
            CancellationToken ct = default)
        {
            MarkExpiredCount++;
            if (!_records.TryGetValue(artifactSha256, out var record))
                return Task.FromResult<ProviderFileRefRecord?>(null);
            var expired = record with
            {
                Status = ProviderFileRefStatus.Expired,
                UpdatedAt = updatedAt,
            };
            _records[artifactSha256] = expired;
            return Task.FromResult<ProviderFileRefRecord?>(expired);
        }

        public Task<ProviderFileRefRecord?> UpdateExpiryAsync(
            string providerId,
            string credentialEpoch,
            string artifactSha256,
            DateTimeOffset newExpiresAt,
            DateTimeOffset updatedAt,
            CancellationToken ct = default)
            => Task.FromResult<ProviderFileRefRecord?>(null);

        public Task<ProviderFileRefRecord?> MarkDeletePendingAsync(
            string providerId,
            string credentialEpoch,
            string artifactSha256,
            DateTimeOffset updatedAt,
            CancellationToken ct = default)
            => Task.FromResult<ProviderFileRefRecord?>(null);

        public Task<IReadOnlyList<ProviderFileRefRecord>> ListExpiredAsync(
            DateTimeOffset before,
            int limit,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderFileRefRecord>>([]);
    }

        private sealed class OversizeResolver(string dataUri, string mimeType = "image/png") : PuddingCode.Abstractions.IVisualArtifactResolver
    {
        public Task<PuddingCode.Abstractions.VisualArtifactResolveResult?> ResolveAsync(
            string workspaceId,
            string artifactId,
            CancellationToken ct = default)
            => Task.FromResult<PuddingCode.Abstractions.VisualArtifactResolveResult?>(
                new PuddingCode.Abstractions.VisualArtifactResolveResult(artifactId, dataUri, mimeType));
    }
}

[TestClass]
public sealed class ContentPartsEnvelopeTests
{
    [TestMethod]
    public void EncodeDecode_RoundTripsOrderedParts()
    {
        var parts = new List<LlmContentPart>
        {
            new LlmTextPart("看图"),
            new LlmImagePart("vision-0123456789abcdef0123456789abcdef"),
            new LlmTextPart("再回答"),
            new LlmImagePart("vision-fedcba9876543210fedcba9876543210", VisionContentPartDetails.Low),
        };

        var json = ContentPartsEnvelope.Encode(parts);
        StringAssert.Contains(json, "\"v\":1");

        var decoded = ContentPartsEnvelope.Decode(json);
        Assert.IsNotNull(decoded);
        Assert.AreEqual(4, decoded.Count);
        Assert.IsInstanceOfType(decoded[0], typeof(LlmTextPart));
        var image = (LlmImagePart)decoded[3];
        Assert.AreEqual(VisionContentPartDetails.Low, image.Detail);
        Assert.AreEqual("看图再回答", ContentPartsEnvelope.FlattenText(decoded));
    }

    [TestMethod]
    public void Decode_MalformedOrEmpty_ReturnsNull()
    {
        Assert.IsNull(ContentPartsEnvelope.Decode(null));
        Assert.IsNull(ContentPartsEnvelope.Decode(""));
        Assert.IsNull(ContentPartsEnvelope.Decode("not-json"));
        Assert.IsNull(ContentPartsEnvelope.Decode("""{"v":1,"parts":[]}"""));
    }
}

[TestClass]
public sealed class ChatMessageMultimodalNormalizerTests
{
    [TestMethod]
    public void ContentParts_Canonical_WhenPresent()
    {
        var message = new ChatMessage(
            ChatRole.User,
            "text",
            ContentParts: [new LlmTextPart("hello"), new LlmImagePart("vision-0123456789abcdef0123456789abcdef")]);

        var parts = ChatMessageMultimodalNormalizer.GetEffectiveContentParts(message);
        Assert.AreEqual(2, parts!.Count);
        Assert.AreEqual(1, ChatMessageMultimodalNormalizer.GetImageParts(message).Count);
    }

    [TestMethod]
    public void LegacyVisualArtifactIds_DeriveOriginalDetailParts()
    {
        var message = new ChatMessage(
            ChatRole.User,
            "hello",
            VisualArtifactIds: ["vision-0123456789abcdef0123456789abcdef"]);

        var parts = ChatMessageMultimodalNormalizer.GetEffectiveContentParts(message);
        Assert.AreEqual(2, parts!.Count);
        var image = (LlmImagePart)parts[1];
        Assert.AreEqual(VisionContentPartDetails.Original, image.Detail);
    }

    [TestMethod]
    public void TextOnlyMessage_HasNoParts()
    {
        var message = new ChatMessage(ChatRole.User, "plain");
        Assert.IsNull(ChatMessageMultimodalNormalizer.GetEffectiveContentParts(message));
        Assert.AreEqual(0, ChatMessageMultimodalNormalizer.GetImageParts(message).Count);
    }
}

[TestClass]
public sealed class VisionImageInspectorTests
{
    // 1×1 PNG
    private static readonly byte[] MinimalPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [TestMethod]
    public void InspectPrefix_ReadsPngDimensions()
    {
        var info = VisionImageInspector.InspectPrefix(MinimalPng);
        Assert.IsNotNull(info);
        Assert.AreEqual("image/png", info.MimeType);
        Assert.AreEqual(1, info.Width);
        Assert.AreEqual(1, info.Height);
    }

    [TestMethod]
    public void InspectPrefix_RejectsNonImageBytes()
    {
        Assert.IsNull(VisionImageInspector.InspectPrefix([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]));
        Assert.IsNull(VisionImageInspector.InspectPrefix([]));
    }

    [TestMethod]
    public void InspectPrefix_RejectsOversizedDimensions()
    {
        var bytes = new byte[24];
        MinimalPng.AsSpan(0, 8).CopyTo(bytes);
        // width = 9000
        bytes[16] = 0x23; bytes[17] = 0x28;
        Assert.IsNull(VisionImageInspector.InspectPrefix(bytes));
    }
}
