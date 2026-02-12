using Minio;
using Minio.DataModel.Args;

namespace PuddingPlatform.Services;

/// <summary>
/// MinIO 对象存储服务，负责 Skill 包文件的上传、预签名 URL 生成与删除。
/// </summary>
public sealed class MinioStorageService
{
    private readonly IMinioClient _client;
    private readonly string _bucket;
    private readonly ILogger<MinioStorageService> _logger;

    public MinioStorageService(IConfiguration config, ILogger<MinioStorageService> logger)
    {
        _logger = logger;
        var endpoint  = config["Minio__Endpoint"]  ?? "localhost:9000";
        var accessKey = config["Minio__AccessKey"] ?? "pudding";
        var secretKey = config["Minio__SecretKey"] ?? "pudding_dev_minio";
        _bucket = config["Minio__BucketSkills"]    ?? "pudding-skills";

        _client = new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .Build();
    }

    /// <summary>确保存储桶存在（如不存在则创建）。</summary>
    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        var exists = await _client.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(_bucket), ct);
        if (!exists)
        {
            await _client.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(_bucket), ct);
            _logger.LogInformation("[Minio] Bucket '{Bucket}' created.", _bucket);
        }
    }

    /// <summary>将流上传到 MinIO，返回对象 key。</summary>
    public async Task<string> UploadAsync(
        string objectKey,
        Stream stream,
        long size,
        string contentType,
        CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);

        var args = new PutObjectArgs()
            .WithBucket(_bucket)
            .WithObject(objectKey)
            .WithStreamData(stream)
            .WithObjectSize(size)
            .WithContentType(contentType);

        await _client.PutObjectAsync(args, ct);
        _logger.LogInformation("[Minio] Uploaded '{Key}' ({Size} bytes)", objectKey, size);
        return objectKey;
    }

    /// <summary>生成预签名下载 URL（有效期 24 小时）。</summary>
    public async Task<string> GetPresignedDownloadUrlAsync(
        string objectKey,
        int expirySeconds = 86400,
        CancellationToken ct = default)
    {
        var args = new PresignedGetObjectArgs()
            .WithBucket(_bucket)
            .WithObject(objectKey)
            .WithExpiry(expirySeconds);

        return await _client.PresignedGetObjectAsync(args);
    }

    /// <summary>删除对象。</summary>
    public async Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        var args = new RemoveObjectArgs()
            .WithBucket(_bucket)
            .WithObject(objectKey);

        await _client.RemoveObjectAsync(args, ct);
        _logger.LogInformation("[Minio] Deleted '{Key}'", objectKey);
    }

    /// <summary>构造 Skill 包在 MinIO 中的标准 Object Key。</summary>
    public static string BuildObjectKey(string skillPackageId, string version, string fileName) =>
        $"skill-packages/{skillPackageId}/{version}/{fileName}";
}
