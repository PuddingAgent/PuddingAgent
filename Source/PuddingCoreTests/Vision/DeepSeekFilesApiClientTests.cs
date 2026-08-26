using System.Net;
using System.Net.Http.Headers;
using PuddingCode.Core;

namespace PuddingCoreTests;

/// <summary>ADR-077 V3-S1：DeepSeek Files API 上传客户端单测（fake HttpMessageHandler，不依赖真实网络）。</summary>
[TestClass]
public sealed class DeepSeekFilesApiClientTests
{
    private const string ApiKey = "sk-test-secret-key-0123456789";
    private const string Endpoint = "https://api.deepseek.com/files";
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03];

    // ──────── 上传成功 ────────

    [TestMethod]
    public async Task UploadAsync_Success_ReturnsFileId_WithAuthAndMultipartBody()
    {
        const long createdAt = 1_750_000_000L;
        HttpRequestMessage? captured = null;
        string? purpose = null;
        string? lifetimeSent = null;
        byte[]? fileBytes = null;

        // 请求体断言必须在 fake handler 内完成：UploadAsync 的 using 作用域在方法返回时
        // 会 dispose request/content，handler 执行时尚未释放。
        var client = CreateClient(request =>
        {
            captured = request;
            var multipart = Assert.IsInstanceOfType<MultipartFormDataContent>(request.Content);
            purpose = ReadPartAsync(multipart, "purpose").GetAwaiter().GetResult();
            lifetimeSent = ReadPartAsync(multipart, "lifetime").GetAwaiter().GetResult();
            fileBytes = ReadFilePartAsync(multipart, "file").GetAwaiter().GetResult();
            return JsonResponse(HttpStatusCode.OK, $$"""
                {"id":"file-api-abc123","object":"file","bytes":11,
                 "created_at":{{createdAt}},"purpose":"user_data","lifetime":604800}
                """);
        });

        var result = await client.UploadAsync(PngBytes, "image/png", 604_800, CancellationToken.None);

        // 端点 / 方法 / Authorization 头
        Assert.IsNotNull(captured);
        Assert.AreEqual(HttpMethod.Post, captured!.Method);
        Assert.AreEqual(Endpoint, captured.RequestUri!.AbsoluteUri);
        Assert.IsNotNull(captured.Headers.Authorization);
        Assert.AreEqual("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.AreEqual(ApiKey, captured.Headers.Authorization.Parameter);

        // multipart 请求体：purpose / lifetime / file 字节
        Assert.AreEqual("user_data", purpose);
        Assert.AreEqual("604800", lifetimeSent);
        CollectionAssert.AreEqual(PngBytes, fileBytes);

        // 结果值类型 + ExpiresAt 由 provider created_at + lifetime 计算
        Assert.AreEqual("file-api-abc123", result.FileId);
        Assert.AreEqual("image/png", result.MimeType);
        Assert.AreEqual(PngBytes.Length, result.SourceBytes);
        Assert.AreEqual(604_800, result.LifetimeSeconds);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(createdAt), result.UploadedAt);
        Assert.AreEqual(createdAt + 604_800, result.ExpiresAt.ToUnixTimeSeconds());
    }

    // ──────── 上传失败 / 错误码映射 ────────

    [TestMethod]
    public async Task UploadAsync_HttpError_ThrowsProviderFileUploadFailed_WithoutSecretOrBody()
    {
        var client = CreateClient(_ => JsonResponse(
            HttpStatusCode.InternalServerError,
            """{"error":{"message":"upstream storage unavailable"}}"""));

        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            client.UploadAsync(PngBytes, "image/png", 604_800));

        Assert.AreEqual(VisionErrorCodes.ProviderFileUploadFailed, ex.Code);
        StringAssert.Contains(ex.Message, "500");
        StringAssert.Contains(ex.Message, "upstream storage unavailable");
        Assert.IsFalse(ex.Message.Contains(ApiKey, StringComparison.Ordinal), "异常不得含 ApiKey");
        Assert.IsFalse(ex.Message.Contains("user_data", StringComparison.Ordinal), "异常不得含请求体内容");
        Assert.IsFalse(ex.Message.Contains("vision-", StringComparison.Ordinal), "异常不得含 multipart 字段");
    }

    [TestMethod]
    public async Task UploadAsync_ProviderEchoesKeyInError_IsSanitized()
    {
        // 普通插值字符串：{{ / }} 是字面大括号，{ApiKey} 是插值；避免 raw string 中字面 }} 与插值结束标记冲突
        var errorJson = $"{{\"error\":{{\"message\":\"auth failed key={ApiKey} please retry\"}}}}";
        var client = CreateClient(_ => JsonResponse(HttpStatusCode.BadRequest, errorJson));

        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            client.UploadAsync(PngBytes, "image/png", 604_800));

        Assert.AreEqual(VisionErrorCodes.ProviderFileUploadFailed, ex.Code);
        StringAssert.Contains(ex.Message, "400");
        Assert.IsFalse(ex.Message.Contains(ApiKey, StringComparison.Ordinal), "provider 回显的 key 必须被脱敏");
    }

    [TestMethod]
    public async Task UploadAsync_NonJsonErrorBody_DoesNotEchoRawBody()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream <html>garbage with sk-leak-candidate</html>"),
        });

        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            client.UploadAsync(PngBytes, "image/png", 604_800));

        Assert.AreEqual(VisionErrorCodes.ProviderFileUploadFailed, ex.Code);
        StringAssert.Contains(ex.Message, "502");
        Assert.IsFalse(ex.Message.Contains("sk-leak-candidate", StringComparison.Ordinal), "非 JSON 原文不得进入异常");
        Assert.IsFalse(ex.Message.Contains("<html>", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UploadAsync_SuccessResponseWithoutFileId_ThrowsUploadFailed()
    {
        var client = CreateClient(_ => JsonResponse(
            HttpStatusCode.OK,
            """{"object":"file","bytes":11,"purpose":"user_data"}"""));

        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            client.UploadAsync(PngBytes, "image/png", 604_800));

        Assert.AreEqual(VisionErrorCodes.ProviderFileUploadFailed, ex.Code);
        StringAssert.Contains(ex.Message, "file id");
    }

    // ──────── lifetime 边界 ────────

    [TestMethod]
    public async Task UploadAsync_LifetimeBelowMin_ClampedToOneHour()
    {
        long sentLifetime = 0;
        var client = CreateClient(request =>
        {
            var multipart = (MultipartFormDataContent)request.Content!;
            sentLifetime = long.Parse(ReadPartAsync(multipart, "lifetime").GetAwaiter().GetResult()!);
            return JsonResponse(HttpStatusCode.OK, """{"id":"file-api-lower","created_at":1750000000}""");
        });

        var result = await client.UploadAsync(PngBytes, "image/png", lifetimeSeconds: 60);

        Assert.AreEqual(3_600, sentLifetime, "lifetime < 3600 必须钳制到官方下限");
        Assert.AreEqual(3_600, result.LifetimeSeconds);
        Assert.AreEqual(1_750_003_600, result.ExpiresAt.ToUnixTimeSeconds());
    }

    [TestMethod]
    public async Task UploadAsync_LifetimeAboveMax_ClampedToThirtyDays()
    {
        long sentLifetime = 0;
        var client = CreateClient(request =>
        {
            var multipart = (MultipartFormDataContent)request.Content!;
            sentLifetime = long.Parse(ReadPartAsync(multipart, "lifetime").GetAwaiter().GetResult()!);
            return JsonResponse(HttpStatusCode.OK, """{"id":"file-api-upper","created_at":1750000000}""");
        });

        var result = await client.UploadAsync(PngBytes, "image/png", lifetimeSeconds: 99_999_999);

        Assert.AreEqual(2_592_000, sentLifetime, "lifetime > 2592000 必须钳制到官方上限");
        Assert.AreEqual(2_592_000, result.LifetimeSeconds);
    }

    // ──────── 入参校验 ────────

    [TestMethod]
    public async Task UploadAsync_UnsupportedMime_Rejected()
    {
        var client = CreateClient(_ => JsonResponse(HttpStatusCode.OK, """{"id":"file-api-x"}"""));
        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            client.UploadAsync(PngBytes, "application/pdf", 604_800));
        Assert.AreEqual(VisionErrorCodes.MediaInvalid, ex.Code);
    }

    [TestMethod]
    public async Task UploadAsync_EmptyBytes_Rejected()
    {
        var client = CreateClient(_ => JsonResponse(HttpStatusCode.OK, """{"id":"file-api-x"}"""));
        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            client.UploadAsync([], "image/png", 604_800));
        Assert.AreEqual(VisionErrorCodes.MediaInvalid, ex.Code);
    }

    [TestMethod]
    public async Task UploadAsync_OverFileLimit_RejectedWithRequestLimitExceeded()
    {
        var smallPolicy = new VisionRequestPolicy { FilesMaxBytesPerImage = 10 };
        var client = CreateClient(_ => JsonResponse(HttpStatusCode.OK, """{"id":"file-api-x"}"""), smallPolicy);

        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            client.UploadAsync(PngBytes, "image/png", 604_800));
        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, ex.Code);
    }

    // ──────── file_id 失效 helper（V3-S2 重建路径预留） ────────

    [TestMethod]
    public void ThrowIfFileExpired_ExpiredReference_ThrowsProviderFileExpired()
    {
        var expired = new ProviderFileReference(
            "file-api-expired",
            "image/png",
            DateTimeOffset.UtcNow.AddSeconds(-1));

        var ex = Assert.ThrowsExactly<VisionPipelineException>(() =>
            DeepSeekFilesApiClient.ThrowIfFileExpired(expired, DateTimeOffset.UtcNow));

        Assert.AreEqual(VisionErrorCodes.ProviderFileExpired, ex.Code);
        Assert.IsFalse(ex.Message.Contains("file-api-expired", StringComparison.Ordinal),
            "file_id 不进异常 message（ADR §8）");
    }

    [TestMethod]
    public void ThrowIfFileExpired_ValidReference_DoesNotThrow()
    {
        var valid = new ProviderFileReference(
            "file-api-valid",
            "image/png",
            DateTimeOffset.UtcNow.AddHours(1));
        DeepSeekFilesApiClient.ThrowIfFileExpired(valid, DateTimeOffset.UtcNow); // 不抛即通过
    }

    // ──────── 值类型语义 ────────

    [TestMethod]
    public void ProviderFileUploadResult_ExpiresAt_ComputedFromUploadedAtPlusLifetime()
    {
        var uploadedAt = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        var result = new ProviderFileUploadResult("file-api-t", "image/webp", 42, 604_800, uploadedAt);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), result.ExpiresAt);
    }

    [TestMethod]
    public void IsSupportedImageMime_AcceptsOfficialFormats()
    {
        Assert.IsTrue(DeepSeekFilesApiClient.IsSupportedImageMime("image/jpeg"));
        Assert.IsTrue(DeepSeekFilesApiClient.IsSupportedImageMime("image/png"));
        Assert.IsTrue(DeepSeekFilesApiClient.IsSupportedImageMime("image/gif"));
        Assert.IsTrue(DeepSeekFilesApiClient.IsSupportedImageMime("image/webp"));
        Assert.IsFalse(DeepSeekFilesApiClient.IsSupportedImageMime("image/bmp"));
        Assert.IsFalse(DeepSeekFilesApiClient.IsSupportedImageMime("application/pdf"));
        Assert.IsFalse(DeepSeekFilesApiClient.IsSupportedImageMime(null));
    }

    // ──────── helpers ────────

    private static DeepSeekFilesApiClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> send,
        VisionRequestPolicy? policy = null)
        => new(
            new HttpClient(new StubHttpMessageHandler(send)),
            ApiKey,
            policy,
            Endpoint);

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
        => new(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private static async Task<string?> ReadPartAsync(MultipartFormDataContent multipart, string name)
    {
        foreach (var part in multipart)
        {
            var disposition = part.Headers.ContentDisposition;
            if (disposition?.Name?.Trim('"') != name)
                continue;
            if (part is StringContent stringContent)
                return await stringContent.ReadAsStringAsync();
            return null;
        }

        return null;
    }

    private static async Task<byte[]?> ReadFilePartAsync(MultipartFormDataContent multipart, string name)
    {
        foreach (var part in multipart)
        {
            var disposition = part.Headers.ContentDisposition;
            if (disposition?.Name?.Trim('"') != name)
                continue;
            if (part is ByteArrayContent byteContent)
                return await byteContent.ReadAsByteArrayAsync();
            return null;
        }

        return null;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
