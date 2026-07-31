using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// Volcengine Ark adapter for the OpenAI-compatible image generation endpoint.
/// Provider URLs are downloaded immediately because Ark result URLs are temporary.
/// </summary>
public sealed class VolcengineArkImageGenerationProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<VolcengineArkImageGenerationProvider> logger)
    : IImageGenerationProvider
{
    private const int MaxImageBytes = 50 * 1024 * 1024;
    private const int MaxErrorBodyCharacters = 4_096;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public bool CanHandle(string providerId)
        => string.Equals(
            providerId,
            "volcengine-ark",
            StringComparison.OrdinalIgnoreCase);

    public async Task<ImageGenerationProviderResult> GenerateAsync(
        ImageGenerationProviderRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        ValidateModelCapabilities(request);

        var client = httpClientFactory.CreateClient("ImageGeneration");
        var endpoint = $"{request.Endpoint.TrimEnd('/')}/images/generations";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["prompt"] = request.Prompt,
            ["response_format"] = "url",
            ["size"] = request.Size,
            ["stream"] = false,
            ["output_format"] = request.OutputFormat,
            ["watermark"] = request.Watermark,
            ["optimize_prompt_options"] = new
            {
                mode = request.OptimizePromptMode,
            },
        };
        var supportsSequentialImageGeneration =
            !request.ModelId.Contains(
                "seedream-5-0-pro",
                StringComparison.OrdinalIgnoreCase);
        if (supportsSequentialImageGeneration)
        {
            payload["sequential_image_generation"] =
                request.ImageCount > 1 ? "auto" : "disabled";
        }
        if (supportsSequentialImageGeneration && request.ImageCount > 1)
        {
            payload["sequential_image_generation_options"] = new
            {
                max_images = request.ImageCount,
            };
        }
        if (request.InputImages.Count == 1)
            payload["image"] = request.InputImages[0];
        else if (request.InputImages.Count > 1)
            payload["image"] = request.InputImages;
        if (request.EnableWebSearch)
            payload["tools"] = new[] { new { type = "web_search" } };

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(
                payload,
                options: JsonOptions),
        };
        message.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", request.ApiKey);

        using var response = await client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            if (errorBody.Length > MaxErrorBodyCharacters)
                errorBody = errorBody[..MaxErrorBodyCharacters];
            throw new InvalidOperationException(
                $"Ark image generation failed with HTTP {(int)response.StatusCode}: {errorBody}");
        }

        await using var responseStream =
            await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(
            responseStream,
            cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                "Ark image generation returned no downloadable image URL.");
        }

        var images = new List<ImageGenerationProviderImage>(
            data.GetArrayLength());
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlElement)
                || string.IsNullOrWhiteSpace(urlElement.GetString()))
            {
                throw new InvalidOperationException(
                    "Ark image generation returned an item without a downloadable image URL.");
            }

            var imageUri = new Uri(urlElement.GetString()!, UriKind.Absolute);
            if (!string.Equals(
                    imageUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Ark image generation returned a non-HTTPS image URL.");
            }

            using var download = await client.GetAsync(
                imageUri,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            download.EnsureSuccessStatusCode();
            if (download.Content.Headers.ContentLength is > MaxImageBytes)
            {
                throw new InvalidOperationException(
                    $"Generated image exceeds the {MaxImageBytes}-byte artifact limit.");
            }

            var content = await ReadBoundedAsync(
                await download.Content.ReadAsStreamAsync(ct),
                ct);
            var mimeType = NormalizeMimeType(
                download.Content.Headers.ContentType?.MediaType,
                content);
            images.Add(new ImageGenerationProviderImage
            {
                Content = content,
                MimeType = mimeType,
            });
        }
        logger.LogInformation(
            "[ImageGeneration:Ark] Generated model={ModelId} images={ImageCount} bytes={Bytes}",
            request.ModelId,
            images.Count,
            images.Sum(item => item.Content.Length));
        return new ImageGenerationProviderResult
        {
            Images = images,
        };
    }

    private static void ValidateModelCapabilities(
        ImageGenerationProviderRequest request)
    {
        var model = request.ModelId.ToLowerInvariant();
        var isPro = model.Contains(
            "seedream-5-0-pro",
            StringComparison.Ordinal);
        var isLite = model.Contains(
            "seedream-5-0-",
            StringComparison.Ordinal)
            && !isPro;

        if (isPro)
        {
            if (request.ImageCount != 1)
            {
                throw new InvalidOperationException(
                    "Seedream 5.0 Pro does not support sequential multi-image output.");
            }
            if (request.EnableWebSearch)
            {
                throw new InvalidOperationException(
                    "Seedream 5.0 Pro does not support image web search.");
            }
            ValidateSize(
                request.Size,
                ["1K", "2K"],
                minPixels: 921_600,
                maxPixels: 4_624_220,
                "Seedream 5.0 Pro");
        }
        else if (isLite)
        {
            if (string.Equals(
                    request.OptimizePromptMode,
                    "fast",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Seedream 5.0 Lite only supports standard prompt optimization.");
            }
            ValidateSize(
                request.Size,
                ["2K", "3K", "4K"],
                minPixels: 3_686_400,
                maxPixels: 16_777_216,
                "Seedream 5.0 Lite");
        }
    }

    private static void ValidateSize(
        string size,
        IReadOnlyCollection<string> allowedTiers,
        long minPixels,
        long maxPixels,
        string modelName)
    {
        if (allowedTiers.Contains(
                size,
                StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var dimensions = size.Split(
            ['x', 'X'],
            StringSplitOptions.RemoveEmptyEntries);
        if (dimensions.Length != 2
            || !int.TryParse(dimensions[0], out var width)
            || !int.TryParse(dimensions[1], out var height)
            || width <= 0
            || height <= 0)
        {
            throw new InvalidOperationException(
                $"{modelName} size must be {string.Join(", ", allowedTiers)} or WIDTHxHEIGHT.");
        }

        var pixels = (long)width * height;
        var ratio = (double)width / height;
        if (pixels < minPixels
            || pixels > maxPixels
            || ratio < 1d / 16d
            || ratio > 16d)
        {
            throw new InvalidOperationException(
                $"{modelName} custom size is outside its supported pixel or aspect-ratio range.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        CancellationToken ct)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            if (destination.Length + read > MaxImageBytes)
            {
                throw new InvalidOperationException(
                    $"Generated image exceeds the {MaxImageBytes}-byte artifact limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        if (destination.Length == 0)
            throw new InvalidOperationException("Generated image is empty.");
        return destination.ToArray();
    }

    private static string NormalizeMimeType(
        string? responseMimeType,
        ReadOnlySpan<byte> content)
    {
        var normalized = responseMimeType?.Trim().ToLowerInvariant();
        if (normalized is "image/jpeg" or "image/png" or "image/webp")
            return normalized;

        if (content.Length >= 8
            && content[0] == 0x89
            && content[1] == 0x50
            && content[2] == 0x4E
            && content[3] == 0x47
            && content[4] == 0x0D
            && content[5] == 0x0A
            && content[6] == 0x1A
            && content[7] == 0x0A)
        {
            return "image/png";
        }
        if (content.Length >= 3
            && content[0] == 0xFF
            && content[1] == 0xD8
            && content[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (content.Length >= 12
            && content[..4].SequenceEqual("RIFF"u8)
            && content.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        throw new InvalidOperationException(
            $"Ark returned an unsupported image media type '{responseMimeType}'.");
    }
}
